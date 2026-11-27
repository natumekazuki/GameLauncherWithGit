using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Application.Services;

namespace GameLauncherWithGit.Components.PlayRecording;

public partial class PlayRecordingPanel
{
    private const int TextPageSize = 24;
    private bool _latest = true, _pickingRange;
    private bool _textError, _rangesError, _galleryError, _pathsError, _scenesError;
    private int _galleryIndex;
    private IReadOnlyList<PlayRecordingScreenshot>? _galleryImages;
    private readonly List<CapturePosition> _olderPages = [];
    private CapturePosition? _nextBefore;
    private long? _textRangeId;
    private IReadOnlyList<CaptureTextItem>? _textItems;
    private IReadOnlyList<CaptureRange>? _ranges;
    private IReadOnlyList<CaptureLabel> _labels = [];
    private IReadOnlyList<string> _rangeTags = [], _speakers = [];
    private CaptureTextItem? _editItem, _galleryItem, _first, _last;
    private long _editLabel;
    private string _editSpeaker = "";
    private sealed class GalleryImage(string source) { public string Source { get; } = source; }
    private GalleryImage? _galleryImage;
    private string? _preferredGalleryImageId, _galleryText;
    private ScenarioFlowGraph? _graph;
    private bool _matchingComplete;
    private ScenarioSceneFlowSnapshot? _sceneFlow;
    private bool _savedScenesOpen;
    private PlayRecordingScreenshotResult? _screenshotResult;

    private bool TextRangeValid => _first is not null && _last is not null && _first.StartPosition.CompareTo(_last.EndPosition) < 0;
    private IEnumerable<CaptureLabel> LabelChoices => _labels
        .Where(x => x.Kind is CaptureKind.Dialogue or CaptureKind.Narration || x.Id == _editItem?.LabelId)
        .GroupBy(x => x.Kind).Select(g => g.FirstOrDefault(x => x.Id == _editItem?.LabelId) ?? g.First());
    private bool EditingNarration => _labels.Any(x => x.Id == _editLabel && x.Kind == CaptureKind.Narration);

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

    private Task OpenRangeAsync(DisplayTarget target, long rangeId) => ReadForDisplayedTargetAsync(target, async () =>
    {
        await OpenTextViewAsync(false, rangeId);
        FocusAfterRender(FocusTarget.Text);
    });

    private Task ChangeTextViewAsync(DisplayTarget target, bool latest) =>
        ReadForDisplayedTargetAsync(target, () => OpenTextViewAsync(latest, null));

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

    private Task PageTextAsync(DisplayTarget target, int direction) => ReadForDisplayedTargetAsync(target, async () =>
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
        _galleryImages = null;
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

    private bool CanPageGallery(int direction) =>
        _galleryImages is { } images && _galleryIndex + direction >= 0 && _galleryIndex + direction < images.Count;

    private Task PageGalleryAsync(DisplayTarget target, int direction) => ReadForDisplayedTargetAsync(target, async () =>
    {
        if (!CanPageGallery(direction)) return;
        _preferredGalleryImageId = null;
        _galleryIndex += direction;
        await LoadGalleryImageAsync(refreshList: false);
    });

    private void GalleryDecodeFailed(GalleryImage image)
    {
        if (!ReferenceEquals(_galleryImage, image)) return;
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

    /// <summary>Resolves saved range references without hiding their IDs when range names are unavailable.</summary>
    private string SavedSceneRangeName(long id) => _ranges?.Any(range => range.Id == id) == true
        ? RangeName(id)
        : $"区間 {id}";
}
