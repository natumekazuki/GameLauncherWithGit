using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Application.Services;

namespace GameLauncherWithGit.Components.PlayRecording;

public partial class PlayRecordingPanel
{
    private Task RefreshLatestTextAsync() => _textError ? Task.CompletedTask : LoadTextAsync();

    private Task LoadTextAsync()
    {
        if (_state is not { } state) return Task.CompletedTask;
        var target = ForDisplay(state);
        var before = _olderPages.Count == 0 ? null : _olderPages[^1];
        var rangeId = _textRangeId;
        return ReadAsync(target, "texts", gameId => Operations.GetItemsAsync(gameId, target.Recording, TextPageSize, before: before, rangeId: rangeId),
            ApplyTextPage, () => _textError = true);
    }

    private Task EditTextAsync(DisplayTarget target, CaptureTextItem item) => ReadForDisplayedTargetAsync(target, async () =>
    {
        await ReadAsync(target, "labels", gameId => Operations.GetLabelsAsync(gameId, target.Recording), labels =>
        {
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
        await OpenTextViewAsync(false, null);
        if (IsWorkspaceCurrent(target))
            await ShowGalleryAsync(item, imageId, showRawText: imageId is not null);
    }

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
                images => _galleryImages = images, () => { _galleryImages = null; _galleryError = true; });
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
            png => _galleryImage = new("data:image/png;base64," + Convert.ToBase64String(png)), () => _galleryError = true);
    }

    private Task LoadPathsAsync()
    {
        if (_state is not { } state) return Task.CompletedTask;
        return ReadAsync(ForDisplay(state), "observed flow", gameId => Operations.GetObservedFlowAsync(gameId, state.Target), flow =>
        {
            _graph = flow.Graph;
            _matchingComplete = flow.MatchingComplete;
            _pathsError = false;
        }, () => _pathsError = true);
    }

    private Task CutPathAsync(DisplayTarget target, CaptureTextItem item) =>
        CutPathAsync(target, gameId => Operations.CutPathAsync(gameId, target.Recording, item), item.Id);

    private Task CutOccurrenceAsync(DisplayTarget target, ScenarioFlowOccurrence occurrence) =>
        CutPathAsync(target, gameId => Operations.CutPathAsync(gameId, target.Recording, occurrence.Start));

    private Task CutPathAsync(DisplayTarget target, Func<string, Task> operation, long? rowId = null) => RunMutationAsync(target, async () =>
    {
        await UseActionAsync(target, operation);
        await RefreshStatusAsync(loadPane: _pane == Pane.Paths, refreshLatestText: false);
        if (rowId is { } id && IsWorkspaceCurrent(target)) FocusAfterRender(FocusTarget.PathRow, id);
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
            flow => _sceneFlow = flow, () => _scenesError = true);
    }
}
