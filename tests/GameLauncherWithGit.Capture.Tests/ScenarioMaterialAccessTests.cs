using System.Collections.Frozen;
using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Application.Services;
using Xunit;

namespace GameLauncherWithGit.Capture.Tests;

public sealed class ScenarioMaterialAccessTests
{
    private static readonly ScenarioMaterialReaderIdentity Reader = new("local-session", "turn-1", "local-target");
    private static ScenarioMaterialVersion Version(string version) => version switch
    {
        "v0" => new(1, 0, 1),
        "v2" => new(1, 2, 1),
        "matching2" => new(1, 1, 2),
        _ => new(1, 1, 1)
    };
    private static ScenarioOccurrenceRef Occurrence(string name) => new(1, name == "lap1" ? 10 : 20,
        name == "outside" ? 2 : name == "deleted" ? 3 : 1, 0, 12);

    private static ScenarioMaterialReadState State() => new(
        new(ScenarioMaterialAccessService.Audience, Reader, 1, Version("v1"),
            new[] { "lap1", "lap2", "deleted" }.Select(Occurrence).ToFrozenSet()),
        Version("v1"),
        new Dictionary<ScenarioOccurrenceRef, FrozenSet<string>>
        {
            [Occurrence("lap1")] = Array.Empty<string>().ToFrozenSet(StringComparer.Ordinal),
            [Occurrence("lap2")] = new[] { "shared-image" }.ToFrozenSet(StringComparer.Ordinal),
            [Occurrence("outside")] = new[] { "shared-image" }.ToFrozenSet(StringComparer.Ordinal)
        }.ToFrozenDictionary());

    // @test-value v2
    // kind = "security"
    // claim = "内部読取policyは選択作品・出現・実行中reader以外と失効後を拒否し、authority不明を区別する"
    // oracle = { type = "contract", ref = "https://github.com/natumekazuki/GameLauncherWithGit/issues/33" }
    // fault = "作品、出現、Session、Turn、推論先、audienceの照合を省略するか失効・接続断を許可する"
    // observable = "CheckのAllowed、Denied、AuthorityUnavailable結果"
    // observation_boundary = "component-behavior"
    // scope = "合成2作品と信頼済み入力snapshotの純粋policy。認証transportや実DB読取は含まない"
    // lifecycle = "permanent"
    // impact = "後続readerが別作品・範囲外・失効済みの要求を許可する"
    // distinction = "型検査だけでは同じ型の異なる作品・reader値の取り違えを検出できず、既存保存testは認可判断を通らない"
    // @end-test-value
    [Theory]
    [InlineData("valid", ScenarioMaterialAccessResult.Allowed)]
    [InlineData("other-game", ScenarioMaterialAccessResult.Denied)]
    [InlineData("other-game-state", ScenarioMaterialAccessResult.Denied)]
    [InlineData("outside", ScenarioMaterialAccessResult.Denied)]
    [InlineData("session", ScenarioMaterialAccessResult.Denied)]
    [InlineData("turn", ScenarioMaterialAccessResult.Denied)]
    [InlineData("target", ScenarioMaterialAccessResult.Denied)]
    [InlineData("audience", ScenarioMaterialAccessResult.Denied)]
    [InlineData("revoked", ScenarioMaterialAccessResult.Denied)]
    [InlineData("scope-changed", ScenarioMaterialAccessResult.Denied)]
    [InlineData("disconnected", ScenarioMaterialAccessResult.AuthorityUnavailable)]
    public void ChecksCurrentPermission(string variation, object expected)
    {
        ScenarioMaterialReadState? state = State();
        var reader = Reader;
        var request = new ScenarioMaterialAccessRequest(1, Version("v1"), Occurrence("lap2"));
        switch (variation)
        {
            case "other-game": request = request with { CaptureGameId = 2 }; break;
            case "other-game-state": state = state with { MaterialVersion = state.MaterialVersion with { CaptureGameId = 2 } }; break;
            case "outside": request = request with { Occurrence = Occurrence("outside") }; break;
            case "session": reader = reader with { SessionId = "other-session" }; break;
            case "turn": reader = reader with { TurnId = "turn-2" }; break;
            case "target": reader = reader with { InferenceTarget = "other-target" }; break;
            case "audience": state = state with { Permission = state.Permission! with { Audience = "WithMate" } }; break;
            case "revoked": state = state with { Permission = null }; break;
            case "scope-changed": state = state with { Permission = state.Permission! with { Occurrences = new[] { Occurrence("lap1") }.ToFrozenSet() } }; break;
            case "disconnected": state = null; break;
        }
        Assert.Equal((ScenarioMaterialAccessResult)expected, ScenarioMaterialAccessService.Check(reader, request, state));
    }

    // @test-value v2
    // kind = "contract"
    // claim = "旧資料版と削除済み出現を別結果にし、画像は許可出現との関連がある時だけ許可する"
    // oracle = { type = "contract", ref = "https://github.com/natumekazuki/GameLauncherWithGit/issues/33" }
    // fault = "古い版を許可する、削除を別出現へ付け替える、または画像IDだけで周回・範囲を越えて許可する"
    // observable = "CheckのVersionMismatch、OccurrenceDeleted、ImageNotAtOccurrence、Allowed、Denied結果"
    // observation_boundary = "component-behavior"
    // scope = "合成の2周目のみの画像、共有画像の範囲外関連、版変更と削除のpolicy判断"
    // lifecycle = "permanent"
    // impact = "誤った周回の画像や訂正前の本文を根拠として提示する"
    // distinction = "保存/ZIPのtestでは認可済み出現を介した読取判断と版の三者一致を検証できない"
    // @end-test-value
    [Theory]
    [InlineData("lap2", "shared-image", "v1", "v1", ScenarioMaterialAccessResult.Allowed)]
    [InlineData("lap1", "shared-image", "v1", "v1", ScenarioMaterialAccessResult.ImageNotAtOccurrence)]
    [InlineData("outside", "shared-image", "v1", "v1", ScenarioMaterialAccessResult.Denied)]
    [InlineData("deleted", null, "v1", "v1", ScenarioMaterialAccessResult.OccurrenceDeleted)]
    [InlineData("lap2", null, "v0", "v1", ScenarioMaterialAccessResult.VersionMismatch)]
    [InlineData("lap2", null, "v1", "v2", ScenarioMaterialAccessResult.VersionMismatch)]
    [InlineData("lap2", null, "v2", "v2", ScenarioMaterialAccessResult.VersionMismatch)]
    [InlineData("lap2", null, "v1", "matching2", ScenarioMaterialAccessResult.VersionMismatch)]
    public void ChecksVersionAndOccurrenceImage(string occurrence, string? image, string requestedVersion,
        string currentVersion, object expected)
    {
        var state = State() with { MaterialVersion = Version(currentVersion) };
        Assert.Equal((ScenarioMaterialAccessResult)expected, ScenarioMaterialAccessService.Check(Reader,
            new(1, Version(requestedVersion), Occurrence(occurrence), image), state));
    }
}
