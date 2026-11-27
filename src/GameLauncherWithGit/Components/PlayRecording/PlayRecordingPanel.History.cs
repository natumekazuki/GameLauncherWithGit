using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Application.Services;
using Microsoft.JSInterop;

namespace GameLauncherWithGit.Components.PlayRecording;

public partial class PlayRecordingPanel
{
    /// <summary>Replaces the tail only while the reader remains at its end.</summary>
    private async Task RefreshLatestTextAsync()
    {
        if (_textError) return;
        try
        {
            if (_textScroller.Id is not null && !await Js.InvokeAsync<bool>("playRecordingText.atEnd", _textScroller))
            {
                StopFollowing();
                return;
            }
        }
        catch (JSDisconnectedException) { return; }
        await LoadTextAsync(checkScrollBeforeApply: true);
    }

    private Task LoadTextAsync() => LoadTextAsync(checkScrollBeforeApply: false);

    /// <summary>Loads one target-checked page and keeps its held cursor when live follow is paused.</summary>
    private async Task LoadTextAsync(bool checkScrollBeforeApply)
    {
        if (_state is not { } state) return;
        var target = ForDisplay(state);
        var before = _olderPages.Count == 0 ? _pausedBefore : _olderPages[^1];
        var rangeId = _textRangeId;
        PlayRecordingPage? page = null;
        await ReadAsync(target, "texts", gameId => Operations.GetItemsAsync(gameId, target.Recording, TextPageSize, before: before,
            rangeId: rangeId, textSearch: _textSearch, speakerSearch: _speakerSearch), value => page = value, () => _textError = true,
            _textItems is null ? loading => _textLoading = loading : null);
        if (page is null || !IsWorkspaceCurrent(target)) return;
        // Reading can resume during the server fetch; do not overwrite a page that is no longer at the tail.
        if (checkScrollBeforeApply)
        {
            try
            {
                if (!_latest || _textScroller.Id is not null && !await Js.InvokeAsync<bool>("playRecordingText.atEnd", _textScroller))
                {
                    if (IsWorkspaceCurrent(target)) StopFollowing();
                    return;
                }
            }
            catch (JSDisconnectedException) { return; }
        }
        if (!IsWorkspaceCurrent(target)) return;
        ApplyTextPage(page);
        await LoadImageCountsAsync();
    }

    /// <summary>Loads only metadata for the visible page, keeping confirmed zero separate from failure.</summary>
    private async Task LoadImageCountsAsync()
    {
        if (_state is not { } state || _textItems is not { Count: > 0 } rows) return;
        var target = ForDisplay(state);
        var events = rows.Select(x => new CaptureEventKey(x.SessionId, x.Sequence)).Distinct().ToArray();
        await ReadAsync(target, "image counts", gameId => Operations.GetScreenshotCountsAsync(gameId, target.Recording, events),
            counts =>
            {
                if (!ReferenceEquals(rows, _textItems)) return;
                if (_imageCounts is null || _imageCounts.Count != counts.Count ||
                    counts.Any(x => !_imageCounts.TryGetValue(x.Key, out var count) || count != x.Value))
                    _imageCounts = counts;
                _imageCountsFailed = false;
            },
            () =>
            {
                if (!ReferenceEquals(rows, _textItems)) return;
                _imageCounts = null;
                _imageCountsFailed = true;
            });
    }

    private Task EditTextAsync(DisplayTarget target, CaptureTextItem item) => ReadForDisplayedTargetAsync(target, async () =>
    {
        await ReadAsync(target, "labels", gameId => Operations.GetLabelsAsync(gameId, target.Recording), labels =>
        {
            _detailRangeEdit = null;
            _labels = labels.ToArray();
            BeginTextEdit(item);
        }, () => _actionError = new("分類の取得失敗", "分類を読み込めませんでした。本文を更新してください。"));
    });

