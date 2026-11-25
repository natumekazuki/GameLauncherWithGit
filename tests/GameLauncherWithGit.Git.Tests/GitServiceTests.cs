using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Infrastructure.Abstractions;
using GameLauncherWithGit.Infrastructure.Models;
using GameLauncherWithGit.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace GameLauncherWithGit.Git.Tests;

public sealed class GitServiceTests : IDisposable
{
	private readonly string _directory = Path.Combine(Path.GetTempPath(), $"git-service-tests-{Guid.NewGuid():N}");
	private readonly Dictionary<string, string?> _originalEnvironment = [];
	private readonly List<string> _links = [];
	private readonly TestLog _log = new();
	private readonly GitService _service;

	public GitServiceTests()
	{
		Directory.CreateDirectory(_directory);
		SetEnvironment("GIT_CONFIG_NOSYSTEM", "1");
		SetEnvironment("GIT_CONFIG_GLOBAL", Path.Combine(_directory, "global-config"));
		SetEnvironment("GIT_TEST_ASSUME_DIFFERENT_OWNER", null);
		SetEnvironment("LC_ALL", "C");
		_service = new GitService(_log, NullLogger<GitService>.Instance);
	}

	// @test-value v2
	// kind = "invariant"
	// claim = "所有者不一致を模擬した日本語と空白を含む二つのリポジトリは、それぞれ対象自身だけを信頼して操作できる"
	// oracle = { type = "contract", ref = "https://github.com/natumekazuki/GameLauncherWithGit/issues/19" }
	// fault = "対象ルートを設定しない、または並行操作の設定を共有して別の対象を渡す"
	// observable = "各 status の終了結果と各 Git 実行時の safe.directory 値"
	// observation_boundary = "public-boundary"
	// scope = "GitService.RunAsync のリポジトリ別実行"
	// lifecycle = "permanent"
	// impact = "登録済みリポジトリの同期失敗や別対象への信頼範囲拡大を防ぐ"
	// distinction = "build は実 Git の所有者チェックと並行実行時の設定を検証できない"
	// @end-test-value
	[Fact]
	public async Task DifferentOwnersUseOnlyTheirOwnRoot()
	{
		var first = CreateRepository("日本語 の保存 1");
		var second = CreateRepository("日本語 の保存 2");
		SetEnvironment("GIT_TEST_ASSUME_DIFFERENT_OWNER", "1");

		var statuses = await Task.WhenAll(
			_service.RunAsync(first, ["status", "--porcelain"]),
			_service.RunAsync(second, ["status", "--porcelain"]));
		Assert.All(statuses, result => Assert.True(result.IsSuccess, result.GetFailureReason()));

		var values = await Task.WhenAll(
			_service.RunAsync(first, ["config", "--get-all", "safe.directory"]),
			_service.RunAsync(second, ["config", "--get-all", "safe.directory"]));
		Assert.Equal(first.Replace('\\', '/'), values[0].StandardOutput.Trim());
		Assert.Equal(second.Replace('\\', '/'), values[1].StandardOutput.Trim());
		Assert.False(File.Exists(Path.Combine(_directory, "global-config")));
	}

	// @test-value v2
	// kind = "invariant"
	// claim = "選択した子フォルダやリポジトリでないフォルダを親リポジトリとして操作せず、ルートの再選択を案内する"
	// oracle = { type = "contract", ref = "https://github.com/natumekazuki/GameLauncherWithGit/issues/19" }
	// fault = "選択されたフォルダから親へ探索し、親を暗黙に信頼する"
	// observable = "子フォルダと通常フォルダの実行結果および利用者向け説明"
	// observation_boundary = "public-boundary"
	// scope = "GitService.RunAsync の対象検証"
	// lifecycle = "permanent"
	// impact = "利用者が指定していない親のリポジトリを変更することを防ぐ"
	// distinction = "実 Git の親探索を伴うため静的な型検査や build では検出できない"
	// @end-test-value
	[Fact]
	public async Task NonRootDirectoriesAreRejected()
	{
		var root = CreateRepository("parent");
		var child = Directory.CreateDirectory(Path.Combine(root, "child")).FullName;
		var unrelated = Directory.CreateDirectory(Path.Combine(_directory, "unrelated")).FullName;
		SetEnvironment("GIT_TEST_ASSUME_DIFFERENT_OWNER", "1");

		foreach (var path in new[] { child, unrelated })
		{
			var result = await _service.RunAsync(path, ["status"]);
			Assert.False(result.IsSuccess);
			Assert.Contains("ルートを選択", result.GetFailureReason());
		}
	}

