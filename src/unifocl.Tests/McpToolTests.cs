using Xunit;

/// <summary>
/// Unit tests for MCP server tool methods — called directly as static methods,
/// no MCP transport or running daemon needed.
/// </summary>
public class McpToolTests
{
    // ── ListCommands ──────────────────────────────────────────────────────────

    [Fact]
    public void ListCommands_CoreCategory_ReturnsNonEmpty()
    {
        var result = UnifoclCommandLookupTools.ListCommands(scope: "root", category: "core");
        Assert.True(result.Total > 0, "core category should contain commands");
        Assert.Equal("root", result.Scope);
    }

    [Fact]
    public void ListCommands_AllCategory_ReturnsMoreThanCore()
    {
        var core = UnifoclCommandLookupTools.ListCommands(scope: "all", category: "core");
        var all = UnifoclCommandLookupTools.ListCommands(scope: "all", category: "all");
        Assert.True(all.Total > core.Total, "all category should return more commands than core");
        Assert.NotNull(all.AvailableCategories);
        Assert.Contains("validate", all.AvailableCategories!);
    }

    [Fact]
    public void ListCommands_ValidateCategory_ContainsScripts()
    {
        var result = UnifoclCommandLookupTools.ListCommands(scope: "root", category: "validate");
        Assert.True(result.Total > 0);
        Assert.Contains(result.Commands, c =>
            c.Trigger.Contains("scripts", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ListCommands_QueryFilter_NarrowsResults()
    {
        var all = UnifoclCommandLookupTools.ListCommands(scope: "root", category: "all");
        var filtered = UnifoclCommandLookupTools.ListCommands(scope: "root", category: "all", query: "build");
        Assert.True(filtered.Total < all.Total, "query filter should narrow results");
        Assert.All(filtered.Commands, c =>
            Assert.True(
                c.Trigger.Contains("build", StringComparison.OrdinalIgnoreCase)
                || c.Signature.Contains("build", StringComparison.OrdinalIgnoreCase)
                || c.Description.Contains("build", StringComparison.OrdinalIgnoreCase),
                $"command '{c.Trigger}' should match 'build' query"));
    }

    [Fact]
    public void ListCommands_ScopeProject_ReturnsProjectCommands()
    {
        var result = UnifoclCommandLookupTools.ListCommands(scope: "project", category: "all");
        Assert.True(result.Total > 0);
        Assert.Equal("project", result.Scope);
    }

    [Fact]
    public void ListCommands_ScopeInspector_ReturnsInspectorCommands()
    {
        var result = UnifoclCommandLookupTools.ListCommands(scope: "inspector", category: "all");
        Assert.True(result.Total > 0);
        Assert.Equal("inspector", result.Scope);
    }

    [Fact]
    public void ListCommands_LimitCaps_Results()
    {
        var result = UnifoclCommandLookupTools.ListCommands(scope: "all", category: "all", limit: 3);
        Assert.True(result.Returned <= 3);
        Assert.True(result.Total >= result.Returned);
    }

    // ── LookupCommand ─────────────────────────────────────────────────────────

    [Fact]
    public void LookupCommand_ExactTrigger_ReturnsMatch()
    {
        var result = UnifoclCommandLookupTools.LookupCommand("/open");
        Assert.True(result.Total > 0, "/open should match at least one command");
        Assert.Contains(result.Commands, c => c.Trigger == "/open");
    }

    [Fact]
    public void LookupCommand_ValidateScripts_Findable()
    {
        var result = UnifoclCommandLookupTools.LookupCommand("/validate scripts");
        Assert.True(result.Total > 0, "/validate scripts should be discoverable");
        Assert.Contains(result.Commands, c =>
            c.Trigger.Contains("scripts", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LookupCommand_FuzzyMatch_ReturnsSuggestions()
    {
        var result = UnifoclCommandLookupTools.LookupCommand("build");
        Assert.True(result.Total > 1, "fuzzy 'build' should match multiple commands");
    }

    [Fact]
    public void LookupCommand_EmptyInput_ReturnsEmpty()
    {
        var result = UnifoclCommandLookupTools.LookupCommand("");
        Assert.Equal(0, result.Total);
    }

    // ── GetAgentWorkflowGuide ─────────────────────────────────────────────────

    [Fact]
    public void GetAgentWorkflowGuide_QuickStart_ReturnsValidJson()
    {
        var json = UnifoclAgentWorkflowTools.GetAgentWorkflowGuide("quick_start");
        Assert.Contains("overview", json);
        Assert.Contains("script_workflow", json);
    }

    [Fact]
    public void GetAgentWorkflowGuide_ScriptWorkflow_ReturnsNewSection()
    {
        var json = UnifoclAgentWorkflowTools.GetAgentWorkflowGuide("script_workflow");
        Assert.Contains("validate scripts", json);
        Assert.Contains("recommended_steps", json);
        Assert.Contains("asset.create_script", json);
        Assert.Contains("/validate asmdef", json);
    }

    [Fact]
    public void GetAgentWorkflowGuide_Modes_MentionsHostModeCompilation()
    {
        var json = UnifoclAgentWorkflowTools.GetAgentWorkflowGuide("modes");
        Assert.Contains("host_mode_compilation", json);
        Assert.Contains("batchmode", json);
    }

    [Fact]
    public void GetAgentWorkflowGuide_All_ContainsScriptWorkflow()
    {
        var json = UnifoclAgentWorkflowTools.GetAgentWorkflowGuide("all");
        Assert.Contains("script_workflow", json);
        Assert.Contains("host_mode_compilation", json);
        Assert.Contains("/validate scripts", json);
    }

    [Fact]
    public void GetAgentWorkflowGuide_UnknownSection_ReturnsError()
    {
        var json = UnifoclAgentWorkflowTools.GetAgentWorkflowGuide("nonexistent");
        Assert.Contains("error", json);
        Assert.Contains("available", json);
    }

    // ── GetMutateSchema ───────────────────────────────────────────────────────

    [Fact]
    public void GetMutateSchema_ReturnsValidJsonWithOps()
    {
        var json = UnifoclMutateTools.GetMutateSchema();
        Assert.Contains("create", json);
        Assert.Contains("set_field", json);
        Assert.Contains("add_component", json);
        Assert.Contains("mode_compatibility", json);
        // Should mention validate scripts in the host mode note
        Assert.Contains("validate scripts", json);
    }

    // ── ValidateMutateBatch ───────────────────────────────────────────────────

    [Fact]
    public void ValidateMutateBatch_ValidCreate_Passes()
    {
        var result = UnifoclMutateTools.ValidateMutateBatch(
            """[{"op":"create","type":"canvas","name":"HUD","parent":"/"}]""");
        Assert.True(result.Valid);
        Assert.Empty(result.Errors);
        Assert.Single(result.Items);
        Assert.True(result.Items[0].Valid);
    }

    [Fact]
    public void ValidateMutateBatch_MissingRequiredField_Fails()
    {
        var result = UnifoclMutateTools.ValidateMutateBatch(
            """[{"op":"rename","target":"/Foo"}]""");
        Assert.False(result.Valid);
        Assert.Contains(result.Errors, e => e.Contains("'name' required"));
    }

    [Fact]
    public void ValidateMutateBatch_UnknownOp_Fails()
    {
        var result = UnifoclMutateTools.ValidateMutateBatch(
            """[{"op":"explode","target":"/Foo"}]""");
        Assert.False(result.Valid);
        Assert.Contains(result.Errors, e => e.Contains("unknown op"));
    }

    [Fact]
    public void ValidateMutateBatch_EmptyArray_Fails()
    {
        var result = UnifoclMutateTools.ValidateMutateBatch("[]");
        Assert.False(result.Valid);
    }

    [Fact]
    public void ValidateMutateBatch_InvalidJson_Fails()
    {
        var result = UnifoclMutateTools.ValidateMutateBatch("{not json}");
        Assert.False(result.Valid);
        Assert.Contains(result.Errors, e => e.Contains("JSON parse error"));
    }

    [Fact]
    public void ValidateMutateBatch_MultipleMixedOps_ReportsPerOp()
    {
        var result = UnifoclMutateTools.ValidateMutateBatch("""
            [
                {"op":"create","type":"empty","name":"A","parent":"/"},
                {"op":"remove"},
                {"op":"set_field","target":"/A","component":"Transform","field":"position","value":"(0,0,0)"}
            ]
            """);
        Assert.False(result.Valid);
        Assert.Equal(3, result.Items.Count);
        Assert.True(result.Items[0].Valid);
        Assert.False(result.Items[1].Valid);
        Assert.True(result.Items[2].Valid);
    }

    // ── ExecCommandRegistry ───────────────────────────────────────────────────

    [Fact]
    public void ExecRegistry_ValidateScripts_IsRegistered()
    {
        var registry = new ExecCommandRegistry();
        Assert.True(registry.IsKnown("validate.scripts"));
        Assert.True(registry.TryGetRisk("validate.scripts", out var risk));
        Assert.Equal(ExecRiskLevel.SafeRead, risk);
    }

    [Theory]
    [InlineData("validate.scene-list")]
    [InlineData("validate.missing-scripts")]
    [InlineData("validate.packages")]
    [InlineData("validate.build-settings")]
    [InlineData("validate.asmdef")]
    [InlineData("validate.asset-refs")]
    [InlineData("validate.addressables")]
    [InlineData("validate.scripts")]
    public void ExecRegistry_AllValidateOps_AreRegistered(string op)
    {
        var registry = new ExecCommandRegistry();
        Assert.True(registry.IsKnown(op), $"operation '{op}' should be registered");
    }

    // ── Debug Artifact: ExecRegistry ──────────────────────────────────────────

    [Fact]
    public void ExecRegistry_DebugArtifactCollect_IsRegisteredAsSafeRead()
    {
        var registry = new ExecCommandRegistry();
        Assert.True(registry.IsKnown("debug-artifact.collect"), "debug-artifact.collect should be registered");
        Assert.True(registry.TryGetRisk("debug-artifact.collect", out var risk));
        Assert.Equal(ExecRiskLevel.SafeRead, risk);
    }

    [Fact]
    public void ExecRegistry_DebugArtifactPrep_IsRegisteredAsPrivilegedExec()
    {
        var registry = new ExecCommandRegistry();
        Assert.True(registry.IsKnown("debug-artifact.prep"), "debug-artifact.prep should be registered");
        Assert.True(registry.TryGetRisk("debug-artifact.prep", out var risk));
        Assert.Equal(ExecRiskLevel.PrivilegedExec, risk);
    }

    [Fact]
    public void ExecRegistry_DebugArtifactOps_AreRouterHandled()
    {
        var registry = new ExecCommandRegistry();
        Assert.False(
            registry.TryBuildProjectRequest(
                new ExecV2Request("test", null, "debug-artifact.collect", null, null),
                false, out _, out var collectError));
        Assert.Contains("handled by the router", collectError);

        Assert.False(
            registry.TryBuildProjectRequest(
                new ExecV2Request("test", null, "debug-artifact.prep", null, null),
                false, out _, out var prepError));
        Assert.Contains("handled by the router", prepError);
    }

    // ── Debug Artifact: CLI Catalog ───────────────────────────────────────────

    [Fact]
    public void CliCatalog_DebugArtifactCollect_IsRegistered()
    {
        var commands = CliCommandCatalog.CreateRootCommands();
        Assert.Contains(commands, c => c.Trigger == "/debug-artifact collect");
    }

    [Fact]
    public void CliCatalog_DebugArtifactPrep_IsRegistered()
    {
        var commands = CliCommandCatalog.CreateRootCommands();
        Assert.Contains(commands, c => c.Trigger == "/debug-artifact prep");
    }

    [Fact]
    public void CliCatalog_DebugArtifactCommands_InDiagCategory()
    {
        var commands = CliCommandCatalog.CreateRootCommands();
        var artifactCommands = commands.Where(c => c.Trigger.StartsWith("/debug-artifact")).ToList();
        Assert.True(artifactCommands.Count >= 3, "should have prep, collect, and parent entries");
        Assert.All(artifactCommands, c =>
            Assert.Equal("diag", c.Category));
    }

    // ── Debug Artifact: Workflow Guide ────────────────────────────────────────

    [Fact]
    public void GetAgentWorkflowGuide_DebugArtifact_ReturnsWorkflow()
    {
        var json = UnifoclAgentWorkflowTools.GetAgentWorkflowGuide("debug_artifact");
        Assert.DoesNotContain("\"error\"", json);
        Assert.Contains("prep", json);
        Assert.Contains("collect", json);
        Assert.Contains("playmode", json);
        Assert.Contains("profiler", json);
        Assert.Contains("tier", json);
    }

    [Fact]
    public void GetAgentWorkflowGuide_DebugArtifact_ContainsExecExamples()
    {
        var json = UnifoclAgentWorkflowTools.GetAgentWorkflowGuide("debug_artifact");
        Assert.Contains("exec_examples", json);
        Assert.Contains("--session-seed", json);
        Assert.Contains("debug-artifact.prep", json);
        Assert.Contains("debug-artifact.collect", json);
    }

    [Fact]
    public void GetAgentWorkflowGuide_DebugArtifact_DocumentsTiers()
    {
        var json = UnifoclAgentWorkflowTools.GetAgentWorkflowGuide("debug_artifact");
        Assert.Contains("\"0\"", json);
        Assert.Contains("\"1\"", json);
        Assert.Contains("\"2\"", json);
        Assert.Contains("\"3\"", json);
    }

    [Fact]
    public void GetAgentWorkflowGuide_QuickStart_MentionsDebugArtifact()
    {
        var json = UnifoclAgentWorkflowTools.GetAgentWorkflowGuide("quick_start");
        Assert.Contains("debug_artifact", json);
    }

    // ── Debug Artifact: Workflow Guide Section Registry ───────────────────────

    [Theory]
    [InlineData("quick_start")]
    [InlineData("exec_flags")]
    [InlineData("modes")]
    [InlineData("mutate")]
    [InlineData("categories")]
    [InlineData("session")]
    [InlineData("discovery")]
    [InlineData("script_workflow")]
    [InlineData("debug_artifact")]
    [InlineData("all")]
    public void GetAgentWorkflowGuide_AllSections_ReturnNonError(string section)
    {
        var json = UnifoclAgentWorkflowTools.GetAgentWorkflowGuide(section);
        Assert.DoesNotContain("\"error\"", json);
    }

    // ── Debug Artifact: Runtime Command Catalog ──────────────────────────────

    [Theory]
    [InlineData("/console dump")]
    [InlineData("/console tail")]
    [InlineData("/console clear")]
    [InlineData("/playmode start")]
    [InlineData("/playmode stop")]
    [InlineData("/profiler inspect")]
    [InlineData("/profiler start")]
    [InlineData("/profiler stop")]
    [InlineData("/recorder start")]
    [InlineData("/recorder stop")]
    [InlineData("/recorder status")]
    [InlineData("/time scale")]
    [InlineData("/compile request")]
    [InlineData("/compile status")]
    public void CliCatalog_RuntimeCommands_AreRegistered(string trigger)
    {
        var commands = CliCommandCatalog.CreateRootCommands();
        Assert.Contains(commands, c => c.Trigger == trigger);
    }

    // ── Debug Artifact: ExecRegistry Runtime Ops ─────────────────────────────

    [Theory]
    [InlineData("console dump")]
    [InlineData("console clear")]
    [InlineData("playmode.start")]
    [InlineData("playmode.stop")]
    [InlineData("time.scale")]
    [InlineData("compile.status")]
    [InlineData("compile.request")]
    [InlineData("profiling.inspect")]
    [InlineData("profiling.start_recording")]
    [InlineData("profiling.stop_recording")]
    [InlineData("recorder.start")]
    [InlineData("recorder.stop")]
    [InlineData("recorder.status")]
    public void ExecRegistry_RuntimeOps_AreRegistered(string op)
    {
        var registry = new ExecCommandRegistry();
        Assert.True(registry.IsKnown(op), $"operation '{op}' should be registered");
    }

    [Fact]
    public void ExecRegistry_RuntimeOps_HaveExpectedRiskLevels()
    {
        var registry = new ExecCommandRegistry();

        void AssertRisk(string op, ExecRiskLevel expected)
        {
            Assert.True(registry.TryGetRisk(op, out var risk), $"{op} should have a risk level");
            Assert.Equal(expected, risk);
        }

        AssertRisk("console dump", ExecRiskLevel.SafeRead);
        AssertRisk("console clear", ExecRiskLevel.SafeWrite);
        AssertRisk("playmode.start", ExecRiskLevel.PrivilegedExec);
        AssertRisk("playmode.stop", ExecRiskLevel.PrivilegedExec);
        AssertRisk("time.scale", ExecRiskLevel.SafeWrite);
        AssertRisk("compile.status", ExecRiskLevel.SafeRead);
        AssertRisk("compile.request", ExecRiskLevel.SafeWrite);
        AssertRisk("profiling.inspect", ExecRiskLevel.SafeRead);
        AssertRisk("profiling.start_recording", ExecRiskLevel.PrivilegedExec);
        AssertRisk("profiling.stop_recording", ExecRiskLevel.PrivilegedExec);
        AssertRisk("recorder.start", ExecRiskLevel.PrivilegedExec);
        AssertRisk("recorder.stop", ExecRiskLevel.PrivilegedExec);
        AssertRisk("recorder.status", ExecRiskLevel.SafeRead);
    }
}
