using GameLauncherWithGit.Infrastructure.Abstractions;
using GameLauncherWithGit.Infrastructure.Models;
using Microsoft.Extensions.Logging;

namespace GameLauncherWithGit.Infrastructure.Services;

public sealed class GitService : IGitService
{
	private readonly ILogger<GitService> _logger;

	public GitService(ILogger<GitService> logger)
	{
		_logger = logger;
	}

	public Task<GitCommandResult> RunAsync(
		string repositoryPath,
		string arguments,
		CancellationToken cancellationToken = default)
	{
		_logger.LogInformation(
			"Git command placeholder invoked. repositoryPath={RepositoryPath}, arguments={Arguments}",
			repositoryPath,
			arguments);

		return Task.FromResult(new GitCommandResult(0, "placeholder", string.Empty));
	}
}
