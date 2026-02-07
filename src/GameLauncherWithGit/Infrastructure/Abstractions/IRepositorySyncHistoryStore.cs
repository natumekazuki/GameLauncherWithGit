using GameLauncherWithGit.Application.Models;

namespace GameLauncherWithGit.Infrastructure.Abstractions;

public interface IRepositorySyncHistoryStore
{
	Task AppendAsync(RepositorySyncHistoryItem entry, CancellationToken cancellationToken = default);

	Task<IReadOnlyDictionary<string, IReadOnlyList<RepositorySyncHistoryItem>>> GetLatestByRepositoryIdsAsync(
		IReadOnlyCollection<string> repositoryIds,
		int limitPerRepository,
		CancellationToken cancellationToken = default);
}
