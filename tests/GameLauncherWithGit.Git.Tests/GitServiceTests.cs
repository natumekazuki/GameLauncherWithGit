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
		SetEnvironment("GIT_TEST_ASSUME_DIFFERENT_OWNER", "1");

		foreach (var path in new[] { worktree, link })
		{
			var result = await _service.RunAsync(path, ["status", "--porcelain"]);
			Assert.True(result.IsSuccess, result.GetFailureReason());
		}
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

	private string CreateRepository(string name)
	{
		var path = Directory.CreateDirectory(Path.Combine(_directory, name)).FullName;
		RunGit(path, "init", "--quiet");
		return path;
	}

	private static void RunGit(string directory, params string[] arguments)
	{
		var info = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
		foreach (var argument in arguments) info.ArgumentList.Add(argument);
		using var process = Process.Start(info)!;
		var output = process.StandardOutput.ReadToEnd();
		var error = process.StandardError.ReadToEnd();
		process.WaitForExit();
		Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)}: {output}\n{error}");
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
		public Task AppendGitErrorAsync(string? repositoryPath, IReadOnlyList<string> arguments, GitCommandResult result, CancellationToken cancellationToken = default)
		{
			Results.Enqueue(result);
			return Task.CompletedTask;
		}
		public Task AppendErrorAsync(string message, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task MaintainLogFilesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<IReadOnlyList<LogViewerEntry>> GetLatestEntriesAsync(int limit, LogLevel? severity = null, string? keyword = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task OpenLatestErrorLogAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task OpenLogDirectoryAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}
}
