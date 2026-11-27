using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Application.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Microsoft.Maui.ApplicationModel.DataTransfer;

namespace GameLauncherWithGit.Components.Pages;

public partial class Capture
{
    [Inject] private IClipboard ScenarioClipboard { get; set; } = default!;

    private CaptureTextItem? _detailItem;
    private CaptureOccurrenceKey? _detailKey;
    private IReadOnlyList<string>? _detailImages;
    private int _detailImageIndex;
    private string? _detailImageSource, _detailImageError, _detailOpener;
    private string? _detailMessage;
    private bool _detailImagesLoading, _focusDetail, _restoreDetailFocus;
    private bool _correctionOpen, _focusCorrection;
    private ElementReference _detailHost, _correctionHost;
    private DetailRangeEdit? _detailRangeEdit;
    private long? _restoreDetailRangeFocus;
    private bool _focusDetailRange;
    private ElementReference _detailRangeForm;

    /// <summary>Holds name and tags for one saved range without changing the selected occurrence.</summary>
    private sealed class DetailRangeEdit(CaptureOccurrenceKey occurrence, CaptureRange range)
    {
        public CaptureOccurrenceKey Occurrence { get; } = occurrence;
        public long RangeId { get; } = range.Id;
        public string Name { get; set; } = range.Name;
        public string[] Tags { get; set; } = range.Tags.ToArray();
    }

    private ScreenshotSuccess? VisibleShot(ScreenshotStatus status) =>
        ViewingLiveRecord && status.LastSuccess is { } shot && shot.Target.CaptureGameId == _recordId &&
        shot.Target.Event.SessionId == _snapshot.SessionId ? shot : null;

    /// <summary>Opens the recorded event and exact saved image of a confirmed screenshot.</summary>
    private Task OpenScreenshotResultAsync(ScreenshotSuccess shot) => RunAsync(async () =>
    {
        if (VisibleShot(Screenshots.Snapshot) != shot) throw ScopeChanged();
        var source = shot.Target.Event;
        var rows = await Store.GetGameCaptureItemsAsync(_recordId, limit: 1, latest: true,
            before: new(source.SessionId, checked(source.Sequence + 1), 0));
        if (rows.SingleOrDefault() is not { } item || new CaptureEventKey(item.SessionId, item.Sequence) != source)
            throw new InvalidOperationException("撮影に対応する本文は見つかりません。");
        _flowOpen = false;
        await ShowDetailAsync(item, opener: "capture-last-shot", preferredImageId: shot.ImageId);
    }, failureLabel: "撮影結果の取得失敗");

    /// <summary>Uses the row position to resolve the full source item even when a range clips the row.</summary>
    private Task OpenRowDetailAsync(CaptureTextItem row) => RunAsync(async () =>
    {
        var item = await FindOccurrenceItemAsync(_recordId, row.StartPosition);
        await ShowDetailAsync(item, RowDetailId(row));
    }, failureLabel: "本文の取得失敗");

    private static string RowDetailId(CaptureTextItem row) => $"capture-item-{row.Id}";

    /// <summary>Replaces only the detail selection; the text query and position stay in the mounted feed.</summary>
    private async Task ShowDetailAsync(CaptureTextItem item, string? opener = null, string? preferredImageId = null)
    {
        var expected = _displayedScope;
        await RequireCurrentScopeAsync(expected);
        var key = new CaptureOccurrenceKey(expected!.RecordId, item.StartPosition);
        _detailItem = item;
        _detailKey = key;
        _detailOpener = opener;
        _detailMessage = null;
        _detailRangeEdit = null;
        _restoreDetailRangeFocus = null;
        _focusDetailRange = false;
        _detailImageIndex = 0;
        _following = false;
        _correctionOpen = _edit?.StartPosition == item.StartPosition;
        _focusDetail = true;
        await LoadDetailImagesAsync(key, preferredImageId);
    }

    /// <summary>Validates the record and position for every action emitted by the shared detail.</summary>
    private bool IsDetailCurrent(CaptureOccurrenceKey key) => key == _detailKey && key.RecordId == _recordId &&
        _detailItem is { } item && item.StartPosition == key.Position &&
        new CaptureEventKey(item.SessionId, item.Sequence) == key.EventKey;

