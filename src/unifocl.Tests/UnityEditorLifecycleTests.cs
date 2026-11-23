using Xunit;

public class UnityEditorLifecycleTests
{
    [Fact]
    public void OpenArgumentsStartABatchmodeEditorAndWait()
    {
        var args = UnityEditorLifecycle.BuildOpenArguments("/p/Game", ignoreCompileErrors: false, TimeSpan.FromSeconds(90.5), "/p/Game/Library/unifocl/editor.log");

        Assert.Equal("open", args[0]);
        Assert.Equal("/p/Game", args[1]);
        Assert.Contains("--batchmode", args);
        Assert.Contains("--until-ready", args);
        Assert.Equal("91", args[args.IndexOf("--timeout") + 1]);
        Assert.Equal("json", args[args.IndexOf("--format") + 1]);

        var editorArgs = args[args.IndexOf("--args") + 1];
        Assert.Contains("-logFile /p/Game/Library/unifocl/editor.log", editorArgs);
        Assert.Contains($"{UnityEditorLifecycle.IdleTimeoutArgument} {UnityEditorLifecycle.IdleTimeoutSeconds}", editorArgs);
        Assert.DoesNotContain("-ignoreCompileErrors", editorArgs);
    }

    [Fact]
    public void UnsafeOpenIgnoresCompileErrorsAndQuotesPaths()
    {
        var args = UnityEditorLifecycle.BuildOpenArguments("/p/My Game", ignoreCompileErrors: true, TimeSpan.FromSeconds(60), "/p/My Game/Library/unifocl/editor.log");
        var editorArgs = args[args.IndexOf("--args") + 1];

        Assert.Contains("-ignoreCompileErrors", editorArgs);
        Assert.Contains("-logFile \"/p/My Game/Library/unifocl/editor.log\"", editorArgs);
    }

    [Fact]
    public void ParsesOpenResults()
    {
        var launched = UnityEditorLifecycle.ParseOpen(
            """{"success":true,"data":{"launchedPid":16005,"ready":{"outcome":"ready"}},"errors":[]}""", string.Empty);
        Assert.True(launched.Ok);
        Assert.Equal(16005, launched.Pid);
        Assert.False(launched.AlreadyOpen);

        var reused = UnityEditorLifecycle.ParseOpen("""{"success":true,"data":{"alreadyOpen":true,"pid":17850},"errors":[]}""", string.Empty);
        Assert.True(reused.AlreadyOpen);
        Assert.Equal(17850, reused.Pid);

        var exited = UnityEditorLifecycle.ParseOpen(
            """{"success":false,"data":{"launchedPid":16598},"errors":[{"code":"OPEN_EDITOR_EXITED_BEFORE_READY","message":"The Unity Editor exited before it was ready."}]}""",
            string.Empty);
        Assert.False(exited.Ok);
        Assert.Equal("OPEN_EDITOR_EXITED_BEFORE_READY", exited.ErrorCode);
        Assert.Equal("The Unity Editor exited before it was ready.", exited.Error);

        var noJson = UnityEditorLifecycle.ParseOpen(string.Empty, "error: no Editor installed for 6000.9.0f1");
        Assert.False(noJson.Ok);
        Assert.Contains("no Editor installed", noJson.Error);
    }

    [Fact]
    public void ParsesCloseResultsWithDiscardedScenes()
    {
        var closed = UnityEditorLifecycle.ParseClose(
            """{"success":true,"data":{"closed":true,"unsaved":["Assets/Scenes/SampleScene.unity"]},"errors":[]}""", string.Empty);
        Assert.True(closed.Ok);
        Assert.Equal(["Assets/Scenes/SampleScene.unity"], closed.Unsaved);

        Assert.False(UnityEditorLifecycle.ParseClose(string.Empty, "error: nothing to close").Ok);
    }

    [Fact]
    public void ExtractsCompileErrorsFromTheEditorLog()
    {
        var errors = UnityEditorLifecycle.ExtractCompileErrors(
        [
            "Assets/Broken.cs(1,42): error CS0029: Cannot implicitly convert type 'string' to 'int'",
            "Assets/Broken.cs(1,42): error CS0029: Cannot implicitly convert type 'string' to 'int'",
            "Scripts have compiler errors.",
            "warning CS0168: unused",
        ]);

        Assert.Equal(["Assets/Broken.cs(1,42): error CS0029: Cannot implicitly convert type 'string' to 'int'"], errors);
    }

    [Fact]
    public void MatchesTheEditorForTheProject()
    {
        var editors = new List<PipelineEditorInstance>
        {
            new("/p/Other", 7801, 2, "ready", "6000.6.6f1", false),
            new("/p/Game/", 7800, 1, "ready", "6000.6.6f1", true),
        };

        Assert.Equal(1, UnityEditorLifecycle.Match(editors, "/p/Game")?.Pid);
        Assert.Null(UnityEditorLifecycle.Match(editors, "/p/Missing"));
    }
}
