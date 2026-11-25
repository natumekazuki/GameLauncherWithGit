using GameLauncherWithGit.Infrastructure.Abstractions;
using GameLauncherWithGit.Infrastructure.Models;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace GameLauncherWithGit.Infrastructure.Services;

public sealed class GitService : IGitService
{
	private static readonly Version MinimumGitVersion = new(2, 38, 0);
	private readonly ILogAccessService _logAccessService;
	private readonly ILogger<GitService> _logger;

	public GitService(ILogAccessService logAccessService, ILogger<GitService> logger)
	{
		_logAccessService = logAccessService;
		_logger = logger;
	}

	public async Task<GitCommandResult> CheckAvailabilityAsync(CancellationToken cancellationToken = default)
	{
		string[] arguments = ["--version"];
		var result = await ExecuteAsync(null, null, arguments, cancellationToken);
		if (result.IsSuccess)
		{
			var match = Regex.Match(result.StandardOutput, @"\bgit version (\d+)\.(\d+)\.(\d+)", RegexOptions.CultureInvariant);
			if (!match.Success || !Version.TryParse($"{match.Groups[1]}.{match.Groups[2]}.{match.Groups[3]}", out var version))
			{
				result = new GitCommandResult(-1, result.StandardOutput, result.StandardError,
					"Git のバージョンを確認できません。Git 2.38.0 以降をインストールしてください。");
			}
			else if (version < MinimumGitVersion)
			{
				result = new GitCommandResult(-1, result.StandardOutput, result.StandardError,
					$"Git {version} は対応していません。Git 2.38.0 以降に更新してください。");
			}
		}

		return await RecordFailureAsync(null, arguments, result);
	}

	public Task<GitCommandResult> RunAsync(
		string repositoryPath,
		IReadOnlyList<string> arguments,
		CancellationToken cancellationToken = default) =>
		RunInternalAsync(repositoryPath, arguments, cancellationToken);

	Task<GitCommandResult> IGitService.ReadCadencePushUrlsAsync(string repositoryPath, string remote, CancellationToken token) =>
		ReadCadenceUrlsAsync(repositoryPath, remote, push: true, token);

	Task<GitCommandResult> IGitService.ReadCadenceFetchUrlsAsync(string repositoryPath, string remote, CancellationToken token) =>
		ReadCadenceUrlsAsync(repositoryPath, remote, push: false, token);

	private Task<GitCommandResult> ReadCadenceUrlsAsync(string repositoryPath, string remote, bool push, CancellationToken token) =>
		RunInternalAsync(repositoryPath, push ? ["remote", "get-url", "--push", "--all", "--", remote]
			: ["remote", "get-url", "--all", "--", remote], token,
			protectedResult: result => result.IsSuccess ? new(0, result.StandardOutput, "") : CadenceEndpointFailure(result),
			safeArguments: ["remote get-url (cadence)"]);

	Task<GitCommandResult> IGitService.RunCadenceEndpointAsync(string repositoryPath, string resolvedUrl,
		string destinationRef, string? commit, CancellationToken token)
	{
		if (!IsCadenceRef(destinationRef, "refs/heads/")
			|| (commit is not null && !IsObjectName(commit)))
			return Task.FromResult(CadenceEndpointFailure(new(-1, "", "")));

		return RunAtCadenceEndpointAsync(repositoryPath, resolvedUrl, alias => commit is null
			? ["-c", "http.followRedirects=false", "ls-remote", "--refs", "--", alias, destinationRef]
			: ["-c", "http.followRedirects=false", "push", "--porcelain", "--no-force", "--no-mirror", "--no-tags",
				"--no-follow-tags", "--recurse-submodules=no", "--", alias, $"{commit}:{destinationRef}"],
			result => ReadCadenceEndpointResult(result, destinationRef, commit),
			commit is null ? "ls-remote (cadence)" : "push (cadence)", token);
	}

	Task<GitCommandResult> IGitService.FetchCadenceTrackingAsync(string repositoryPath, string resolvedUrl,
		string remoteRef, string trackingRef, CancellationToken token)
	{
		if (!IsCadenceRef(remoteRef, "refs/heads/") || !IsCadenceRef(trackingRef, "refs/remotes/"))
			return Task.FromResult(CadenceEndpointFailure(new(-1, "", "")));

		return RunAtCadenceEndpointAsync(repositoryPath, resolvedUrl, alias =>
			["-c", "http.followRedirects=false", "-c", "fetch.writeCommitGraph=false", "fetch", "--no-tags", "--no-prune",
				"--no-prune-tags", "--no-recurse-submodules", "--no-write-fetch-head", "--no-auto-maintenance",
				"--refmap=", "--", alias, $"{remoteRef}:{trackingRef}"],
			result => result.IsSuccess ? new(0, "", "") : CadenceEndpointFailure(result),
			"fetch tracking (cadence)", token, requirePhysicalRoot: true);
	}

