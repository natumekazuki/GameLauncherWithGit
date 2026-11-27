namespace GameLauncherWithGit.Application.Models;

/// <summary>Names the same stored recording identically in the desktop and recording Web views.</summary>
internal static class ScenarioRecordLabels
{
    internal const string Unresolved = "未確定";

    /// <summary>Uses existing history metadata only; never infers a branch for an unresolved record.</summary>
    internal static string ForRecord(long profileId, long? recordId, IReadOnlyList<ScenarioHistoryScope>? scopes)
    {
        // Only the profile's own record is the unbranched recording.
        if (profileId > 0 && recordId == profileId)
            return "通常の記録";

        // Missing branch metadata does not make a selected branch the unbranched recording.
        if (scopes?.FirstOrDefault(scope => scope.ProfileId == profileId && scope.RecordId == recordId)?.BranchRef is not { Length: > 0 } branch)
            return Unresolved;

        // A branch has the same display name regardless of which screen resolved it.
        const string localHead = "refs/heads/";
        var name = branch.StartsWith(localHead, StringComparison.Ordinal) ? branch[localHead.Length..] : branch;
        var matches = scopes.Where(scope => scope.ProfileId == profileId && scope.BranchRef == branch)
            .OrderBy(scope => scope.RecordId).ToArray();

        // Distinguish same-named historical branches without disclosing their local repository paths.
        return matches.Length == 1 ? name : $"{name}（同名 {Array.FindIndex(matches, scope => scope.RecordId == recordId) + 1}/{matches.Length}）";
    }
}