    private bool TextLabelChanged(CaptureTextItem? item) => item is not null && (item.LabelId ?? 0) != _editLabel;
    // The read model hides the speaker of narration, even when a manual value is stored.
    private bool TextSpeakerChanged(CaptureTextItem? item) => item is not null && !EditingNarration
        && _editSpeaker.Trim() != (item.Speaker ?? "");

    private Task SaveTextAsync(DisplayTarget target, CaptureTextItem? item) => RunMutationAsync(target, async () =>
    {
        if (item is null || !ReferenceEquals(item, _editItem)) return;
        await UseActionAsync(target, gameId => Operations.CorrectTextAsync(gameId, target.Recording, item,
            TextLabelChanged(item), _editLabel == 0 ? null : _editLabel,
            TextSpeakerChanged(item), EditingNarration ? null : Optional(_editSpeaker)));
        CancelTextEdit();
        await LoadTextAsync();
        if (_state is not { } state || !IsWorkspaceCurrent(target)) return;
        var current = ForDisplay(state);
        await ReadAsync(current, "selected corrected text", gameId => Operations.GetItemAtAsync(gameId, current.Recording, item.StartPosition),
            value => { _galleryItem = value; _detailSuccess = "本文を訂正しました"; },
            () => _actionError = new("本文の再読込失敗", "訂正は保存しました。本文を開き直してください。"));
    }, "訂正失敗");

    private static string? Optional(string input) => string.IsNullOrWhiteSpace(input) ? null : input.Trim();

    private Task OpenOccurrenceAsync(DisplayTarget target, ScenarioFlowOccurrence occurrence) =>
        ReadForDisplayedTargetAsync(target, () => OpenResolvedTextAsync(target, occurrence.Start));

    private Task OpenCapturedEventAsync(DisplayTarget target, PlayRecordingScreenshotResult shot) =>
        ReadForDisplayedTargetAsync(target, () => OpenResolvedTextAsync(target, new(shot.Event.SessionId, shot.Event.Sequence, 0), shot.ImageId));

    private async Task OpenResolvedTextAsync(DisplayTarget target, CapturePosition position, string? imageId = null)
    {
        CaptureTextItem? item = null;
        await ReadAsync(target, "text at position", gameId => Operations.GetItemAtAsync(gameId, target.Recording, position),
            value => item = value, () => _actionError = new("本文の取得失敗", "対応する本文を読み込めませんでした。記録を更新してください。"));
        if (item is null) return;
        if (_pane == Pane.Text && _galleryItem is null) await RememberReadingPositionAsync(target);
        if (!IsWorkspaceCurrent(target)) return;
        _pane = Pane.Text;
        StopFollowing();
        await ShowGalleryAsync(item, imageId);
    }

    /// <summary>Copies only the selected occurrence of the current workspace.</summary>
    private Task CopyDetailAsync(DisplayTarget target, CaptureOccurrenceKey key) => ReadForDisplayedTargetAsync(target, async () =>
    {
        if (!IsDetailCurrent(target, key) || _galleryItem is not { } item) return;
        _detailSuccess = null;
        try { await Js.InvokeVoidAsync("playRecordingText.copy", item.Text); }
        catch (JSException)
        {
            if (IsDetailCurrent(target, key))
                _actionError = new("コピー失敗", "本文をコピーできませんでした。ブラウザーのクリップボード設定を確認してください。");
            return;
        }
        if (IsDetailCurrent(target, key)) _detailSuccess = "本文をコピーしました";
    });

