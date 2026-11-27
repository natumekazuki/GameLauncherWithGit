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
    private CapturePosition? _pausedBefore;
    private double? _readingPosition, _restoreReadingPosition;
    private string _textSearchInput = "", _speakerSearchInput = "";
    private string? _textSearch, _speakerSearch;
    private long? _textRangeId;
    private IReadOnlyList<CaptureTextItem>? _textItems;
    private IReadOnlyDictionary<CaptureEventKey, int>? _imageCounts;
    private bool _imageCountsFailed;
    private IReadOnlyList<CaptureRange>? _ranges;
    private IReadOnlyList<CaptureLabel> _labels = [];
    private IReadOnlyList<string> _rangeTags = [], _speakers = [];
    private CaptureTextItem? _editItem, _galleryItem, _first, _last;
    private long _editLabel;
    private string _editSpeaker = "";
    private sealed class GalleryImage(string source) { public string Source { get; } = source; }
    private GalleryImage? _galleryImage;
    private string? _preferredGalleryImageId, _detailSuccess;
    private long? _selectedItemId;
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
        var rows = page.Items;
        if (_textItems is null || !_textItems.SequenceEqual(rows))
        {
            _textItems = rows;
            _imageCounts = null;
            _imageCountsFailed = false;
            var ids = rows.Select(row => row.Id).ToHashSet();
            foreach (var id in _rowButtons.Keys.Where(id => !ids.Contains(id)).ToArray()) _rowButtons.Remove(id);
            foreach (var id in ids) _rowButtons.TryAdd(id, new());
            if (_latest) _scrollToLatest = true;
        }
        _textError = false;
    }

    private void ResetTextPages()
    {
        _olderPages.Clear();
        _nextBefore = null;
        _pausedBefore = null;
    }

    private Task OpenRangeAsync(DisplayTarget target, long rangeId) => ReadForDisplayedTargetAsync(target, async () =>
    {
        await OpenTextViewAsync(false, rangeId);
        _savedRangesOpen = false;
        FreezeTextPage();
        FocusAfterRender(FocusTarget.Text);
    });

    /// <summary>Explicitly resumes the live tail without clearing the current search.</summary>
    private Task FollowLatestAsync(DisplayTarget target) => ReadForDisplayedTargetAsync(target, async () =>
    {
        await OpenTextViewAsync(true, null);
        _scrollToLatest = true;
    });

    /// <summary>Stops polling updates while this page is being read.</summary>
    private void StopFollowing()
    {
        if (!_latest) return;
        _latest = false;
        _scrollToLatest = false;
        FreezeTextPage();
    }

    /// <summary>Anchors the first page so that retries and editing do not jump to newer rows.</summary>
    private void FreezeTextPage() => _pausedBefore ??= _textItems?.LastOrDefault()?.EndPosition;

    /// <summary>Applies a new search without replacing the recording target or creating another view.</summary>
    private Task SearchTextsAsync(DisplayTarget target) => ReadForDisplayedTargetAsync(target, async () =>
    {
        _textSearch = Optional(_textSearchInput);
        _speakerSearch = Optional(_speakerSearchInput);
        await OpenTextViewAsync(false, _textRangeId);
        FreezeTextPage();
    });

    private async Task OpenTextViewAsync(bool latest, long? rangeId)
    {
        _pane = Pane.Text;
        _latest = latest;
        _textRangeId = rangeId;
        _readingPosition = null;
        _restoreReadingPosition = null;
        _selectedItemId = null;
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
        StopFollowing();
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
        if (_editItem is not null) FocusAfterRender(FocusTarget.Gallery);
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
        _preferredGalleryImageId = null;
        _detailSuccess = null;
        _galleryError = false;
        _galleryIndex = 0;
        _galleryImages = null;
    }

    /// <summary>Resolves the underlying item even if a range filter clipped its displayed slice.</summary>
    private Task OpenDetailAsync(DisplayTarget target, CaptureTextItem item) =>
        ReadForDisplayedTargetAsync(target, () => OpenResolvedTextAsync(target, item.StartPosition));

    private void ReturnFromGallery()
    {
        if (_galleryItem is { } item) FocusAfterRender(FocusTarget.GalleryButton, item.Id);
        _restoreReadingPosition = _readingPosition;
        CloseTextEditor();
        CloseGallery();
    }

    /// <summary>Opens the controlled detail for one occurrence; only this surface loads image bodies.</summary>
    private async Task ShowGalleryAsync(CaptureTextItem item, string? imageId)
    {
        StopFollowing();
        CloseTextEditor();
        CloseGallery();
        _galleryItem = item;
        _selectedItemId = item.Id;
        _preferredGalleryImageId = imageId;
        FocusAfterRender(FocusTarget.Gallery, item.Id);
        await LoadGalleryImageAsync();
        if (_ranges is null) await LoadRangesAsync();
    }

    /// <summary>Checks a shared-detail event against the still-displayed root and raw occurrence.</summary>
    private bool IsDetailCurrent(DisplayTarget target, CaptureOccurrenceKey key) => IsWorkspaceCurrent(target)
        && _galleryItem is { } item && key == new CaptureOccurrenceKey(target.Recording.HistoryRoot, item.StartPosition);

    /// <summary>Starts selecting a range from the open occurrence using the existing shared draft path.</summary>
    private void PickRangeFromDetail(DisplayTarget target, CaptureOccurrenceKey key)
    {
        if (ControlsDisabled || _textRangeId is not null || _rangeDraft is not null || !IsDetailCurrent(target, key)) return;
        _pickingRange = true;
        _first = _galleryItem;
        _last = null;
        ReturnFromGallery();
        FocusAfterRender(FocusTarget.RangePicker);
    }

    /// <summary>Displays actual counts only after the metadata query returns for this page.</summary>
    private int? ImageCount(CaptureTextItem item) =>
        _imageCounts?.TryGetValue(new(item.SessionId, item.Sequence), out var count) == true ? count : null;

    /// <summary>Returns saved ranges intersecting the selected UTF-16 text slice.</summary>
    private IEnumerable<CaptureRange> RangesFor(CaptureTextItem item) =>
        _ranges?.Where(range => item.EndPosition.CompareTo(range.Start) > 0 && item.StartPosition.CompareTo(range.End) < 0) ?? [];

    private bool CanPageGallery(int direction) =>
        _galleryImages is { } images && _galleryIndex + direction >= 0 && _galleryIndex + direction < images.Count;

    private Task PageGalleryAsync(DisplayTarget target, (CaptureOccurrenceKey Occurrence, int Direction) request) => ReadForDisplayedTargetAsync(target, async () =>
    {
        if (!IsDetailCurrent(target, request.Occurrence) || !CanPageGallery(request.Direction)) return;
        _preferredGalleryImageId = null;
        _galleryIndex += request.Direction;
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
        if (sameName.Count == 1) return range.Name;
        var number = sameName.IndexOf(range) + 1;
        return _rangeStartLabels.TryGetValue(range.Id, out var label)
            ? $"{range.Name} ({label} · {number})"
            : $"{range.Name} ({number})";
    }

    /// <summary>Resolves saved range references without hiding their IDs when range names are unavailable.</summary>
    private string SavedSceneRangeName(long id) => _ranges?.Any(range => range.Id == id) == true
        ? RangeName(id)
        : $"区間 {id}";
}
