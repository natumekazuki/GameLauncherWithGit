namespace GameLauncherWithGit.Application.Services;

// Linked application sources compile without MAUI; tests inject an isolated thumbnail directory.
internal static class FileSystem
{
    public static string AppDataDirectory => throw new InvalidOperationException("Tests must not use the application data directory.");
}
