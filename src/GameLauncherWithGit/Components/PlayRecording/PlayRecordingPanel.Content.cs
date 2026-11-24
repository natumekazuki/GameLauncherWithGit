using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Application.Services;
using Microsoft.AspNetCore.Components;

namespace GameLauncherWithGit.Components.PlayRecording;

public partial class PlayRecordingPanel
{
    private const int TextPageSize = 24;
    private bool _latest = true, _pickingRange;
    private bool _textError, _rangesError, _galleryError, _pathsError, _scenesError;
    private int _galleryIndex, _galleryCount;
    private readonly List<CapturePosition> _olderPages = [];
    private CapturePosition? _nextBefore;
    private long? _textRangeId;
    private IReadOnlyList<CaptureTextItem>? _textItems;
    private IReadOnlyList<CaptureRange>? _ranges;
    private IReadOnlyList<CaptureLabel> _labels = [];
    private IReadOnlyList<string> _rangeTags = [], _speakers = [];
    private CaptureTextItem? _editItem, _galleryItem, _first, _last;
    private long _editLabel, _connectionTarget;
    private string _editSpeaker = "";
    private string? _galleryImage, _preferredGalleryImageId, _galleryText;
    private ScenarioFlowGraph? _graph;
    private bool _matchingComplete;
    private ScenarioSceneFlowSnapshot? _sceneFlow;
    private SceneDraft? _sceneDraft;
    private PlayRecordingScreenshotResult? _screenshotResult;

    private bool TextRangeValid => _first is not null && _last is not null && _first.StartPosition.CompareTo(_last.EndPosition) < 0;
    private IEnumerable<CaptureLabel> LabelChoices => _labels
        .Where(x => x.Kind is CaptureKind.Dialogue or CaptureKind.Narration || x.Id == _editItem?.LabelId)
        .GroupBy(x => x.Kind).Select(g => g.FirstOrDefault(x => x.Id == _editItem?.LabelId) ?? g.First());
    private bool EditingNarration => _labels.Any(x => x.Id == _editLabel && x.Kind == CaptureKind.Narration);
    private string[] SceneTags => _sceneFlow?.Tags.Select(x => x.Name).ToArray() ?? [];

    private void ApplyTextPage(PlayRecordingPage page)
    {
        _nextBefore = page.NextBefore;
        var rows = page.Items.Reverse().ToArray();
        if (_textItems is null || !_textItems.SequenceEqual(rows))
        {
            _textItems = rows;
            var ids = rows.Select(row => row.Id).ToHashSet();
            foreach (var id in _rowButtons.Keys.Where(id => !ids.Contains(id)).ToArray()) _rowButtons.Remove(id);
            foreach (var id in ids) _rowButtons.TryAdd(id, new());
        }
        _textError = false;
    }

    private void ResetTextPages()
    {
        _olderPages.Clear();
        _nextBefore = null;
    }

    private Task ShowLatestAsync() => ChangeTextViewAsync(true, null);
    private Task ShowHistoryAsync() => ChangeTextViewAsync(false, null);
    private Task OpenRangeAsync(DisplayTarget target, long rangeId) => ReadForDisplayedTargetAsync(target, async () =>
    {
        await OpenTextViewAsync(false, rangeId);
        FocusAfterRender(FocusTarget.Text);
    });

    private Task ChangeTextViewAsync(bool latest, long? rangeId) => RunForegroundAsync(() => OpenTextViewAsync(latest, rangeId));

    private async Task OpenTextViewAsync(bool latest, long? rangeId)
    {
        _pane = Pane.Text;
        _latest = latest;
        _textRangeId = rangeId;
        ResetTextPages();
        _textItems = null;
        CloseTextEditor();
        CloseGallery();
        ClearRangePick();
        await LoadTextAsync();
    }

    private Task RefreshTextAsync() => RunForegroundAsync(LoadTextAsync);
    private Task RefreshRangesAsync() => RunForegroundAsync(LoadRangesAsync);
    private Task RefreshPathsAsync() => RunForegroundAsync(LoadPathsAsync);
    private Task RefreshScenesAsync() => RunForegroundAsync(LoadScenesAsync);

    private Task PageTextAsync(int direction) => RunForegroundAsync(async () =>
    {
        if (direction > 0 && _nextBefore is { } older) _olderPages.Add(older);
        else if (direction < 0 && _olderPages.Count > 0) _olderPages.RemoveAt(_olderPages.Count - 1);
        else return;
        _latest = false;
        _textItems = null;
        CloseTextEditor();
        await LoadTextAsync();
    });

    private void ToggleRangePick()
    {
        if (ControlsDisabled) return;
        if (_pickingRange) ClearRangePick();
        else
        {
            _pickingRange = true;
            CloseTextEditor();
        }
    }

