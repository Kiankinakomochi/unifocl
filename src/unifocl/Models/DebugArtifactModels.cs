using System.Text.Json;
using System.Text.Json.Serialization;

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum TicketSeverity
{
    Critical,
    Major,
    Minor,
    Trivial
}

internal sealed record DebugArtifactTicketMeta(
    string? Title = null,
    TicketSeverity? Severity = null,
    List<string>? Labels = null,
    string? Repro = null);

internal sealed record DebugArtifactEnvironment(
    JsonElement? Settings = null,
    JsonElement? CompileStatus = null);

internal sealed record DebugArtifactLogs(
    JsonElement? ConsoleErrors = null,
    JsonElement? ConsoleWarnings = null,
    JsonElement? CompileErrors = null);

internal sealed record DebugArtifactValidation(
    JsonElement? SceneList = null,
    JsonElement? MissingScripts = null,
    JsonElement? Packages = null,
    JsonElement? BuildSettings = null,
    JsonElement? Asmdef = null,
    JsonElement? AssetRefs = null);

internal sealed record DebugArtifactStateDumps(
    JsonElement? HierarchySnapshot = null,
    JsonElement? BuildReport = null,
    JsonElement? BuildArtifactMeta = null);

internal sealed record DebugArtifactPerformance(
    JsonElement? ProfilerInspect = null,
    JsonElement? FrameTiming = null,
    JsonElement? Frames = null,
    JsonElement? GcAlloc = null,
    JsonElement? Markers = null,
    string? ExportSummaryPath = null);

internal sealed record DebugArtifactMedia(
    JsonElement? RecorderStatus = null,
    string? MemorySnapshotPath = null);

internal sealed record DebugArtifactCollectionError(
    string Operation,
    string Error);

internal sealed record DebugArtifactPrepResult(
    bool Ok,
    int Tier,
    bool ProfilerStarted,
    bool RecorderStarted,
    bool ConsoleCleared,
    List<DebugArtifactCollectionError> Errors,
    string NextStep);

internal sealed record DebugArtifact(
    string ArtifactVersion,
    string CollectedAtUtc,
    int Tier,
    double CollectionDurationMs,
    DebugArtifactTicketMeta? TicketMeta,
    DebugArtifactEnvironment? Environment,
    DebugArtifactLogs? Logs,
    DebugArtifactValidation? Validation,
    DebugArtifactStateDumps? StateDumps,
    DebugArtifactPerformance? Performance,
    DebugArtifactMedia? Media,
    List<DebugArtifactCollectionError> Errors);
