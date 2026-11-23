using System.Web;

/// <summary>
/// One request to unifocl's former Editor HTTP bridge, translated into the <c>unifocl.bridge</c>
/// Pipeline command that replaces it.
/// </summary>
/// <param name="Command">The command name within <see cref="EditorLinkRoutes.Group"/>.</param>
/// <param name="Args">The command's arguments.</param>
/// <param name="Timeout">The time the Editor may take, which the Pipeline server enforces.</param>
/// <param name="Reply">How the command's JSON result becomes the response body the caller expects.</param>
internal sealed record EditorLinkRoute(
    string Command,
    IReadOnlyDictionary<string, object?> Args,
    TimeSpan Timeout,
    EditorLinkReply Reply);

internal enum EditorLinkReply
{
    /// <summary>The result's JSON, as the endpoint returned it.</summary>
    Json,

    /// <summary><c>PONG</c>, the text <c>/ping</c> answered with.</summary>
    Pong,

    /// <summary><c>OK</c>, the text <c>/touch</c> answered with.</summary>
    Ok,

    /// <summary>The <c>runtimeId</c> field of <c>ping</c>, the text <c>/runtime-id</c> answered with.</summary>
    RuntimeId,
}

/// <summary>
/// Maps each endpoint of the old Editor HTTP bridge to its <c>unifocl.bridge</c> Pipeline command
/// (see <c>UnifoclBridgeCommands</c> in the Editor package). Paths not listed here, such as
/// <c>/runtime/*</c> and <c>/stop</c>, have no Pipeline command yet.
/// </summary>
internal static class EditorLinkRoutes
{
    public const string Group = "unifocl.bridge";

    private static readonly TimeSpan Quick = TimeSpan.FromSeconds(60);

    // Project commands include builds, UPM changes and test runs, so they get the Editor's own
    // ceiling; the caller's HTTP timeout still cancels its wait sooner.
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(600);

    private static readonly Dictionary<string, object?> NoArgs = new();

    public static EditorLinkRoute? Resolve(string method, string path, string? query, string? body)
    {
        var normalized = path.TrimEnd('/');
        var isPost = string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase);
        var isGet = string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase);
        var parameters = HttpUtility.ParseQueryString(query ?? string.Empty);

        EditorLinkRoute Json(string command, IReadOnlyDictionary<string, object?>? args = null, TimeSpan? timeout = null) =>
            new(command, args ?? NoArgs, timeout ?? Quick, EditorLinkReply.Json);

        Dictionary<string, object?> Payload() => new() { ["payload"] = body ?? string.Empty };

        Dictionary<string, object?> RequestId() => new() { ["request-id"] = parameters["requestId"] ?? string.Empty };

        return (isGet, isPost, normalized.ToLowerInvariant()) switch
        {
            (true, _, "/ping") => new EditorLinkRoute("ping", NoArgs, Quick, EditorLinkReply.Pong),
            (true, _, "/runtime-id") => new EditorLinkRoute("ping", NoArgs, Quick, EditorLinkReply.RuntimeId),
            (_, true, "/touch") => new EditorLinkRoute("ping", NoArgs, Quick, EditorLinkReply.Ok),

            (true, _, "/hierarchy/snapshot") => Json("hierarchy_snapshot"),
            (_, true, "/hierarchy/find") => Json("hierarchy_find", Payload()),
            (_, true, "/hierarchy/command") => Json("hierarchy_command", Payload()),
            (_, true, "/inspect") => Json("inspect", Payload()),

            (_, true, "/project/command") => Json("project_command", Payload(), Long),
            (true, _, "/project/command-status") => Json("project_command_status", RequestId()),
            (_, true, "/project/mutation/submit") => Json("mutation_submit", Payload()),
            (true, _, "/project/mutation/status") => Json("mutation_status", RequestId()),
            (true, _, "/project/mutation/result") => Json("mutation_result", RequestId()),
            (_, true, "/project/mutation/cancel") => Json("mutation_cancel", RequestId()),
            (_, true, "/mcp/unifocl_project_command") => Json("custom_tool", Payload(), Long),

            (true, _, "/build/status") => Json("build_status"),
            (true, _, "/compile/status") => Json("compile_status"),
            (true, _, "/build/log") => Json("build_log", new Dictionary<string, object?>
            {
                ["offset"] = long.TryParse(parameters["offset"], out var offset) ? offset : 0L,
                ["limit"] = int.TryParse(parameters["limit"], out var limit) ? limit : 0,
                ["errors-only"] = bool.TryParse(parameters["errorsOnly"], out var errorsOnly) && errorsOnly,
            }),
            (true, _, "/asset-index") => Json("asset_index", new Dictionary<string, object?>
            {
                ["revision"] = int.TryParse(parameters["revision"], out var revision) ? revision : 0,
            }),

            _ => null,
        };
    }
}
