using GameLauncherWithGit.Application.Services;
using Xunit;

namespace GameLauncherWithGit.Capture.Tests;

public sealed class ScenarioScreenshotPixelsTests
{
    // @test-value v2
    // kind = "invariant"
    // claim = "同じ表示画素はBGRAの行方向・padding・未定義alphaに関係なくtop-down RGBA8 opaqueへ正規化される"
    // oracle = { type = "contract", ref = "Issue 22: sha256-dimensions-rgba8-v1 top-down tightly-packed RGBA8 alpha255" }
    // fault = "B/Rの逆転、上下反転、行padding混入またはGDIの未定義alphaを保存画素へ持ち込む"
    // observable = "正規化したRGBA配列と同一表示画像のhash"
    // observation_boundary = "component-behavior"
    // scope = "screenshot pixel normalization"
    // lifecycle = "permanent"
    // impact = "画像の色・向きが壊れたり同じ画像を別画像として蓄積する"
    // distinction = "native撮影不要の固定4色で画像形式差を検出しbuildやDBtestでは判定できない画素品質を少量で確認する"
    // @end-test-value
    [Fact]
    public void NormalizePreservesColorOrientationAndIgnoresPaddingAndAlpha()
    {
        byte[] topDown = [0, 0, 255, 0, 0, 255, 0, 2, 11, 12, 13, 14,
            255, 0, 0, 3, 255, 255, 255, 4, 21, 22, 23, 24];
        byte[] bottomUp = [255, 0, 0, 9, 255, 255, 255, 8, 31, 32, 33, 34,
            0, 0, 255, 7, 0, 255, 0, 6, 41, 42, 43, 44];
        byte[] expected = [255, 0, 0, 255, 0, 255, 0, 255,
            0, 0, 255, 255, 255, 255, 255, 255];

        var first = ScenarioScreenshotPixels.NormalizeBgra32(2, 2, topDown, 12, false);
        var second = ScenarioScreenshotPixels.NormalizeBgra32(2, 2, bottomUp, 12, true);

        Assert.Equal(expected, first);
        Assert.Equal(expected, second);
        Assert.Equal(ScenarioScreenshotPixels.ComputeHash(2, 2, first), ScenarioScreenshotPixels.ComputeHash(2, 2, second));
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "画像hashは幅高さのLE32とRGBA8のSHA256であり同じbyte列でも寸法または色が違えば別画像になる"
    // oracle = { type = "contract", ref = "Issue 22: sha256-dimensions-rgba8-v1; SHA256 input hex 0200000001000000ff0000ff00ff00ff" }
    // fault = "寸法をhashから省く、endiannessを逆にする、画素の一部をhashしない"
    // observable = "既知入力のSHA256 hexと寸法変更・色変更後のhash"
    // observation_boundary = "component-behavior"
    // scope = "screenshot deduplication identity"
    // lifecycle = "permanent"
    // impact = "異なる画像を重複として削除する、または保存・出力の画像識別が実装間で一致しない"
    // distinction = "DBや型checkでは保証できないbyte-level identity契約を固定vectorで直接検出する"
    // @end-test-value
    [Fact]
    public void HashIncludesDimensionsAndEveryPixelUsingLittleEndianVector()
    {
        byte[] rgba = [255, 0, 0, 255, 0, 255, 0, 255];
        var hash = ScenarioScreenshotPixels.ComputeHash(2, 1, rgba);
        Assert.Equal("99a1c162655acba20214b3657af260db9a767c14b4b2e74c5abc729af7691a83", hash);
        Assert.NotEqual(hash, ScenarioScreenshotPixels.ComputeHash(1, 2, rgba));
        rgba[4] = 1;
        Assert.NotEqual(hash, ScenarioScreenshotPixels.ComputeHash(2, 1, rgba));
    }
}
