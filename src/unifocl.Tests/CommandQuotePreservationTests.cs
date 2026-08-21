using Xunit;

/// <summary>
/// Regression tests for issues #213 and #214: quote characters must survive the
/// exec/one-shot pipeline so paths with spaces stay one argument and /eval
/// snippets reach the daemon verbatim.
/// </summary>
public class CommandQuotePreservationTests
{
    // ── #213: NormalizeContextualInput must not strip quotes ─────────────────

    [Fact]
    public void NormalizeContextualInput_QuotedPathWithSpaces_SurvivesRoundTrip()
    {
        var input = "asset remove \"Assets/Asset Packs/Heat - Complete Modern UI\"";
        var normalized = ProjectCommandRouterService.NormalizeContextualInput(
            input, CliContextMode.Project, _ => { });

        Assert.NotNull(normalized);
        var tokens = ProjectViewServiceUtils.Tokenize(normalized!);
        Assert.Equal(3, tokens.Count);
        Assert.Equal("asset", tokens[0]);
        Assert.Equal("remove", tokens[1]);
        Assert.Equal("Assets/Asset Packs/Heat - Complete Modern UI", tokens[2]);
    }

    [Fact]
    public void NormalizeContextualInput_QuotedRenameArguments_StayGrouped()
    {
        var input = "asset rename \"Assets/A B/c.mat\" \"New Name\"";
        var normalized = ProjectCommandRouterService.NormalizeContextualInput(
            input, CliContextMode.Project, _ => { });

        var tokens = ProjectViewServiceUtils.Tokenize(normalized!);
        Assert.Equal(4, tokens.Count);
        Assert.Equal("Assets/A B/c.mat", tokens[2]);
        Assert.Equal("New Name", tokens[3]);
    }

    [Fact]
    public void NormalizeContextualInput_HeadAlias_StillRewritten()
    {
        var normalized = ProjectCommandRouterService.NormalizeContextualInput(
            "remove \"Assets/My Folder/Foo.asset\"", CliContextMode.Project, _ => { });

        Assert.NotNull(normalized);
        Assert.StartsWith("rm ", normalized!);
        var tokens = ProjectViewServiceUtils.Tokenize(normalized!);
        Assert.Equal(2, tokens.Count);
        Assert.Equal("Assets/My Folder/Foo.asset", tokens[1]);
    }

    [Fact]
    public void NormalizeContextualInput_UpmSubcommandAlias_StillRewritten()
    {
        var normalized = ProjectCommandRouterService.NormalizeContextualInput(
            "upm add com.unity.timeline", CliContextMode.Project, _ => { });

        Assert.Equal("upm install com.unity.timeline", normalized);
    }

    [Fact]
    public void NormalizeContextualInput_UnaliasedInput_PreservedVerbatimTokens()
    {
        var normalized = ProjectCommandRouterService.NormalizeContextualInput(
            "addressable entry add \"Assets/Asset Bundle/UI/Panel.prefab\" --group Default",
            CliContextMode.Project, _ => { });

        var tokens = ProjectViewServiceUtils.Tokenize(normalized!);
        Assert.Contains("Assets/Asset Bundle/UI/Panel.prefab", tokens);
    }

    [Fact]
    public void TryStripInspectorFocusFlags_PreservesQuotedArguments()
    {
        var input = "inspect \"/Canvas/My Panel\" --focus";
        var spans = CliCommandParsingService.TokenizeWithSpans(input);
        var tokens = new List<string>(spans.ConvertAll(s => s.Value));

        var stripped = ProjectCommandRouterService.TryStripInspectorFocusFlags(
            input, spans, tokens, out var strippedInput);

        Assert.True(stripped);
        Assert.Equal("inspect \"/Canvas/My Panel\"", strippedInput);
        Assert.Equal(2, tokens.Count);
        Assert.Equal("/Canvas/My Panel", tokens[1]);
    }

    // ── TokenizeWithSpans fundamentals ───────────────────────────────────────

    [Fact]
    public void TokenizeWithSpans_RawSpansIncludeQuoteCharacters()
    {
        var input = "asset get \"Assets/A B.mat\" field";
        var spans = CliCommandParsingService.TokenizeWithSpans(input);

        Assert.Equal(4, spans.Count);
        Assert.Equal("Assets/A B.mat", spans[2].Value);
        Assert.Equal("\"Assets/A B.mat\"", input.Substring(spans[2].Start, spans[2].Length));
    }

    // ── #214: /eval snippet must reach the daemon verbatim ───────────────────