    /// <summary>Fetches metadata only for this event, then reads at most its selected image.</summary>
    private async Task LoadDetailImagesAsync(CaptureOccurrenceKey key, string? preferredImageId = null)
    {
        if (!IsDetailCurrent(key)) return;
        _detailImagesLoading = true;
        _detailImages = null;
        _detailImageSource = _detailImageError = null;
        var expected = _displayedScope;
        try
        {
            var images = await ReadOccurrenceImageIdsAsync(expected, key, () => IsDetailCurrent(key), _stop.Token);
            _detailImages = images;
            if (preferredImageId is not null)
            {
                // A missing saved image must not be replaced by another attachment.
                var index = Array.IndexOf(images.ToArray(), preferredImageId);
                if (index < 0) throw new InvalidOperationException("保存した画像は見つかりません。");
                _detailImageIndex = index;
            }
            else _detailImageIndex = Math.Clamp(_detailImageIndex, 0, Math.Max(0, images.Count - 1));
            await ReadDetailImageAsync(key);
        }
        catch (Exception ex)
        {
            if (IsDisplayed(expected) && IsDetailCurrent(key)) _detailImageError = $"画像を読み込めませんでした：{ex.Message}";
        }
        finally
        {
            if (IsDetailCurrent(key)) _detailImagesLoading = false;
        }
    }

    /// <summary>Reads one image through the scoped store, with a second check before publishing the bytes.</summary>
    private async Task ReadDetailImageAsync(CaptureOccurrenceKey key)
    {
        _detailImageSource = _detailImageError = null;
        if (!IsDetailCurrent(key) || _detailImages is not { Count: > 0 } images) return;
        var expected = _displayedScope;
        _detailImagesLoading = true;
        try
        {
            var imageId = images[_detailImageIndex];
            var bytes = await ReadOccurrenceImageBytesAsync(expected, key, imageId, () => IsDetailCurrent(key), _stop.Token);
            _detailImageSource = "data:image/png;base64," + Convert.ToBase64String(bytes);
        }
        catch (Exception ex)
        {
            if (IsDisplayed(expected) && IsDetailCurrent(key)) _detailImageError = $"画像を読み込めませんでした：{ex.Message}";
        }
        finally
        {
            if (IsDetailCurrent(key)) _detailImagesLoading = false;
        }
    }

    /// <summary>Reads metadata only from the same event-scoped store for text and flow details.</summary>
    private Task<IReadOnlyList<string>> ReadOccurrenceImageIdsAsync(DisplayedScope? expected, CaptureOccurrenceKey key,
        Func<bool> isSelected, CancellationToken token, bool resolveFlowPosition = false) => ReadSelectedImageDataAsync<IReadOnlyList<string>>(expected, key, isSelected, async () =>
    {
        // Flow supplies a raw UTF-16 position rather than an already resolved text item.
        if (resolveFlowPosition)
        {
            await FindOccurrenceItemAsync(key.RecordId, key.Position);
            token.ThrowIfCancellationRequested();
        }
        var metadata = await ScreenshotStore.GetEventScreenshotMetadataAsync(key.RecordId, [key.EventKey], token);
        return metadata.Single().Images.Select(image => image.ImageId).ToArray();
    }, token);

    /// <summary>The shared flow owns the selected generation; each delegate is bound to that exact graph occurrence.</summary>
    private bool IsFlowImageSelection(DisplayedScope expected, ScenarioFlowOccurrence selected, CaptureOccurrenceKey requested) =>
        requested.RecordId == expected.RecordId && requested.Position == selected.Start &&
        _flowGraph?.Occurrences.Any(occurrence => occurrence.Id == selected.Id && occurrence.Start == selected.Start) == true;

    /// <summary>Lists attachments for the selected raw flow position, not the block as a whole.</summary>
    private Task<IReadOnlyList<string>> LoadFlowImageIdsAsync(DisplayedScope expected, ScenarioFlowOccurrence selected,
        CaptureOccurrenceKey key, CancellationToken token) => ReadOccurrenceImageIdsAsync(expected, key,
        () => IsFlowImageSelection(expected, selected, key), token, resolveFlowPosition: true);

    /// <summary>Loads only the selected image from the flow detail, with the same checks as the text detail.</summary>
    private Task<byte[]> ReadFlowImageAsync(DisplayedScope expected, ScenarioFlowOccurrence selected, CaptureOccurrenceKey key,
        string imageId, CancellationToken token) => ReadOccurrenceImageBytesAsync(expected, key, imageId,
        () => IsFlowImageSelection(expected, selected, key), token);

