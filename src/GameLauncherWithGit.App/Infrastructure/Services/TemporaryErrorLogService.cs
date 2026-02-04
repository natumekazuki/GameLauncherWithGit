namespace GameLauncherWithGit.App.Infrastructure.Services;

public static class TemporaryErrorLogService
{
    private static readonly object SyncRoot = new();

    public static string AppendException(string source, Exception ex)
    {
        return Append(source, ex.ToString());
    }

    public static string AppendMessage(string source, string message)
    {
        return Append(source, message);
    }

    private static string Append(string source, string payload)
    {
        try
        {
            string logPath = ResolveLogPath();
            string entry =
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}{Environment.NewLine}" +
                $"{payload}{Environment.NewLine}{Environment.NewLine}";

            lock (SyncRoot)
            {
                File.AppendAllText(logPath, entry);
            }

            return logPath;
        }
        catch
        {
            return Path.Combine(Path.GetTempPath(), "GameLauncherWithGit-ui-temp.log");
        }
    }

    private static string ResolveLogPath()
    {
        string baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GameLauncherWithGit",
            "logs");

        Directory.CreateDirectory(baseDir);
        return Path.Combine(baseDir, $"ui-temp-{DateTime.Now:yyyyMMdd}.log");
    }
}
