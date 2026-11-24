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

    private Task EditTextAsync(PlayRecordingTarget target, CaptureTextItem item) => ReadForDisplayedTargetAsync(target, async () =>
    {
        if (_state is not { } state) return;
        await ReadAsync("labels", gameId => Operations.GetLabelsAsync(gameId, state.Target), labels =>
        {
            _labels = labels.ToArray();
            BeginTextEdit(item);
        }, () => _actionError = "分類を読み込めませんでした。本文を更新してください。");
    });

    private bool TextLabelChanged(CaptureTextItem? item) => item is not null && (item.LabelId ?? 0) != _editLabel;
    private bool TextSpeakerChanged(CaptureTextItem? item) => item is not null && (EditingNarration
        ? item.Speaker is not null
        : _editSpeaker.Trim() != (item.Speaker ?? "") || item.Kind == CaptureKind.Narration);

    private Task SaveTextAsync(PlayRecordingTarget target, CaptureTextItem? item) => RunMutationAsync(target, async () =>
    {
        if (item is null || !ReferenceEquals(item, _editItem)) return;
        await UseActionAsync(gameId => Operations.CorrectTextAsync(gameId, target, item,
            TextLabelChanged(item), _editLabel == 0 ? null : _editLabel,
            TextSpeakerChanged(item), EditingNarration ? null : Optional(_editSpeaker)));
        CloseTextEditor();
        _notice = "訂正を保存しました。";
        await LoadTextAsync();
    });

    private static string? Optional(string input) => string.IsNullOrWhiteSpace(input) ? null : input.Trim();

    private Task OpenOccurrenceAsync(PlayRecordingTarget target, ScenarioFlowOccurrence occurrence) =>
        ReadForDisplayedTargetAsync(target, () => OpenResolvedTextAsync(target, occurrence.Start));

    private Task OpenCapturedEventAsync(PlayRecordingTarget target, PlayRecordingScreenshotResult shot) =>
        ReadForDisplayedTargetAsync(target, () => OpenResolvedTextAsync(target, new(shot.Event.SessionId, shot.Event.Sequence, 0), shot.ImageId));

    private async Task OpenResolvedTextAsync(PlayRecordingTarget target, CapturePosition position, string? imageId = null)
    {
        CaptureTextItem? item = null;
        await ReadAsync("text at position", gameId => Operations.GetItemAtAsync(gameId, target, position),
            value => item = value, () => _actionError = "対応する本文を読み込めませんでした。記録を更新してください。");
        if (item is null) return;
        await OpenTextViewAsync(false, null);
        if (!_disposed && !_unauthorized) await ShowGalleryAsync(item, imageId, showRawText: imageId is not null);
    }

    private Task LoadGalleryImageAsync()
    {
        if (_state is not { } state || _galleryItem is not { } item) return Task.CompletedTask;
        var key = new CaptureEventKey(item.SessionId, item.Sequence);
        var preferredId = _preferredGalleryImageId;
        var previousIndex = _galleryIndex;
        _galleryImage = null;
        _galleryError = false;
        _galleryCount = -1;
        return ReadAsync("screenshot", async gameId =>
        {
            var images = await Operations.GetScreenshotsAsync(gameId, state.Target, key);
            var index = Math.Clamp(previousIndex, 0, Math.Max(0, images.Count - 1));
            if (preferredId is not null)
            {
                index = Array.FindIndex(images.ToArray(), image => image.ImageId == preferredId);
                if (index < 0) throw new InvalidOperationException("撮影した画像が見つかりません。");
            }
            var png = images.Count == 0 ? null : await Operations.ReadScreenshotAsync(gameId, state.Target, key, images[index].ImageId);
            return (Count: images.Count, Index: index, Png: png);
        }, data =>
        {
            _galleryIndex = data.Index;
            _galleryCount = data.Count;
            _galleryImage = data.Png is null ? null : "data:image/png;base64," + Convert.ToBase64String(data.Png);
        }, () => _galleryError = true);
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

    private Task CutPathAsync(PlayRecordingTarget target, CaptureTextItem item) =>
        CutPathAsync(target, gameId => Operations.CutPathAsync(gameId, target, item));

    private Task CutOccurrenceAsync(PlayRecordingTarget target, ScenarioFlowOccurrence occurrence) =>
        CutPathAsync(target, gameId => Operations.CutPathAsync(gameId, target, occurrence.Start));

    private Task CutPathAsync(PlayRecordingTarget target, Func<string, Task> operation) => RunMutationAsync(target, async () =>
    {
        await UseActionAsync(operation);
        await RefreshStatusAsync(loadPane: true);
        if (_disposed || _unauthorized) return;
        _notice = "ここから別経路にしました。";
    });

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

    private Task SaveSceneAsync(PlayRecordingTarget target, SceneDraft draft) => RunMutationAsync(target, async () =>
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
            await UseActionAsync(gameId => Operations.UpdateSceneAsync(gameId, target, scene, edit));
        else
            id = await UseAsync(gameId => Operations.CreateSceneAsync(gameId, target, edit));
        _sceneDraft = null;
        await RefreshStatusAsync(loadPane: true);
        if (!_disposed && !_unauthorized) _notice = "場面を保存しました。";
        if (_sceneFlow is { } updated && updated.Scenes.FirstOrDefault(x => x.Id == id) is { } saved)
            _sceneDraft = new(saved, saved.Definition, updated.Tags);
    });

    private Task SetSceneConnectionAsync(PlayRecordingTarget target, long fromId, long toId, bool connected) => RunMutationAsync(target, async () =>
    {
        if (_sceneFlow is not { } flow || flow.Scenes.FirstOrDefault(x => x.Id == fromId) is not { } from
            || flow.Scenes.FirstOrDefault(x => x.Id == toId) is not { } to) return;
        await UseActionAsync(gameId => Operations.SetSceneConnectionAsync(gameId, target, from, to, connected));
        _connectionTarget = 0;
        await RefreshStatusAsync(loadPane: true);
        if (!_disposed && !_unauthorized) _notice = connected ? "場面の接続を追加しました。" : "場面の接続を削除しました。";
        if (_sceneFlow is { } updated && updated.Scenes.FirstOrDefault(x => x.Id == fromId) is { } selected)
            _sceneDraft = new(selected, selected.Definition, updated.Tags);
    });
}
