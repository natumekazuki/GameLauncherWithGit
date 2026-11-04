using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Infrastructure.Abstractions;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Threading.Channels;

namespace GameLauncherWithGit.Application.Services;

public sealed class ScenarioCaptureService : IAsyncDisposable
{
    private const int MaxEvents = 1000;
    private const int MaxBytes = 2 * 1024 * 1024;
    private readonly IScenarioCaptureStore _store;
    private readonly ILunaCaptureHost _host;
    private readonly ILogger<ScenarioCaptureService> _logger;
    private readonly object _gate = new();
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
    private long? _session;
    private CapturePosition? _lastBodyPosition, _rangeStart;
    private Process? _body;
    private bool _active;
    private bool _finishing;
    private bool _connected;
    private bool _needsProcess;
    private bool _preview, _connecting;
    private CaptureProcess? _process;
    private string _status = "未接続";
    private readonly List<SpeakerPending> _speakerPending = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _writerTask, _resolverTask;
    private sealed record WriteWork(long Session, CaptureReceived? Received, CaptureRules Rules, TaskCompletionSource? Barrier = null);
    private sealed record SpeakerPending(long Session, long Sequence, DateTimeOffset ReceivedAt, CaptureRules Rules);

    public ScenarioCaptureService(IScenarioCaptureStore store, ILunaCaptureHost host, ILogger<ScenarioCaptureService> logger)
    {
        _store = store; _host = host; _logger = logger;
        host.Output += OnOutput;
        host.ConnectionChanged += OnConnection;
        _writerTask = Task.Run(WriteLoopAsync);
        _resolverTask = Task.Run(ResolveLoopAsync);
    }

    public CaptureSnapshot Snapshot
    {
        get
        {
            lock (_gate) return new()
            {
                Profile = _profile,
                SessionId = _session,
                Status = _status,
                Connected = _connected,
                Preview = _preview,
                Connecting = _connecting,
                Process = _process,
                NeedsProcess = _needsProcess,
                Received = _received,
                Saved = _saved,
                Lost = _lost,
                Candidates = _candidates.Values.ToArray(),
                RangeStart = _rangeStart
            };
        }
    }

    public IReadOnlyList<CaptureProcess> GetProcesses() => _host.GetProcesses();

