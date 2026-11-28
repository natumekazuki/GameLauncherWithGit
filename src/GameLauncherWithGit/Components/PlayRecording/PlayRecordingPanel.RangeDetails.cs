using GameLauncherWithGit.Application.Models;
using Microsoft.AspNetCore.Components;

namespace GameLauncherWithGit.Components.PlayRecording;

public partial class PlayRecordingPanel
{
    /// <summary>The draft can change metadata only, never the occurrence or saved range identity.</summary>
    private sealed class DetailRangeEdit(CaptureOccurrenceKey occurrence, CaptureRange range)
    {
        public CaptureOccurrenceKey Occurrence { get; } = occurrence;
        public long Id { get; } = range.Id;
        public string Name { get; set; } = range.Name;
        public string[] Tags { get; set; } = range.Tags.ToArray();
        public bool CanSave => !string.IsNullOrWhiteSpace(Name) && Name.IndexOfAny(['\r', '\n']) < 0;
    }

    private DetailRangeEdit? _detailRangeEdit;
    private sealed class DetailRangeButton { public ElementReference Open; }
    private readonly Dictionary<long, DetailRangeButton> _detailRangeButtons = [];

    /// <summary>Opens metadata fields in the selected occurrence without changing its image state.</summary>
    private void OpenDetailRangeEdit(DisplayTarget target, CaptureOccurrenceKey key, CaptureRange range)
    {
        if (ControlsDisabled || !IsDetailCurrent(target, key) || _galleryItem is not { } item
            || !RangesFor(item).Any(current => current.Id == range.Id)) return;
        CloseTextEditor();
        _detailSuccess = null;
        _detailRangeEdit = new(key, range);
    }

    /// <summary>Confirms delayed events still refer to the same draft, range and displayed occurrence.</summary>
    private bool IsDetailRangeEditCurrent(DisplayTarget target, DetailRangeEdit draft) =>
        ReferenceEquals(_detailRangeEdit, draft) && IsDetailCurrent(target, draft.Occurrence)
        && _galleryItem is { } item && RangesFor(item).Any(range => range.Id == draft.Id);

    /// <summary>Discards only metadata fields and returns to the matching range action.</summary>
    private void CloseDetailRangeEdit()
    {
        if (_detailRangeEdit is not { } draft) return;
        _detailRangeEdit = null;
        FocusAfterRender(FocusTarget.DetailRange, draft.Id);
    }

    /// <summary>Applies the current name and tags, leaving text search, scroll and image selection untouched.</summary>
    private Task SaveDetailRangeAsync(DisplayTarget target, DetailRangeEdit draft) => RunMutationAsync(target, async () =>
    {
        if (!draft.CanSave || !IsDetailRangeEditCurrent(target, draft)) return;
        await UseActionAsync(target, gameId => Operations.RenameDisplayedRangeAsync(
            gameId, target.Recording, draft.Occurrence.Position, draft.Id, draft.Name, draft.Tags));
        if (!IsDetailRangeEditCurrent(target, draft)) return;
        _pathsNeedReload = _graph is not null;
        if (_openedFlowRange is { } opened && opened.SavedRangeId == draft.Id)
            _openedFlowRange = opened with { Name = draft.Name.Trim() };
        CloseDetailRangeEdit();
        _detailSuccess = "区間名・タグを保存しました";
        await LoadRangesAsync();
    }, "区間名・タグの保存失敗", isStillRelevant: () => IsDetailRangeEditCurrent(target, draft));
}
