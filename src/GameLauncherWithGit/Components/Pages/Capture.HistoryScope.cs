using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Application.Services;
using Microsoft.AspNetCore.Components;

namespace GameLauncherWithGit.Components.Pages;

public partial class Capture
{
    [Inject] private ScenarioRecordTargetResolver HistoryResolver { get; set; } = default!;

    // Identity of a particular display, not just its record ID: A → B → A must not revive A's old callbacks.
    private readonly record struct PlayBoundary(long Version, long? SessionId);
    private readonly record struct CurrentScope(long? RecordId, PlayBoundary? Play,
        ScenarioRecordTargetResolver.DesktopHistoryObservation? History);

    private sealed class DisplayedScope(long recordId, string viewKey, CurrentScope boundary)
    {
        public long RecordId { get; } = recordId;
        public string ViewKey { get; } = viewKey;
        public CurrentScope Boundary { get; } = boundary;
    }

    private DisplayedScope? _displayedScope;
    private ScenarioRecordTargetResolver.DesktopHistoryObservation? _historyObservation;
    private long _viewSequence;
    private long _recordId => _displayedScope?.RecordId ?? 0;
    private long _historyChoice;
    private long? _currentRecordId;
    private IReadOnlyList<ScenarioHistoryScope> _historyScopes = [];
    private string? _scopeLabel, _scopeError;
    private bool ShowHistoryScopes => _profile is not null &&
        (_historyScopes.Any(scope => scope.BranchRef is not null) || _profile.GameId is not null && _currentRecordId != _profileId);
    private string ScopeTitle => string.Join(" · ", new[] { _profile?.Title, _scopeLabel }.OfType<string>());
    private string CurrentScopeLabel => _historyScopes.FirstOrDefault(scope => scope.RecordId == _currentRecordId) is { BranchRef: not null } scope
        ? $"現在のブランチ（{HistoryScopeLabel(scope)}）"
        : _currentRecordId == _profileId ? "通常の記録" : "現在のブランチ";

    private string HistoryScopeLabel(ScenarioHistoryScope scope)
    {
        const string localRef = "refs/heads/";
        if (scope.BranchRef is not { } branch) return "従来の記録";
        var name = branch.StartsWith(localRef, StringComparison.Ordinal) ? branch[localRef.Length..] : branch;
        if (_historyScopes.Count(item => item.BranchRef == branch) == 1) return name;
        return $"{name} ({scope.RepositoryKey})";
    }

    private bool IsDisplayed(DisplayedScope? expected) => expected is not null && ReferenceEquals(expected, _displayedScope)
        && !_stop.IsCancellationRequested;

    private bool TracksCurrentScope => _historyChoice == 0;

    private PlayRecordingTarget HistoryTarget(CaptureSnapshot snapshot) => _profile?.GameId is { } gameId
        ? PlayRecordingOperations.DesktopHistoryTarget(snapshot, gameId, _recordId, fixedRecord: !TracksCurrentScope,
            observation: _displayedScope?.Boundary.History)
        : PlayRecordingOperations.DesktopHistoryTarget(snapshot, _profileId, _recordId);

    private Task AtScopeAsync(DisplayedScope? expected, Func<Task> action) => IsDisplayed(expected) ? action() : Task.CompletedTask;
    private void AtScope(DisplayedScope? expected, Action action) { if (IsDisplayed(expected)) action(); }

    private async Task RequireCurrentScopeAsync(DisplayedScope? expected)
    {
        if (!IsDisplayed(expected)) throw ScopeChanged();
        if (TracksCurrentScope && (!await IsCurrentScopeAsync(expected!) || !IsDisplayed(expected))) throw ScopeChanged();
    }

    private async Task<bool> CanPublishAsync(DisplayedScope? expected)
    {
        if (!IsDisplayed(expected)) return false;
        if (!TracksCurrentScope) return true;
        var current = await IsCurrentScopeAsync(expected!);
        if (!IsDisplayed(expected)) return false;
        if (current) return true;
        ResetScopeContent();
        _displayedScope = null;
        _scopeLabel = null;
        return false;
    }

