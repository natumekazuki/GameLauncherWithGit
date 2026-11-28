using System.Globalization;
using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Application.Services;
using Microsoft.JSInterop;

namespace GameLauncherWithGit.Components.Pages;

public partial class Capture
{
    private const string ObservedTextChoice = "observed";
    private ScenarioRangeFlowGraph? _flowGraph;
    private bool _flowOpen, _flowMatchingComplete, _flowLoading, _flowNeedsReload;
    private string? _flowReadError;
    private CaptureRangeSelection? _observedTextInterval;
    private double? _textTopBeforeFlow, _restoreFlowTextTop;
    private string TextRangeChoice => _observedTextInterval is null
        ? _rangeId.ToString(CultureInfo.InvariantCulture) : ObservedTextChoice;

    /// <summary>Switches mounted surfaces without discarding the text query or the chart state.</summary>
    private Task ShowViewAsync(bool flow) => RunAsync(async () =>
    {
        if (flow == _flowOpen) return;
        if (flow && _liveFeedModule is { } module && _feed.Id is not null)
            _textTopBeforeFlow = await module.InvokeAsync<double>("position", _feed);
        _flowOpen = flow;
        if (flow)
        {
            _following = false;
            if (_flowNeedsReload || _flowGraph is null && _flowReadError is null) await LoadFlowAsync();
        }
        else _restoreFlowTextTop = _textTopBeforeFlow;
    });

    /// <summary>Reads the ranges and observed traversals from one material snapshot without remounting the chart.</summary>
    private async Task LoadFlowAsync()
    {
        if (_displayedScope is not { } expected) return;
        _flowLoading = true;
        _flowReadError = null;
        StateHasChanged();
        try
        {
            var material = await Task.Run(() => MaterialReader.GetScenarioMaterialAsync(expected.RecordId, new()));
            var graph = await Task.Run(() => ScenarioRangeFlowProjector.Build(material));
            if (!await CanPublishAsync(expected)) return;
            _flowGraph = graph;
            _flowMatchingComplete = material.MatchingComplete;
            _flowNeedsReload = false;
        }
        catch (Exception ex) when (!_stop.IsCancellationRequested)
        {
            if (IsDisplayed(expected)) _flowReadError = ex.Message;
        }
        finally
        {
            if (IsDisplayed(expected)) _flowLoading = false;
        }
    }

    /// <summary>Opens the chosen saved range or one read-only observed interval in the existing text surface.</summary>
    private Task OpenFlowRangeAsync(DisplayedScope expected, ScenarioRangeFlowNode node) => RunAsync(async () =>
    {
        if (!_flowOpen || _flowLoading || _flowReadError is not null ||
            _flowGraph?.Nodes.Contains(node) != true) return;
        await RequireCurrentScopeAsync(expected);

        if (node.SavedRangeId is { } id)
        {
            var ranges = await Store.GetCaptureRangesAsync(expected.RecordId);
            if (!await CanPublishAsync(expected)) return;
            if (!ranges.Any(range => range.Id == id))
                throw new InvalidOperationException("区間が見つかりません。フローチャートを更新してください。");
            _ranges = ranges;
        }

        // The explicit open replaces the text query. Plain tab switching leaves it intact.
        ClearTextRange();
        CloseEditors();
        ClearDetail();
        DiscardCorrection();
        _rangeId = node.SavedRangeId ?? 0;
        _observedTextInterval = node.SavedRangeId is null ? new(expected.RecordId, node.Start, node.End) : null;
        _filter = "all";
        _textSearchInput = _textSearch = _speakerSearchInput = _speakerSearch = "";
        _following = false;
        _offset = 0;
        _pageCeiling = null;
        _selected.Clear();
        await LoadItemsAsync();
        if (!await CanPublishAsync(expected)) return;
        _flowOpen = false;
        _textTopBeforeFlow = _restoreFlowTextTop = null;
        _scrollToEnd = false;
        _focusFeed = _itemsError is null;
    }, failureLabel: "区間の本文取得失敗");
}
