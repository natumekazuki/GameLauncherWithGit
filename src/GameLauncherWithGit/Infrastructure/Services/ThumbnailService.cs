using GameLauncherWithGit;
using GameLauncherWithGit.Infrastructure.Abstractions;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace GameLauncherWithGit.Infrastructure.Services;

public sealed class ThumbnailService : IThumbnailService
{
	private const int MaxLongEdge = 512;
	private readonly ILogger<ThumbnailService> _logger;
	private readonly string _thumbnailDirectoryPath;

	public ThumbnailService(ILogger<ThumbnailService> logger)
	{
		_logger = logger;
		_thumbnailDirectoryPath = Path.Combine(AppDataPaths.BaseDirectory, "thumbnails");
	}

	public Task<string?> CreateThumbnailAsync(
		string sourceImagePath,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(sourceImagePath))
		{
			return Task.FromResult<string?>(null);
		}

		if (!File.Exists(sourceImagePath))
		{
			throw new InvalidOperationException($"サムネイル元画像が見つかりません: {sourceImagePath}");
		}

		cancellationToken.ThrowIfCancellationRequested();
		Directory.CreateDirectory(_thumbnailDirectoryPath);

		using var sourceBitmap = SKBitmap.Decode(sourceImagePath);
		if (sourceBitmap is null || sourceBitmap.Width <= 0 || sourceBitmap.Height <= 0)
		{
			throw new InvalidOperationException($"サムネイル画像の読み込みに失敗しました: {sourceImagePath}");
		}

		var maxSide = Math.Max(sourceBitmap.Width, sourceBitmap.Height);
		var scale = Math.Min(1d, (double)MaxLongEdge / maxSide);
		var targetWidth = Math.Max(1, (int)Math.Round(sourceBitmap.Width * scale));
		var targetHeight = Math.Max(1, (int)Math.Round(sourceBitmap.Height * scale));

		using var resizedBitmap = targetWidth == sourceBitmap.Width && targetHeight == sourceBitmap.Height
			? sourceBitmap.Copy()
			: sourceBitmap.Resize(
				new SKImageInfo(targetWidth, targetHeight, SKColorType.Rgba8888, SKAlphaType.Premul),
				SKFilterQuality.Medium);

		if (resizedBitmap is null)
		{
			throw new InvalidOperationException($"サムネイル画像の縮小に失敗しました: {sourceImagePath}");
		}

		using var image = SKImage.FromBitmap(resizedBitmap);
		using var data = image.Encode(SKEncodedImageFormat.Png, quality: 100);
		if (data is null)
		{
			throw new InvalidOperationException($"サムネイル画像のエンコードに失敗しました: {sourceImagePath}");
		}

		var fileName = $"{Guid.NewGuid():N}.png";
		var outputPath = Path.Combine(_thumbnailDirectoryPath, fileName);

		using (var outputStream = File.Open(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
		{
			data.SaveTo(outputStream);
		}

		_logger.LogInformation(
			"Thumbnail generated. source={SourceImagePath}, output={OutputPath}, size={Width}x{Height}",
			sourceImagePath,
			outputPath,
			targetWidth,
			targetHeight);

		return Task.FromResult<string?>(outputPath);
	}
}