    private static InvalidOperationException ScopeChanged() =>
        new("記録範囲が変わりました。表示を確認してから操作してください。");

    private CurrentScope FromPlayTarget(long? recordId, long scopeVersion)
    {
        var snapshot = CaptureService.Snapshot;
        return new(recordId, new(scopeVersion, snapshot.RecordId == recordId ? snapshot.SessionId : null), null);
    }

    private async Task<bool> IsCurrentScopeAsync(DisplayedScope expected)
    {
        var play = CaptureService.CurrentPlayTarget;
        if (_profile is not { } profile) return false;
        if (play.Profile?.Id == profile.Id) return FromPlayTarget(play.RecordId, play.ScopeVersion) == expected.Boundary;
        return profile.GameId is { } gameId && expected.Boundary.History is { } observation
            && ReferenceEquals(observation, _historyObservation)
            && await observation.IsCurrentAsync(gameId, expected.RecordId, _stop.Token)
            && CaptureService.CurrentPlayTarget.Profile?.Id != profile.Id;
    }

    private async Task<CurrentScope> ReadCurrentScopeAsync()
    {
        if (_stop.IsCancellationRequested || _profile is not { } profile) return default;
        var play = CaptureService.CurrentPlayTarget;
        if (play.Profile?.Id == profile.Id || profile.GameId is null)
        {
            ReleaseHistoryObservation();
            if (play.Profile?.Id == profile.Id)
                return FromPlayTarget(play.RecordId, play.ScopeVersion);
            return new(profile.Id, null, null);
        }

        var token = _stop.Token;
        var observed = _historyObservation;
        if (observed is { RecordId: { } recordId } && observed.ProfileId == profile.Id
            && await observed.IsCurrentAsync(profile.GameId, recordId, token)
            && ReferenceEquals(observed, _historyObservation))
            return new(recordId, null, observed);

        var next = await HistoryResolver.ObserveDesktopHistoryAsync(profile.GameId, profile.Id, token);
        // A nested editor may load while polling, or the page can close while Git is resolving HEAD.
        if (token.IsCancellationRequested || _profile?.Id != profile.Id
            || CaptureService.CurrentPlayTarget.Profile?.Id == profile.Id || !ReferenceEquals(observed, _historyObservation))
        {
            next.Dispose();
            return default;
        }
        _historyObservation = next;
        observed?.Dispose();
        return new(next.RecordId, null, next);
    }

    private void ReleaseHistoryObservation()
    {
        var previous = _historyObservation;
        _historyObservation = null;
        previous?.Dispose();
    }

    private Task WithHistoryWriteAsync(DisplayedScope? expected, PlayRecordingTarget target, Func<long, Task> write)
    {
        if (!IsDisplayed(expected) || target.HistoryRoot != expected!.RecordId) throw ScopeChanged();
        return RecordingOperations.RunDesktopHistoryAsync(target, recordId =>
        {
            if (!IsDisplayed(expected)) throw ScopeChanged();
            return write(recordId);
        });
    }

    private Task WriteHistoryAsync(PlayRecordingTarget target, Func<long, Task> write) =>
        WithHistoryWriteAsync(_displayedScope, target, write);

    private Task ChangeHistoryScopeAsync(ChangeEventArgs e) => RunAsync(async () =>
    {
        if (!long.TryParse(e.Value?.ToString(), out var choice) ||
            (choice == 0 ? _profile?.GameId is null : !_historyScopes.Any(scope => scope.RecordId == choice))) return;
        _historyChoice = choice;
        await RefreshScopeAsync(reload: true);
    }, refreshScope: false);

    private Task RetryScopeAsync() => RunAsync(() => RefreshScopeAsync(reload: true), refreshScope: false);

