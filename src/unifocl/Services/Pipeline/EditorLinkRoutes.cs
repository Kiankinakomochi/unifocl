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
/// (see <c>UnifoclBridgeCommands</c> and <c>UnifoclRuntimeCommands</c> in the Editor package). The
/// Editor no longer listens on HTTP, so a path not listed here has no Editor endpoint.
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

        Dictionary<string, object?> Query(string name, string arg, string fallback = "") =>
            new() { [arg] = parameters[name] ?? fallback };

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

            (true, _, "/runtime/targets") => Json("runtime_targets"),
            (_, true, "/runtime/attach") => Json("runtime_attach", Query("target", "target", "editor:playmode")),
            (true, _, "/runtime/status") => Json("runtime_status"),
            (_, true, "/runtime/detach") => Json("runtime_detach"),
            (true, _, "/runtime/manifest") => Json("runtime_manifest"),
            (_, true, "/runtime/exec") => Json("runtime_exec", Payload()),
            (_, true, "/runtime/job/submit") => Json("runtime_job_submit", Payload()),
            (true, _, "/runtime/job/status") => Json("runtime_job_status", Query("jobId", "job-id")),
            (_, true, "/runtime/job/cancel") => Json("runtime_job_cancel", Query("jobId", "job-id")),
            (true, _, "/runtime/job/list") => Json("runtime_job_list"),
            (_, true, "/runtime/stream/subscribe") => Json("runtime_stream_subscribe", Payload()),
            (_, true, "/runtime/stream/unsubscribe") => Json("runtime_stream_unsubscribe", Query("subscriptionId", "subscription-id")),
            (_, true, "/runtime/watch/add") => Json("runtime_watch_add", Payload()),
            (_, true, "/runtime/watch/remove") => Json("runtime_watch_remove", Query("watchId", "watch-id")),
            (true, _, "/runtime/watch/list") => Json("runtime_watch_list"),
            (true, _, "/runtime/watch/poll") => Json("runtime_watch_poll"),

            _ => null,
        };
    }
}
