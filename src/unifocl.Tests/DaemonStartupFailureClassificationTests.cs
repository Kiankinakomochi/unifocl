using Xunit;

/// <summary>
/// Regression tests for issue #121: daemon startup result filtering treated "Tundra build failed"
/// as a compile error, while <see cref="CliAgenticIssueService"/> downgrades the same line to a
/// recoverable warning during one-shot agentic parsing. Startup classification must prioritize
/// concrete compiler diagnostics and keep transient build-bootstrap failures out of the
/// compile-error diagnosis path.
/// </summary>
public class DaemonStartupFailureClassificationTests
{
    // ── ExtractCompileErrorLines ─────────────────────────────────────────────

    [Fact]
    public void ExtractCompileErrorLines_IgnoresTundraOnlyFailure()
    {
        var outputTail = new Queue<string>(new[]
        {
            "Refreshing native plugins compatible for Editor",
            "Tundra build failed (0.32 seconds), 1 items updated, 254 evaluated",
            "AssetDatabase: script compilation time: 0.5s"
        });

        Assert.Empty(DaemonControlService.ExtractCompileErrorLines(outputTail));
    }

    [Fact]
    public void ExtractCompileErrorLines_KeepsConcreteCompilerDiagnostics()
    {
        var outputTail = new Queue<string>(new[]
        {
            "Assets/Scripts/Broken.cs(12,5): error CS1002: ; expected",
            "Scripts have compiler errors.",
            "Script Compilation Error"
        });

        var lines = DaemonControlService.ExtractCompileErrorLines(outputTail);

        Assert.Equal(3, lines.Count);
        Assert.Contains(lines, line => line.Contains("error CS1002"));
    }

    [Fact]
    public void ExtractCompileErrorLines_MixedLog_KeepsCompilerErrorsAndDropsTundra()
    {
        var outputTail = new Queue<string>(new[]
        {
            "Tundra build failed (1.02 seconds), 3 items updated, 254 evaluated",
            "Assets/Scripts/Broken.cs(12,5): error CS0246: The type or namespace name 'Foo' could not be found"
        });

        var lines = DaemonControlService.ExtractCompileErrorLines(outputTail);

        var line = Assert.Single(lines);
        Assert.Contains("error CS0246", line);
    }

    // ── ExtractRecoverableBuildWarningLines ──────────────────────────────────

    [Fact]
    public void ExtractRecoverableBuildWarningLines_CollectsTundraFailures()
    {
        var outputTail = new Queue<string>(new[]
        {
            "Refreshing native plugins compatible for Editor",
            "TUNDRA BUILD FAILED (0.32 seconds), 1 items updated, 254 evaluated"
        });

        var warnings = DaemonControlService.ExtractRecoverableBuildWarningLines(outputTail);

        var warning = Assert.Single(warnings);
        Assert.Contains("TUNDRA BUILD FAILED", warning);
    }

    [Fact]
    public void ExtractRecoverableBuildWarningLines_IgnoresCompilerDiagnostics()
    {
        var outputTail = new Queue<string>(new[]
        {
            "Assets/Scripts/Broken.cs(12,5): error CS1002: ; expected",
            "Scripts have compiler errors."
        });

        Assert.Empty(DaemonControlService.ExtractRecoverableBuildWarningLines(outputTail));
    }

    // ── Alignment with CliAgenticIssueService ────────────────────────────────

    [Fact]
    public void AgenticParsing_StillDowngradesTundraBootstrapLineToRecoverableWarning()
    {
        var (errors, warnings, requiresEscalation, _) = CliAgenticIssueService.ParseAgenticIssuesFromLogs(
            new List<string> { "[grey]unity[/]: Tundra build failed (0.32 seconds), 1 items updated" });

        Assert.Empty(errors);
        Assert.False(requiresEscalation);
        var warning = Assert.Single(warnings);
        Assert.Equal("W_UNITY_COMPILE_RECOVERABLE", warning.Code);
    }

    [Fact]
    public void IsRecoverableBuildFailureLine_MatchesTundraCaseInsensitively()
    {
        Assert.True(CliAgenticIssueService.IsRecoverableBuildFailureLine("Tundra build failed (0.32 seconds)"));
        Assert.True(CliAgenticIssueService.IsRecoverableBuildFailureLine("unity: TUNDRA BUILD FAILED"));
        Assert.False(CliAgenticIssueService.IsRecoverableBuildFailureLine("error CS1002: ; expected"));
    }
}
