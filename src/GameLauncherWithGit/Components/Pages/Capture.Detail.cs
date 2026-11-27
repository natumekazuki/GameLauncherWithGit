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
    private IReadOnlyList<ScenarioScreenshot>? _detailImages;
    private int _detailImageIndex;
    private string? _detailImageSource, _detailImageError, _detailOpener;
    private string? _detailMessage;
    private bool _detailImagesLoading, _focusDetail, _restoreDetailFocus;
    private bool _correctionOpen, _focusCorrection;
    private ElementReference _detailHost, _correctionHost;

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
            var images = await ScreenshotStore.GetEventScreenshotsAsync(key.RecordId, key.EventKey);
            if (!await CanPublishAsync(expected) || !IsDetailCurrent(key)) return;
            _detailImages = images;
            if (preferredImageId is not null)
            {
                // A missing saved image must not be replaced by another attachment.
                var index = Array.FindIndex(images.ToArray(), image => image.ImageId == preferredImageId);
                if (index < 0) throw new InvalidOperationException("保存した画像は見つかりません。");
                _detailImageIndex = index;
            }
            else _detailImageIndex = Math.Clamp(_detailImageIndex, 0, Math.Max(0, images.Count - 1));
            await ReadDetailImageAsync(key);
        }
        catch (Exception ex)
        {
            if (IsDetailCurrent(key)) _detailImageError = $"画像を読み込めませんでした：{ex.Message}";
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
        try
        {
            var bytes = await ScreenshotStore.ReadEventScreenshotPngAsync(key.RecordId, key.EventKey, images[_detailImageIndex].ImageId);
            if (await CanPublishAsync(expected) && IsDetailCurrent(key))
                _detailImageSource = "data:image/png;base64," + Convert.ToBase64String(bytes);
        }
        catch (Exception ex)
        {
            if (IsDetailCurrent(key)) _detailImageError = $"画像を読み込めませんでした：{ex.Message}";
        }
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
        _detailImageSource = _detailImageError = null;
        _detailImagesLoading = _focusDetail = _correctionOpen = false;
    }

    private void HandleDetailKey(KeyboardEventArgs args)
    {
        if (args.Key == "Escape" && _detailKey is { } key) CloseDetail(key);
    }

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
    }
}
