using GameLauncherWithGit.Application.Models;

namespace GameLauncherWithGit.Infrastructure.Abstractions;

public interface IScenarioHistoryStore
{
    /// <summary>Returns the stable record ID for a profile, canonical repository key and full local branch ref.</summary>
    /// <remarks>The caller supplies the canonical key and a symbolic ref under refs/heads/. New histories start empty.</remarks>
    /// <exception cref="ArgumentException">The profile or branch identity is invalid.</exception>
    Task<long> GetOrCreateBranchRecordIdAsync(long profileId, string repositoryKey, string branchRef);

    /// <summary>Returns a profile's legacy history first, then its existing branch histories, without creating any.</summary>
    /// <exception cref="ArgumentException">The ID does not identify a capture profile.</exception>
    Task<IReadOnlyList<ScenarioHistoryScope>> GetScenarioHistoryScopesAsync(long profileId);
}
