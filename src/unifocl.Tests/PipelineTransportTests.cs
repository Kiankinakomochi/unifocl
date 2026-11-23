using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

public class PipelineTransportTests
{
    // ── UnityCliLocator ───────────────────────────────────────────────────────

    [Fact]
    public void Locate_PrefersOverride()
    {
        var found = UnityCliLocator.Resolve("/opt/u/unity", "/usr/bin", "/home/me", isWindows: false, p => p == "/opt/u/unity");
        Assert.Equal("/opt/u/unity", found);
    }

    [Fact]
    public void Locate_MissingOverride_DoesNotFallBack()
    {
        // An explicit override that points nowhere is a configuration error, not a hint to search.
        var found = UnityCliLocator.Resolve("/nope/unity", "/usr/bin", "/home/me", isWindows: false, p => p == "/usr/bin/unity");
        Assert.Null(found);
    }

    [Fact]
    public void Locate_SearchesPathThenInstallerDefault()
    {
        Assert.Equal("/b/unity",
            UnityCliLocator.Resolve(null, "/a:/b", "/home/me", isWindows: false, p => p == "/b/unity"));
        Assert.Equal(Path.Combine("/home/me", ".unity", "bin", "unity"),
            UnityCliLocator.Resolve(null, "/a", "/home/me", isWindows: false, p => p.EndsWith(".unity/bin/unity")));
    }

    // ── Arguments ─────────────────────────────────────────────────────────────

    [Fact]
    public void BuildArguments_PutsCommandFirstAndFormatsValues()
    {
        var argv = UnityCommandTransport.BuildArguments(
            "unifocl.tool.spike",
            "spawn_cubes",
            new Dictionary<string, object?>
            {
                ["args"] = "{\"count\":2}",
                ["dry-run"] = true,
                ["limit"] = 2.5,
                ["skipped"] = null,
                ["names"] = new[] { "a", "b" },
            },
            "/p",
            TimeSpan.FromSeconds(9.2));

        Assert.Equal(
            new[]
            {
                "command", "unifocl.tool.spike", "spawn_cubes",
                "--args", "{\"count\":2}",
                "--dry-run", "true",
                "--limit", "2.5",
                "--names", "[\"a\",\"b\"]",
                "--project-path", "/p",
                "--timeout", "10",
                "--no-banner", "--non-interactive", "--format", "json",
            },
            argv);
    }

    [Fact]
    public void ToolArguments_NestsArgsUnderCommand()
    {
        var node = UnityMcpTransport.BuildToolArguments("validate", new Dictionary<string, object?> { ["check"] = "scene-list", ["x"] = null });
        Assert.Equal("{\"command\":\"validate\",\"args\":{\"check\":\"scene-list\"}}", node.ToJsonString());
        Assert.Equal("unifocl_tool_spike", UnityMcpTransport.ToolName("unifocl.tool.spike"));
    }

    // ── Results ───────────────────────────────────────────────────────────────

    [Fact]
    public void ParseResult_SingleObject()
    {
        var result = PipelineOutput.ParseResult("{\"passed\":true}\n");
        Assert.True(result!.Value.GetProperty("passed").GetBoolean());
    }

    [Fact]
    public void ParseResult_RecordLinesBecomeArray()
    {
        var result = PipelineOutput.ParseResult("{\"tool\":\"a\"}\n{\"tool\":\"b\"}\n");
        Assert.Equal(JsonValueKind.Array, result!.Value.ValueKind);
        Assert.Equal("b", result.Value[1].GetProperty("tool").GetString());
    }

    [Fact]
    public void ParseResult_TextAndEmpty()
    {
        Assert.Equal("Hello world!", PipelineOutput.ParseResult("Hello world!\n")!.Value.GetString());
        Assert.Null(PipelineOutput.ParseResult("  \n"));
    }

    [Fact]
    public void ParseError_CliStderr()
    {
        var message = PipelineOutput.ParseError(
            "error: Refusing to run 'spike.spawn_cubes'. Pass confirm=true to run it, or dry-run=true to preview it.\n",
            "fallback");
        Assert.Equal("Refusing to run 'spike.spawn_cubes'. Pass confirm=true to run it, or dry-run=true to preview it.", message);
    }

