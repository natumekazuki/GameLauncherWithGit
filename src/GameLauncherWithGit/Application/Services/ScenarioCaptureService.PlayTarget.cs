using GameLauncherWithGit.Application.Models;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace GameLauncherWithGit.Application.Services;

public sealed partial class ScenarioCaptureService
{
    private const string UnknownRecord = "現在の Git ブランチの記録対象を確定できません。";
    private readonly ScenarioRecordTargetResolver? _recordTargets;
    private readonly Task _targetMonitorTask;
    private CaptureProfile? _playProfile;
    private ScenarioRecordTargetResolver.ResolvedRecord? _playRecord;
    private Process? _playProcess;
    private bool _playProcessExited;
    private long _playEpoch;
    private CaptureContinuation? _continuation;

    private sealed record CaptureContinuation(int ProcessId, CaptureRules Rules, bool Paused);

    // The caller holds _gate. The play target can exist before/after extraction, and may have no writable root.
    private long? CurrentPlayRecordId => _playProcessExited ? null
        : _recordTargets is null ? _playProfile?.Id : _playRecord is { IsCurrent: true } record ? record.RecordId : null;

    private long? ActiveRecordId => _recordTargets is null ? _profile?.Id
        : _profile?.Id == _playProfile?.Id ? CurrentPlayRecordId : null;

    internal bool UsesPcPlayTarget => _recordTargets is not null;

    internal Task<long?> ResolveDesktopRecordIdAsync(string gameId) =>
        _recordTargets?.GetCurrentRecordIdAsync(gameId) ?? Task.FromResult<long?>(null);

    internal Task<bool> OwnsDesktopRecordAsync(string gameId, long recordId) =>
        _recordTargets?.OwnsRecordAsync(gameId, recordId) ?? Task.FromResult(false);

    internal Task<bool> OwnsDesktopRecordAsync(long profileId, long recordId) =>
        _recordTargets?.OwnsRecordAsync(profileId, recordId) ?? Task.FromResult(false);

    internal (CaptureProfile? Profile, long? RecordId, long ScopeVersion) CurrentPlayTarget
    {
        get { lock (_gate) return (_playProfile, CurrentPlayRecordId, _playEpoch); }
    }

    /// <summary>Prepares a PC-only extraction setup without publishing that game as the play target.</summary>
    public Task PreparePlayTargetAsync(string gameId) => RunOperationAsync(async () =>
    {
        var profile = await _store.GetOrCreateCaptureProfileAsync(gameId);
        await PreparePlayTargetCoreAsync(profile);
    });

    private async Task PreparePlayTargetCoreAsync(CaptureProfile profile)
    {
        lock (_gate)
        {
            if (_active && _profile?.Id == profile.Id) return;
            if (_active && !_preview) throw new InvalidOperationException("現在の取得を終了してからゲームを変更してください。");
        }
        await FinishCoreAsync();
        lock (_gate)
        {
            _profile = profile;
            _continuation = null;
            _session = null;
            _needsProcess = true;
            _status = "ゲーム本体を選択してください";
            _operationVersion++;
        }
        StateChanged?.Invoke();
    }

    /// <summary>Clears the PC play selection only if it still belongs to the supplied game.</summary>
    public Task ClearPlayTargetAsync(string gameId) => RunOperationAsync(async () =>
    {
        if (CurrentPlayTarget.Profile?.GameId == gameId) await ClearPlayTargetCoreAsync();
    });

    private async Task SelectPlayTargetCoreAsync(CaptureProfile profile, Process? process = null)
    {
        if (CurrentPlayTarget.Profile?.Id == profile.Id && process is null)
        {
            await RefreshPlayTargetCoreAsync();
            return;
        }

        await ClearPlayTargetCoreAsync();
        // A trial connection may exist before any play target has been published.
        await FinishCoreAsync();
        lock (_gate)
        {
            _playProfile = profile;
            _profile = profile;
            _session = null;
            _status = "未接続";
            _playProcess = process;
            _playProcessExited = false;
            _playEpoch++;
            _operationVersion++;
        }
        if (process is not null) WatchPlayProcess(process);
        await RefreshPlayTargetCoreAsync();
        StateChanged?.Invoke();
    }

    private async Task ClearPlayTargetCoreAsync()
    {
        ScenarioRecordTargetResolver.ResolvedRecord? previous;
        Process? previousProcess;
        bool stopCapture;
        lock (_gate)
        {
            if (_playProfile is null) return;
            stopCapture = _profile?.Id == _playProfile.Id;
            _playProfile = null;
            _continuation = null;
            previousProcess = _playProcess;
            _playProcess = null;
            _playProcessExited = false;
            previous = _playRecord;
            _playRecord = null;
            _playEpoch++;
            _operationVersion++;
        }
        if (previousProcess is not null) ReleasePlayProcess(previousProcess);
        previous?.Dispose();
        if (stopCapture)
        {
            await FinishCoreAsync();
            lock (_gate) { _profile = null; _session = null; _needsProcess = false; }
        }
        StateChanged?.Invoke();
    }