    /// <summary>Reads one PNG; the store verifies that the image belongs to the selected record and event.</summary>
    private Task<byte[]> ReadOccurrenceImageBytesAsync(DisplayedScope? expected, CaptureOccurrenceKey key, string imageId,
        Func<bool> isSelected, CancellationToken token) => ReadSelectedImageDataAsync(expected, key, isSelected,
        () => ScreenshotStore.ReadEventScreenshotPngAsync(key.RecordId, key.EventKey, imageId), token);

    /// <summary>Rejects stale selection and record authority on both sides of an image-store await.</summary>
    private async Task<T> ReadSelectedImageDataAsync<T>(DisplayedScope? expected, CaptureOccurrenceKey key,
        Func<bool> isSelected, Func<Task<T>> read, CancellationToken token)
    {
        await RequireImageSelectionAsync(expected, key, isSelected, token);
        var data = await read();
        await RequireImageSelectionAsync(expected, key, isSelected, token);
        return data;
    }

    /// <summary>Uses the existing display boundary rather than trusting a record ID from a child.</summary>
    private async Task RequireImageSelectionAsync(DisplayedScope? expected, CaptureOccurrenceKey key,
        Func<bool> isSelected, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (expected?.RecordId != key.RecordId || !isSelected()) throw ScopeChanged();
        await RequireCurrentScopeAsync(expected);
        token.ThrowIfCancellationRequested();
        if (!isSelected()) throw ScopeChanged();
    }

    private Task RetryDetailImagesAsync(CaptureOccurrenceKey key) => RunAsync(() => LoadDetailImagesAsync(key));

    /// <summary>Moves one slot within the current event, rejecting callbacks from a previous detail.</summary>
    private Task MoveDetailImageAsync((CaptureOccurrenceKey Occurrence, int Direction) request) => RunAsync(async () =>
    {
        if (!IsDetailCurrent(request.Occurrence) || request.Direction is not (-1 or 1) || _detailImages is not { } images) return;
        var index = _detailImageIndex + request.Direction;
        if (index < 0 || index >= images.Count) return;
        _detailImageIndex = index;
        await ReadDetailImageAsync(request.Occurrence);
    });

    /// <summary>Reports a decode error only if both the occurrence and rendered source still match.</summary>
    private void DetailDecodeFailed((CaptureOccurrenceKey Occurrence, string Source) request)
    {
        if (!IsDetailCurrent(request.Occurrence) || _detailImageSource != request.Source) return;
        _detailImageSource = null;
        _detailImageError = "画像を表示できませんでした。";
    }

    /// <summary>Copies the selected source item, not a different item now at the same list index.</summary>
    private Task CopyDetailAsync(CaptureOccurrenceKey key) => RunAsync(async () =>
    {
        if (!IsDetailCurrent(key) || _detailItem is not { } item) return;
        await ScenarioClipboard.SetTextAsync(item.Text);
        _detailMessage = "本文をコピーしました。";
    }, failureLabel: "本文のコピー失敗");

    /// <summary>Closes presentation without discarding an unfinished correction or range draft.</summary>
    private void CloseDetail(CaptureOccurrenceKey key)
    {
        if (!IsDetailCurrent(key) || _busy) return;
        ClearDetail();
        _restoreDetailFocus = true;
    }

    /// <summary>Clears the detail pane when closed or retargeted, while leaving the feed mounted.</summary>
    private void ClearDetail()
    {
        _detailItem = null;
        _detailKey = null;
        _detailImages = null;
        _detailMessage = null;
        _detailRangeEdit = null;
        _restoreDetailRangeFocus = null;
        _focusDetailRange = false;
        _detailImageSource = _detailImageError = null;
        _detailImagesLoading = _focusDetail = _correctionOpen = false;
    }

    private void HandleDetailKey(KeyboardEventArgs args)
    {
        if (args.Key != "Escape" || _busy) return;
        // Close the innermost editor before dismissing the selected occurrence.
        if (_detailRangeEdit is { } editor) CloseDetailRangeEdit(editor);
        else if (_detailKey is { } key) CloseDetail(key);
    }

    /// <summary>Matches the displayed occurrence span against saved ranges, including clipped text.</summary>
    private IEnumerable<CaptureRange> DetailRanges(CaptureOccurrenceKey key, CaptureTextItem item) => _ranges.Where(range =>
        range.CaptureGameId == key.RecordId && range.Start.CompareTo(item.EndPosition) < 0 &&
        range.End.CompareTo(key.Position) > 0);

