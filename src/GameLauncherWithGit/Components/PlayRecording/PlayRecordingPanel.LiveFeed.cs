using GameLauncherWithGit.Application.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace GameLauncherWithGit.Components.PlayRecording;

public partial class PlayRecordingPanel
{
    private IJSObjectReference? _liveFeed;
    private DotNetObjectReference<PlayRecordingPanel>? _feedOwner;
    private ElementReference _observedFeed;
    private long _feedScope, _appliedFeedRevision, _appliedSpeakerRevision, _appliedScreenshotCount;
    private string? _observedScope;
    private string FeedScope => _feedScope.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private (string Scope, long Ticket)? _pendingTail;
    private bool _atFeedEnd;
    private int _wakeQueued;

    private bool CanFollowText => !_disposed && !_statusError && !_textError && _pane == Pane.Text && _latest
        && _textItems is not null && !HasTextRange && _olderPages.Count == 0 && _pausedBefore is null
        && _textSearch is null && _speakerSearch is null && !_pickingRange && _editItem is null && _galleryItem is null
        && !_savedRangesOpen && _rangeDraft?.Open != true && _finishConfirmation is null;

    /// <summary>Coalesces wakeups without doing browser or history I/O on the capture writer.</summary>
    private void OnLiveFeedCommitted(ScenarioLiveFeedVersion _) => QueueFeedWake();

    private void QueueFeedWake()
    {
        if (_disposed || Interlocked.Exchange(ref _wakeQueued, 1) != 0) return;
        _ = WakeFeedAsync();
    }

    private async Task WakeFeedAsync()
    {
        try
        {
            await InvokeAsync(async () =>
            {
                // Yield even when a scroll callback already runs on the renderer context.
                await Task.Yield();
                if (_pollRead is { } read) await read;
                Interlocked.Exchange(ref _wakeQueued, 0);
                await RefreshInBackgroundAsync(checkStatus: false);
            });
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _wakeQueued, 0);
            if (!_disposed) Logger.LogWarning("Play recording feed wakeup failed ({ExceptionType}).", ex.GetType().Name);
        }
    }

    [JSInvokable]
    public void FeedFollowing(string scope, bool atEnd)
    {
        if (_disposed || scope != FeedScope) return;
        _atFeedEnd = atEnd;
        if (atEnd) QueueFeedWake();
        StateHasChanged();
    }

    /// <summary>Registers the rendered viewport, then restores or scrolls only that current viewport.</summary>
    private async Task RenderFeedAsync()
    {
        if (_liveFeed is null || _textScroller.Id is null) return;
        var scope = FeedScope;
        var element = _textScroller;
        if (_observedFeed.Id != element.Id || _observedScope != scope)
        {
            if (_observedFeed.Id is not null) await _liveFeed.InvokeVoidAsync("unobserve", _observedFeed);
            _observedFeed = element;
            _observedScope = scope;
            await _liveFeed.InvokeVoidAsync("observe", element, _feedOwner, scope);
        }
        if (_disposed || scope != FeedScope || _pane != Pane.Text || _galleryItem is not null || _statusError) return;
        if (_restoreReadingPosition is { } top)
        {
            _restoreReadingPosition = null;
            await _liveFeed.InvokeVoidAsync("restore", element, top);
        }
        else if (_scrollToLatest)
        {
            _scrollToLatest = false;
            await _liveFeed.InvokeVoidAsync("scroll", element, true);
        }
        else if (_pendingTail is { } pending)
        {
            _pendingTail = null;
            if (pending.Scope == scope)
                await _liveFeed.InvokeVoidAsync("apply", element, scope, pending.Ticket, CanFollowText);
        }
    }

    /// <summary>Reads at most one latest page for a still-unapplied, matching committed version.</summary>
    private async Task RefreshLatestTextAsync()
    {
        if (!CanFollowText || _busy || _pendingTail is not null || _liveFeed is null || _state is not { } state) return;
        var body = PendingFeed(Operations.LiveFeedVersion, _appliedFeedRevision, state.Target);
        var speakers = PendingFeed(Operations.LiveFeedSpeakerVersion, _appliedSpeakerRevision, state.Target);
        if ((body ?? speakers) is not { } version) return;

        var target = ForDisplay(state);
        var scope = FeedScope;
        var element = _textScroller;
        if (!_atFeedEnd || !await _liveFeed.InvokeAsync<bool>("atEnd", element, scope)
            || !IsDisplayedTargetCurrent(target) || !CanFollowText || _busy) return;

        PlayRecordingPage? page = null;
        await ReadAsync(target, "live text", gameId => Operations.GetLiveItemsAsync(gameId, target.Recording, version, TextPageSize),
            value => page = value, () => { if (IsDisplayedTargetCurrent(target)) _textError = true; });
        if (page is null || !IsDisplayedTargetCurrent(target) || !CanFollowText || _busy) return;
        if (body is null)
        {
            // Keep the page membership; a reader who moved away also keeps the old viewport.
            var current = _textItems!;
            var resolved = page.Items.ToDictionary(row => row.Id, row => row.Speaker);
            var updated = current.Select(row => resolved.TryGetValue(row.Id, out var speaker) && row.Speaker != speaker
                ? row with { Speaker = speaker } : row).ToArray();
            if (!current.SequenceEqual(updated))
            {
                var speakerTicket = await _liveFeed.InvokeAsync<long?>("arm", element, scope);
                if (speakerTicket is null || !IsDisplayedTargetCurrent(target) || !CanFollowText || _busy) return;
                _textItems = updated;
                _pendingTail = (scope, speakerTicket.Value);
            }
            _appliedSpeakerRevision = version.Revision;
            return;
        }
        // Check the old DOM again after I/O; a reader who moved away still owns the old page.
        var ticket = await _liveFeed.InvokeAsync<long?>("arm", element, scope);
        if (ticket is null || !IsDisplayedTargetCurrent(target) || !CanFollowText || _busy) return;
        _appliedFeedRevision = version.Revision;
        if (speakers is not null) _appliedSpeakerRevision = speakers.Revision;
        if (!ApplyTextPage(page)) return;
        _pendingTail = (scope, ticket.Value);
        StateHasChanged();
        await LoadImageCountsAsync();
    }

    private static ScenarioLiveFeedVersion? PendingFeed(ScenarioLiveFeedVersion? version, long applied, PlayRecordingTarget target) =>
        version is not null && version.Revision > applied && version.Matches(target.HistoryRoot, target.SessionId, target.ScopeVersion)
            ? version : null;
}
