namespace GameLauncherWithGit.Infrastructure.Models;

public sealed record GitCommandResult(
	int ExitCode,
	string StandardOutput,
	string StandardError,
	string? UserMessage = null)
{
	public bool IsSuccess => ExitCode == 0;

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
