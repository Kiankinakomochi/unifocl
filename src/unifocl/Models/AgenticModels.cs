using System.Text.Json.Serialization;

internal enum AgenticOutputFormat
{
    Json,
    Yaml
}

internal sealed record ExecLaunchOptions(
    string CommandText,
    bool Agentic,
    AgenticOutputFormat Format,
    string? ProjectPath,
    CliContextMode? ContextMode,
    int? AttachPort,
    string? RequestId);

internal sealed record AgenticExecutionRequest(
    string CommandText,
    string ContextMode,
    string SessionSeed,
    string OutputMode,
    string RequestId);

internal sealed record AgenticError(
    [property: JsonPropertyOrder(1)] string Code,
    [property: JsonPropertyOrder(2)] string Message,
    [property: JsonPropertyOrder(3)] string? Hint = null);

internal sealed record AgenticWarning(
    [property: JsonPropertyOrder(1)] string Code,
    [property: JsonPropertyOrder(2)] string Message);

internal sealed record AgenticMeta(
    [property: JsonPropertyOrder(1)] string SchemaVersion,
    [property: JsonPropertyOrder(2)] string Protocol,
    [property: JsonPropertyOrder(3)] int ExitCode,
    [property: JsonPropertyOrder(4)] string TimestampUtc,
    [property: JsonPropertyOrder(5)] Dictionary<string, object?>? Extra = null);

internal sealed record AgenticResponseEnvelope(
    [property: JsonPropertyOrder(1)] string Status,
    [property: JsonPropertyOrder(2)] string RequestId,
    [property: JsonPropertyOrder(3)] string Mode,
    [property: JsonPropertyOrder(4)] string Action,
    [property: JsonPropertyOrder(5)] object? Data,
    [property: JsonPropertyOrder(6)] List<AgenticError> Errors,
    [property: JsonPropertyOrder(7)] List<AgenticWarning> Warnings,
    [property: JsonPropertyOrder(8)] AgenticMeta Meta);

internal sealed record AgenticCapabilities(
    [property: JsonPropertyOrder(1)] string SchemaVersion,
    [property: JsonPropertyOrder(2)] string Protocol,
    [property: JsonPropertyOrder(3)] string[] Formats,
    [property: JsonPropertyOrder(4)] string[] Modes,
    [property: JsonPropertyOrder(5)] string[] Endpoints,
    [property: JsonPropertyOrder(6)] Dictionary<string, string[]>? WorktreeTools = null);