    private async Task RefreshScopeAsync(bool reload = false)
    {
        CurrentScope currentScope = default;
        string? error = null;
        try { currentScope = await ReadCurrentScopeAsync(); }
        catch (Exception ex) when (!_stop.IsCancellationRequested) { error = ex.Message; }
        var current = currentScope.RecordId;

        if (reload || current != _currentRecordId || _historyScopes.Count == 0 && _profile is not null)
        {
            try { _historyScopes = _profile is null ? [] : await HistoryStore.GetScenarioHistoryScopesAsync(_profileId); }
            catch (Exception ex) when (!_stop.IsCancellationRequested)
            {
                // Never retain another profile's choices or guess a root when scope lookup fails.
                _historyScopes = [];
                error = ex.Message;
            }
        }
        _currentRecordId = current;
        var chosen = TracksCurrentScope ? current : _historyChoice;
        var scope = _historyScopes.FirstOrDefault(item => item.RecordId == chosen);
        _scopeError = _profile is null || scope is not null ? null
            : error ?? "現在のブランチを特定できません。ブランチを確認して再試行してください。";
        _scopeLabel = scope?.BranchRef is not null ? HistoryScopeLabel(scope)
            : scope is not null && ShowHistoryScopes && _currentRecordId != _profileId ? "従来の記録" : null;
        var boundary = TracksCurrentScope ? currentScope : default;
        if (!reload && (scope?.RecordId ?? 0) == _recordId && (scope is null || _displayedScope?.Boundary == boundary)) return;

        if (scope is not null && _recordId == scope.RecordId)
        {
            // Retire callbacks for the old session without losing the reader's filters or scroll position.
            _displayedScope = new(scope.RecordId, (++_viewSequence).ToString(System.Globalization.CultureInfo.InvariantCulture), boundary);
            _rangeListsVersion = -1;
            return;
        }

        ResetScopeContent();
        _displayedScope = scope is null ? null : new(scope.RecordId,
            (++_viewSequence).ToString(System.Globalization.CultureInfo.InvariantCulture), boundary);
        if (_displayedScope is null) return;
        await LoadScopeListsAsync();
        await LoadItemsAsync();
        _scrollToEnd = true;
    }

    private void ResetScopeContent()
    {
        var hadView = _displayedScope is not null;
        var editorWasOpen = EditorOpen;
        CloseEditors();
        ClearRangeDraft();
        _restoreFocus |= editorWasOpen;
        _focusFeed |= hadView && !editorWasOpen;
        _focusEditor = _focusExportSave = _focusRangeFilter = _focusHistoryContent = false;
        _flowOpen = false;
        _flowGraph = null;
        _flowHighlighted = null;
        _pendingLiveRange = null;
        _rangeDraftTarget = _settingsTarget = null;
        _rangeListsVersion = -1;
        _gameTags = [];
        _gameSpeakers = [];
        _ranges = [];
        _items = [];
        _itemsError = _screenshotCountsError = null;
        _screenshotCounts = null;
        _hasMore = false;
        _rangeId = 0;
        ClearTextRange();
        _offset = 0;
        _selected.Clear();
        _filter = "all";
        _textSearchInput = _textSearch = _speakerSearchInput = _speakerSearch = "";
        _exportIncludeTags = _exportExcludeTags = [];
        _following = true;
        _pageCeiling = null;
    }

    private async Task LoadScopeListsAsync()
    {
        var expected = _displayedScope;
        if (expected is null) return;
        var ranges = await Store.GetCaptureRangesAsync(expected.RecordId);
        var tags = (await SceneStore.GetScenarioSceneFlowAsync(expected.RecordId)).Tags;
        var speakers = await Store.GetGameCaptureSpeakersAsync(expected.RecordId);
        if (!await CanPublishAsync(expected)) return;
        (_ranges, _gameTags, _gameSpeakers) = (ranges, tags, speakers);
    }
}
