using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Application.Services;
using Microsoft.AspNetCore.Components;

namespace GameLauncherWithGit.Components.Pages;

public partial class Capture
{
    [Inject] private ScenarioRecordTargetResolver HistoryResolver { get; set; } = default!;

    // Identity of a particular display, not just its record ID: A → B → A must not revive A's old callbacks.
    private readonly record struct PlayBoundary(long Version, long? SessionId);

    private sealed class DisplayedScope(long recordId, string viewKey, PlayBoundary? playBoundary)
    {
        public long RecordId { get; } = recordId;
        public string ViewKey { get; } = viewKey;
        public PlayBoundary? Boundary { get; } = playBoundary;
    }

    private DisplayedScope? _displayedScope;
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

    private bool TracksCurrentScope => _live || _historyChoice == 0;
    private PlayBoundary? CurrentPlayBoundary
    {
        get
        {
            var play = CaptureService.CurrentPlayTarget;
            if (!TracksCurrentScope || play.Profile?.Id != _profileId) return null;
            var snapshot = CaptureService.Snapshot;
            return new(play.ScopeVersion, snapshot.RecordId == play.RecordId ? snapshot.SessionId : null);
        }
    }

    private PlayRecordingTarget HistoryTarget(CaptureSnapshot snapshot) => _profile?.GameId is { } gameId
        ? PlayRecordingOperations.DesktopHistoryTarget(snapshot, gameId, _recordId, fixedRecord: !TracksCurrentScope)
        : PlayRecordingOperations.DesktopHistoryTarget(snapshot, _profileId, _recordId);

    private Task AtScopeAsync(DisplayedScope? expected, Func<Task> action) => IsDisplayed(expected) ? action() : Task.CompletedTask;
    private void AtScope(DisplayedScope? expected, Action action) { if (IsDisplayed(expected)) action(); }

    private async Task RequireCurrentScopeAsync(DisplayedScope? expected)
    {
        if (!IsDisplayed(expected)) throw ScopeChanged();
        if (TracksCurrentScope)
        {
            var id = await ResolveCurrentRecordAsync();
            if (!IsDisplayed(expected) || id != expected!.RecordId || expected.Boundary != CurrentPlayBoundary)
                throw ScopeChanged();
        }
    }

    private async Task<bool> CanPublishAsync(DisplayedScope? expected)
    {
        if (!IsDisplayed(expected)) return false;
        if (!TracksCurrentScope) return true;
        var id = await ResolveCurrentRecordAsync();
        if (!IsDisplayed(expected)) return false;
        if (id == expected!.RecordId && expected.Boundary == CurrentPlayBoundary) return true;
        ResetScopeContent();
        _displayedScope = null;
        _scopeLabel = null;
        return false;
    }

    private static InvalidOperationException ScopeChanged() =>
        new("記録範囲が変わりました。表示を確認してから操作してください。");

    private Task<long?> ResolveCurrentRecordAsync()
    {
        if (_profile is not { } profile) return Task.FromResult<long?>(null);
        var play = CaptureService.CurrentPlayTarget;
        if (play.Profile?.Id == profile.Id) return Task.FromResult(play.RecordId);
        if (_live) return Task.FromResult<long?>(null);
        if (profile.GameId is not { } gameId) return Task.FromResult<long?>(profile.Id);
        return HistoryResolver.GetCurrentRecordIdAsync(gameId);
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
        long? current = null;
        string? error = null;
        try { current = await ResolveCurrentRecordAsync(); }
        catch (Exception ex) when (!_stop.IsCancellationRequested) { error = ex.Message; }

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
        var chosen = _live || _historyChoice == 0 ? current : _historyChoice;
        var scope = _historyScopes.FirstOrDefault(item => item.RecordId == chosen);
        _scopeError = _profile is null || scope is not null ? null
            : error ?? "現在のブランチを特定できません。ブランチを確認して再試行してください。";
        _scopeLabel = scope?.BranchRef is not null ? HistoryScopeLabel(scope)
            : scope is not null && ShowHistoryScopes && _currentRecordId != _profileId ? "従来の記録" : null;
        var boundary = CurrentPlayBoundary;
        if (!reload && (scope?.RecordId ?? 0) == _recordId && (scope is null || _displayedScope?.Boundary == boundary)) return;

        ResetScopeContent();
        _displayedScope = scope is null ? null : new(scope.RecordId,
            (++_viewSequence).ToString(System.Globalization.CultureInfo.InvariantCulture), boundary);
        if (_displayedScope is null) return;
        await LoadScopeListsAsync();
        await LoadItemsAsync();
        _scrollToEnd = LatestOrder;
    }

    private void ResetScopeContent()
    {
        var hadView = _displayedScope is not null;
        var editorWasOpen = EditorOpen;
        CloseEditors();
        _restoreFocus |= editorWasOpen;
        _focusFeed |= hadView && !editorWasOpen;
        _focusEditor = _focusExportSave = _focusRangeFilter = _focusHistoryContent = false;
        _sceneEditorOpen = false;
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
        _following = _live;
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
