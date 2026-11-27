using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Infrastructure.Abstractions;
using Microsoft.AspNetCore.Components;

namespace GameLauncherWithGit.Components.Pages;

public partial class Capture
{
    [SupplyParameterFromQuery(Name = "gameId")] public string? RequestedGameId { get; set; }
    [Inject] private IGameLibraryStore Library { get; set; } = default!;

    private bool _routeLoaded, _followPlaySelection;
    private string? _loadedGameId;
    private GameCardItem? _requestedGame;
    private bool InvalidGameId => _loadedGameId is not null && _requestedGame is null;
    private string? SelectedGameId => _profile?.GameId ?? (_profileId == 0 ? _requestedGame?.Id : null);
    private string? SelectedGameTitle => _profile?.Title ?? (_profileId == 0 ? _requestedGame?.Title : null);
    private bool ViewingLiveRecord => TracksCurrentScope && _recordId != 0 && _playState is { } play &&
        play.GameId == SelectedGameId && play.Target.RecordId == _recordId &&
        _displayedScope?.Boundary.Play is { } boundary && boundary.Version == play.Target.ScopeVersion;
    private bool ViewingSetup => SelectedGameId is { } gameId && TracksCurrentScope &&
        (_snapshot.PlayGameId is null || _snapshot.PlayGameId == gameId) &&
        (!_snapshot.Active || _snapshot.Profile?.GameId == gameId);
    private bool CanPrepareRecording => ViewingSetup &&
        (!_snapshot.Active || _snapshot.Preview || _snapshot.NeedsProcess);

    /// <summary>Resolves explicit links on navigation, including query-only changes to the same page.</summary>
    protected override async Task OnParametersSetAsync()
    {
        _launchMessage = Navigation.HistoryEntryState;
        if (_routeLoaded && _loadedGameId == RequestedGameId) return;

        // Serialize scope changes with an in-flight poll or mutation; stale route loads must not win.
        var requested = RequestedGameId;
        while (_busy || _pollComplete is not null || (_routeLoaded && _loading))
        {
            await Task.Delay(20, _stop.Token);
            if (RequestedGameId != requested) return;
        }
        _loadedGameId = requested;
        _routeLoaded = true;
        await LoadInitialAsync();
    }

    /// <summary>Loads the linked registration without creating capture data or changing the play target.</summary>
    private async Task LoadInitialAsync()
    {
        _loading = true;
        _initialError = null;
        var requested = _loadedGameId;
        try
        {
            await ReadRecordingStateAsync();
            var profiles = await Store.GetCaptureProfilesAsync();
            var game = requested is null ? null : await Library.FindByIdAsync(requested, _stop.Token);
            if (requested != _loadedGameId || _stop.IsCancellationRequested) return;
            _requestedGame = game;
            _profiles = profiles;
            var play = CaptureService.CurrentPlayTarget;
            _followPlaySelection = requested is null && play.Profile is not null;
            // An invalid explicit ID must not inherit the active or first registered profile.
            _profileId = requested is null
                ? play.Profile?.Id ?? profiles.FirstOrDefault()?.Id ?? 0
                : game is null ? 0 : profiles.FirstOrDefault(profile => profile.GameId == game.Id)?.Id ?? 0;
            await LoadProfileAsync();
        }
        catch (Exception ex) when (!_stop.IsCancellationRequested)
        {
            _initialError = $"シナリオ画面を読み込めませんでした：{ex.Message}";
        }
        finally
        {
            if (requested == _loadedGameId) _loading = false;
        }
    }

    /// <summary>Switches only the history view back to the current play target.</summary>
    private Task ReturnToRecordingAsync(string gameId)
    {
        if (SelectedGameId == gameId && _profileId != 0)
            return ChangeHistoryScopeAsync(new() { Value = "0" });
        Navigation.NavigateTo($"/capture?gameId={Uri.EscapeDataString(gameId)}");
        return Task.CompletedTask;
    }
}
