using GameLauncherWithGit.Infrastructure.Models;

namespace GameLauncherWithGit.Infrastructure.Abstractions;

public interface IGitService
{
	Task<GitCommandResult> CheckAvailabilityAsync(CancellationToken cancellationToken = default);

	Task<GitCommandResult> RunAsync(
		string repositoryPath,
		IReadOnlyList<string> arguments,
		CancellationToken cancellationToken = default);

	internal Task<GitCommandResult> ReadCadencePushUrlsAsync(string repositoryPath, string remote, CancellationToken token) =>
		Task.FromResult(new GitCommandResult(-1, "", "", "Git の送信先設定を確認してください。"));

	internal Task<GitCommandResult> ReadCadenceFetchUrlsAsync(string repositoryPath, string remote, CancellationToken token) =>
		Task.FromResult(new GitCommandResult(-1, "", ""));

	// A null commit queries the advertised branch tip; otherwise it pushes exactly that commit to the branch.
	internal Task<GitCommandResult> RunCadenceEndpointAsync(string repositoryPath, string resolvedUrl,
		string destinationRef, string? commit, CancellationToken token) =>
		Task.FromResult(new GitCommandResult(-1, "", "", "Git の送信先設定を確認してください。"));

	// Fetches exactly one advertised branch into a verified, non-symbolic remote-tracking ref.
	internal Task<GitCommandResult> FetchCadenceTrackingAsync(string repositoryPath, string resolvedUrl,
		string remoteRef, string trackingRef, CancellationToken token) =>
		Task.FromResult(new GitCommandResult(-1, "", ""));
}