    private static string DetailRangeButtonId(long rangeId) => $"capture-detail-range-{rangeId}";

    /// <summary>Opens only the clicked range and keeps the preview and feed in place.</summary>
    private void EditDetailRange(CaptureOccurrenceKey key, long rangeId)
    {
        if (_busy || !IsDetailCurrent(key) || _detailItem is not { } item) return;
        if (_detailRangeEdit is { } open)
        {
            // Keep the expanded button as the toggle for that exact editor.
            if (open.RangeId == rangeId) CloseDetailRangeEdit(open);
            return;
        }
        if (DetailRanges(key, item).FirstOrDefault(range => range.Id == rangeId) is not { } range) return;
        _error = _errorLabel = _detailMessage = null;
        _detailRangeEdit = new(key, range);
        _focusDetailRange = true;
    }

    /// <summary>Discards the local edit and restores its opener, leaving the preview mounted.</summary>
    private void CloseDetailRangeEdit(DetailRangeEdit edit)
    {
        if (!ReferenceEquals(edit, _detailRangeEdit)) return;
        _detailRangeEdit = null;
        _focusDetailRange = false;
        _error = _errorLabel = null;
        _restoreDetailRangeFocus = edit.RangeId;
    }

    /// <summary>Changes only the selected range's metadata through the existing record-scoped write.</summary>
    private Task SaveDetailRangeAsync(DetailRangeEdit edit, PlayRecordingTarget target) => RunAsync(async () =>
    {
        if (!ReferenceEquals(edit, _detailRangeEdit) || !IsDetailCurrent(edit.Occurrence) || _detailItem is not { } item ||
            !DetailRanges(edit.Occurrence, item).Any(range => range.Id == edit.RangeId))
            throw ScopeChanged();

        var expected = _displayedScope;
        await WriteHistoryAsync(target, recordId => Store.UpdateCaptureRangeAsync(recordId, edit.RangeId, edit.Name, edit.Tags));
        // Refresh only range metadata so the filtered page and selected image do not move.
        var ranges = await Store.GetCaptureRangesAsync(edit.Occurrence.RecordId);
        if (!await CanPublishAsync(expected) || !ReferenceEquals(edit, _detailRangeEdit) || !IsDetailCurrent(edit.Occurrence)) return;
        _ranges = ranges;
        CloseDetailRangeEdit(edit);
    }, failureLabel: "区間の名前・タグを保存できませんでした");

    /// <summary>Resumes the current item’s correction draft without replacing an unsaved draft for another line.</summary>
    private void EditDetail(CaptureOccurrenceKey key)
    {
        if (!IsDetailCurrent(key) || _detailItem is not { } item || _edit is { } edit && edit.StartPosition != key.Position) return;
        if (_edit is null)
        {
            _edit = item;
            _editLabel = item.LabelId ?? 0;
            _editSpeaker = item.Speaker ?? "";
        }
        _correctionOpen = true;
        _focusCorrection = true;
    }

    private void DiscardCorrection()
    {
        _correctionOpen = false;
        _edit = null;
    }

    /// <summary>Returns focus to the selected detail or its originating row without scrolling it.</summary>
    private async Task UpdateDetailFocusAsync()
    {
        if (_module is null || _busy) return;
        if (_focusDetail && _detailKey is not null)
        {
            _focusDetail = false;
            await _module.InvokeVoidAsync("focusTextDetail", _detailHost);
        }
        if (_restoreDetailFocus)
        {
            _restoreDetailFocus = false;
            await _module.InvokeVoidAsync("focusTextDetailOpener", _detailOpener);
            _detailOpener = null;
        }
        if (_focusCorrection && _correctionOpen && _detailKey is not null)
        {
            _focusCorrection = false;
            await _correctionHost.FocusAsync();
        }
        if (_restoreDetailRangeFocus is { } rangeId && _detailKey is not null)
        {
            _restoreDetailRangeFocus = null;
            await _module.InvokeVoidAsync("focusTextDetailOpener", DetailRangeButtonId(rangeId));
        }
        if (_focusDetailRange && _detailRangeEdit is not null)
        {
            // Reveal the newly opened inputs inside the scrolling detail, not by moving the feed.
            _focusDetailRange = false;
            await _detailRangeForm.FocusAsync();
        }
    }
}
