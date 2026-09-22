using Xunit;

/// <summary>
/// /probuilder binds positionals and --kebab-case options onto probuilder.* tool parameters; these
/// cover the binding rules the Unity side relies on (names, switches, negative values, variadics).
/// </summary>
public class ProBuilderCommandArgsTests
{
    private static RuntimeCommandService.ProBuilderCliOperation Op(string key)
        => RuntimeCommandService.ProBuilderOperations[key];

    [Fact]
    public void ShapeCreate_BindsPositionalOptionsSwitchesAndDryRun()
    {
        var ok = RuntimeCommandService.TryBuildProBuilderArgs(
            Op("shape create"),
            ["cube", "--name", "Floor", "--size", "10,0.2,10", "--pivot", "bottom", "--no-collider", "--dry-run"],
            out var args, out var dryRun, out var error);

        Assert.True(ok, error);
        Assert.True(dryRun);
        Assert.Equal("cube", args["shape"]);
        Assert.Equal("Floor", args["name"]);
        Assert.Equal("10,0.2,10", args["size"]);
        Assert.Equal("bottom", args["pivot"]);
        Assert.Equal("false", args["collider"]);
        Assert.False(args.ContainsKey("dryRun"));
    }

    [Fact]
    public void KebabCaseFlags_MapToCamelCaseParameters()
    {
        var ok = RuntimeCommandService.TryBuildProBuilderArgs(
            Op("mesh info"),
            ["/Level/Floor", "--max-faces", "10", "--no-include-faces"],
            out var args, out _, out var error);

        Assert.True(ok, error);
        Assert.Equal("/Level/Floor", args["target"]);
        Assert.Equal("10", args["maxFaces"]);
        Assert.Equal("false", args["includeFaces"]);
    }

    [Fact]
    public void NegativeVectorValue_IsTreatedAsOptionValue()
    {
        var ok = RuntimeCommandService.TryBuildProBuilderArgs(
            Op("face move"),
            ["Wall", "--faces", "left", "--offset", "-1,0,0"],
            out var args, out _, out var error);

        Assert.True(ok, error);
        Assert.Equal("-1,0,0", args["offset"]);
        Assert.Equal("left", args["faces"]);
    }

    [Fact]
    public void Merge_JoinsRemainingPositionalsWithSemicolons()
    {
        var ok = RuntimeCommandService.TryBuildProBuilderArgs(
            Op("mesh merge"),
            ["/A", "/Group/B", "C D", "--name", "Merged"],
            out var args, out _, out var error);

        Assert.True(ok, error);
        Assert.Equal("/A;/Group/B;C D", args["targets"]);
        Assert.Equal("Merged", args["name"]);
    }

    [Theory]
    [InlineData("--bogus", "x")]
    [InlineData("--faces")]
    public void InvalidOptions_AreRejected(params string[] extra)
    {
        var tokens = new List<string> { "Wall" };
        tokens.AddRange(extra);

        var ok = RuntimeCommandService.TryBuildProBuilderArgs(Op("face extrude"), tokens, out _, out _, out var error);

        Assert.False(ok);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void MissingAndExtraPositionals_AreRejected()
    {
        Assert.False(RuntimeCommandService.TryBuildProBuilderArgs(Op("mesh export"), ["/A"], out _, out _, out var missing));
        Assert.Contains("assetPath", missing);

        Assert.False(RuntimeCommandService.TryBuildProBuilderArgs(Op("face flip"), ["/A", "/B"], out _, out _, out var extra));
        Assert.Contains("/B", extra);
    }

    [Fact]
    public void EverySubcommand_IsListedInTheRootCatalog()
    {
        var triggers = CliCommandCatalog.CreateRootCommands().Select(spec => spec.Trigger).ToHashSet();

        foreach (var key in RuntimeCommandService.ProBuilderOperations.Keys)
        {
            Assert.Contains($"/probuilder {key}", triggers);
        }
    }
}