    public async Task<bool> BeginGameAsync(string gameId)
    {
        try
        {
            var profile = await _store.GetOrCreateCaptureProfileAsync(gameId);
            if (!profile.Enabled) return false;
            lock (_gate)
                if (_active || _finishing) { _logger.LogWarning("Capture is already active; second capture skipped. game={Game}", gameId); return false; }
            lock (_gate)
            {
                _profile = profile; _session = null; _active = true; _connected = false; _needsProcess = true;
                _sequence = _received = _saved = _lost = 0; _status = "ゲーム本体を選択してください";
                _lastBodyPosition = null; _rangeStart = null; _preview = true; _process = null;
                _speakerPending.Clear();
                _buffer.Clear(); _candidates.Clear(); _attempted.Clear(); _currentSelections.Clear(); _bufferBytes = 0;
            }
            if (profile.BodyExecutablePath is not null)
            {
                var matches = GetProcesses().Where(x => string.Equals(x.Path, profile.BodyExecutablePath, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length == 1)
                {
                    try { await SelectProcessAsync(matches[0].Id, preview: false); }
                    catch when (Snapshot.NeedsProcess) { return true; }
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Capture startup failed. game={Game}", gameId);
            lock (_gate) _status = "抽出の開始に失敗しました。ログを確認してください。";
            return false;
        }
    }

    public async Task SelectProcessAsync(int id, bool preview = true)
    {
        CaptureProfile profile;
        lock (_gate)
        {
            if (_connecting || _finishing) throw new InvalidOperationException("接続処理の完了を待ってください。");
            profile = _profile ?? throw new InvalidOperationException("ゲームが選択されていません。");
            _connecting = true;
        }
        try
        {
            await FinishAsync();
            profile = (await _store.GetCaptureProfilesAsync()).Single(x => x.Id == profile.Id);
            lock (_gate)
            {
                _active = true; _connected = false; _needsProcess = false; _preview = true; _session = null;
                _sequence = _received = _saved = _lost = 0; _status = "接続中";
                _process = null; _lastBodyPosition = null;
                _buffer.Clear(); _candidates.Clear(); _attempted.Clear(); _currentSelections.Clear(); _bufferBytes = 0;
                _profile = profile;
            }
            var candidate = GetProcesses().SingleOrDefault(x => x.Id == id)
                ?? throw new InvalidOperationException("対象プロセスが見つかりません。");
            var process = Process.GetProcessById(id);
            process.EnableRaisingEvents = true;
            process.Exited += OnBodyExited;
            lock (_gate) { _body = process; _process = candidate; }
            if (process.HasExited) throw new InvalidOperationException("ゲーム本体は終了しています。");
            await _host.ConnectAsync(id);
            lock (_gate)
            {
                if (!_active || _body != process) throw new InvalidOperationException("接続中にゲーム本体が終了しました。");
                _connected = true; _status = "テスト受信中（保存しません）";
            }
            if (!preview && profile.Rules.Selections.Any(x => x.Role == CaptureRole.Body)) await ConfirmProcessAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Capture connection failed. game={Game} pid={Pid}", profile.Id, id);
            await FinishAsync();
            lock (_gate) { _needsProcess = true; _status = "接続失敗・選び直して再試行できます"; }
            throw;
        }
        finally { lock (_gate) _connecting = false; }
    }

    public async Task ConfirmProcessAsync()
    {
        CaptureProfile profile; CaptureProcess target; Process body;
        lock (_gate)
        {
            if (!_active || !_connected || !_preview || _body is null || _process is null || _profile is null)
                throw new InvalidOperationException("テスト接続して受信内容を確認してください。");
            if (!_profile.Rules.Selections.Any(x => x.Role == CaptureRole.Body))
                throw new InvalidOperationException("本文・セリフ・地の文の取得元を選択してください。");
            profile = _profile; target = _process; body = _body;
        }
        var persisted = (await _store.GetCaptureProfilesAsync()).Single(x => x.Id == profile.Id);
        profile = persisted with { Rules = profile.Rules, BodyExecutablePath = target.Path };
        var session = await _store.BeginCaptureSessionAsync(profile.Id);
        try
        {
            await _store.SaveCaptureProfileAsync(profile);
            lock (_gate)
            {
                if (!_active || !_connected || _body != body) throw new InvalidOperationException("確定中に接続が終了しました。");
                _profile = profile; _session = session; _preview = false;
                _sequence = _received = _saved = _lost = 0;
                _lastBodyPosition = new(session, 0, 0); _status = "記録中・受信待ち";
                _buffer.Clear(); _attempted.Clear(); _bufferBytes = 0;
            }
        }
        catch { await _store.EndCaptureSessionAsync(session); throw; }
    }

    private async void OnBodyExited(object? sender, EventArgs args)
    {
        lock (_gate) if (sender != _body) return;
        await FinishAsync();
    }

    private void OnConnection(uint pid, bool connected)
    {
        lock (_gate)
        {
            if (_body?.Id != pid) return;
            _connected = connected;
            _status = connected ? (_preview ? "テスト受信中（保存しません）" : "記録中・受信待ち") : "切断されました";
        }
        if (!connected) _ = FinishAsync();
    }

    private void OnOutput(CaptureSource source, string text)
    {
        lock (_gate)
        {
            if (!_active || _body?.Id != source.ProcessId || _profile is null) return;
            _received++;
            if (_candidates.ContainsKey(source.Key) || _candidates.Count < 256)
                _candidates[source.Key] = new(source, text.Length > 1500 ? text[..1500] : text, CaptureRole.None, null);
            RefreshCandidates(_profile.Rules);
            if (_preview) { _status = "テスト受信中（保存しません）"; return; }
            if (_session is null) return;
            var (role, label) = Selection(source, _profile.Rules);
            var received = new CaptureReceived(++_sequence, DateTimeOffset.UtcNow, source, text, role, label);
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
        if (_pendingBytes + size > MaxBytes || !_writes.Writer.TryWrite(new(session, received, rules))) { Lose("save-queue"); return; }
        _pendingBytes += size;
        if (received.Role == CaptureRole.Body && received.Text.Length > 0)
        {
            var position = new CapturePosition(session, received.Sequence, received.Text.Length);
            if (_lastBodyPosition is null || position.CompareTo(_lastBodyPosition) > 0) _lastBodyPosition = position;
        }
    }

    public void StartRange()
    {
        lock (_gate)
        {
            if (!_active || !_connected || _preview || _lastBodyPosition is null) throw new InvalidOperationException("取得中のゲームで区間を開始してください。");
            if (_rangeStart is not null) throw new InvalidOperationException("指定中の区間を終了または取り消してください。");
            _rangeStart = _lastBodyPosition;
        }
    }

    public CaptureRangeSelection EndRange()
    {
        lock (_gate)
        {
            if (_rangeStart is null || _lastBodyPosition is null || _profile is null) throw new InvalidOperationException("区間の開始を指定してください。");
            if (_rangeStart.CompareTo(_lastBodyPosition) >= 0) throw new InvalidOperationException("開始後の本文がまだ届いていません。");
            var selection = new CaptureRangeSelection(_profile.Id, _rangeStart, _lastBodyPosition);
            _rangeStart = null;
            return selection;
        }
    }

    public void CancelRange() { lock (_gate) _rangeStart = null; }

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

    public async Task SaveRulesAsync(CaptureRules rules, CaptureSource? selectedSource = null)
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
            if (_active && !_preview && _session is { } session)
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
        await foreach (var work in _writes.Reader.ReadAllAsync())
        {
            if (work.Barrier is not null) { work.Barrier.TrySetResult(); continue; }
            var received = work.Received!;
            try
            {
                var saved = await _store.AppendCaptureAsync(work.Session, received, work.Rules);
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
                        _speakerPending.Add(new(work.Session, received.Sequence, received.ReceivedAt, work.Rules));
                    }
                }
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
        foreach (var group in ready.GroupBy(x => (x.Session, x.Rules)))
        {
            try
            {
                await _store.ResolveCaptureSpeakersAsync(group.Key.Session, group.Key.Rules,
                group.Min(x => x.ReceivedAt), now, group.Select(x => x.Sequence).ToArray());
            }
            catch (Exception ex) { _logger.LogError(ex, "Capture speaker matching failed. session={Session}", group.Key.Session); }
        }
    }

    public async Task FinishAsync()
    {
        Process? body; long? session;
        lock (_gate)
        {
            if (!_active) return;
            _active = false; _finishing = true; _connected = false; _needsProcess = true; _preview = false;
            _rangeStart = null;
            body = _body; _body = null; session = _session;
            if (!_status.Contains("失敗")) _status = "取得終了";
            _buffer.Clear(); _bufferBytes = 0;
        }
        try
        {
            if (body is not null)
            {
                body.Exited -= OnBodyExited;
                _host.Detach(body.Id);
                body.Dispose();
            }
            await FlushPendingAsync();
            if (session is { } id)
            {
                await ResolvePendingAsync(DateTimeOffset.UtcNow);
                await _store.EndCaptureSessionAsync(id);
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "Capture finish failed. session={Session}", session); }
        finally { lock (_gate) _finishing = false; }
    }

    public async ValueTask DisposeAsync()
    {
        await FinishAsync();
        _host.Output -= OnOutput; _host.ConnectionChanged -= OnConnection;
        _stop.Cancel(); _writes.Writer.TryComplete();
        await Task.WhenAll(_writerTask, _resolverTask);
        _stop.Dispose();
    }
}