    private async Task LoadGalleryImageAsync(bool refreshList = true)
    {
        if (_state is not { } state || _galleryItem is not { } item) return;
        var target = ForDisplay(state);
        var key = new CaptureEventKey(item.SessionId, item.Sequence);
        _galleryImage = null;
        _galleryError = false;
        if (refreshList)
        {
            await ReadAsync(target, "screenshot list", gameId => Operations.GetScreenshotsAsync(gameId, target.Recording, key),
                images => _galleryImages = images, () => { _galleryImages = null; _galleryError = true; }, loading => _galleryLoading = loading);
        }
        if (!IsWorkspaceCurrent(target) || _galleryImages is not { Count: > 0 } images) return;
        _galleryIndex = Math.Clamp(_galleryIndex, 0, images.Count - 1);
        if (_preferredGalleryImageId is { } preferredId)
        {
            var index = Array.FindIndex(images.ToArray(), image => image.ImageId == preferredId);
            if (index < 0)
            {
                _galleryError = true;
                return;
            }
            _galleryIndex = index;
        }
        var imageId = images[_galleryIndex].ImageId;
        var imageTarget = ForDisplay(_state!);
        await ReadAsync(imageTarget, "screenshot", gameId => Operations.ReadScreenshotAsync(gameId, imageTarget.Recording, key, imageId),
            png => _galleryImage = new("data:image/png;base64," + Convert.ToBase64String(png)), () => _galleryError = true,
            loading => _galleryLoading = loading);
    }

    private bool _pathsLoading, _pathsNeedReload;
    private long _flowReadOrder;

    /// <summary>Reads the current root's flow while keeping the existing child hidden until the new result is confirmed.</summary>
    private async Task LoadPathsAsync()
    {
        if (_state is not { } state) return;
        var target = ForDisplay(state);
        var order = ++_flowReadOrder;
        bool IsCurrentRead() => order == _flowReadOrder && IsWorkspaceCurrent(target);
        _pathsError = false;
        _pathsNeedReload = false;
        _pathsLoading = true;
        StateHasChanged();
        try
        {
            await ReadAsync(target, "observed flow", gameId => Operations.GetObservedFlowAsync(gameId, target.Recording), flow =>
            {
                if (!IsCurrentRead()) return;
                _graph = flow.Graph;
                _matchingComplete = flow.MatchingComplete;
            }, () =>
            {
                if (IsCurrentRead()) _pathsError = true;
            });
        }
        catch (DisplayChangedException) when (IsCurrentRead())
        {
            // A stale command on the same root does not make its previous graph current.
            _pathsError = true;
        }
        finally
        {
            if (IsCurrentRead())
            {
                _pathsLoading = false;
                if (!_disposed) StateHasChanged();
            }
        }
    }

    /// <summary>Invalidates the graph and any pending publication when the display identity is no longer known.</summary>
    private void ClearObservedFlow()
    {
        _flowReadOrder++;
        _graph = null;
        _matchingComplete = _pathsError = _pathsLoading = _pathsNeedReload = false;
    }

    private Task CutPathAsync(DisplayTarget target, CaptureTextItem item) =>
        CutPathAsync(target, gameId => Operations.CutPathAsync(gameId, target.Recording, item), item.Id);

    private Task CutOccurrenceAsync(DisplayTarget target, ScenarioFlowOccurrence occurrence) =>
        CutPathAsync(target, gameId => Operations.CutPathAsync(gameId, target.Recording, occurrence.Start));

    private Task CutPathAsync(DisplayTarget target, Func<string, Task> operation, long? rowId = null) => RunMutationAsync(target, async () =>
    {
        await UseActionAsync(target, operation);
        _actionSuccess = "経路の区切りを保存しました";
        await RefreshStatusAsync(loadPane: _pane == Pane.Paths, refreshLatestText: false);
        if (rowId is { } id && IsWorkspaceCurrent(target))
            FocusAfterRender(_galleryItem is null ? FocusTarget.PathRow : FocusTarget.Gallery, id);
    }, "経路変更失敗");

    /// <summary>Reads existing manual scenes on demand under the current Web target and connection.</summary>
    private async Task ToggleSavedScenesAsync()
    {
        if (_state is not { } state) return;
        _savedScenesOpen = !_savedScenesOpen;
        _sceneFlow = null;
        _scenesError = false;
        if (!_savedScenesOpen) return;

        // ReadAsync discards responses after the displayed target or connection changes.
        await ReadAsync(ForDisplay(state), "scene flow", gameId => Operations.GetSceneFlowAsync(gameId, state.Target),
            flow => _sceneFlow = flow, () => _scenesError = true, loading => _scenesLoading = loading);
    }
}