	// @test-value v2
	// kind = "invariant"
	// claim = "gitfile を持つ worktree とそのリンク経由のルートは所有者不一致時も個別に操作できる"
	// oracle = { type = "contract", ref = "https://github.com/natumekazuki/GameLauncherWithGit/issues/19" }
	// fault = ".git をディレクトリに限定する、またはリンク経由の worktree の信頼対象を誤る"
	// observable = "実 worktree とリンク経由での status の結果"
	// observation_boundary = "public-boundary"
	// scope = "GitService.RunAsync の gitfile 対応"
	// lifecycle = "permanent"
	// impact = "既存の worktree を関連リポジトリとして登録・同期できなくなる"
	// distinction = "通常の .git ディレクトリだけの検査や build では gitfile を検証できない"
	// @end-test-value
	[Fact]
	public async Task GitFileWorktreeIsAccepted()
	{
		var root = CreateRepository("main");
		RunGit(root, "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "--allow-empty", "-m", "initial");
		var worktree = Path.Combine(_directory, "別 worktree");
		RunGit(root, "worktree", "add", "--detach", worktree);
		Assert.True(File.Exists(Path.Combine(worktree, ".git")));
		var link = Path.Combine(_directory, "リンク");
		CreateDirectoryAlias(link, worktree);
		SetEnvironment("GIT_TEST_ASSUME_DIFFERENT_OWNER", "1");

		foreach (var path in new[] { worktree, link })
		{
			var result = await _service.RunAsync(path, ["status", "--porcelain"]);
			Assert.True(result.IsSuccess, result.GetFailureReason());
		}
	}

	private void CreateDirectoryAlias(string link, string worktree)
	{
		if (OperatingSystem.IsWindows())
		{
			var command = $"New-Item -ItemType Junction -Path '{link.Replace("'", "''")}' -Target '{worktree.Replace("'", "''")}' | Out-Null";
			var info = new ProcessStartInfo("powershell.exe") { RedirectStandardError = true, CreateNoWindow = true };
			info.ArgumentList.Add("-NoProfile");
			info.ArgumentList.Add("-NonInteractive");
			info.ArgumentList.Add("-Command");
			info.ArgumentList.Add(command);
			using var process = Process.Start(info)!;
			var error = process.StandardError.ReadToEnd();
			process.WaitForExit();
			Assert.True(process.ExitCode == 0, error);
		}
		else
		{
			Directory.CreateSymbolicLink(link, worktree);
		}
		_links.Add(link);
	}

	// @test-value v2
	// kind = "invariant"
	// claim = "Git 引数の空白・日本語・引用符を保ち、失敗時には UTF-8 の標準エラー全文をログへ渡す"
	// oracle = { type = "contract", ref = "https://github.com/natumekazuki/GameLauncherWithGit/issues/19" }
	// fault = "コマンド文字列を空白分割する、または標準エラーを既定文字コードや先頭行だけで扱う"
	// observable = "Git が保存・返却した値と失敗結果およびログへ渡された標準エラー"
	// observation_boundary = "public-boundary"
	// scope = "GitService.RunAsync の引数とエラー"
	// lifecycle = "permanent"
	// impact = "引数破損による誤操作やエラー原因の判別不能を防ぐ"
	// distinction = "実プロセスの引数境界と出力バイト列は build や型検査では検証できない"
	// @end-test-value
	[Fact]
	public async Task ArgumentsAndCompleteUtf8ErrorsArePreserved()
	{
		var root = CreateRepository("引数 の試験");
		SetEnvironment("GIT_TEST_ASSUME_DIFFERENT_OWNER", "1");
		const string value = "日本語 space \"quoted\" \\ end";
		var set = await _service.RunAsync(root, ["config", "--local", "test.value", value]);
		var get = await _service.RunAsync(root, ["config", "--get", "test.value"]);
		Assert.True(set.IsSuccess, set.GetFailureReason());
		Assert.Equal(value, get.StandardOutput.TrimEnd('\r', '\n'));

		var failure = await _service.RunAsync(root, ["show", "存在しない 分岐"]);
		Assert.False(failure.IsSuccess);
		Assert.Contains("存在しない 分岐", failure.StandardError);
		Assert.Contains("Use '--'", failure.StandardError);
		var info = new ProcessStartInfo("git")
		{
			WorkingDirectory = root,
			RedirectStandardError = true,
			StandardErrorEncoding = Encoding.UTF8,
			CreateNoWindow = true
		};
		foreach (var argument in new[] { "-c", $"safe.directory={root.Replace('\\', '/')}", "show", "存在しない 分岐" })
		{
			info.ArgumentList.Add(argument);
		}
		using var process = Process.Start(info)!;
		var expectedError = await process.StandardError.ReadToEndAsync();
		await process.WaitForExitAsync();
		Assert.NotEqual(0, process.ExitCode);
		Assert.Equal(expectedError, failure.StandardError);
		Assert.Contains(_log.Results, logged => logged.StandardError == failure.StandardError);
	}

	// @test-value v2
	// kind = "invariant"
	// claim = "Git を起動できない場合は起動失敗を案内し、キャンセルは失敗として返す"
	// oracle = { type = "contract", ref = "https://github.com/natumekazuki/GameLauncherWithGit/issues/19" }
	// fault = "Git が見つからない場合やキャンセルを成功として扱う"
	// observable = "起動確認とキャンセル済みリポジトリ操作の終了結果と説明"
	// observation_boundary = "public-boundary"
	// scope = "GitService の起動確認とキャンセル"
	// lifecycle = "permanent"
	// impact = "同期できない環境を正常と誤認し、利用者が原因を把握できなくなる"
	// distinction = "実プロセスの起動失敗とキャンセルは build では検証できない"
	// @end-test-value
	[Fact]
	public async Task MissingGitAndCancellationAreNotSuccess()
	{
		var root = CreateRepository("cancel");
		using var cancellation = new CancellationTokenSource();
		cancellation.Cancel();
		var canceled = await _service.RunAsync(root, ["status"], cancellation.Token);
		Assert.False(canceled.IsSuccess);
		Assert.Equal(-2, canceled.ExitCode);

		SetEnvironment("PATH", _directory);
		var missing = await _service.CheckAvailabilityAsync();
		Assert.False(missing.IsSuccess);
		Assert.Contains("Git の起動", missing.GetFailureReason());
		Assert.Contains(_log.Results, logged => logged.ExitCode == missing.ExitCode);
	}

	// @test-value v2
	// kind = "invariant"
	// claim = "Git の所有者拒否、アクセス拒否、非リポジトリはそれぞれ異なる説明となり、終了コードだけでは原因を決めない"
	// oracle = { type = "contract", ref = "https://github.com/natumekazuki/GameLauncherWithGit/issues/19" }
	// fault = "終了コード 128 だけで所有者エラーと判断する、または全エラーを非リポジトリと説明する"
	// observable = "同じ終了コードで異なる標準エラーを持つ結果の利用者向け説明"
	// observation_boundary = "component-behavior"
	// scope = "GitCommandResult の失敗説明"
	// lifecycle = "permanent"
	// impact = "利用者が必要なアクセス権の確認やフォルダの再選択を判断できなくなる"
	// distinction = "同じ終了コードの異なる障害を型検査や build は識別できない"
	// @end-test-value
	[Fact]
	public void FailureReasonsUseTheErrorInsteadOfTheExitCode()
	{
		var ownership = new GitCommandResult(128, "", "fatal: detected dubious ownership");
		var permission = new GitCommandResult(128, "", "fatal: Permission denied");
		var notRepository = new GitCommandResult(128, "", "fatal: not a git repository");
		var unknown = new GitCommandResult(128, "", "fatal: another failure");
		Assert.Contains("所有者", ownership.GetFailureReason());
		Assert.Contains("アクセス", permission.GetFailureReason());
		Assert.Contains("リポジトリ", notRepository.GetFailureReason());
		Assert.Equal("fatal: another failure", unknown.GetFailureReason());
	}

	// @test-value v2
	// kind = "invariant"
	// claim = "実監視登録から期限済みの未送信を復元し、指定された別名の宛先枝だけを更新する"
	// oracle = { type = "contract", ref = "Session Graph game-sync-cadence-push-scope; game-sync-cadence-watch-lifetime" }
	// fault = "実監視登録の復元が未接続、tracking名を送信先にする、または暗黙タグ／別枝も送る"
	// observable = "実RepositoryWatcherService登録後の成功履歴と、隔離bareの全refの変更前後"
	// observation_boundary = "public-boundary"
	// scope = "production watcher-to-positive-cadence push against a temporary local bare repository"
	// lifecycle = "permanent"
	// @end-test-value
	[Fact]
	public async Task WatcherRegistrationRestoresAndSendsOnlyTheConfiguredDestination()
	{
		var (root, bare) = CreateCadenceRepositories();
		RunGit(root, "config", "--local", "remote.origin.push", "refs/heads/sample:refs/heads/destination");
		RunGit(root, "config", "--local", "push.followTags", "true");
		MakeOutgoingCommit(root);
		RunGit(root, "tag", "-a", "-m", "unsent tag", "local-only");
		var head = RunGit(root, "rev-parse", "HEAD").Trim();
		var before = Refs(bare);
		Assert.NotEqual(head, before.Single(line => line.StartsWith("refs/heads/destination ")).Split(' ')[1]);

		using var watcher = new RepositoryWatcherService(NullLogger<RepositoryWatcherService>.Instance);
		using var f = new SyncCadenceTests.Fixture(commit: 10, push: 1, repository: root, git: _service, watcher: watcher);
		watcher.Register(root, root);
		var history = await f.FirstHistory.Task.WaitAsync(TimeSpan.FromSeconds(15));
		watcher.Unregister(root);

		Assert.Equal(RepositorySyncHistoryStatus.Succeeded, history.Status);
		Assert.Equal(before.Select(line => line.StartsWith("refs/heads/destination ") ? $"refs/heads/destination {head}" : line), Refs(bare));
	}

	// @test-value v2
	// kind = "invariant"
	// claim = "送信範囲を安全に一枝へ限定できない設定は、隔離bareを変更せず失敗として返す"
	// oracle = { type = "contract", ref = "Session Graph game-sync-cadence-push-scope CAD-CORE-01" }
	// fault = "force・複数・matching・mirrorを実pushへ渡し、実行後にだけ結果を判定する"
	// observable = "新送信経路での失敗履歴の理由、隔離bareの全ref先端および存在が不変"
	// observation_boundary = "public-boundary"
	// scope = "preflight refusal through SyncOrchestrator with real Git"
	// lifecycle = "permanent"
	// @end-test-value
	[Theory]
	[InlineData("force")]
	[InlineData("multiple")]
	[InlineData("matching")]
	[InlineData("mirror")]
	public async Task UnsafePushConfigurationCannotChangeAnyRemoteRef(string kind)
	{
		var (root, bare) = CreateCadenceRepositories();
		MakeOutgoingCommit(root);
		RunGit(root, "branch", "-f", "other", "HEAD");
		RunGit(root, "tag", "-a", "-m", "unsent tag", "local-only");
		switch (kind)
		{
			case "force":
				RunGit(root, "config", "--local", "remote.origin.push", "+refs/heads/sample:refs/heads/destination");
				break;
			case "multiple":
				RunGit(root, "config", "--local", "--add", "remote.origin.push", "refs/heads/sample:refs/heads/destination");
				RunGit(root, "config", "--local", "--add", "remote.origin.push", "refs/heads/other:refs/heads/other");
				break;
			case "matching":
				RunGit(root, "config", "--local", "push.default", "matching");
				break;
			case "mirror":
				RunGit(root, "config", "--local", "remote.origin.mirror", "true");
				break;
		}
		var before = Refs(bare);
		var git = new RecordingGit(_service);
		using var f = new SyncCadenceTests.Fixture(commit: 1, push: 1, repository: root, git: git);
		Assert.True(f.Sync.TryQueueRepositorySyncNow(root));
		var history = await f.FirstHistory.Task.WaitAsync(TimeSpan.FromSeconds(15));

		Assert.Equal(RepositorySyncHistoryStatus.Failed, history.Status);
		Assert.Contains("送信先設定", history.Reason);
		Assert.DoesNotContain("push", git.Commands);
		Assert.Equal(before, Refs(bare));
	}

	// @test-value v2
	// kind = "invariant"
	// claim = "単一宛先が決まっていても分岐したremote枝を強制更新しない"
	// oracle = { type = "contract", ref = "Session Graph game-sync-cadence-push-scope CAD-CORE-01" }
	// fault = "非fast-forwardを強制送信する、または拒否を成功として記録する"
	// observable = "実Gitでのpush実行と失敗履歴、隔離bareの全refの不変"
	// observation_boundary = "public-boundary"
	// scope = "non-force push through SyncOrchestrator"
	// lifecycle = "permanent"
	// @end-test-value
	[Fact]
	public async Task ExplicitPushDoesNotOverwriteADivergentDestination()
	{
		var (root, bare) = CreateCadenceRepositories();
		RunGit(root, "config", "--local", "remote.origin.push", "refs/heads/sample:refs/heads/destination");
		RunGit(root, "switch", "other");
		RunGit(root, "commit", "--allow-empty", "-m", "destination advance");
		RunGit(root, "push", "origin", "refs/heads/other:refs/heads/destination");
		RunGit(root, "switch", "sample");
		MakeOutgoingCommit(root);
		var before = Refs(bare);
		var git = new RecordingGit(_service);
		using var f = new SyncCadenceTests.Fixture(commit: 1, push: 1, repository: root, git: git);
		Assert.True(f.Sync.TryQueueRepositorySyncNow(root));
		var history = await f.FirstHistory.Task.WaitAsync(TimeSpan.FromSeconds(15));
		Assert.Contains("push", git.Commands);
		Assert.Equal(RepositorySyncHistoryStatus.Failed, history.Status);
		Assert.Equal(before, Refs(bare));
	}

	// @test-value v2
	// kind = "invariant"
	// claim = "検証後に名前付きremoteのpushurlが置換・追加されても、解決済みの単一宛先にだけ送信する"
	// oracle = { type = "contract", ref = "Session Graph game-sync-cadence-push-scope CAD-CORE-01" }
	// fault = "送信時にremote名を再解決する、または検証済みURLにinsteadOfを再適用する"
	// observable = "送信呼出し直前の合成config変更、元bareの対象枝更新、置換bare・追加bareの全ref不変"
	// observation_boundary = "public-boundary"
	// scope = "real pinned Git transport after preflight, including chained URL rewrite"
	// lifecycle = "permanent"
	// @end-test-value
	[Fact]
	public async Task VerifiedEndpointSurvivesConcurrentPushUrlReplacementAndAddition()
	{
		var (root, original) = CreateCadenceRepositories();
		var replacement = CreateBare("replacement.git");
		var additional = CreateBare("additional.git");
		MakeOutgoingCommit(root);
		var head = RunGit(root, "rev-parse", "HEAD").Trim();
		const string unresolved = "fixture-remote-alias";
		RunGit(root, "remote", "set-url", "origin", unresolved);
		RunGit(root, "config", "--local", "remote.origin.pushurl", unresolved);
		RunGit(root, "config", "--local", $"url.{original}.insteadOf", unresolved);
		RunGit(root, "config", "--local", $"url.{additional}.insteadOf", original);
		Assert.Equal(original, RunGit(root, "remote", "get-url", "--push", "origin").Trim());
		var git = new RecordingGit(_service)
		{
			BeforeEndpointSend = () =>
			{
				RunGit(root, "config", "--local", "--replace-all", "remote.origin.pushurl", replacement);
				RunGit(root, "config", "--local", "--add", "remote.origin.pushurl", additional);
			}
		};
		var originalRefs = Refs(original);
		var replacementRefs = Refs(replacement);
		var additionalRefs = Refs(additional);
		using var f = new SyncCadenceTests.Fixture(commit: 1, push: 1, repository: root, git: git);
		Assert.True(f.Sync.TryQueueRepositorySyncNow(root));
		var history = await f.FirstHistory.Task.WaitAsync(TimeSpan.FromSeconds(20));

		Assert.Single(git.Commands, command => command == "push");
		Assert.Equal(RepositorySyncHistoryStatus.Succeeded, history.Status);
		Assert.Equal(originalRefs.Select(line => line.StartsWith("refs/heads/sample ") ? $"refs/heads/sample {head}" : line), Refs(original));
		Assert.Equal(replacementRefs, Refs(replacement));
		Assert.Equal(additionalRefs, Refs(additional));
	}

	// @test-value v2
	// kind = "invariant"
	// claim = "同一remote名の送信先を移すと、取得先も一緒に移した場合を含め古いtrackingの一致で送信済みとしない"
	// oracle = { type = "contract", ref = "Session Graph game-sync-cadence-push-scope CAD-CORE-01" }
	// fault = "tracking refがHEADと等しいか新しいfetch/push URL同士が一致するだけで、新宛先の未送信を取り残す"
	// observable = "監視登録後の成功履歴、fetch側の全ref不変とpush側の宛先枝新設"
	// observation_boundary = "public-boundary"
	// scope = "separate Git fetch and push endpoints"
	// lifecycle = "permanent"
	// @end-test-value
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task EqualFetchTrackingDoesNotHideADifferentPushEndpoint(bool moveFetchUrlToo)
	{
		var (root, fetch) = CreateCadenceRepositories();
		var push = CreateBare("separate-push.git");
		if (moveFetchUrlToo) RunGit(root, "remote", "set-url", "origin", push);
		RunGit(root, "remote", "set-url", "--push", "origin", push);
		var head = RunGit(root, "rev-parse", "HEAD").Trim();
		Assert.Equal(head, RunGit(root, "rev-parse", "refs/remotes/origin/sample").Trim());
		var fetchRefs = Refs(fetch);

		using var watcher = new RepositoryWatcherService(NullLogger<RepositoryWatcherService>.Instance);
		using var f = new SyncCadenceTests.Fixture(commit: 1, push: 0, repository: root, git: _service, watcher: watcher);
		watcher.Register(root, root);
		var history = await f.FirstHistory.Task.WaitAsync(TimeSpan.FromSeconds(15));
		Assert.Equal(RepositorySyncHistoryStatus.Succeeded, history.Status);
		Assert.Equal(fetchRefs, Refs(fetch));
		Assert.Equal(new[] { $"refs/heads/sample {head}" }, Refs(push));
	}

	// @test-value v2
	// kind = "invariant"
	// claim = "確定宛先の照会・送信失敗は、Gitの機械的な原因だけを返して生のendpoint値をログへ渡さない"
	// oracle = { type = "contract", ref = "Session Graph game-sync-cadence-push-scope Turn 9093" }
	// fault = "URLをコマンド引数に入れる、または失敗の生出力・リモート名をログや利用者向け失敗へ流す"
	// observable = "合成ローカルパス・remote名での実Git失敗結果、引数ログ、詳細ログ、同期失敗履歴と通知"
	// observation_boundary = "public-boundary"
	// scope = "protected Git endpoint output and diagnostics"
	// lifecycle = "permanent"
	// @end-test-value
	[Fact]
	public async Task EndpointFailuresDoNotExposeRawGitArgumentsOrOutput()
	{
		var (root, _) = CreateCadenceRepositories();
		const string marker = "synthetic-auth-marker";
		var missing = Path.Combine(_directory, marker + ".git");
		SetEnvironment("GIT_CONFIG_COUNT", "1");
		SetEnvironment("GIT_CONFIG_KEY_0", "user.name");
		SetEnvironment("GIT_CONFIG_VALUE_0", "Fixture");
		var logger = new CapturedGitLogger();
		var errorLog = new TestLog();
		IGitService git = new GitService(errorLog, logger);
		var head = RunGit(root, "rev-parse", "HEAD").Trim();
		const string destination = "refs/heads/sample";
		var failures = new[]
		{
			await git.ReadCadencePushUrlsAsync(root, marker, CancellationToken.None),
			await git.RunCadenceEndpointAsync(root, missing, destination, null, CancellationToken.None),
			await git.RunCadenceEndpointAsync(root, missing, destination, head, CancellationToken.None)
		};
		Assert.Equal(3, errorLog.Results.Count);
		Assert.All(failures.Concat(errorLog.Results), failure =>
		{
			Assert.False(failure.IsSuccess);
			Assert.Empty(failure.StandardOutput);
			Assert.Empty(failure.StandardError);
			Assert.DoesNotContain(marker, failure.GetFailureReason());
		});
		Assert.DoesNotContain(logger.Lines, line => line.Contains(marker, StringComparison.Ordinal));
		Assert.DoesNotContain(errorLog.Arguments.SelectMany(args => args), arg => arg.Contains(marker, StringComparison.Ordinal));
		Assert.Equal("1", Environment.GetEnvironmentVariable("GIT_CONFIG_COUNT"));
		Assert.Equal("user.name", Environment.GetEnvironmentVariable("GIT_CONFIG_KEY_0"));
		Assert.Equal("Fixture", Environment.GetEnvironmentVariable("GIT_CONFIG_VALUE_0"));

		RunGit(root, "remote", "set-url", "--push", "origin", missing);
		using var f = new SyncCadenceTests.Fixture(commit: 1, push: 0, repository: root, git: git);
		f.Watch();
		var history = await f.FirstHistory.Task.WaitAsync(TimeSpan.FromSeconds(15));
		var notification = await f.FirstNotification.Task.WaitAsync(TimeSpan.FromSeconds(15));
		Assert.Equal(RepositorySyncHistoryStatus.Failed, history.Status);
		Assert.NotNull(history.Reason);
		Assert.DoesNotContain(marker, history.Reason);
		Assert.DoesNotContain(marker, notification);
		Assert.DoesNotContain(logger.Lines, line => line.Contains(marker, StringComparison.Ordinal));
		Assert.DoesNotContain(errorLog.Results, result => result.GetFailureReason().Contains(marker, StringComparison.Ordinal));
	}

	// @test-value v2
	// kind = "invariant"
	// claim = "Tray一括の対象集計は物理先の別名を一件にまとめ、同じobject庫の別worktreeを別件にする"
	// oracle = { type = "contract", ref = "Session Graph game-git-repository-identity-boundary" }
	// fault = "Trayが別名を重複計上する、またはリンクされた別worktreeを同一件として落とす"
	// observable = "TrayServiceが受付数算出前に使う実対象選択の返却パス"
	// observation_boundary = "component-behavior"
	// scope = "Tray sync target selection without native UI or notifications"
	// lifecycle = "permanent"
	// @end-test-value
	[Fact]
	public void TrayCountsAnAliasOnceButKeepsASeparateLinkedWorktree()
	{
		var (root, _) = CreateCadenceRepositories();
		var linked = Path.Combine(_directory, "linked-worktree");
		RunGit(root, "worktree", "add", "--detach", linked);
		var alias = Path.Combine(_directory, "physical-alias");
		CreateDirectoryAlias(alias, root);
		using var f = new SyncCadenceTests.Fixture(commit: 0, push: 0);
		var targetPaths = TraySyncTargets.GetDistinctRepositories(
			[f.Game("alias-only", alias), f.Game("real", root), f.Game("linked", linked)]);
		Assert.Equal(new[] { GitService.ResolveDirectoryPath(root), GitService.ResolveDirectoryPath(linked) }, targetPaths);
	}

	private (string Root, string Bare) CreateCadenceRepositories()
	{
		var root = CreateRepository("cadence");
		var bare = CreateBare("cadence.git");
		RunGit(root, "symbolic-ref", "HEAD", "refs/heads/sample");
		RunGit(root, "config", "--local", "user.name", "Fixture");
		RunGit(root, "config", "--local", "user.email", "fixture@example.invalid");
		RunGit(root, "commit", "--allow-empty", "-m", "base");
		RunGit(root, "branch", "other");
		RunGit(root, "remote", "add", "origin", bare);
		RunGit(root, "push", "--set-upstream", "origin", "sample");
		RunGit(root, "push", "origin", "sample:refs/heads/destination", "other");
		return (root, bare);
	}

	private string CreateBare(string name)
	{
		var bare = Directory.CreateDirectory(Path.Combine(_directory, name)).FullName;
		RunGit(bare, "init", "--bare", "--quiet");
		return bare;
	}

	private void MakeOutgoingCommit(string root)
	{
		SetEnvironment("GIT_COMMITTER_DATE", DateTimeOffset.UtcNow.AddDays(-1).ToString("O"));
		RunGit(root, "commit", "--allow-empty", "-m", "outgoing");
	}

	private static string[] Refs(string bare) =>
		RunGit(bare, "for-each-ref", "--format=%(refname) %(objectname)").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

	private string CreateRepository(string name)
	{
		var path = Directory.CreateDirectory(Path.Combine(_directory, name)).FullName;
		RunGit(path, "init", "--quiet");
		return path;
	}

	private static string RunGit(string directory, params string[] arguments)
	{
		var info = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
		foreach (var argument in arguments) info.ArgumentList.Add(argument);
		using var process = Process.Start(info)!;
		var output = process.StandardOutput.ReadToEnd();
		var error = process.StandardError.ReadToEnd();
		process.WaitForExit();
		Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)}: {output}\n{error}");
		return output;
	}

	private void SetEnvironment(string key, string? value)
	{
		_originalEnvironment.TryAdd(key, Environment.GetEnvironmentVariable(key));
		Environment.SetEnvironmentVariable(key, value);
	}

	public void Dispose()
	{
		foreach (var (key, value) in _originalEnvironment) Environment.SetEnvironmentVariable(key, value);
		foreach (var link in _links) Directory.Delete(link);
		var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
		foreach (var file in Directory.EnumerateFiles(_directory, "*", options))
		{
			File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
		}
		Directory.Delete(_directory, recursive: true);
	}

	private sealed class TestLog : ILogAccessService
	{
		public ConcurrentQueue<GitCommandResult> Results { get; } = new();
		public ConcurrentQueue<IReadOnlyList<string>> Arguments { get; } = new();
		public Task AppendGitErrorAsync(string? repositoryPath, IReadOnlyList<string> arguments, GitCommandResult result, CancellationToken cancellationToken = default)
		{
			Results.Enqueue(result);
			Arguments.Enqueue(arguments);
			return Task.CompletedTask;
		}
		public Task AppendErrorAsync(string message, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task MaintainLogFilesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IReadOnlyList<LogViewerEntry>> GetLatestEntriesAsync(int limit, LogLevel? severity = null, string? keyword = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task OpenLatestErrorLogAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task OpenLogDirectoryAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}

	private sealed class CapturedGitLogger : ILogger<GitService>
	{
		public List<string> Lines { get; } = [];
		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
		public bool IsEnabled(LogLevel logLevel) => true;
		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
			Func<TState, Exception?, string> formatter)
		{
			Lines.Add(formatter(state, exception));
			if (state is IEnumerable<KeyValuePair<string, object?>> fields)
				foreach (var values in fields.Select(field => field.Value).OfType<IReadOnlyList<string>>())
					Lines.AddRange(values);
		}
	}

	private sealed class RecordingGit(IGitService git) : IGitService
	{
		public ConcurrentQueue<string> Commands { get; } = new();
		public Action? BeforeEndpointSend { get; init; }
		public Task<GitCommandResult> CheckAvailabilityAsync(CancellationToken cancellationToken = default) =>
			git.CheckAvailabilityAsync(cancellationToken);
		public Task<GitCommandResult> RunAsync(string repositoryPath, IReadOnlyList<string> arguments,
			CancellationToken cancellationToken = default)
		{
			Commands.Enqueue(arguments[0]);
			return git.RunAsync(repositoryPath, arguments, cancellationToken);
		}
		Task<GitCommandResult> IGitService.ReadCadencePushUrlsAsync(string repositoryPath, string remote, CancellationToken token) =>
			git.ReadCadencePushUrlsAsync(repositoryPath, remote, token);
		Task<GitCommandResult> IGitService.RunCadenceEndpointAsync(string repositoryPath, string resolvedUrl,
			string destinationRef, string? commit, CancellationToken token)
		{
			if (commit is not null)
			{
				Commands.Enqueue("push");
				BeforeEndpointSend?.Invoke();
			}
			return git.RunCadenceEndpointAsync(repositoryPath, resolvedUrl, destinationRef, commit, token);
		}
	}
}
