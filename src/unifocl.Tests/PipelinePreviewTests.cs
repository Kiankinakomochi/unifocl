using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

public class PipelinePreviewTests
{
    [Fact]
    public void PreviewInputKeepsJsonWithSpaces()
    {
        Assert.True(RuntimeCommandService.TryParsePreview(
            "/preview gameobject create {\"name\": \"My Probe\", \"parent\": \"Root\"}", out var command, out var args, out _));

        Assert.Equal("gameobject create", command);
        Assert.Equal("My Probe", args!["name"]!.GetValue<string>());
        Assert.Equal("Root", args["parent"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("/preview")]
    [InlineData("/preview gameobject")]
    [InlineData("/preview gameobject {\"name\":\"x\"}")]
    [InlineData("/preview gameobject create {not json}")]
    [InlineData("/preview gameobject create [1,2]")]
    public void PreviewInputNeedsACommandAndAnObject(string input)
    {
        Assert.False(RuntimeCommandService.TryParsePreview(input, out _, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Theory]
    [InlineData("animation.clip create", "animation.clip", "create")]
    [InlineData("  asset   create_folder ", "asset", "create_folder")]
    public void CommandsSplitIntoGroupAndName(string command, string group, string name)
    {
        Assert.True(PipelinePreviewService.TrySplit(command, out var g, out var n));
        Assert.Equal(group, g);
        Assert.Equal(name, n);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("a b c")]
    public void CommandsWithoutExactlyTwoPartsAreRefused(string command)
    {
        Assert.False(PipelinePreviewService.TrySplit(command, out _, out _));
    }

    [Fact]
    public void ConfirmIsAddedOnlyWhenTheCommandAsksForItAndTheCallerDidNotDecide()
    {
        var asksForConfirm = JsonDocument.Parse("{\"target\":{\"hasConfirm\":true}}").RootElement;
        var noConfirm = JsonDocument.Parse("{\"target\":{\"hasConfirm\":false}}").RootElement;

        Assert.Equal(true, PipelinePreviewService.BuildArgs(new JsonObject { ["name"] = "x" }, asksForConfirm)["confirm"]);
        Assert.False(PipelinePreviewService.BuildArgs(null, noConfirm).ContainsKey("confirm"));
        Assert.False(PipelinePreviewService.BuildArgs(new JsonObject { ["dry-run"] = true }, asksForConfirm).ContainsKey("confirm"));
        var explicitConfirm = PipelinePreviewService.BuildArgs(new JsonObject { ["confirm"] = false }, asksForConfirm)["confirm"];
        Assert.False(((JsonNode)explicitConfirm!).GetValue<bool>());
    }

    [Fact]
    public void ReportSaysWhatChangedAndWhatWasNotUndone()
    {
        var report = JsonDocument.Parse("""
            {"reverted":false,"objectsAdded":["Assets/S.unity::Probe"],"filesCreated":["Assets/New"],
             "blocked":["save: Assets/S.unity"],"notReverted":["modified on disk: Assets/raw.txt"]}
            """).RootElement;
        var outcome = new PipelinePreviewOutcome(true, "gameobject create", true, null, null, report, null);

        var lines = PipelinePreviewService.Describe(outcome);

        Assert.False(outcome.Reverted);
        Assert.Contains(lines, l => l.Contains("added: Assets/S.unity::Probe"));
        Assert.Contains(lines, l => l.Contains("file created: Assets/New"));
        Assert.Contains(lines, l => l.Contains("write refused: save: Assets/S.unity"));
        Assert.Contains(lines, l => l.Contains("still in place: modified on disk: Assets/raw.txt"));
    }

    [Fact]
    public void ARevertedPreviewSaysSo()
    {
        var report = JsonDocument.Parse("{\"reverted\":true,\"objectsAdded\":[\"S::Probe\"]}").RootElement;
        var outcome = new PipelinePreviewOutcome(true, "gameobject create", true, null, null, report, null);

        Assert.True(outcome.Reverted);
        Assert.Contains(PipelinePreviewService.Describe(outcome), l => l.Contains("every change was undone"));
    }

    [Fact]
    public void PreviewIsACatalogedOperation()
    {
        Assert.True(new ExecCommandRegistry().IsKnown("preview.run"));
        Assert.Contains(CliCommandCatalog.CreateRootCommands(), c => c.Trigger == "/preview");
    }
}

public class PipelineArgumentFormatTests
{
    [Fact]
    public void JsonNodeStringsPassWithoutQuotes()
    {
        Assert.Equal("return 1;", PipelineOutput.FormatArgument(JsonValue.Create("return 1;")));
        Assert.Equal("true", PipelineOutput.FormatArgument(JsonValue.Create(true)));
        Assert.Equal("3", PipelineOutput.FormatArgument(JsonValue.Create(3)));
        Assert.Equal("{\"a\":1}", PipelineOutput.FormatArgument(new JsonObject { ["a"] = 1 }));
    }
}
