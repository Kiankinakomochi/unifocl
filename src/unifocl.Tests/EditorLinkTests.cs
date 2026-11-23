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
    public void EveryBridgeEndpointHasACommand(string method, string path, string command)
    {
        Assert.Equal(command, EditorLinkRoutes.Resolve(method, path, null, "{}")?.Command);
    }

    [Theory]
    [InlineData("POST", "/stop")]
    [InlineData("GET", "/runtime/targets")]
    [InlineData("POST", "/runtime/exec")]
    [InlineData("GET", "/hierarchy/command")]
    [InlineData("GET", "/nope")]
    public void UnmappedRequestsFallThrough(string method, string path)
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