	internal const string CadenceEmbeddedCredentialsMessage =
		"HTTP(S) URL に認証情報を含めたまま自動同期できません。URL から認証情報を外し、Git Credential Helper などを設定してください。";

	internal static bool HasCadenceEmbeddedHttpCredentials(string url)
	{
		if (!IsCadenceHttpUrl(url)) return false;
		var authority = url.AsSpan(url.IndexOf("://", StringComparison.Ordinal) + 3);
		var end = authority.IndexOfAny("/?#".AsSpan());
		return (end < 0 ? authority : authority[..end]).Contains('@')
			|| (Uri.TryCreate(url, UriKind.Absolute, out var parsed) && !string.IsNullOrEmpty(parsed.UserInfo));
	}

	private static bool IsCadenceHttpUrl(string url) => url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
		|| url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

	async Task<GitCommandResult> IGitService.RunCadenceIncomingAsync(string repositoryPath, string? remote, string? mergeTarget,
		Func<bool>? mayStart, CancellationToken token)
	{
		var isPull = mergeTarget is not null;
		var commandName = isPull ? "pull (cadence)" : "fetch (cadence)";
		Task<GitCommandResult> Read(string[] arguments) => RunInternalAsync(repositoryPath, arguments, token,
			protectedResult: value => value.IsSuccess ? new(0, value.StandardOutput, "") : new(value.ExitCode, "", ""),
			safeArguments: ["read incoming Git configuration (cadence)"]);

		if (remote is null)
		{
			var selected = await Read(["symbolic-ref", "--quiet", "--short", "HEAD"]);
			token.ThrowIfCancellationRequested();
			var branch = selected.StandardOutput.Trim();
			if (selected.IsSuccess && branch.Length > 0)
			{
				var configured = await Read(["config", "--get", $"branch.{branch}.remote"]);
				token.ThrowIfCancellationRequested();
				if (configured.IsSuccess) remote = configured.StandardOutput.TrimEnd('\r', '\n');
			}
			remote = string.IsNullOrEmpty(remote) ? "origin" : remote;
		}

		// git fetch/pull may use the HTTP helper too. For a verified HTTP URL use a
		// one-shot alias, and never let the ordinary named path start an HTTP helper.
		var urls = IsCadenceHttpUrl(remote) ? new GitCommandResult(0, remote, "")
			: await ReadCadenceUrlsAsync(repositoryPath, remote, push: false, token);
		token.ThrowIfCancellationRequested();
		var candidates = urls.IsSuccess ? urls.StandardOutput.TrimEnd('\r', '\n').Split('\n') : [];
		if (candidates.Any(HasCadenceEmbeddedHttpCredentials))
			return new(-1, "", "", CadenceEmbeddedCredentialsMessage);

		if (candidates is [var verified] && IsCadenceHttpUrl(verified))
		{
			// Retain the named remote's ordinary fetch mapping so pull can still
			// find the upstream for rebase, without using its mutable URL.
			var options = new List<(string Key, string Value)>();
			if (!IsCadenceHttpUrl(remote))
			{
				foreach (var key in new[] { "fetch", "tagOpt", "prune", "pruneTags" })
				{
					var configured = await Read(["config", "--null", "--get-all", $"remote.{remote}.{key}"]);
					if (!configured.IsSuccess && configured.ExitCode != 1)
						return configured.ProtectCadenceIncoming(isPull);
					if (configured.StandardOutput.EndsWith('\0'))
						options.AddRange(configured.StandardOutput[..^1].Split('\0').Select(value => (key, value)));
				}
			}
			return await RunAtCadenceEndpointAsync(repositoryPath, verified, alias => isPull
				? ["-c", "http.followRedirects=false", "pull", "--rebase", "--autostash", "--", alias, mergeTarget!]
				: ["-c", "http.followRedirects=false", "fetch", "--", alias],
				result => result.ProtectCadenceIncoming(isPull), commandName, token,
				remoteOptions: options, mayStart: mayStart);
		}

		string[] command = isPull
			? ["-c", "protocol.http.allow=never", "-c", "protocol.https.allow=never", "pull", "--rebase", "--autostash", "--", remote, mergeTarget!]
			: ["-c", "protocol.http.allow=never", "-c", "protocol.https.allow=never", "fetch"];
		return await RunInternalAsync(repositoryPath, command, token,
			configure: start =>
			{
				if (start.Environment.TryGetValue("GIT_ALLOW_PROTOCOL", out var protocols) && protocols is not null)
				{
					start.Environment["GIT_ALLOW_PROTOCOL"] = string.Join(':', protocols.Split(':')
						.Where(protocol => protocol is not ("http" or "https")));
				}
			},
			protectedResult: result => result.ProtectCadenceIncoming(isPull), safeArguments: [commandName], mayStart: mayStart);
	}