    [Fact]
    public void TryParseEvalCommand_OuterSingleQuotes_AreStripped()
    {
        var ok = CliCommandParsingService.TryParseEvalCommand(
            "/eval 'return 1+1;'", out var args, out var error);

        Assert.True(ok, error);
        Assert.Equal("return 1+1;", args!.Code);
    }

    [Fact]
    public void TryParseEvalCommand_BareStatement_PassedVerbatim()
    {
        var ok = CliCommandParsingService.TryParseEvalCommand(
            "/eval return 1+1;", out var args, out _);

        Assert.True(ok);
        Assert.Equal("return 1+1;", args!.Code);
    }

    [Fact]
    public void TryParseEvalCommand_StringLiteral_KeepsInteriorQuotes()
    {
        var ok = CliCommandParsingService.TryParseEvalCommand(
            "/eval System.IO.Directory.Exists(\"/tmp\")", out var args, out _);

        Assert.True(ok);
        Assert.Equal("return (System.IO.Directory.Exists(\"/tmp\"));", args!.Code);
    }

    [Fact]
    public void TryParseEvalCommand_StringLiteralWithSpaces_KeptIntact()
    {
        var ok = CliCommandParsingService.TryParseEvalCommand(
            "/eval UnityEditor.AssetDatabase.IsValidFolder(\"Assets/Asset Packs/TerrainSampleAssets\")",
            out var args, out _);

        Assert.True(ok);
        Assert.Equal(
            "return (UnityEditor.AssetDatabase.IsValidFolder(\"Assets/Asset Packs/TerrainSampleAssets\"));",
            args!.Code);
    }

    [Fact]
    public void TryParseEvalCommand_MultiStatementBody_PassedVerbatim()
    {
        var ok = CliCommandParsingService.TryParseEvalCommand(
            "/eval 'var x = 2; return x * 2;'", out var args, out _);

        Assert.True(ok);
        Assert.Equal("var x = 2; return x * 2;", args!.Code);
    }

    [Fact]
    public void TryParseEvalCommand_TrailingFlags_ParsedAndExcludedFromCode()
    {
        var ok = CliCommandParsingService.TryParseEvalCommand(
            "/eval 'Debug.Log(\"hi there\");' --timeout 5000 --dry-run", out var args, out _);

        Assert.True(ok);
        Assert.Equal("Debug.Log(\"hi there\");", args!.Code);
        Assert.Equal(5000, args.TimeoutMs);
        Assert.True(args.DryRun);
    }

    [Fact]
    public void TryParseEvalCommand_Declarations_ExtractedWithSpaces()
    {
        var ok = CliCommandParsingService.TryParseEvalCommand(
            "/eval 'return X;' --declarations \"static int X = 5;\"", out var args, out _);

        Assert.True(ok);
        Assert.Equal("return X;", args!.Code);
        Assert.Equal("static int X = 5;", args.Declarations);
    }

    [Fact]
    public void TryParseEvalCommand_ExpressionStartingAndEndingWithQuote_NotUnwrapped()
    {
        // "hello" + "world" starts and ends with a double quote but is not a
        // wrapped snippet — the interior quote occurrences must prevent stripping.
        var ok = CliCommandParsingService.TryParseEvalCommand(
            "/eval \"hello\" + \"world\"", out var args, out _);

        Assert.True(ok);
        Assert.Equal("return (\"hello\" + \"world\");", args!.Code);
    }

    [Fact]
    public void TryParseEvalCommand_SemicolonInsideStringOnly_StillWrappedAsExpression()
    {
        var ok = CliCommandParsingService.TryParseEvalCommand(
            "/eval System.IO.Path.Combine(\"a;b\", \"c\")", out var args, out _);

        Assert.True(ok);
        Assert.Equal("return (System.IO.Path.Combine(\"a;b\", \"c\"));", args!.Code);
    }

    [Fact]
    public void TryParseEvalCommand_MissingCode_Fails()
    {
        var ok = CliCommandParsingService.TryParseEvalCommand(
            "/eval --dry-run", out _, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryParseEvalCommand_FlagBetweenCodeTokens_FailsLoudly()
    {
        var ok = CliCommandParsingService.TryParseEvalCommand(
            "/eval foo --timeout 500 bar", out _, out var error);

        Assert.False(ok);
        Assert.Contains("before or after", error);
    }

    [Fact]
    public void TryParseEvalCommand_FlagLikeTextInsideQuotedCode_TreatedAsCode()
    {
        var ok = CliCommandParsingService.TryParseEvalCommand(
            "/eval 'Debug.Log(\"--dry-run\");'", out var args, out _);

        Assert.True(ok);
        Assert.Equal("Debug.Log(\"--dry-run\");", args!.Code);
        Assert.False(args.DryRun);
    }
}
