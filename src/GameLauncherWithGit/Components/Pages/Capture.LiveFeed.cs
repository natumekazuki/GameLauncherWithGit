using GameLauncherWithGit.Application.Services;
using Microsoft.JSInterop;

namespace GameLauncherWithGit.Components.Pages;

public partial class Capture
{
    private IJSObjectReference? _liveFeedModule;
    private string? _observedFeedScope;
    private bool _readingLiveFeed;
    private FeedRevisions _renderedFeedRevisions;
    private PendingLiveRender? _liveFeedRender;
    private bool _applyingLiveFeed;

    /// <summary>Retains the version and scroll ticket for one rendered page.</summary>
    private sealed record PendingLiveRender(DisplayedScope Scope, ScenarioLiveFeedVersion Version, FeedRevisions Revisions, long Ticket);

    /// <summary>Keeps body commits and delayed speaker writes independently consumable.</summary>
    private readonly record struct FeedRevisions(long Body, long Speakers)
    {
        public bool Advances(FeedRevisions previous) => Body > previous.Body || Speakers > previous.Speakers;
    }

    private bool CanFollowLiveFeed => FollowingView && ViewingLiveRecord && !_busy && !_loading && !_stop.IsCancellationRequested &&
        RequestedGameId == _loadedGameId && _scrollToEnd is null && !EditorOpen && _detailKey is null && _edit is null && _selected.Count == 0 &&
        !HasHistorySearch && !HistorySearchPending && _filter == "all" && _rangeId == 0 && _rangeSelection is null &&
        _pendingLiveRange is null && _openRowMenuViewKey != _displayedScope?.ViewKey;

    /// <summary>Schedules a reader turn without waiting for the UI on the capture writer thread.</summary>
    private void OnLiveFeedCommitted(ScenarioLiveFeedVersion version) => _ = DispatchLiveFeedReadAsync();

    private async Task DispatchLiveFeedReadAsync()
    {
        if (_stop.IsCancellationRequested) return;
        try { await InvokeAsync(TryReadLiveFeedAsync); }
        catch (ObjectDisposedException) { /* A queued event can outlive the component. */ }
        catch (InvalidOperationException) when (_stop.IsCancellationRequested) { }
    }

    /// <summary>Wakes a deferred version when shared viewport tracking reports a return to the tail.</summary>
    [JSInvokable]
    public Task FeedFollowing(string viewKey, bool atEnd)
    {
        if (_displayedScope?.ViewKey != viewKey || !FollowingView || EditorOpen || _detailKey is not null || _selected.Count > 0)
            return Task.CompletedTask;
        if (_following != atEnd) { _following = atEnd; StateHasChanged(); }
        return atEnd ? TryReadLiveFeedAsync() : Task.CompletedTask;
    }

    private bool CurrentLiveVersion(DisplayedScope scope, ScenarioLiveFeedVersion version)
    {
        var current = CaptureService.Snapshot;
        return IsDisplayed(scope) && version.RecordId == scope.RecordId &&
            version.Matches(current.RecordId, current.SessionId, current.PlayScopeVersion);
    }

    /// <summary>Reads the two version counters only for the record and session represented by this view.</summary>
    private (ScenarioLiveFeedVersion? Identity, FeedRevisions Revisions) ReadFeedCheckpoint(DisplayedScope? scope)
    {
        if (scope is null) return default;
        var body = CaptureService.LiveFeedVersion;
        var speakers = CaptureService.LiveFeedSpeakerVersion;
        if (body is not null && !CurrentLiveVersion(scope, body)) body = null;
        if (speakers is not null && !CurrentLiveVersion(scope, speakers)) speakers = null;
        return (body ?? speakers, new(body?.Revision ?? 0, speakers?.Revision ?? 0));
    }

    private void ConsumeFeedRevisions(FeedRevisions revisions) => _renderedFeedRevisions =
        new(Math.Max(_renderedFeedRevisions.Body, revisions.Body), Math.Max(_renderedFeedRevisions.Speakers, revisions.Speakers));

    /// <summary>Reads only the pending latest page at the tail of the same unprotected viewport.</summary>
    private async Task TryReadLiveFeedAsync()
    {
        if (_readingLiveFeed || _liveFeedRender is not null || _pollComplete is not null || !CanFollowLiveFeed ||
            _liveFeedModule is not { } module || _displayedScope is not { } scope || _observedFeedScope != scope.ViewKey) return;
        var (identity, revisions) = ReadFeedCheckpoint(scope);
        if (identity is not { } version || !revisions.Advances(_renderedFeedRevisions)) return;

        var feed = _feed;
        bool CanReplace() => CanFollowLiveFeed && CurrentLiveVersion(scope, version) && _observedFeedScope == scope.ViewKey;
        _readingLiveFeed = true;
        try
        {
            if (!await module.InvokeAsync<bool>("atEnd", feed, scope.ViewKey) || !CanReplace()) return;

            var page = await QueryItemsAsync(201, 0, null);
            if (!CanReplace() || !await IsCurrentScopeAsync(scope)) return;
            var visible = page.Count > 200 ? page.Skip(1).ToArray() : page;
            var counts = await ReadScreenshotCountsAsync(scope.RecordId, visible);
            if (!CanReplace()) return;

            // Arm against the old DOM immediately before publishing; do not wait for I/O again before rendering.
            var ticket = await module.InvokeAsync<long?>("arm", feed, scope.ViewKey);
            if (ticket is null) return;
            if (!CanReplace())
            {
                await module.InvokeVoidAsync("apply", feed, scope.ViewKey, ticket.Value, false);
                return;
            }
            _following = true;
            PublishItemPage(page.Count > 200, visible);
            ApplyScreenshotCounts(counts);
            _liveFeedRender = new(scope, version, revisions, ticket.Value);
            StateHasChanged();
        }
        catch (JSDisconnectedException) { /* A retired viewport cannot receive a page or a scroll ticket. */ }
        catch (Exception) when (_stop.IsCancellationRequested) { /* This component no longer owns a display. */ }
        catch (Exception ex) when (!_stop.IsCancellationRequested)
        {
            if (CanReplace()) { _itemsError = ex.Message; StateHasChanged(); }
        }
        finally { _readingLiveFeed = false; }
    }

    /// <summary>Applies the scroll ticket to the rendered page and consumes only its starting version.</summary>
    private async Task ApplyLiveFeedRenderAsync(PendingLiveRender? render)
    {
        if (render is null || !ReferenceEquals(render, _liveFeedRender) || _applyingLiveFeed || _liveFeedModule is not { } module) return;
        _applyingLiveFeed = true;
        try
        {
            // Shared tracking also checks user input received while Blazor painted the new rows.
            var follow = CanFollowLiveFeed && CurrentLiveVersion(render.Scope, render.Version) && _observedFeedScope == render.Scope.ViewKey;
            await module.InvokeVoidAsync("apply", _feed, render.Scope.ViewKey, render.Ticket, follow);
            if (follow && CurrentLiveVersion(render.Scope, render.Version))
                ConsumeFeedRevisions(render.Revisions);
        }
        finally { _liveFeedRender = null; _applyingLiveFeed = false; }

        // A commit during the query is not consumed by applying an older page.
        if (ReadFeedCheckpoint(_displayedScope).Revisions.Advances(_renderedFeedRevisions)) _ = DispatchLiveFeedReadAsync();
    }
}
