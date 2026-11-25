namespace GameLauncherWithGit.Infrastructure.Models;

public sealed record GitCommandResult(
	int ExitCode,
	string StandardOutput,
	string StandardError,
	string? UserMessage = null)
{
	public bool IsSuccess => ExitCode == 0;

	internal bool HasPullConflict()
	{
		var text = $"{StandardError}\n{StandardOutput}";
		return text.Contains("CONFLICT", StringComparison.OrdinalIgnoreCase)
			|| text.Contains("Resolve all conflicts manually", StringComparison.OrdinalIgnoreCase)
			|| text.Contains("could not apply", StringComparison.OrdinalIgnoreCase)
			|| text.Contains("fix conflicts and then run", StringComparison.OrdinalIgnoreCase)
			|| text.Contains("競合", StringComparison.OrdinalIgnoreCase);
	}

	internal bool HasPullConfigurationError()
	{
		var text = $"{StandardError}\n{StandardOutput}";
		return text.Contains("Cannot rebase onto multiple branches", StringComparison.OrdinalIgnoreCase)
			|| text.Contains("There is no tracking information for the current branch", StringComparison.OrdinalIgnoreCase)
			|| text.Contains("no upstream configured", StringComparison.OrdinalIgnoreCase)
			|| text.Contains("upstream branch", StringComparison.OrdinalIgnoreCase)
			|| text.Contains("Please specify which branch you want to rebase against", StringComparison.OrdinalIgnoreCase)
			|| text.Contains("Updating an unborn branch with changes added to the index", StringComparison.OrdinalIgnoreCase);
	}

	internal GitCommandResult ProtectCadenceIncoming(bool pull)
	{
		if (IsSuccess) return new(0, "", "");
		if (ExitCode == -2) return new(-2, "", "", "Git 実行がキャンセルされました。");
		if (pull && HasPullConflict())
			return new(ExitCode, "", "CONFLICT", "取り込み中に競合が発生しました。Git で競合を解消してから同期を再開してください。");
		if (pull && HasPullConfigurationError())
			return new(ExitCode, "", "no upstream configured", "取り込み先の設定を確認してから同期を再開してください。");
		return new(ExitCode, "", "", "リモートから取得できませんでした。Git の取得先設定・認証・接続を確認してください。");
	}

	public string GetFailureReason()
	{
		if (!string.IsNullOrWhiteSpace(UserMessage))
		{
			return UserMessage;
		}

		if (StandardError.Contains("dubious ownership", StringComparison.OrdinalIgnoreCase))
		{
			return "Git がリポジトリの所有者を信頼できないため操作できません。所有者とアクセス権を確認してください。";
		}

		if (StandardError.Contains("permission denied (publickey", StringComparison.OrdinalIgnoreCase))
		{
			return "リモートリポジトリの SSH 認証に失敗しました。鍵とアクセス権を確認してください。";
		}

		if (StandardError.Contains("permission denied", StringComparison.OrdinalIgnoreCase)
			|| StandardError.Contains("access is denied", StringComparison.OrdinalIgnoreCase)
			|| StandardError.Contains("アクセスが拒否", StringComparison.OrdinalIgnoreCase))
		{
			return "リポジトリまたは関連ファイルへのアクセスが拒否されました。アクセス権を確認してください。";
		}

		if (StandardError.Contains("not a git repository", StringComparison.OrdinalIgnoreCase)
			|| StandardError.Contains("Git リポジトリではありません", StringComparison.OrdinalIgnoreCase))
		{
			return "Git リポジトリを確認できません。作業ツリーのルートを選択してください。";
		}

		return StandardError
			.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.FirstOrDefault()
			?? StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault()
			?? $"exit code: {ExitCode}";
	}
}