	private static bool IsCadenceRef(string name, string prefix) =>
		name.StartsWith(prefix, StringComparison.Ordinal) && name.Length > prefix.Length
		&& name.IndexOfAny([':', '*', '?', '[', '\r', '\n', '\t', '\0']) < 0;

	private Task<GitCommandResult> RunAtCadenceEndpointAsync(string repositoryPath, string resolvedUrl,
		Func<string, string[]> buildCommand, Func<GitCommandResult, GitCommandResult> protectedResult,
		string safeCommand, CancellationToken token, bool requirePhysicalRoot = false,
		IReadOnlyList<(string Key, string Value)>? remoteOptions = null, Func<bool>? mayStart = null)
	{
		if (string.IsNullOrWhiteSpace(resolvedUrl) || resolvedUrl.IndexOfAny(['\r', '\n', '\t', '\0']) >= 0
			|| !IsSupportedCadenceTransport(resolvedUrl))
			return Task.FromResult(CadenceEndpointFailure(new(-1, "", "")));
		if (HasCadenceEmbeddedHttpCredentials(resolvedUrl))
			return Task.FromResult(new GitCommandResult(-1, "", "", CadenceEmbeddedCredentialsMessage));
		var alias = $"cadence-{Guid.NewGuid():N}";
		var marker = $"{alias}.invalid";
		return RunInternalAsync(repositoryPath, buildCommand(alias), token,
			configure: start => ConfigureCadenceEndpoint(start, alias, marker, resolvedUrl, remoteOptions),
			protectedResult: protectedResult, safeArguments: [safeCommand], requirePhysicalRoot: requirePhysicalRoot, mayStart: mayStart);
	}

	private static bool IsSupportedCadenceTransport(string url)
	{
		if (Regex.IsMatch(url, @"^[\w+.-]+::")) return false; // Do not start an arbitrary remote helper.
		var scheme = Regex.Match(url, @"^([A-Za-z][A-Za-z0-9+.-]*)://");
		return !scheme.Success || scheme.Groups[1].Value.ToLowerInvariant() is
			"file" or "http" or "https" or "ssh" or "git" or "git+ssh" or "ssh+git";
	}

	private static void ConfigureCadenceEndpoint(ProcessStartInfo start, string alias, string marker, string url,
		IReadOnlyList<(string Key, string Value)>? remoteOptions)
	{
		// Git rewrites the one-shot marker once. The resolved URL is never re-entered as a named remote or URL argument.
		var env = start.Environment;
		var count = env.TryGetValue("GIT_CONFIG_COUNT", out var configured) && !string.IsNullOrEmpty(configured)
			? int.Parse(configured, NumberStyles.None, CultureInfo.InvariantCulture) : 0;
		if (count < 0 || count > int.MaxValue - 3 - (remoteOptions?.Count ?? 0)) throw new InvalidOperationException();
		void Add(string key, string value)
		{
			var index = count++.ToString(CultureInfo.InvariantCulture);
			env["GIT_CONFIG_KEY_" + index] = key;
			env["GIT_CONFIG_VALUE_" + index] = value;
		}
		Add($"remote.{alias}.url", marker);
		Add($"remote.{alias}.pushurl", marker);
		Add($"url.{url}.insteadOf", marker);
		if (remoteOptions is not null)
			foreach (var option in remoteOptions) Add($"remote.{alias}.{option.Key}", option.Value);
		env["GIT_CONFIG_COUNT"] = count.ToString(CultureInfo.InvariantCulture);
	}