    private void ReleasePlayProcess(Process process)
    {
        process.Exited -= OnPlayProcessExited;
        process.Dispose();
    }

    private void WatchPlayProcess(Process process)
    {
        // Keep the play handle independent of the extraction connection. Don't call Process event APIs under _gate.
        process.Exited += OnPlayProcessExited;
        try
        {
            process.EnableRaisingEvents = true;
            if (process.HasExited) OnPlayProcessExited(process, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.LogWarning(ex, "Unable to observe the PC play process; withdrawing the recording target.");
            OnPlayProcessExited(process, EventArgs.Empty);
        }
    }

    private void OnPlayProcessExited(object? sender, EventArgs args)
    {
        lock (_gate)
        {
            if (_playProcess != sender || _playProcessExited) return;
            _playProcessExited = true;
            _operationVersion++;
            _playEpoch++;
        }
        // Return to the Process event before disposing its handle on the operation gate.
        _ = Task.Run(() => RunOperationAsync(async () =>
        {
            lock (_gate) if (_playProcess != sender) return;
            await ClearPlayTargetCoreAsync();
        }));
    }

    private async Task MonitorPlayTargetAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                if (CurrentPlayTarget.Profile is null) continue;
                try { await RunOperationAsync(RefreshPlayTargetCoreAsync); }
                catch (Exception ex) when (!_stop.IsCancellationRequested)
                { _logger.LogWarning(ex, "Unable to refresh the current scenario record target."); }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    internal async Task RefreshPlayTargetCoreAsync()
    {
        if (_recordTargets is null || _playProfile is not { GameId: { } gameId } profile) return;
        if (_playProcessExited || _playProcess?.HasExited == true) { await ClearPlayTargetCoreAsync(); return; }

        ScenarioRecordTargetResolver.RepositorySetting setting;
        try { setting = await _recordTargets.ReadRepositoryPathAsync(gameId, _stop.Token); }
        catch
        {
            await InvalidatePlayRecordCoreAsync();
            throw;
        }
        if (!setting.GameExists) { await ClearPlayTargetCoreAsync(); return; }
        if (_playRecord is { ObservationIsCurrent: true } current && current.Setting == setting) return;

        ScenarioRecordTargetResolver.ResolvedRecord? next = null;
        if (_playRecord is { RecordId: null } unknown && unknown.Setting == setting)
        {
            next = await _recordTargets.ResolveAsync(setting, profile.Id, _stop.Token);
            if (!next.IsCurrent)
            {
                lock (_gate) _playRecord = next;
                unknown.Dispose();
                return;
            }
        }
        try
        {
            await InvalidatePlayRecordCoreAsync();
            next ??= await _recordTargets.ResolveAsync(setting, profile.Id, _stop.Token);
        }
        catch { next?.Dispose(); throw; }
        lock (_gate)
        {
            _playRecord = next;
            if (_profile?.Id == profile.Id)
            {
                if (CurrentPlayRecordId is null) _status = UnknownRecord;
                else if (!_active) _status = "記録を開始できます";
            }
        }
        if (next.IsCurrent && _continuation is { } continuation)
        {
            _continuation = null;
            if (_playProcessExited || _profile?.Id != profile.Id || _active)
            {
                StateChanged?.Invoke();
                return;
            }
            try
            {
                await SelectProcessCoreAsync(continuation.ProcessId, preview: true);
                lock (_gate)
                {
                    _profile = _profile! with { Rules = continuation.Rules };
                    RefreshCandidates(continuation.Rules);
                }
                await ConfirmProcessCoreAsync(persistSetup: false, paused: continuation.Paused);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { _logger.LogWarning(ex, "Unable to resume capture for the new scenario record target."); }
        }
        StateChanged?.Invoke();
    }

    private async Task InvalidatePlayRecordCoreAsync()
    {
        ScenarioRecordTargetResolver.ResolvedRecord? previous;
        CaptureContinuation? continuation;
        bool belongsToPlay;
        lock (_gate)
        {
            previous = _playRecord;
            if (previous is null) return;
            belongsToPlay = _profile?.Id == _playProfile?.Id;
            continuation = belongsToPlay && _active && _connected && !_preview && _session is not null && _profile is not null && _process is { } process
                ? new(process.Id, _profile.Rules, _paused) : null;
            _playRecord = null;
            _playEpoch++;
            _operationVersion++;
        }
        previous.Dispose();
        StateChanged?.Invoke();
        if (!belongsToPlay) return;
        if (continuation is not null)
        {
            if (await FinishCoreAsync(requireDrain: true)) _continuation = continuation;
        }
        else await FinishCoreAsync();
        lock (_gate) { _session = null; _lastBodyPosition = null; _rangeStart = null; }
    }
}
