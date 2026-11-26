namespace GameLauncherWithGit.Application.Models;

/// <summary>A work's legacy history or the history for one repository and local branch.</summary>
/// <remarks>The legacy scope uses the profile ID as its record ID and has no repository key or branch ref.</remarks>
public sealed record ScenarioHistoryScope(long RecordId, long ProfileId, string? RepositoryKey, string? BranchRef);
