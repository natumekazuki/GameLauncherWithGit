using GameLauncherWithGit.Application.Models;
using Xunit;

namespace GameLauncherWithGit.Capture.Tests;

/// <summary>Protects the recording destination displayed by both the desktop and Web views.</summary>
public sealed class ScenarioRecordLabelsTests
{
    /// <summary>An unknown destination must not be presented as the known unbranched record.</summary>
    [Fact]
    public void OnlyTheProfileRecordIsUnbranched()
    {
        Assert.Equal("通常の記録", ScenarioRecordLabels.ForRecord(10, 10, null));
        Assert.Equal("未確定", ScenarioRecordLabels.ForRecord(10, null, null));
        Assert.Equal("未確定", ScenarioRecordLabels.ForRecord(10, 20, null));
        Assert.Equal("未確定", ScenarioRecordLabels.ForRecord(10, 20, [new(20, 99, "other", "refs/heads/archive")]));
    }

    /// <summary>Same-named histories are distinguishable without exposing either repository path.</summary>
    [Fact]
    public void LabelsBranchesAndDisambiguatesOnlyWithinTheProfile()
    {
        ScenarioHistoryScope[] scopes =
        [
            new(22, 10, "second", "refs/heads/archive"),
            new(21, 10, "first", "refs/heads/archive"),
            new(23, 10, "second", "refs/heads/play"),
            new(24, 99, "other", "refs/heads/play")
        ];
        Assert.Equal("archive（同名 1/2）", ScenarioRecordLabels.ForRecord(10, 21, scopes));
        Assert.Equal("archive（同名 2/2）", ScenarioRecordLabels.ForRecord(10, 22, scopes));
        Assert.Equal("play", ScenarioRecordLabels.ForRecord(10, 23, scopes));
    }
}
