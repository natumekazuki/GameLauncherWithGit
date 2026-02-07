using GameLauncherWithGit.Application.Models;
using Microsoft.Extensions.Logging;

namespace GameLauncherWithGit.Infrastructure.Abstractions;

public interface ILogAccessService
{
	Task AppendErrorAsync(string message, CancellationToken cancellationToken = default);

	Task MaintainLogFilesAsync(CancellationToken cancellationToken = default);

	Task<IReadOnlyList<LogViewerEntry>> GetLatestEntriesAsync(
		int limit,
		LogLevel? severity = null,
		string? keyword = null,
		CancellationToken cancellationToken = default);

	Task OpenLatestErrorLogAsync(CancellationToken cancellationToken = default);

	Task OpenLogDirectoryAsync(CancellationToken cancellationToken = default);
}
