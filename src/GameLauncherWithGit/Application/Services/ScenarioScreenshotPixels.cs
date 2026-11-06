using System.Buffers.Binary;
using System.Security.Cryptography;

namespace GameLauncherWithGit.Application.Services;

public static class ScenarioScreenshotPixels
{
    public const string HashAlgorithm = "sha256-dimensions-rgba8-v1";

    public static byte[] NormalizeBgra32(int width, int height, ReadOnlySpan<byte> source, int stride, bool bottomUp)
    {
        var rowBytes = GetRowBytes(width, height);
        if (stride < rowBytes || source.Length < checked(stride * height))
            throw new ArgumentException("画素バッファの大きさまたは行幅が不正です。", nameof(source));

        var rgba = new byte[checked(rowBytes * height)];
        for (var y = 0; y < height; y++)
        {
            var row = source.Slice(checked((bottomUp ? height - 1 - y : y) * stride), rowBytes);
            var output = rgba.AsSpan(y * rowBytes, rowBytes);
            for (var x = 0; x < rowBytes; x += 4)
            {
                output[x] = row[x + 2];
                output[x + 1] = row[x + 1];
                output[x + 2] = row[x];
                output[x + 3] = 255;
            }
        }
        return rgba;
    }

    public static string ComputeHash(int width, int height, ReadOnlySpan<byte> rgba)
    {
        var rowBytes = GetRowBytes(width, height);
        if (rgba.Length != checked(rowBytes * height))
            throw new ArgumentException("RGBA8画素の大きさが寸法に一致しません。", nameof(rgba));
        Span<byte> dimensions = stackalloc byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(dimensions, width);
        BinaryPrimitives.WriteInt32LittleEndian(dimensions[4..], height);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(dimensions);
        hash.AppendData(rgba);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static int GetRowBytes(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        return checked(width * 4);
    }
}
