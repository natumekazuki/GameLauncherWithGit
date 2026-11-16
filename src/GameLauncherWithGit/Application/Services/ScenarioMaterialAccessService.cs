using GameLauncherWithGit.Application.Models;

namespace GameLauncherWithGit.Application.Services;

// Pure read policy only. No issuer, token store, listener, DB access or DI registration.
// Allowed is a decision about the supplied snapshot, not authentication or a reusable lease.
internal static class ScenarioMaterialAccessService
{
    internal const string Audience = "GameLauncher.ScenarioMaterial";

    internal static ScenarioMaterialAccessResult Check(
        ScenarioMaterialReaderIdentity authenticatedReader,
        ScenarioMaterialAccessRequest request,
        ScenarioMaterialReadState? current)
    {
        if (current is null)
            return ScenarioMaterialAccessResult.AuthorityUnavailable;

        var permission = current.Permission;
        if (permission is null || permission.Audience != Audience
            || string.IsNullOrWhiteSpace(authenticatedReader.SessionId)
            || string.IsNullOrWhiteSpace(authenticatedReader.TurnId)
            || string.IsNullOrWhiteSpace(authenticatedReader.InferenceTarget)
            || permission.Reader != authenticatedReader
            || request.CaptureGameId <= 0 || request.CaptureGameId != permission.CaptureGameId
            || request.CaptureGameId != current.MaterialVersion.CaptureGameId
            || request.CaptureGameId != request.Occurrence.CaptureGameId
            || !permission.Occurrences.Contains(request.Occurrence))
            return ScenarioMaterialAccessResult.Denied;

        // Do not expose existence or current revisions before the scope check.
        if (request.MaterialVersion != permission.MaterialVersion
            || request.MaterialVersion != current.MaterialVersion)
            return ScenarioMaterialAccessResult.VersionMismatch;

        if (!current.ImagesByOccurrence.TryGetValue(request.Occurrence, out var images))
            return ScenarioMaterialAccessResult.OccurrenceDeleted;

        if (request.ImageId is not null && !images.Contains(request.ImageId))
            return ScenarioMaterialAccessResult.ImageNotAtOccurrence;

        return ScenarioMaterialAccessResult.Allowed;
    }
}
