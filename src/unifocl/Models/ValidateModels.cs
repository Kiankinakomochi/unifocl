using System.Text.Json.Serialization;

/// <summary>
/// Severity level for a single validation diagnostic.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum ValidateSeverity
{
    Error,
    Warning,
    Info
}

/// <summary>
/// A single validation finding produced by any validate sub-command.
/// Shared across all validators so consumers can process results uniformly.
/// </summary>
internal sealed record ValidateDiagnostic(
    ValidateSeverity Severity,
    string ErrorCode,
    string Message,
    string? AssetPath = null,
    string? ObjectPath = null,
    string? SceneContext = null,
    bool Fixable = false);

/// <summary>
/// Envelope returned by every validate operation.
/// </summary>
internal sealed record ValidateResult(
    string Validator,
    bool Passed,
    int ErrorCount,
    int WarningCount,
    List<ValidateDiagnostic> Diagnostics);
