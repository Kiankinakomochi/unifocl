using System.Text.Json;
using Xunit;

public class EditorLinkTests
{
    [Theory]
    [InlineData("GET", "/ping", "ping")]
    [InlineData("GET", "/runtime-id", "ping")]
    [InlineData("POST", "/touch", "ping")]
    [InlineData("GET", "/hierarchy/snapshot", "hierarchy_snapshot")]
    [InlineData("POST", "/hierarchy/find", "hierarchy_find")]
    [InlineData("POST", "/hierarchy/command", "hierarchy_command")]
    [InlineData("POST", "/inspect", "inspect")]
    [InlineData("POST", "/project/command", "project_command")]
    [InlineData("GET", "/project/command-status", "project_command_status")]
    [InlineData("POST", "/project/mutation/submit", "mutation_submit")]
    [InlineData("GET", "/project/mutation/status", "mutation_status")]
    [InlineData("GET", "/project/mutation/result", "mutation_result")]
    [InlineData("POST", "/project/mutation/cancel", "mutation_cancel")]
    [InlineData("POST", "/mcp/unifocl_project_command", "custom_tool")]
    [InlineData("GET", "/build/status", "build_status")]
    [InlineData("GET", "/build/log", "build_log")]
    [InlineData("GET", "/compile/status", "compile_status")]
    [InlineData("GET", "/asset-index", "asset_index")]
    [InlineData("GET", "/runtime/targets", "runtime_targets")]
    [InlineData("POST", "/runtime/attach", "runtime_attach")]
    [InlineData("GET", "/runtime/status", "runtime_status")]
    [InlineData("POST", "/runtime/detach", "runtime_detach")]
    [InlineData("GET", "/runtime/manifest", "runtime_manifest")]
    [InlineData("POST", "/runtime/exec", "runtime_exec")]
    [InlineData("POST", "/runtime/job/submit", "runtime_job_submit")]
    [InlineData("GET", "/runtime/job/status", "runtime_job_status")]
    [InlineData("POST", "/runtime/job/cancel", "runtime_job_cancel")]
    [InlineData("GET", "/runtime/job/list", "runtime_job_list")]
    [InlineData("POST", "/runtime/stream/subscribe", "runtime_stream_subscribe")]
    [InlineData("POST", "/runtime/stream/unsubscribe", "runtime_stream_unsubscribe")]
    [InlineData("POST", "/runtime/watch/add", "runtime_watch_add")]
    [InlineData("POST", "/runtime/watch/remove", "runtime_watch_remove")]
    [InlineData("GET", "/runtime/watch/list", "runtime_watch_list")]
    [InlineData("GET", "/runtime/watch/poll", "runtime_watch_poll")]
    public void EveryBridgeEndpointHasACommand(string method, string path, string command)
    {
        Assert.Equal(command, EditorLinkRoutes.Resolve(method, path, null, "{}")?.Command);
    }

    [Theory]
    [InlineData("POST", "/stop")]
    [InlineData("POST", "/agent/exec")]
    [InlineData("GET", "/runtime/exec")]
    [InlineData("GET", "/hierarchy/command")]
    [InlineData("GET", "/nope")]
    public void UnmappedRequestsHaveNoCommand(string method, string path)
    {
        Assert.Null(EditorLinkRoutes.Resolve(method, path, null, null));
    }

    [Fact]
    public void BodyBecomesPayloadAndQueryBecomesArgs()
    {
        var command = EditorLinkRoutes.Resolve("POST", "/hierarchy/command/", null, "{\"action\":\"mk\"}")!;
        Assert.Equal("{\"action\":\"mk\"}", command.Args["payload"]);

        var status = EditorLinkRoutes.Resolve("GET", "/project/mutation/status", "requestId=a%20b", null)!;
        Assert.Equal("a b", status.Args["request-id"]);

        var log = EditorLinkRoutes.Resolve("GET", "/build/log", "offset=120&limit=50&errorsOnly=true", null)!;
        Assert.Equal(120L, log.Args["offset"]);
        Assert.Equal(50, log.Args["limit"]);
        Assert.Equal(true, log.Args["errors-only"]);

        var job = EditorLinkRoutes.Resolve("GET", "/runtime/job/status", "jobId=j1", null)!;
        Assert.Equal("j1", job.Args["job-id"]);
        Assert.Equal("w1", EditorLinkRoutes.Resolve("POST", "/runtime/watch/remove", "watchId=w1", null)!.Args["watch-id"]);
        Assert.Equal("s1", EditorLinkRoutes.Resolve("POST", "/runtime/stream/unsubscribe", "subscriptionId=s1", null)!.Args["subscription-id"]);
        Assert.Equal("editor:playmode", EditorLinkRoutes.Resolve("POST", "/runtime/attach", null, null)!.Args["target"]);
        Assert.Equal("player:2", EditorLinkRoutes.Resolve("POST", "/runtime/attach", "target=player%3A2", null)!.Args["target"]);

        var index = EditorLinkRoutes.Resolve("GET", "/asset-index", "revision=7", null)!;
        Assert.Equal(7, index.Args["revision"]);
        Assert.Equal(0, EditorLinkRoutes.Resolve("GET", "/asset-index", null, null)!.Args["revision"]);
    }

    [Fact]
    public void ProjectCommandsGetTheLongTimeout()
    {
        Assert.True(EditorLinkRoutes.Resolve("POST", "/project/command", null, "{}")!.Timeout >= TimeSpan.FromMinutes(5));
        Assert.True(EditorLinkRoutes.Resolve("GET", "/build/status", null, null)!.Timeout <= TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task EditorPortsNeverFallBackToHttp()
    {
        // Nothing listens on the port; a request without a command must be answered locally.
        const int port = 19999;
        EditorLinkHandler.RegisterProject(port, Path.GetTempPath());
        using var client = EditorLinkHandler.CreateClient(TimeSpan.FromSeconds(5));

        using var response = await client.PostAsync($"http://127.0.0.1:{port}/stop", null);

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public void RepliesMatchTheHttpBridge()
    {
        var ping = JsonDocument.Parse("{\"runtimeId\":\"abc\",\"bridge\":\"pipeline\"}").RootElement;
        Assert.Equal("PONG", EditorLinkHandler.FormatReply(EditorLinkReply.Pong, ping));
        Assert.Equal("OK", EditorLinkHandler.FormatReply(EditorLinkReply.Ok, ping));
        Assert.Equal("abc", EditorLinkHandler.FormatReply(EditorLinkReply.RuntimeId, ping));

        var payload = JsonDocument.Parse("{\"ok\":true,\"message\":\"x\"}").RootElement;
        Assert.Equal("{\"ok\":true,\"message\":\"x\"}", EditorLinkHandler.FormatReply(EditorLinkReply.Json, payload));
        Assert.Equal(string.Empty, EditorLinkHandler.FormatReply(EditorLinkReply.Json, null));
    }
}
