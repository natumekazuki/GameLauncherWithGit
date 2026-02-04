namespace GameLauncherWithGit.App.Application.Models;

public sealed class GameItem
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Title { get; set; } = string.Empty;

    public string ExecutablePath { get; set; } = string.Empty;

    public string? ThumbnailPath { get; set; }

    public string? RelatedRepositoryPath { get; set; }

    public DateTimeOffset? LastPlayedAt { get; set; }

    public GameSyncStatus Status { get; set; } = GameSyncStatus.Unknown;
}
