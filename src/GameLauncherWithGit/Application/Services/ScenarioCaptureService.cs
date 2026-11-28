using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Infrastructure.Abstractions;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Threading.Channels;

namespace GameLauncherWithGit.Application.Services;

public sealed partial class ScenarioCaptureService : IAsyncDisposable
{
    private const string FormalConnectionRequired = "正式記録中のゲームで操作してください。";
    private const int MaxEvents = 1000;
    private const int MaxBytes = 2 * 1024 * 1024;
    private const int MaxCandidateSamples = 5;
    private const int MaxSampleLength = 1500;
    private readonly IScenarioCaptureStore _store;
    private readonly ILunaCaptureHost _host;
    private readonly ILogger<ScenarioCaptureService> _logger;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _operations = new(1, 1);
    private long _operationVersion;
    private readonly Channel<WriteWork> _writes = Channel.CreateBounded<WriteWork>(new BoundedChannelOptions(MaxEvents)
    { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly LinkedList<CaptureReceived> _buffer = [];
    private readonly Dictionary<string, CaptureCandidate> _candidates = [];
    private readonly Dictionary<string, long> _attempted = [];
    private readonly Dictionary<string, (CaptureRole Role, long? Label)> _currentSelections = [];
    private int _bufferBytes;
    private int _pendingBytes;
    private long _sequence;
    private long _received;
    private long _saved;
    private long _lost;
    private CaptureProfile? _profile;
    private IReadOnlyList<CaptureLabel> _labels = [];
    private long? _session;
    private CapturePosition? _lastBodyPosition, _rangeStart;
    private Process? _body;
    private Action<CaptureSource, string>? _connectionOutput;
    private Action<uint, bool>? _connectionChanged;
    private long _connectionEpoch;
    private bool _active;
    private bool _finishing;
    private bool _connected;
    private bool _connectionLost;
    private bool _needsProcess;
    private bool _preview, _connecting, _paused;
    private long _pathEpoch;
    private CaptureProcess? _process;
    private string _status = "未接続";
    private readonly List<SpeakerPending> _speakerPending = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _writerTask, _resolverTask;
    private sealed record WriteWork(long Session, CaptureReceived? Received, CaptureRules Rules, TaskCompletionSource? Barrier = null,
        long PathEpoch = 0, long? RecordId = null, long ScopeVersion = 0);
    private sealed record SpeakerPending(long Session, long Sequence, DateTimeOffset ReceivedAt, long? RecordId, long ScopeVersion);

    public event Action? StateChanged;

    internal async Task<T> RunOperationAsync<T>(Func<Task<T>> action)
    {
        await _operations.WaitAsync();
        try { return await action(); }
        finally { _operations.Release(); }
    }

    internal Task RunOperationAsync(Func<Task> action) => RunOperationAsync(async () => { await action(); return true; });

    internal void ChangeOperationVersion() { lock (_gate) _operationVersion++; }

    internal (long Version, long? ProfileId, long? RecordId, long? SessionId, bool Active, bool Formal, bool Paused,
        CapturePosition? LastBody, CapturePosition? RangeStart, bool RangeCanStart) OperationState
    {
        get
        {
            lock (_gate) return (_operationVersion, _profile?.Id, ActiveRecordId, _session, _active || _finishing || _connecting,
                HasFormalConnection, _paused, _lastBodyPosition, _rangeStart, RangeStartRejection() is null);
        }
    }

    public (ScreenshotTarget Target, int ProcessId) FreezeScreenshotTarget()
    {
        var (target, processId, _) = FreezeScreenshotPlayTarget();
        return (target, processId);
    }

    internal (ScreenshotTarget Target, int ProcessId, long PlayEpoch) FreezeScreenshotPlayTarget()
    {
        lock (_gate)
        {
            if (!_active || _finishing || !_connected || _preview || _paused || ActiveRecordId is not { } recordId || _process is null
                || _session is not { } session || _lastBodyPosition is not { Sequence: > 0 } position
                || position.SessionId != session)
                throw new InvalidOperationException("正式記録を開始し、本文を受信してから撮影してください。");
            return (new(recordId, new(session, position.Sequence)), _process.Id, _playEpoch);
        }
    }

    internal void RequireScreenshotPlayTarget(long playEpoch, long recordId)
    {
        lock (_gate)
            if (_recordTargets is not null && (_playEpoch != playEpoch || CurrentPlayRecordId != recordId))
                throw new InvalidOperationException("撮影中にプレイ中の記録対象が変わりました。");
    }

    internal void RequireScreenshotProcessAlive(ScreenshotTarget target, int processId)
    {
        lock (_gate)
            if (!_connected || ActiveRecordId != target.CaptureGameId || _session != target.Event.SessionId
                || _body is not { } body || body.Id != processId || body.HasExited)
                throw new InvalidOperationException("撮影対象のゲームが終了しました。");
    }

    public ScenarioCaptureService(IScenarioCaptureStore store, ILunaCaptureHost host, ILogger<ScenarioCaptureService> logger,
        ScenarioRecordTargetResolver recordTargets) : this(store, host, recordTargets, logger) { }

    internal ScenarioCaptureService(IScenarioCaptureStore store, ILunaCaptureHost host, ILogger<ScenarioCaptureService> logger)
        : this(store, host, null, logger) { }

    private ScenarioCaptureService(IScenarioCaptureStore store, ILunaCaptureHost host, ScenarioRecordTargetResolver? recordTargets,
        ILogger<ScenarioCaptureService> logger)
    {
        _store = store; _host = host; _logger = logger;
        _recordTargets = recordTargets;
        _writerTask = Task.Run(WriteLoopAsync);
        _resolverTask = Task.Run(ResolveLoopAsync);
        _targetMonitorTask = recordTargets is null ? Task.CompletedTask : Task.Run(MonitorPlayTargetAsync);
    }

    public CaptureSnapshot Snapshot
    {
        get
        {
            lock (_gate) return new()
            {
                OperationVersion = _operationVersion,
                PlayScopeVersion = _playEpoch,
                Profile = _profile,
                RecordId = ActiveRecordId,
                PlayGameId = _playProcessExited ? null : _playProfile?.GameId,
                SessionId = _session,
                Status = _status,
                Connected = _connected,
                Preview = _preview,
                Connecting = _connecting,
                Recording = HasFormalConnection && !_paused,
                Paused = _paused,
                Process = _process,
                NeedsProcess = _needsProcess,
                Active = _active || _finishing || _connecting,
                Received = _received,
                Saved = _saved,
                Lost = _lost,
                Candidates = _candidates.Values.ToArray(),
                PreviewItems = _preview ? BuildPreviewItems() : [],
                RangeStart = _rangeStart,
                RangeCanStart = RangeStartRejection() is null,
                RangeCanEnd = _rangeStart is not null && _lastBodyPosition?.CompareTo(_rangeStart) > 0
            };
        }
    }

    public IReadOnlyList<CaptureProcess> GetProcesses() => _host.GetProcesses();
    public CaptureProcess? GetForegroundProcess() => _host.GetForegroundProcess();

    private CaptureTextItem[] BuildPreviewItems()
    {
        if (_profile is null) return [];
        var rules = _profile.Rules;
        var events = _candidates.Values.SelectMany(candidate => candidate.Samples.Where(sample => !sample.Truncated).Select(sample =>
            new CaptureReceived(sample.Sequence, sample.ReceivedAt, candidate.Source, sample.Text, candidate.Role, candidate.LabelId)))
            .OrderBy(x => x.Sequence).ToArray();
        var items = new List<CaptureTextItem>();
        foreach (var received in events.Where(x => x.Role == CaptureRole.Body))
            foreach (var segment in ScenarioTextProcessor.Split(received.Text, rules))
            {
                var label = received.LabelId is { } labelId ? _labels.FirstOrDefault(x => x.Id == labelId)
                    : _labels.FirstOrDefault(x => x.Kind == segment.Kind);
                items.Add(new(items.Count + 1, 0, received.Sequence, received.ReceivedAt, received.Text,
                    segment.Start, segment.Length, label?.Id, label?.Name, label?.Kind, null, false, false, false));
            }
        var speakers = ScenarioTextProcessor.MatchSpeakers(events, items, DateTimeOffset.UtcNow);
        return items.Select(item => speakers.TryGetValue(item.Id, out var speaker) ? item with { Speaker = speaker } : item).ToArray();
    }

    public Task<bool> BeginGameAsync(string gameId) => RunOperationAsync(() => BeginGameCoreAsync(gameId, reportFailure: false));

    // The launcher already started the game and must distinguish a failed follow-up from a normal skip.
    internal Task<bool> BeginGameForLaunchAsync(string gameId, Process? process) =>
        RunOperationAsync(() => BeginGameCoreAsync(gameId, reportFailure: true, launched: true, process: process));

    private async Task<bool> BeginGameCoreAsync(string gameId, bool reportFailure, bool launched = false, Process? process = null)
    {
        try
        {
            if (launched && _recordTargets is not null)
                await ClearPlayTargetCoreAsync();
            var profile = await _store.GetOrCreateCaptureProfileAsync(gameId);
            if (!launched && _recordTargets is not null)
            {
                if (!profile.Enabled) return false;
                await PreparePlayTargetCoreAsync(profile);
                return true;
            }
            if (_recordTargets is not null || profile.Enabled)
                await SelectPlayTargetCoreAsync(profile, process);
            if (!profile.Enabled) return false;
            var labels = await _store.GetCaptureLabelsAsync(profile.Id);
            lock (_gate)
                if (_active || _finishing || _playProfile?.Id != profile.Id || _playProcessExited) return false;
            lock (_gate)
            {
                _operationVersion++;
                _profile = profile; _session = null; _active = true; _connected = false; _needsProcess = true;
                _connectionLost = false;
                _labels = labels;
                _sequence = _received = _saved = _lost = 0; _status = "ゲーム本体を選択してください";
                _lastBodyPosition = null; _rangeStart = null; _preview = true; _paused = false; _process = null;
                _speakerPending.Clear();
                _buffer.Clear(); _candidates.Clear(); _attempted.Clear(); _currentSelections.Clear(); _bufferBytes = 0;
            }
            if (profile.BodyExecutablePath is { } path)
            {
                if (await FindConfiguredBodyAsync(profile.Id, path) is { } candidate)
                {
                    try { await SelectProcessCoreAsync(candidate.Id, preview: false, requiredPath: path, expectedStart: candidate.StartedAt); }
                    catch when (Snapshot.NeedsProcess) { return true; }
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Capture startup failed. game={Game}", gameId);
            lock (_gate) _status = "抽出の開始に失敗しました。ログを確認してください。";
            if (reportFailure) throw;
            return false;
        }
        finally
        {
            if (process is not null && _playProcess != process) process.Dispose();
        }
    }

    internal async Task StartConfiguredCoreAsync(CaptureProfile profile)
    {
        if (!CanStartConfigured(profile))
            throw new InvalidOperationException("ゲームの記録設定と現在の接続状態を確認してください。");
        if (profile.BodyExecutablePath is not { } path || await FindConfiguredBodyAsync(profile.Id, path) is not { } candidate)
            throw new InvalidOperationException("記録対象のゲームを1つだけ起動してください。");
        if (_recordTargets is null) await SelectPlayTargetCoreAsync(profile);
        lock (_gate)
        {
            if (CurrentPlayRecordId is null || _playProfile?.Id != profile.Id) throw new InvalidOperationException(UnknownRecord);
            _profile = profile; _session = null; _operationVersion++;
        }
        await SelectProcessCoreAsync(candidate.Id, preview: false, requiredPath: path, expectedStart: candidate.StartedAt);
    }

    private async Task<(int Id, DateTime StartedAt)?> FindConfiguredBodyAsync(long profileId, string path)
    {
        var candidates = GetProcesses().Where(process => string.Equals(process.Path, path, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (_recordTargets is not null)
        {
            // The play monitor resolves a launcher handoff before this operation. Never use the only
            // matching executable if its process is not the one associated with this play selection.
            if (_launchBodyHandoff is not null || _playProcess is not { } tracked
                || tracked.HasExited || !candidates.Any(candidate => candidate.Id == tracked.Id)) return null;
            return (tracked.Id, tracked.StartTime.ToUniversalTime());
        }
        if (candidates.Length != 1 || await HasOtherProfileForBodyAsync(profileId, path)) return null;
        using var process = GetMatchingProcess(candidates[0].Id, path);
        return process is null ? null : (process.Id, process.StartTime.ToUniversalTime());
    }

    internal bool CanStartConfigured(CaptureProfile profile)
    {
        lock (_gate)
        {
            if (!profile.HasConfirmedRecordingSetup || _finishing || _connecting) return false;
            if (!_active) return true;
            // Launch can finish before the configured game body appears. That waiting state is not a trial connection.
            return _recordTargets is not null && _needsProcess && !_connected && _body is null
                && _profile?.Id == profile.Id && _playProfile?.Id == profile.Id;
        }
    }

    public Task SelectProcessAsync(int id, bool preview = true) => RunOperationAsync(async () =>
    {
        await RefreshPlayTargetCoreAsync();
        await SelectProcessCoreAsync(id, preview);
    });

    internal async Task SelectProcessCoreAsync(int id, bool preview = true, string? requiredPath = null, DateTime? expectedStart = null)
    {
        CaptureProfile profile;
        lock (_gate)
        {
            if (_connecting || _finishing) throw new InvalidOperationException("接続処理の完了を待ってください。");
            profile = _profile ?? throw new InvalidOperationException("ゲームが選択されていません。");
            _connecting = true;
            _operationVersion++;
        }
        try
        {
            await FinishCoreAsync();
            profile = (await _store.GetCaptureProfilesAsync()).Single(x => x.Id == profile.Id);
            if (requiredPath is not null && (!profile.HasConfirmedRecordingSetup
                || !string.Equals(profile.BodyExecutablePath, requiredPath, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("ゲームの記録設定が更新されました。");
            var labels = await _store.GetCaptureLabelsAsync(profile.Id);
            lock (_gate)
            {
                _active = true; _connected = false; _needsProcess = false; _preview = true; _paused = false; _session = null;
                _connectionLost = false;
                _sequence = _received = _saved = _lost = 0; _status = "接続中";
                _process = null; _lastBodyPosition = null;
                _buffer.Clear(); _candidates.Clear(); _attempted.Clear(); _currentSelections.Clear(); _bufferBytes = 0;
                _profile = profile;
                _labels = labels;
            }
            var candidate = GetProcesses().SingleOrDefault(x => x.Id == id)
                ?? throw new InvalidOperationException("対象プロセスが見つかりません。");
            await _host.WaitForPreviousConnectionAsync(id);
            var process = Process.GetProcessById(id);
            if (requiredPath is not null || expectedStart is not null)
            {
                bool matches;
                try
                {
                    matches = (requiredPath is null || string.Equals(process.MainModule?.FileName, requiredPath, StringComparison.OrdinalIgnoreCase))
                        && (expectedStart is null || process.StartTime.ToUniversalTime() == expectedStart);
                }
                catch { process.Dispose(); throw; }
                if (!matches) { process.Dispose(); throw new InvalidOperationException("起動中のゲームが変わりました。"); }
            }
            process.EnableRaisingEvents = true;
            process.Exited += OnBodyExited;
            lock (_gate)
            {
                _body = process; _process = candidate;
                var epoch = ++_connectionEpoch;
                _host.Output += _connectionOutput = (source, text) => OnOutput(epoch, source, text);
                _host.ConnectionChanged += _connectionChanged = (pid, connected) => OnConnection(epoch, pid, connected);
            }
            if (process.HasExited) throw new InvalidOperationException("ゲーム本体は終了しています。");
            await _host.ConnectAsync(id);
            lock (_gate)
            {
                if (!_active || _body != process || _connectionLost || process.HasExited)
                    throw new InvalidOperationException("接続中にゲーム本体が終了しました。");
                _connected = true; _status = "テスト受信中（保存しません）";
            }
            if (!preview && profile.Rules.Selections.Any(x => x.Role == CaptureRole.Body))
                await ConfirmProcessCoreAsync(persistSetup: requiredPath is null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Capture connection failed. game={Game} pid={Pid}", profile.Id, id);
            await FinishCoreAsync();
            lock (_gate) { _needsProcess = true; _status = "接続失敗・選び直して再試行できます"; }
            throw;
        }
        finally { lock (_gate) _connecting = false; StateChanged?.Invoke(); }
    }

    public Task ConfirmProcessAsync() => RunOperationAsync(async () =>
    {
        await RefreshPlayTargetCoreAsync();
        await ConfirmProcessCoreAsync();
    });

    internal async Task ConfirmProcessCoreAsync(bool persistSetup = true, bool paused = false)
    {
        CaptureProfile profile; CaptureProcess target; Process body; bool takePlaySelection;
        lock (_gate)
        {
            if (!_active || !_connected || !_preview || _body is null || _process is null || _profile is null)
                throw new InvalidOperationException("テスト接続して受信内容を確認してください。");
            if (!_profile.Rules.Selections.Any(x => x.Role == CaptureRole.Body))
                throw new InvalidOperationException("本文・セリフ・地の文の取得元を選択してください。");
            profile = _profile; target = _process; body = _body;
            takePlaySelection = _recordTargets is not null && _playProfile?.Id != profile.Id;
        }
        ScenarioRecordTargetResolver.ResolvedRecord? draftRecord = null;
        Process? draftProcess = null;
        Process? previousPlayProcess = null;
        Process? currentPlayProcess = null;
        long? session = null;
        var persisted = (await _store.GetCaptureProfilesAsync()).Single(x => x.Id == profile.Id);
        profile = persisted with { Rules = profile.Rules, BodyExecutablePath = target.Path };
        try
        {
            if (takePlaySelection)
            {
                var setting = await _recordTargets!.ReadRepositoryPathAsync(profile.GameId!, _stop.Token);
                draftRecord = await _recordTargets.ResolveAsync(setting, profile.Id, _stop.Token);
            }
            long recordId;
            lock (_gate)
                recordId = (takePlaySelection ? draftRecord is { IsCurrent: true } ? draftRecord.RecordId : null : ActiveRecordId)
                    ?? throw new InvalidOperationException(UnknownRecord);
            if (_recordTargets is not null && (takePlaySelection || _playProcess?.Id != body.Id))
                draftProcess = Process.GetProcessById(body.Id);
            session = await _store.BeginCaptureSessionAsync(recordId);
            if (persistSetup) await _store.SaveCaptureProfileAsync(profile);
            lock (_gate)
            {
                if (!_active || !_connected || _body != body || body.HasExited
                    || (takePlaySelection ? draftRecord is not { IsCurrent: true } : ActiveRecordId != recordId))
                    throw new InvalidOperationException("確定中に接続または記録対象が変わりました。");
                if (draftProcess is not null)
                {
                    previousPlayProcess = _playProcess;
                    _playProcess = currentPlayProcess = draftProcess;
                    _playProcessExited = false;
                    draftProcess = null;
                }
                _launchBodyHandoff = null;
                if (takePlaySelection)
                {
                    _playRecord?.Dispose();
                    _playRecord = draftRecord; draftRecord = null;
                    _playProfile = profile;
                    _continuation = null;
                    _playEpoch++;
                }
                _profile = profile; _session = session; _preview = false; _paused = paused; _pathEpoch++;
                _operationVersion++;
                _sequence = _received = _saved = _lost = 0;
                _lastBodyPosition = paused ? null : new(session.Value, 0, 0);
                _status = paused ? "記録を一時停止中（本文・画像は保存しません）" : "記録中・受信待ち";
                _buffer.Clear(); _attempted.Clear(); _bufferBytes = 0;
                foreach (var key in _candidates.Keys.ToArray())
                    _candidates[key] = _candidates[key] with { Samples = [] };
            }
            if (previousPlayProcess is not null) ReleasePlayProcess(previousPlayProcess);
            if (currentPlayProcess is not null) WatchPlayProcess(currentPlayProcess);
            StateChanged?.Invoke();
        }
        catch { if (session is { } id) await _store.EndCaptureSessionAsync(id); throw; }
        finally { draftRecord?.Dispose(); draftProcess?.Dispose(); }
    }

    private async void OnBodyExited(object? sender, EventArgs args)
    {
        if (sender is not Process process) return;
        lock (_gate)
        {
            if (process != _body) return;
            MarkConnectionLost();
        }
        await Task.Yield();
        await RunOperationAsync(() => FinishCoreAsync(process));
    }

    private void OnConnection(long epoch, uint pid, bool connected)
    {
        if (connected) return;
        Process body;
        lock (_gate)
        {
            if (_connectionEpoch != epoch || _body is not { } current || current.Id != pid) return;
            body = current;
            MarkConnectionLost();
        }
        _ = RunOperationAsync(() => FinishCoreAsync(body));
    }

    private void MarkConnectionLost()
    {
        if (!_connectionLost) _operationVersion++;
        _connected = false;
        _connectionLost = true;
        _status = "切断されました";
    }

    private void OnOutput(long epoch, CaptureSource source, string text)
    {
        lock (_gate)
        {
            if (_connectionEpoch != epoch || !_active || _paused || _connectionLost || _body?.Id != source.ProcessId
                || _profile is null || (!_preview && _recordTargets is not null && ActiveRecordId is null)) return;
            _received++;
            var receivedAt = DateTimeOffset.UtcNow;
            if (_candidates.ContainsKey(source.Key) || _candidates.Count < 256)
            {
                var truncated = text.Length > MaxSampleLength;
                var sample = new CaptureSample(_received, receivedAt, truncated ? text[..MaxSampleLength] : text, truncated);
                var samples = _candidates.TryGetValue(source.Key, out var previous) ? previous.Samples : [];
                _candidates[source.Key] = new(source, CaptureRole.None, null)
                { Samples = samples.TakeLast(MaxCandidateSamples - 1).Append(sample).ToArray() };
            }
            RefreshCandidates(_profile.Rules);
            if (_preview) { _status = "テスト受信中（保存しません）"; return; }
            if (_session is null) return;
            var (role, label) = Selection(source, _profile.Rules);
            var received = new CaptureReceived(++_sequence, receivedAt, source, text, role, label);
            var size = text.Length * 2L;
            if (size > MaxBytes) { Lose("receive-size"); return; }
            _buffer.AddLast(received); _bufferBytes += (int)size;
            while (_buffer.First is { } first && (_buffer.Count > MaxEvents || _bufferBytes > MaxBytes
                || first.Value.ReceivedAt < received.ReceivedAt.AddSeconds(-30)
                || _buffer.Count(x => x.Source.Key == source.Key) > 200))
            {
                _bufferBytes -= first.Value.Text.Length * 2; _buffer.RemoveFirst();
                if (!_attempted.TryGetValue(first.Value.Source.Key, out var water) || first.Value.Sequence > water) Lose("candidate-buffer");
            }
            if (role != CaptureRole.None) Queue(received, _profile.Rules, _session.Value);
            _status = "受信中";
        }
    }

    private void Lose(string stage)
    {
        _lost++;
        _logger.LogWarning("Capture data was discarded. game={Game} stage={Stage}", _profile?.Id, stage);
    }

    private void Queue(CaptureReceived received, CaptureRules rules, long session)
    {
        if (_attempted.TryGetValue(received.Source.Key, out var water) && received.Sequence <= water) return;
        _attempted[received.Source.Key] = received.Sequence;
        var size = received.Text.Length * 2;
        if (_pendingBytes + size > MaxBytes || !_writes.Writer.TryWrite(new(session, received, rules,
            PathEpoch: _pathEpoch, RecordId: ActiveRecordId, ScopeVersion: _playEpoch))) { Lose("save-queue"); return; }
        _pendingBytes += size;
        if (received.Role == CaptureRole.Body && !string.IsNullOrWhiteSpace(received.Text))
        {
            var position = new CapturePosition(session, received.Sequence, received.Text.Length);
            if (_lastBodyPosition is null || position.CompareTo(_lastBodyPosition) > 0) _lastBodyPosition = position;
        }
    }

    // Evaluate under _gate for both state readers and the operation's current-state check.
    private string? RangeStartRejection()
    {
        if (!HasFormalConnection) return FormalConnectionRequired;
        if (_paused || _lastBodyPosition is null) return "取得中のゲームで区間を開始してください。";
        if (_rangeStart is not null) return "指定中の区間を終了または取り消してください。";
        return null;
    }

    public void StartRange()
    {
        lock (_gate)
        {
            if (RangeStartRejection() is { } message) throw new InvalidOperationException(message);
            _rangeStart = _lastBodyPosition;
            _operationVersion++;
        }
    }

    public CaptureRangeSelection EndRange()
    {
        lock (_gate)
        {
            RequireFormalConnection();
            if (_rangeStart is null || _lastBodyPosition is null || ActiveRecordId is not { } recordId) throw new InvalidOperationException("区間の開始を指定してください。");
            if (_rangeStart.CompareTo(_lastBodyPosition) >= 0) throw new InvalidOperationException("開始後の本文がまだ届いていません。");
            var selection = new CaptureRangeSelection(recordId, _rangeStart, _lastBodyPosition);
            _rangeStart = null;
            _operationVersion++;
            return selection;
        }
    }

    public void CancelRange()
    {
        lock (_gate) { RequireFormalConnection(); _rangeStart = null; _operationVersion++; }
    }

    public void BreakPath()
    {
        lock (_gate)
        {
            RequireFormalConnection();
            if (_paused) throw new InvalidOperationException("記録を再開してから経路を区切ってください。");
            _pathEpoch++;
            _operationVersion++;
            // Candidate backfill must not cross a user-declared path boundary.
            _buffer.Clear(); _bufferBytes = 0;
            _status = "次の本文から別経路で記録します";
        }
        StateChanged?.Invoke();
    }

    public void PauseRecording()
    {
        lock (_gate)
        {
            RequireFormalConnection();
            if (_paused) return;
            _paused = true; _pathEpoch++;
            _operationVersion++;
            _lastBodyPosition = null;
            _buffer.Clear(); _bufferBytes = 0;
            foreach (var key in _candidates.Keys.ToArray())
                _candidates[key] = _candidates[key] with { Samples = [] };
            _status = "記録を一時停止中（本文・画像は保存しません）";
        }
        StateChanged?.Invoke();
    }

    public void ResumeRecording()
    {
        lock (_gate)
        {
            RequireFormalConnection();
            if (!_paused) return;
            _paused = false;
            _operationVersion++;
            _status = "記録再開・次の本文から別経路";
        }
        StateChanged?.Invoke();
    }

    private bool HasFormalConnection => _active && !_finishing && _connected && !_preview && _session is not null && ActiveRecordId is not null;

    private void RequireFormalConnection()
    {
        if (!HasFormalConnection) throw new InvalidOperationException(FormalConnectionRequired);
    }

    public async Task FlushPendingAsync()
    {
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _writes.Writer.WriteAsync(new(0, null, new(), barrier));
        await barrier.Task;
    }

    private (CaptureRole Role, long? Label) Selection(CaptureSource source, CaptureRules rules)
    {
        if (_currentSelections.TryGetValue(source.Key, out var current)) return current;
        var matches = rules.Selections.Where(x => x.Matches(source)).ToArray();
        if (matches.Length != 1 || _candidates.Values.Count(x => matches[0].Matches(x.Source)) != 1)
            return (CaptureRole.None, null);
        return (matches[0].Role, matches[0].LabelId);
    }

    private void RefreshCandidates(CaptureRules rules)
    {
        foreach (var key in _candidates.Keys.ToArray())
        {
            var candidate = _candidates[key];
            var (role, label) = Selection(candidate.Source, rules);
            _candidates[key] = candidate with { Role = role, LabelId = label };
        }
    }

    public Task SaveRulesAsync(CaptureRules rules, CaptureSource? selectedSource = null) =>
        RunOperationAsync(() => SaveRulesCoreAsync(rules, selectedSource));

    internal async Task SaveRulesCoreAsync(CaptureRules rules, CaptureSource? selectedSource = null)
    {
        CaptureProfile profile;
        lock (_gate) profile = _profile ?? throw new InvalidOperationException("ゲームが選択されていません。");
        bool preview;
        lock (_gate) preview = _preview;
        if (!preview)
        {
            var persisted = (await _store.GetCaptureProfilesAsync()).Single(x => x.Id == profile.Id);
            await _store.SaveCaptureProfileAsync(persisted with { Rules = rules });
        }
        lock (_gate)
        {
            if (_profile?.Id != profile.Id) return;
            profile = _profile with { Rules = rules }; _profile = profile;
            if (selectedSource is not null)
            {
                var match = rules.Selections.Single(x => x.Matches(selectedSource));
                _currentSelections[selectedSource.Key] = (match.Role, match.LabelId);
            }
            RefreshCandidates(rules);
            if (HasFormalConnection && !_paused && _session is { } session)
                foreach (var received in _buffer.Where(x => x.ReceivedAt >= DateTimeOffset.UtcNow.AddSeconds(-30)))
                {
                    var (role, label) = Selection(received.Source, rules);
                    if (role != CaptureRole.None)
                        Queue(received with { Role = role, LabelId = label }, rules, session);
                }
        }
    }

    private async Task WriteLoopAsync()
    {
        (long Session, long Epoch)? lastBodyPath = null;
        await foreach (var work in _writes.Reader.ReadAllAsync())
        {
            if (work.Barrier is not null) { work.Barrier.TrySetResult(); continue; }
            var received = work.Received!;
            try
            {
                var hasBody = received.Role == CaptureRole.Body && received.Text.Length > 0;
                var path = (work.Session, work.PathEpoch);
                var saved = await _store.AppendCaptureAsync(work.Session, received, work.Rules,
                    startNewPath: hasBody && lastBodyPath != path);
                if (saved && hasBody) lastBodyPath = path;
                lock (_gate)
                {
                    if (_session == work.Session && saved) _saved++;
                    if (_session == work.Session && saved && received.Role == CaptureRole.Body)
                    {
                        if (_speakerPending.Count >= MaxEvents)
                        {
                            _speakerPending.RemoveAt(0);
                            _logger.LogWarning("Speaker matching limit exceeded; text remains saved with unresolved speaker. session={Session}", work.Session);
                        }
                        var nameSourceConfigured = work.Rules.Selections.Any(selection => selection.Role == CaptureRole.Name);
                        _speakerPending.Add(new(work.Session, received.Sequence, received.ReceivedAt,
                            nameSourceConfigured ? work.RecordId : null, work.ScopeVersion));
                    }
                }
                // Publish only after the store has committed a new body event.
                if (saved && hasBody && !string.IsNullOrWhiteSpace(received.Text) && work.RecordId is { } recordId)
                    PublishLiveFeed(recordId, work.Session, work.ScopeVersion);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Capture save failed. session={Session} stage=save", work.Session);
                lock (_gate) if (_session == work.Session) { _lost++; _status = "保存失敗（一部の受信は未保存です）"; }
            }
            finally { lock (_gate) _pendingBytes -= received.Text.Length * 2; }
        }
    }

    private async Task ResolveLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                await ResolvePendingAsync(DateTimeOffset.UtcNow);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private async Task ResolvePendingAsync(DateTimeOffset now)
    {
        SpeakerPending[] ready;
        lock (_gate)
        {
            ready = _speakerPending.Where(x => x.ReceivedAt.AddSeconds(2) <= now).ToArray();
            foreach (var item in ready) _speakerPending.Remove(item);
        }
        foreach (var group in ready.GroupBy(x => x.Session))
        {
            try
            {
                await _store.ResolveCaptureSpeakersAsync(group.Key,
                group.Min(x => x.ReceivedAt), now, group.Select(x => x.Sequence).ToArray());
                // Matching commits later than the body; separately invalidate its presentation, not the arrival version.
                lock (_gate)
                {
                    if (_session == group.Key && ActiveRecordId is { } recordId
                        && group.Any(item => item.RecordId == recordId && item.ScopeVersion == _playEpoch))
                        PublishLiveFeedSpeakers(recordId, group.Key, _playEpoch);
                }
            }
            catch (Exception ex) { _logger.LogError(ex, "Capture speaker matching failed. session={Session}", group.Key); }
        }
    }

    public Task FinishAsync() => RunOperationAsync(() => FinishCoreAsync());

    internal async Task<bool> FinishCoreAsync(Process? expectedBody = null, bool reportFailure = false, bool requireDrain = false)
    {
        Process? body; long? session;
        lock (_gate)
        {
            if (!_active || (expectedBody is not null && _body != expectedBody)) return false;
            _operationVersion++;
            _connectionEpoch++;
            if (_connectionOutput is not null) _host.Output -= _connectionOutput;
            if (_connectionChanged is not null) _host.ConnectionChanged -= _connectionChanged;
            _connectionOutput = null; _connectionChanged = null;
            _active = false; _finishing = true; _connected = false; _needsProcess = true; _preview = false; _paused = false;
            _rangeStart = null;
            body = _body; _body = null; session = _session;
            if (!_status.Contains("失敗")) _status = "取得終了";
            _buffer.Clear(); _bufferBytes = 0;
            _candidates.Clear();
        }
        StateChanged?.Invoke();
        var failed = false;
        var drained = false;
        try
        {
            if (body is not null)
            {
                body.Exited -= OnBodyExited;
                try
                {
                    if (requireDrain) drained = await _host.DetachAndDrainAsync(body.Id);
                    else _host.Detach(body.Id);
                }
                catch (Exception ex)
                {
                    failed = true;
                    _logger.LogError(ex, "Capture detach failed. session={Session}", session);
                }
                finally { body.Dispose(); }
            }
            await FlushPendingAsync();
            if (session is { } id)
            {
                await ResolvePendingAsync(DateTimeOffset.UtcNow);
                await _store.EndCaptureSessionAsync(id);
            }
        }
        catch (Exception ex) { failed = true; _logger.LogError(ex, "Capture finish failed. session={Session}", session); }
        finally
        {
            lock (_gate)
            {
                if (_recordTargets is not null) _session = null;
                _finishing = false;
            }
            StateChanged?.Invoke();
        }
        if (failed && reportFailure) throw new InvalidOperationException("取得は停止しましたが、終了処理を完了できませんでした。PCで状態を確認してください。");
        return drained && !failed;
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        await RunOperationAsync(async () => { await FinishCoreAsync(); await ClearPlayTargetCoreAsync(); });
        _writes.Writer.TryComplete();
        await Task.WhenAll(_writerTask, _resolverTask, _targetMonitorTask);
        _stop.Dispose();
    }
}
