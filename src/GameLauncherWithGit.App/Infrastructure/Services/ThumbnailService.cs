using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using GameLauncherWithGit.App.Infrastructure.Abstractions;
using Microsoft.Extensions.Logging;

namespace GameLauncherWithGit.App.Infrastructure.Services;

public sealed class ThumbnailService : IThumbnailService
{
    private const int MaxLongEdge = 512;
    private readonly ILogger<ThumbnailService> _logger;

    public ThumbnailService(ILogger<ThumbnailService> logger)
    {
        _logger = logger;
    }

    public Task<bool> TryGenerateThumbnailAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(sourcePath))
            {
                return Task.FromResult(false);
            }

            cancellationToken.ThrowIfCancellationRequested();

            using Image source = Image.FromFile(sourcePath);

            (int width, int height) = CalculateTargetSize(source.Width, source.Height);
            using var bitmap = new Bitmap(width, height);
            using Graphics graphics = Graphics.FromImage(bitmap);

            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            graphics.DrawImage(source, 0, 0, width, height);

            string? dir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            bitmap.Save(destinationPath, ImageFormat.Png);
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "サムネイル生成に失敗: sourcePath={SourcePath}", sourcePath);
            return Task.FromResult(false);
        }
    }

    private static (int width, int height) CalculateTargetSize(int sourceWidth, int sourceHeight)
    {
        int longEdge = Math.Max(sourceWidth, sourceHeight);
        if (longEdge <= MaxLongEdge)
        {
            return (sourceWidth, sourceHeight);
        }

        double ratio = MaxLongEdge / (double)longEdge;
        int width = Math.Max(1, (int)Math.Round(sourceWidth * ratio));
        int height = Math.Max(1, (int)Math.Round(sourceHeight * ratio));
        return (width, height);
    }
}