    [Fact]
    public void ParseError_McpServerPrefixIsStripped()
    {
        var message = PipelineOutput.ParseError(
            "Error: Pipeline server returned 400 Bad Request: Parameter Validation Failed. unknown check 'bogus'; expected scene-list",
            "fallback");
        Assert.Equal("unknown check 'bogus'; expected scene-list", message);

        Assert.Equal(
            "The unifocl tool has no command named nope. Its commands are: tool_list.",
            PipelineOutput.ParseError("Error: The unifocl tool has no command named nope. Its commands are: tool_list.", "x"));
    }

    [Fact]
    public void ParseToolResponse_SuccessErrorAndRpcError()
    {
        var ok = UnityMcpTransport.ParseToolResponse(JsonNode.Parse(
            "{\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"{\\n  \\\"passed\\\": true\\n}\"}]},\"jsonrpc\":\"2.0\",\"id\":10}")!.AsObject());
        Assert.True(ok.Ok);
        Assert.True(ok.Result!.Value.GetProperty("passed").GetBoolean());

        var failed = UnityMcpTransport.ParseToolResponse(JsonNode.Parse(
            "{\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"Error: Pipeline server returned 400 Bad Request: Parameter Validation Failed. Refusing to run 'x'.\"}],\"isError\":true},\"id\":11}")!.AsObject());
        Assert.False(failed.Ok);
        Assert.Equal("Refusing to run 'x'.", failed.Error);

        var refused = UnityMcpTransport.ParseToolResponse(JsonNode.Parse(
            "{\"error\":{\"code\":-32602,\"message\":\"Unknown tool\"},\"id\":12}")!.AsObject());
        Assert.False(refused.Ok);
        Assert.Equal("Unknown tool", refused.Error);
    }

    [Fact]
    public void ParseStatus_ReadsInstances()
    {
        const string json = """
            {"success":true,"command":"status","data":{"count":1,"instances":[
              {"port":7800,"projectPath":"/p","version":"6000.6.6f1","editorVersion":"6000.6.6f1","pid":81069,"state":"ready","batchmode":true}
            ]},"errors":[]}
            """;
        var instance = Assert.Single(UnityCommandTransport.ParseStatus(json));
        Assert.Equal(7800, instance.Port);
        Assert.True(instance.IsReady);
        Assert.True(instance.Batchmode);
        Assert.Empty(UnityCommandTransport.ParseStatus("{\"success\":false,\"data\":{\"instances\":[]}}"));
        Assert.Empty(UnityCommandTransport.ParseStatus("not json"));
    }

    // ── Live (opt-in) ─────────────────────────────────────────────────────────

    /// <summary>
    /// Runs both transports against a real Editor. Set UNIFOCL_PIPELINE_TEST_PROJECT to a Unity project
    /// with com.unity.pipeline and the unifocl bridge open in an Editor; otherwise this does nothing.
    /// </summary>
    [Fact]
    public async Task Live_BothTransportsAgree()
    {
        var project = Environment.GetEnvironmentVariable("UNIFOCL_PIPELINE_TEST_PROJECT");
        if (string.IsNullOrWhiteSpace(project))
        {
            return;
        }

        var unityCli = UnityCliLocator.Locate();
        Assert.NotNull(unityCli);

        await using var command = new UnityCommandTransport(unityCli!, project);
        await using var mcp = new UnityMcpTransport(unityCli!, project);
        Assert.Contains(await command.StatusAsync(), i => i.IsReady);

        foreach (IPipelineTransport transport in new IPipelineTransport[] { command, mcp })
        {
            var validate = await transport.ExecAsync("unifocl", "validate", new Dictionary<string, object?> { ["check"] = "missing-scripts" });
            Assert.True(validate.Ok, validate.Error);
            Assert.Equal("missing-scripts", validate.Result!.Value.GetProperty("validator").GetString());

            var bogus = await transport.ExecAsync("unifocl", "validate", new Dictionary<string, object?> { ["check"] = "bogus" });
            Assert.False(bogus.Ok);
            Assert.StartsWith("unknown check 'bogus'", bogus.Error);

            var tools = await transport.ExecAsync("unifocl", "tool_list");
            Assert.True(tools.Ok, tools.Error);
            Assert.Equal(JsonValueKind.Array, tools.Result!.Value.ValueKind);
        }
    }
}
