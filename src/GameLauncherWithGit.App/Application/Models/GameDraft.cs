namespace GameLauncherWithGit.App.Application.Models;

public sealed class GameDraft
{
    public string Title { get; set; } = string.Empty;

    public string ExecutablePath { get; set; } = string.Empty;

    public string? ThumbnailSourcePath { get; set; }

    public string? RelatedRepositoryPath { get; set; }
}