    private void PickRangeEdge(CaptureTextItem item, bool start)
    {
        if (ControlsDisabled || !_pickingRange) return;
        if (start) _first = item;
        else _last = item;
    }

    private void ClearRangePick()
    {
        _pickingRange = false;
        _first = _last = null;
    }

    private void BeginTextEdit(CaptureTextItem item)
    {
        _editItem = item;
        _editLabel = item.LabelId ?? 0;
        _editSpeaker = item.Speaker ?? "";
        FocusAfterRender(FocusTarget.Classification, item.Id);
    }

    private void CancelTextEdit()
    {
        if (_editItem is { } item) FocusAfterRender(FocusTarget.EditButton, item.Id);
        CloseTextEditor();
    }

    private void CloseTextEditor()
    {
        _editItem = null;
        _editLabel = 0;
        _editSpeaker = "";
    }

    private void CloseGallery()
    {
        _galleryItem = null;
        _galleryImage = null;
        _galleryText = null;
        _preferredGalleryImageId = null;
        _galleryError = false;
        _galleryIndex = 0;
        _galleryCount = -1;
    }

    private Task OpenGalleryAsync(DisplayTarget target, CaptureTextItem item) => ReadForDisplayedTargetAsync(target, () => ShowGalleryAsync(item));

    private void ReturnFromGallery()
    {
        if (_galleryItem is { } item) FocusAfterRender(FocusTarget.GalleryButton, item.Id);
        CloseGallery();
    }

    private Task ShowGalleryAsync(CaptureTextItem item, string? imageId = null, bool showRawText = false)
    {
        CloseTextEditor();
        CloseGallery();
        _galleryItem = item;
        _galleryText = showRawText ? item.RawText : item.Text;
        _preferredGalleryImageId = imageId;
        FocusAfterRender(FocusTarget.Gallery, item.Id);
        return LoadGalleryImageAsync();
    }

    private Task ReloadGalleryAsync() => RunForegroundAsync(LoadGalleryImageAsync);

    private Task PageGalleryAsync(int direction) => RunForegroundAsync(async () =>
    {
        var index = _galleryIndex + direction;
        if (index < 0 || index >= _galleryCount) return;
        _preferredGalleryImageId = null;
        _galleryIndex = index;
        await LoadGalleryImageAsync();
    });

    private void GalleryDecodeFailed(string image)
    {
        if (_galleryImage != image) return;
        _galleryImage = null;
        _galleryError = true;
    }

    private void ClearScreenshotResult() => _screenshotResult = null;

    private string RangeName(long id)
    {
        if (_ranges is null || _ranges.FirstOrDefault(r => r.Id == id) is not { } range) return "選んだ区間";
        var sameName = _ranges.Where(r => r.Name == range.Name).OrderBy(r => r.Id).ToList();
        return sameName.Count == 1 ? range.Name : $"{range.Name} ({sameName.IndexOf(range) + 1})";
    }
    private string SceneName(long id) => _sceneFlow?.Scenes.FirstOrDefault(s => s.Id == id) is { } scene ? SceneName(scene.Definition) : "場面";

    private sealed class SceneDraft(ScenarioScene? source, ScenarioSceneEdit definition, IReadOnlyList<ScenarioGameTag> tags)
    {
        public ScenarioScene? Source { get; } = source;
        public long Id => Source?.Id ?? 0;
        public ScenarioSceneEdit Definition { get; } = definition;
        public string Title { get; set; } = definition.Title;
        public string Number { get; set; } = definition.DisplayNumber ?? "";
        public string Chapter { get; set; } = definition.Chapter ?? "";
        public List<long> RangeIds { get; } = [.. definition.RangeIds];
        public string[] Tags { get; set; } = definition.TagIds
            .Select(id => tags.FirstOrDefault(tag => tag.Id == id)?.Name).OfType<string>().ToArray();
    }

    private void NewScene()
    {
        if (ControlsDisabled || _sceneFlow is null) return;
        _connectionTarget = 0;
        _sceneDraft = new(null, new("", null, null, 80, 100, [], []), _sceneFlow.Tags);
    }

    private void SelectScene(ChangeEventArgs args)
    {
        if (ControlsDisabled || _sceneFlow is null || !long.TryParse(args.Value?.ToString(), out var id)) return;
        _connectionTarget = 0;
        _sceneDraft = _sceneFlow.Scenes.FirstOrDefault(x => x.Id == id) is { } scene
            ? new(scene, scene.Definition, _sceneFlow.Tags)
            : null;
    }

    private void CancelScene() => _sceneDraft = null;

    private void SetSceneRange(SceneDraft draft, long rangeId, bool enabled)
    {
        if (ControlsDisabled || _sceneDraft != draft) return;
        if (enabled)
        {
            if (!draft.RangeIds.Contains(rangeId)) draft.RangeIds.Add(rangeId);
        }
        else draft.RangeIds.Remove(rangeId);
    }
}
