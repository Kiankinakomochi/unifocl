using System.Text.Json;

/// <summary>
/// The outcome of one Pipeline command, whichever transport carried it.
/// </summary>
/// <param name="Ok">Whether the command succeeded.</param>
/// <param name="Result">
/// The command's result: an object for a single result, an array for a record list, a string for
/// text, or <c>null</c> when the command wrote nothing.
/// </param>
/// <param name="Error">What went wrong, when <paramref name="Ok"/> is false.</param>
/// <param name="ExitCode">
/// The Unity CLI's exit code, when the transport reports one (<c>unity command</c> does, MCP does not).
/// </param>
internal sealed record PipelineCallResult(bool Ok, JsonElement? Result, string? Error, int? ExitCode)
{
    public static PipelineCallResult Success(JsonElement? result, int? exitCode = 0) => new(true, result, null, exitCode);

    public static PipelineCallResult Failure(string error, int? exitCode = null) => new(false, null, error, exitCode);
}

/// <summary>A running Unity Editor whose Pipeline server the Unity CLI can reach.</summary>
internal sealed record PipelineEditorInstance(
    string ProjectPath,
    int Port,
    int Pid,
    string State,
    string EditorVersion,
    bool Batchmode)
{
    public bool IsReady => string.Equals(State, "ready", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Carries Pipeline commands to a Unity Editor. Every implementation goes through Unity's own CLI
/// binary, which is the agent interface Unity's terms of service authorize; unifocl never opens a
/// connection to the Editor itself.
/// </summary>
internal interface IPipelineTransport : IAsyncDisposable
{
    /// <summary>
    /// Runs <c>&lt;group&gt; &lt;name&gt;</c> with <paramref name="args"/>, keyed by the parameter names
    /// the command declares (e.g. <c>dry-run</c>). Values may be strings, numbers, booleans, or anything
    /// JSON-serializable, which is passed as JSON text.
    /// </summary>
    Task<PipelineCallResult> ExecAsync(
        string group,
        string name,
        IReadOnlyDictionary<string, object?>? args = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);
}
