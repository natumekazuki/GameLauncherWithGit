using System.Collections.Frozen;

namespace GameLauncherWithGit.Application.Models;

// Internal policy inputs, not credentials or a wire protocol. A future trusted adapter must
// resolve these from canonical authority on every read, never deserialize them from a caller.
internal sealed record ScenarioMaterialReaderIdentity(string SessionId, string TurnId, string InferenceTarget);

internal sealed record ScenarioMaterialPermission(
    string Audience,
    ScenarioMaterialReaderIdentity Reader,
    long CaptureGameId,
    ScenarioMaterialVersion MaterialVersion,
    FrozenSet<ScenarioOccurrenceRef> Occurrences);

internal sealed record ScenarioMaterialAccessRequest(
    long CaptureGameId, ScenarioMaterialVersion MaterialVersion, ScenarioOccurrenceRef Occurrence, string? ImageId = null);

// Current permission is null after termination, cancellation, deselection or any scope/target
// change until the trusted owner authorizes a new request context. Connection failure must not
// reuse a cached permission. The material projection must come from the same consistent read.
internal sealed record ScenarioMaterialReadState(
    ScenarioMaterialPermission? Permission,
    ScenarioMaterialVersion MaterialVersion,
    FrozenDictionary<ScenarioOccurrenceRef, FrozenSet<string>> ImagesByOccurrence);

internal enum ScenarioMaterialAccessResult
{
    Allowed,
    Denied,
    AuthorityUnavailable,
    VersionMismatch,
    OccurrenceDeleted,
    ImageNotAtOccurrence
}
