using System.Text.Json;
using System.Text.Json.Serialization;

internal enum ExecRiskLevel
{
    SafeRead,
    SafeWrite,
    DestructiveWrite,
    PrivilegedExec
}

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum ExecV2Status
{
    Accepted,
    ApprovalRequired,
    Running,
    Completed,
    Failed,
    Rejected
}

internal sealed record ExecV2Intent(
    bool DryRun = false,
    string? ApprovalToken = null);

internal sealed record ExecV2Request(
    string RequestId,
    string? SessionId,
    string Operation,
    JsonElement? Args,
    ExecV2Intent? Intent);

internal sealed record ExecV2Response(
    [property: JsonConverter(typeof(JsonStringEnumConverter))] ExecV2Status Status,
    string RequestId,
    string? ApprovalToken = null,
    string? Message = null,
    object? Result = null);