	private static GitCommandResult ReadCadenceEndpointResult(GitCommandResult result, string destination, string? commit)
	{
		if (!result.IsSuccess) return CadenceEndpointFailure(result);
		var lines = result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
		if (commit is null)
		{
			if (lines.Length == 0) return new(0, "", "");
			if (lines is [var line] && line.Split('\t') is [var tip, var target] && target == destination && IsObjectName(tip))
				return new(0, tip, "");
		}
		else
		{
			var statuses = lines.Where(line => line.Length > 1 && line[1] == '\t').ToArray();
			if (statuses is [var line] && line.Split('\t') is [var status, var refs, _]
				&& status is " " or "*" or "=" && refs == $"{commit}:{destination}")
				return new(0, "", "");
		}
		return CadenceEndpointFailure(new(-1, "", ""));
	}

	private static bool IsObjectName(string value) => value.Length is 40 or 64 && value.All(Uri.IsHexDigit);

	private static GitCommandResult CadenceEndpointFailure(GitCommandResult result) =>
		new(result.ExitCode, "", "", result.ExitCode == -2 ? "Git 実行がキャンセルされました。"
			: "送信先へのアクセスまたは反映を確認できません。Git の送信先設定・認証・リモートのブランチを確認してください。");

	private async Task<GitCommandResult> RunInternalAsync(
		string repositoryPath, IReadOnlyList<string> arguments, CancellationToken cancellationToken,
		Action<ProcessStartInfo>? configure = null, Func<GitCommandResult, GitCommandResult>? protectedResult = null,
		IReadOnlyList<string>? safeArguments = null, bool requirePhysicalRoot = false, Func<bool>? mayStart = null)
	{
		var loggedArguments = safeArguments ?? arguments;
		string root;
		try
		{
			cancellationToken.ThrowIfCancellationRequested();
			root = ResolveDirectoryPath(repositoryPath);
			if (requirePhysicalRoot && !string.Equals(repositoryPath, root,
				OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
				return CadenceEndpointFailure(new(-1, "", ""));
			if ((File.GetAttributes(root) & FileAttributes.Directory) == 0)
			{
				throw new DirectoryNotFoundException($"ディレクトリではありません: {root}");
			}

			File.GetAttributes(Path.Combine(root, ".git"));
		}
		catch (OperationCanceledException)
		{
			return new GitCommandResult(-2, string.Empty, "Git 実行がキャンセルされました。");
		}
		catch (UnauthorizedAccessException ex)
		{
			return await RecordFailureAsync(repositoryPath, loggedArguments, new GitCommandResult(-1, string.Empty, ex.ToString(),
				"リポジトリへのアクセスが拒否されました。アクセス権を確認してください。"));
		}
		catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
		{
			return await RecordFailureAsync(repositoryPath, loggedArguments, new GitCommandResult(-1, string.Empty, ex.ToString(),
				"Git リポジトリを確認できません。.git のある作業ツリーのルートを選択してください。"));
		}
		catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
		{
			return await RecordFailureAsync(repositoryPath, loggedArguments, new GitCommandResult(-1, string.Empty, ex.ToString(),
				"リポジトリのパスを確認できません。フォルダとアクセス権を確認してください。"));
		}

		string[] rootArguments = ["rev-parse", "--show-toplevel"];
		var rootResult = await ExecuteAsync(root, root, rootArguments, cancellationToken);
		if (!rootResult.IsSuccess)
		{
			if (rootResult.StandardError.Contains("dubious ownership", StringComparison.OrdinalIgnoreCase))
			{
				var availability = await CheckAvailabilityAsync(cancellationToken);
				if (availability.ExitCode == -2)
				{
					return availability;
				}

				if (!availability.IsSuccess)
				{
					rootResult = rootResult with { UserMessage = availability.GetFailureReason() };
				}
			}

			return await RecordFailureAsync(repositoryPath, rootArguments, rootResult);
		}

		try
		{
			var actualRoot = ResolveDirectoryPath(rootResult.StandardOutput.Trim());
			var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
			if (!string.Equals(root, actualRoot, comparison))
			{
				return await RecordFailureAsync(repositoryPath, rootArguments, new GitCommandResult(-1, rootResult.StandardOutput,
					$"Selected directory: {root}{Environment.NewLine}Git worktree root: {actualRoot}",
					"選択したフォルダと Git の作業ツリーのルートが一致しません。ルートを選択し直してください。"));
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
		{
			return await RecordFailureAsync(repositoryPath, rootArguments, new GitCommandResult(-1, rootResult.StandardOutput, ex.ToString(),
				"Git の作業ツリーのルートを確認できません。フォルダとアクセス権を確認してください。"));
		}

		var result = await ExecuteAsync(root, root, arguments, cancellationToken, configure, safeArguments, mayStart);
		var reported = protectedResult?.Invoke(result) ?? result;
		return await RecordFailureAsync(repositoryPath, loggedArguments, reported,
			recordEmptyFailure: protectedResult is not null && reported.UserMessage is not null);
	}

	internal static string ResolveDirectoryPath(string path)
	{
		var fullPath = Path.GetFullPath(path);
		var pathRoot = Path.GetPathRoot(fullPath) ?? throw new ArgumentException("パスのルートを確認できません。", nameof(path));
		var currentPath = pathRoot;
		foreach (var part in fullPath[pathRoot.Length..].Split(
			[Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
		{
			currentPath = Path.Combine(currentPath, part);
			var target = new DirectoryInfo(currentPath).ResolveLinkTarget(returnFinalTarget: true);
			if (target is not null)
			{
				currentPath = target.FullName;
			}
		}

		return Path.TrimEndingDirectorySeparator(Path.GetFullPath(currentPath));
	}

	private async Task<GitCommandResult> ExecuteAsync(
		string? workingDirectory,
		string? safeDirectory,
		IReadOnlyList<string> arguments,
		CancellationToken cancellationToken,
		Action<ProcessStartInfo>? configure = null,
		IReadOnlyList<string>? safeArguments = null, Func<bool>? mayStart = null)
	{
		var startInfo = new ProcessStartInfo
		{
			FileName = "git",
			WorkingDirectory = workingDirectory ?? AppContext.BaseDirectory,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8,
			UseShellExecute = false,
			CreateNoWindow = true
		};
		if (safeDirectory is not null)
		{
			startInfo.ArgumentList.Add("-c");
			startInfo.ArgumentList.Add($"safe.directory={safeDirectory.Replace('\\', '/')}");
		}

		foreach (var argument in arguments)
		{
			startInfo.ArgumentList.Add(argument);
		}

		using var process = new Process { StartInfo = startInfo };
		try
		{
			cancellationToken.ThrowIfCancellationRequested();
			configure?.Invoke(startInfo);
			if (mayStart?.Invoke() == false) return new(0, "", "");
			_logger.LogInformation("Git command start. cwd={RepositoryPath}, args={Arguments}", workingDirectory, safeArguments ?? arguments);
			process.Start();
			var readStdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
			var readStdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);
			await process.WaitForExitAsync(cancellationToken);
			var standardOutput = await readStdOutTask;
			var standardError = await readStdErrTask;
			_logger.LogInformation("Git command end. cwd={RepositoryPath}, args={Arguments}, exitCode={ExitCode}",
				workingDirectory, safeArguments ?? arguments, process.ExitCode);
			return new GitCommandResult(process.ExitCode, standardOutput, standardError);
		}
		catch (OperationCanceledException)
		{
			try
			{
				if (!process.HasExited)
				{
					process.Kill(entireProcessTree: true);
				}
			}
			catch (InvalidOperationException)
			{
			}

			return new GitCommandResult(-2, string.Empty, "Git 実行がキャンセルされました。");
		}
		catch (Exception ex)
		{
			if (safeArguments is not null)
				return new(-1, "", "");
			_logger.LogError(ex, "Git command failed. cwd={RepositoryPath}, args={Arguments}", workingDirectory, arguments);
			return new GitCommandResult(-1, string.Empty, ex.ToString(),
				"Git の起動または実行に失敗しました。Git のインストールとアクセス権を確認してください。");
		}
	}

	private async Task<GitCommandResult> RecordFailureAsync(
		string? repositoryPath,
		IReadOnlyList<string> arguments,
		GitCommandResult result,
		bool recordEmptyFailure = false)
	{
		if (result.IsSuccess || result.ExitCode == -2
			|| (!recordEmptyFailure && result.ExitCode == 1
				&& string.IsNullOrWhiteSpace(result.StandardError) && string.IsNullOrWhiteSpace(result.StandardOutput)))
		{
			return result;
		}

		try
		{
			await _logAccessService.AppendGitErrorAsync(repositoryPath, arguments, result);
			return result with { UserMessage = $"{result.GetFailureReason()} 詳細は環境設定のログを確認してください。" };
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to record Git error. repositoryPath={RepositoryPath}", repositoryPath);
			return result with { UserMessage = $"{result.GetFailureReason()} Git の詳細をログに保存できませんでした。" };
		}
	}
}
