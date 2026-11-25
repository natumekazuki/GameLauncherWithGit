using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Application.Services;

namespace GameLauncherWithGit.Components.PlayRecording;

public partial class PlayRecordingPanel
{
    private Task RefreshLatestTextAsync() => _textError ? Task.CompletedTask : LoadTextAsync();

    private Task LoadTextAsync()
    {
        if (_state is not { } state) return Task.CompletedTask;
        var before = _olderPages.Count == 0 ? null : _olderPages[^1];
        var rangeId = _textRangeId;
        return ReadAsync("texts", gameId => Operations.GetItemsAsync(gameId, state.Target, TextPageSize, before: before, rangeId: rangeId),
            ApplyTextPage, () => _textError = true);
    }

    private Task EditTextAsync(DisplayTarget target, CaptureTextItem item) => ReadForDisplayedTargetAsync(target, async () =>
    {
        if (_state is not { } state) return;
        await ReadAsync("labels", gameId => Operations.GetLabelsAsync(gameId, state.Target), labels =>
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
        await ReadAsync("text at position", gameId => Operations.GetItemAtAsync(gameId, target.Recording, position),
            value => item = value, () => _actionError = new("本文の取得失敗", "対応する本文を読み込めませんでした。記録を更新してください。"));
        if (item is null) return;
        await OpenTextViewAsync(false, null);
        if (!_disposed && !_unauthorized && IsDisplayedTargetCurrent(target))
            await ShowGalleryAsync(item, imageId, showRawText: imageId is not null);
    }

    private async Task LoadGalleryImageAsync(bool refreshList = true)
    {
        if (_state is not { } state || _galleryItem is not { } item) return;
        var target = ForDisplay(state.Target);
        var key = new CaptureEventKey(item.SessionId, item.Sequence);
        _galleryImage = null;
        _galleryError = false;
        if (refreshList)
        {
            await ReadAsync("screenshot list", gameId => Operations.GetScreenshotsAsync(gameId, target.Recording, key),
                images => _galleryImages = images, () => { _galleryImages = null; _galleryError = true; });
        }
        if (_disposed || _unauthorized || !IsDisplayedTargetCurrent(target) || _galleryImages is not { Count: > 0 } images) return;
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
        await ReadAsync("screenshot", gameId => Operations.ReadScreenshotAsync(gameId, target.Recording, key, imageId),
            png => _galleryImage = new("data:image/png;base64," + Convert.ToBase64String(png)), () => _galleryError = true);
    }

    private Task LoadPathsAsync()
    {
        if (_state is not { } state) return Task.CompletedTask;
        return ReadAsync("observed flow", gameId => Operations.GetObservedFlowAsync(gameId, state.Target), flow =>
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
        if (rowId is { } id) FocusAfterRender(FocusTarget.PathRow, id);
    }, "経路変更失敗");

    private async Task LoadScenesAsync()
    {
        if (_state is not { } state) return;
        await ReadAsync("scene flow", gameId => Operations.GetSceneFlowAsync(gameId, state.Target), flow =>
        {
            _sceneFlow = flow;
            _scenesError = false;
        }, () => _scenesError = true);
        await LoadRangesAsync();
    }

    private Task SaveSceneAsync(DisplayTarget target, SceneDraft draft) => RunMutationAsync(target, async () =>
    {
        if (_sceneFlow is not { } flow || !ReferenceEquals(_sceneDraft, draft)) return;
        var edit = draft.Definition with
        {
            Title = draft.Title.Trim(),
            DisplayNumber = Optional(draft.Number),
            Chapter = Optional(draft.Chapter),
            RangeIds = draft.RangeIds.ToArray(),
            TagIds = draft.Tags.Select(name => flow.Tags.FirstOrDefault(tag => tag.Name == name)?.Id).OfType<long>().ToArray()
        };
        var id = draft.Id;
        if (draft.Source is { } scene)
            await UseActionAsync(target, gameId => Operations.UpdateSceneAsync(gameId, target.Recording, scene, edit));
        else
            id = await UseAsync(target, gameId => Operations.CreateSceneAsync(gameId, target.Recording, edit));
        _sceneDraft = null;
        await RefreshStatusAsync(loadPane: true);
        if (_sceneFlow is { } updated && updated.Scenes.FirstOrDefault(x => x.Id == id) is { } saved)
            _sceneDraft = new(saved, saved.Definition, updated.Tags);
    }, "場面保存失敗");

    private Task SetSceneConnectionAsync(DisplayTarget target, long fromId, long toId, bool connected) => RunMutationAsync(target, async () =>
    {
        if (_sceneFlow is not { } flow || flow.Scenes.FirstOrDefault(x => x.Id == fromId) is not { } from
            || flow.Scenes.FirstOrDefault(x => x.Id == toId) is not { } to) return;
        await UseActionAsync(target, gameId => Operations.SetSceneConnectionAsync(gameId, target.Recording, from, to, connected));
        _connectionTarget = 0;
        await RefreshStatusAsync(loadPane: true);
        if (_sceneFlow is { } updated && updated.Scenes.FirstOrDefault(x => x.Id == fromId) is { } selected)
            _sceneDraft = new(selected, selected.Definition, updated.Tags);
    }, "場面接続失敗");
}
