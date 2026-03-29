using Spectre.Console;
using System.Text;
using System.Text.RegularExpressions;

internal static class MarkdownRenderer
{
    // ─── Public entry point ────────────────────────────────────────────────────

    /// <summary>
    /// Renders markdown text as Spectre markup lines emitted via the log callback.
    /// Each call to <paramref name="log"/> receives one fully-formed Spectre markup string.
    /// </summary>
    public static void RenderToLog(string markdown, Action<string> log)
    {
        if (string.IsNullOrEmpty(markdown))
            return;

        var blocks = ParseBlocks(markdown);
        foreach (var block in blocks)
            RenderBlock(block, log);
    }

    // ─── Block model ───────────────────────────────────────────────────────────

    private enum BlockKind
    {
        Heading,
        Paragraph,
        FencedCode,
        BulletList,
        OrderedList,
        Blockquote,
        Rule,
        Blank
    }

    private sealed record MarkdownBlock(
        BlockKind Kind,
        int Level,
        string Language,
        List<string> Lines);

    // ─── Block parser ──────────────────────────────────────────────────────────

    private static List<MarkdownBlock> ParseBlocks(string markdown)
    {
        var normalized = markdown
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        var rawLines = normalized.Split('\n');
        var blocks = new List<MarkdownBlock>();

        // Mutable accumulator state
        BlockKind? currentKind = null;
        int currentLevel = 0;
        string currentLanguage = string.Empty;
        var currentLines = new List<string>();
        bool inFencedCode = false;

        void Flush()
        {
            if (currentKind is null || currentLines.Count == 0)
                return;
            blocks.Add(new MarkdownBlock(currentKind.Value, currentLevel, currentLanguage, new List<string>(currentLines)));
            currentKind = null;
            currentLevel = 0;
            currentLanguage = string.Empty;
            currentLines.Clear();
        }

        foreach (var rawLine in rawLines)
        {
            // ── Inside fenced code block ────────────────────────────────────────
            if (inFencedCode)
            {
                if (rawLine.TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    // Closing fence — flush the code block
                    Flush();
                    inFencedCode = false;
                }
                else
                {
                    currentLines.Add(rawLine);
                }
                continue;
            }

            // ── Opening fenced code block ───────────────────────────────────────
            var trimmed = rawLine.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                Flush();
                var lang = trimmed.Substring(3).Trim();
                currentKind = BlockKind.FencedCode;
                currentLanguage = lang;
                currentLevel = 0;
                inFencedCode = true;
                continue;
            }

            // ── Heading ─────────────────────────────────────────────────────────
            if (trimmed.StartsWith("### ", StringComparison.Ordinal))
            {
                Flush();
                blocks.Add(new MarkdownBlock(BlockKind.Heading, 3, string.Empty, [trimmed.Substring(4)]));
                continue;
            }
            if (trimmed.StartsWith("## ", StringComparison.Ordinal))
            {
                Flush();
                blocks.Add(new MarkdownBlock(BlockKind.Heading, 2, string.Empty, [trimmed.Substring(3)]));
                continue;
            }
            if (trimmed.StartsWith("# ", StringComparison.Ordinal))
            {
                Flush();
                blocks.Add(new MarkdownBlock(BlockKind.Heading, 1, string.Empty, [trimmed.Substring(2)]));
                continue;
            }

            // ── Horizontal rule (---, ___, ***) ────────────────────────────────
            if (Regex.IsMatch(trimmed, @"^(-{3,}|_{3,}|\*{3,})$"))
            {
                Flush();
                blocks.Add(new MarkdownBlock(BlockKind.Rule, 0, string.Empty, []));
                continue;
            }

            // ── Blank line ──────────────────────────────────────────────────────
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                Flush();
                blocks.Add(new MarkdownBlock(BlockKind.Blank, 0, string.Empty, []));
                continue;
            }

            // ── Blockquote ──────────────────────────────────────────────────────
            if (trimmed.StartsWith("> ", StringComparison.Ordinal) || trimmed == ">")
            {
                var content = trimmed.Length > 2 ? trimmed.Substring(2) : string.Empty;
                if (currentKind == BlockKind.Blockquote)
                {
                    currentLines.Add(content);
                }
                else
                {
                    Flush();
                    currentKind = BlockKind.Blockquote;
                    currentLines.Add(content);
                }
                continue;
            }

            // ── Bullet list ─────────────────────────────────────────────────────
            if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal))
            {
                var content = trimmed.Substring(2);
                if (currentKind == BlockKind.BulletList)
                {
                    currentLines.Add(content);
                }
                else
                {
                    Flush();
                    currentKind = BlockKind.BulletList;
                    currentLines.Add(content);
                }
                continue;
            }

            // ── Ordered list ────────────────────────────────────────────────────
            var orderedMatch = Regex.Match(trimmed, @"^(\d+)\.\s+(.*)$");
            if (orderedMatch.Success)
            {
                var content = orderedMatch.Groups[2].Value;
                if (currentKind == BlockKind.OrderedList)
                {
                    currentLines.Add(content);
                }
                else
                {
                    Flush();
                    currentKind = BlockKind.OrderedList;
                    currentLines.Add(content);
                }
                continue;
            }

            // ── Default: paragraph ──────────────────────────────────────────────
            if (currentKind == BlockKind.Paragraph)
            {
                // Join continuation lines with a space
                currentLines.Add(rawLine);
            }
            else
            {
                Flush();
                currentKind = BlockKind.Paragraph;
                currentLines.Add(rawLine);
            }
        }

        Flush();
        return blocks;
    }

    // ─── Block renderers ───────────────────────────────────────────────────────

    private static void RenderBlock(MarkdownBlock block, Action<string> log)
    {
        switch (block.Kind)
        {
            case BlockKind.Heading:
                RenderHeading(block.Level, block.Lines.Count > 0 ? block.Lines[0] : string.Empty, log);
                break;
            case BlockKind.Paragraph:
                RenderParagraph(string.Join(" ", block.Lines), log);
                break;
            case BlockKind.FencedCode:
                RenderCodeBlock(block.Language, block.Lines, log);
                break;
            case BlockKind.BulletList:
                RenderBulletList(block.Lines, log);
                break;
            case BlockKind.OrderedList:
                RenderOrderedList(block.Lines, log);
                break;
            case BlockKind.Blockquote:
                RenderBlockquote(block.Lines, log);
                break;
            case BlockKind.Rule:
                RenderRule(log);
                break;
            case BlockKind.Blank:
                log(string.Empty);
                break;
        }
    }

    private static void RenderHeading(int level, string content, Action<string> log)
    {
        var width = GetConsoleWidth(72);
        var text = RenderInline(content);

        switch (level)
        {
            case 1:
                var h1Rule = $"[bold {CliTheme.Brand}]{new string('━', width)}[/]";
                log(h1Rule);
                log($"[bold {CliTheme.Brand}]  {text}[/]");
                log(h1Rule);
                break;
            case 2:
                log($"[bold {CliTheme.Brand}]{new string('─', width)}[/]");
                log($"[bold {CliTheme.Brand}]  {text}[/]");
                break;
            case 3:
                log($"[bold {CliTheme.TextPrimary}]  {text}[/]");
                break;
            default:
                log($"[bold {CliTheme.TextPrimary}]{text}[/]");
                break;
        }
    }

    private static void RenderParagraph(string content, Action<string> log)
    {
        var rendered = RenderInline(content);
        log(rendered);
    }

    private static void RenderCodeBlock(string language, List<string> lines, Action<string> log)
    {
        var width = Math.Max(40, GetConsoleWidth(72));

        // Build top border: ┌─ lang ───────────────────┐
        var langLabel = string.IsNullOrWhiteSpace(language) ? string.Empty : $" {language} ";
        var topFill = Math.Max(0, width - 2 - langLabel.Length); // 2 for ┌ and ┐
        var topBorder = $"┌─{langLabel}{new string('─', topFill)}┐";
        log($"[{CliTheme.TextMuted}]{Markup.Escape(topBorder)}[/]");

        // Content lines with syntax highlighting
        foreach (var line in lines)
        {
            var highlighted = ApplySyntaxHighlighting(line, language);
            // Pad with 2 leading spaces, trim to fit within box (width - 4 for borders + spaces)
            log($"  {highlighted}");
        }

        // Bottom border: └─────────────────────────────┘
        var bottomBorder = $"└{new string('─', width - 2)}┘";
        log($"[{CliTheme.TextMuted}]{Markup.Escape(bottomBorder)}[/]");
    }

    private static void RenderBulletList(List<string> items, Action<string> log)
    {
        foreach (var item in items)
        {
            var rendered = RenderInline(item);
            log($"[{CliTheme.Brand}]  ●[/] [{CliTheme.TextPrimary}]{rendered}[/]");
        }
    }

    private static void RenderOrderedList(List<string> items, Action<string> log)
    {
        for (var i = 0; i < items.Count; i++)
        {
            var rendered = RenderInline(items[i]);
            log($"[{CliTheme.TextSecondary}]  {i + 1}.[/] [{CliTheme.TextPrimary}]{rendered}[/]");
        }
    }

    private static void RenderBlockquote(List<string> lines, Action<string> log)
    {
        foreach (var line in lines)
        {
            var rendered = RenderInline(line);
            log($"[{CliTheme.TextMuted}]▎[/] [italic {CliTheme.TextSecondary}]{rendered}[/]");
        }
    }

    private static void RenderRule(Action<string> log)
    {
        var width = GetConsoleWidth(72);
        log($"[{CliTheme.TextMuted}]{new string('─', width)}[/]");
    }

    // ─── Inline renderer ───────────────────────────────────────────────────────

    // Matches inline code, bold, italic in priority order.
    // Named groups: ic=inline-code, bold=bold, italic=italic, text=plain text
    private static readonly Regex InlinePattern = new(
        @"(?:`(?<ic>[^`]+)`)" +
        @"|(?:\*\*(?<bold>[^*]+)\*\*)" +
        @"|(?:__(?<bold2>[^_]+)__)" +
        @"|(?:\*(?<italic>[^*]+)\*)" +
        @"|(?:_(?<italic2>[^_]+)_)" +
        @"|(?<text>[^`*_]+)",
        RegexOptions.Compiled);

    private static string RenderInline(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var sb = new StringBuilder();
        var matches = InlinePattern.Matches(text);

        foreach (Match m in matches)
        {
            if (m.Groups["ic"].Success)
            {
                var code = Markup.Escape(m.Groups["ic"].Value);
                sb.Append($"[bold {CliTheme.TextPrimary} on {CliTheme.SurfaceBackground}] {code} [/]");
            }
            else if (m.Groups["bold"].Success)
            {
                var bold = Markup.Escape(m.Groups["bold"].Value);
                sb.Append($"[bold {CliTheme.TextPrimary}]{bold}[/]");
            }
            else if (m.Groups["bold2"].Success)
            {
                var bold = Markup.Escape(m.Groups["bold2"].Value);
                sb.Append($"[bold {CliTheme.TextPrimary}]{bold}[/]");
            }
            else if (m.Groups["italic"].Success)
            {
                var italic = Markup.Escape(m.Groups["italic"].Value);
                sb.Append($"[italic {CliTheme.TextSecondary}]{italic}[/]");
            }
            else if (m.Groups["italic2"].Success)
            {
                var italic = Markup.Escape(m.Groups["italic2"].Value);
                sb.Append($"[italic {CliTheme.TextSecondary}]{italic}[/]");
            }
            else if (m.Groups["text"].Success)
            {
                sb.Append(Markup.Escape(m.Groups["text"].Value));
            }
        }

        return sb.ToString();
    }

    // ─── Syntax highlighting ───────────────────────────────────────────────────

    private static string ApplySyntaxHighlighting(string rawLine, string language)
    {
        if (string.IsNullOrEmpty(rawLine))
            return string.Empty;

        return language.ToLowerInvariant() switch
        {
            "cs" or "csharp" or "c#" => HighlightCSharp(rawLine),
            "js" or "javascript" or "ts" or "typescript" => HighlightJavaScript(rawLine),
            "json" => HighlightJson(rawLine),
            "sh" or "bash" or "shell" or "zsh" => HighlightShell(rawLine),
            _ => $"[{CliTheme.TextSecondary}]{Markup.Escape(rawLine)}[/]"
        };
    }

    // C# and JavaScript keywords share a common base
    private static readonly string[] CSharpKeywords =
    [
        "using", "namespace", "class", "struct", "interface", "enum", "record",
        "public", "private", "protected", "internal", "static", "abstract",
        "override", "virtual", "sealed", "new", "return", "if", "else",
        "for", "foreach", "while", "switch", "case", "break", "continue",
        "var", "void", "string", "int", "bool", "null", "true", "false",
        "async", "await", "readonly", "const", "this", "base", "throw",
        "try", "catch", "finally", "in", "out", "ref", "params", "is", "as",
        "get", "set", "value", "when", "where"
    ];

    private static readonly string[] JavaScriptKeywords =
    [
        "function", "var", "let", "const", "class", "extends", "new", "return",
        "if", "else", "for", "while", "switch", "case", "break", "continue",
        "import", "export", "default", "from", "typeof", "instanceof", "in",
        "null", "undefined", "true", "false", "async", "await", "of",
        "try", "catch", "finally", "throw", "this", "super", "static",
        "get", "set", "type", "interface", "enum", "as", "void"
    ];

    private static string HighlightCSharp(string rawLine) =>
        HighlightGenericCode(rawLine, CSharpKeywords, singleLineComment: "//");

    private static string HighlightJavaScript(string rawLine) =>
        HighlightGenericCode(rawLine, JavaScriptKeywords, singleLineComment: "//");

    private static string HighlightGenericCode(string rawLine, string[] keywords, string singleLineComment)
    {
        // Fast path: full-line comment
        var stripped = rawLine.TrimStart();
        if (stripped.StartsWith(singleLineComment, StringComparison.Ordinal))
            return $"[italic {CliTheme.TextMuted}]{Markup.Escape(rawLine)}[/]";

        // Tokenize: comment, string, keyword, identifier, other
        var pattern =
            $@"({Regex.Escape(singleLineComment)}.*$)" +       // group 1: comment to end of line
            @"|(""(?:[^""\\]|\\.)*""|'(?:[^'\\]|\\.)*')" +    // group 2: string literal
            @"|(`[^`]*`)" +                                     // group 3: template literal / verbatim
            @"|([A-Za-z_]\w*)" +                               // group 4: identifier/keyword
            @"|([^A-Za-z_""'`]+)";                             // group 5: punctuation/numbers/spaces

        var sb = new StringBuilder();
        var keywordSet = new HashSet<string>(keywords, StringComparer.Ordinal);

        foreach (Match m in Regex.Matches(rawLine, pattern, RegexOptions.Multiline))
        {
            if (m.Groups[1].Success)
                sb.Append($"[italic {CliTheme.TextMuted}]{Markup.Escape(m.Groups[1].Value)}[/]");
            else if (m.Groups[2].Success || m.Groups[3].Success)
                sb.Append($"[{CliTheme.Success}]{Markup.Escape(m.Value)}[/]");
            else if (m.Groups[4].Success)
            {
                var word = m.Groups[4].Value;
                sb.Append(keywordSet.Contains(word)
                    ? $"[{CliTheme.Info}]{Markup.Escape(word)}[/]"
                    : $"[{CliTheme.TextPrimary}]{Markup.Escape(word)}[/]");
            }
            else
                sb.Append($"[{CliTheme.TextPrimary}]{Markup.Escape(m.Value)}[/]");
        }

        return sb.Length > 0 ? sb.ToString() : $"[{CliTheme.TextPrimary}]{Markup.Escape(rawLine)}[/]";
    }

    private static string HighlightJson(string rawLine)
    {
        // Pattern: key, string value, number/bool/null, structural chars
        var pattern =
            @"(""[^""]*""\s*:)" +          // group 1: key
            @"|(""[^""]*"")" +              // group 2: string value
            @"|(\b(?:true|false|null)\b)" + // group 3: literal
            @"|(\b-?\d+(?:\.\d+)?\b)" +    // group 4: number
            @"|([{}\[\],])";               // group 5: structural

        var sb = new StringBuilder();
        var lastIndex = 0;

        foreach (Match m in Regex.Matches(rawLine, pattern))
        {
            if (m.Index > lastIndex)
                sb.Append(Markup.Escape(rawLine.Substring(lastIndex, m.Index - lastIndex)));

            if (m.Groups[1].Success)
                sb.Append($"[{CliTheme.Info}]{Markup.Escape(m.Value)}[/]");
            else if (m.Groups[2].Success)
                sb.Append($"[{CliTheme.Success}]{Markup.Escape(m.Value)}[/]");
            else if (m.Groups[3].Success || m.Groups[4].Success)
                sb.Append($"[{CliTheme.Warning}]{Markup.Escape(m.Value)}[/]");
            else
                sb.Append($"[{CliTheme.TextSecondary}]{Markup.Escape(m.Value)}[/]");

            lastIndex = m.Index + m.Length;
        }

        if (lastIndex < rawLine.Length)
            sb.Append(Markup.Escape(rawLine.Substring(lastIndex)));

        return sb.Length > 0 ? sb.ToString() : $"[{CliTheme.TextSecondary}]{Markup.Escape(rawLine)}[/]";
    }

    private static string HighlightShell(string rawLine)
    {
        var stripped = rawLine.TrimStart();

        // Full-line comment
        if (stripped.StartsWith("#", StringComparison.Ordinal))
            return $"[italic {CliTheme.TextMuted}]{Markup.Escape(rawLine)}[/]";

        var pattern =
            @"(#.*)$" +                    // group 1: inline comment
            @"|(""[^""]*""|'[^']*')" +     // group 2: quoted string
            @"|(-{1,2}[A-Za-z][\w-]*)" +   // group 3: flags
            @"|([A-Za-z_][\w./\-]*)" +     // group 4: word/command/path
            @"|([^A-Za-z_\-""'#]+)";       // group 5: other

        var sb = new StringBuilder();
        bool isFirstWord = true;

        foreach (Match m in Regex.Matches(rawLine, pattern, RegexOptions.Multiline))
        {
            if (m.Groups[1].Success)
                sb.Append($"[italic {CliTheme.TextMuted}]{Markup.Escape(m.Groups[1].Value)}[/]");
            else if (m.Groups[2].Success)
                sb.Append($"[{CliTheme.Success}]{Markup.Escape(m.Value)}[/]");
            else if (m.Groups[3].Success)
                sb.Append($"[{CliTheme.Info}]{Markup.Escape(m.Value)}[/]");
            else if (m.Groups[4].Success)
            {
                if (isFirstWord)
                {
                    sb.Append($"[bold {CliTheme.Brand}]{Markup.Escape(m.Value)}[/]");
                    isFirstWord = false;
                }
                else
                    sb.Append($"[{CliTheme.TextPrimary}]{Markup.Escape(m.Value)}[/]");
            }
            else
            {
                if (m.Value.Trim().Length > 0)
                    isFirstWord = false;
                sb.Append($"[{CliTheme.TextPrimary}]{Markup.Escape(m.Value)}[/]");
            }
        }

        return sb.Length > 0 ? sb.ToString() : $"[{CliTheme.TextPrimary}]{Markup.Escape(rawLine)}[/]";
    }

    // ─── Helpers ───────────────────────────────────────────────────────────────

    private static int GetConsoleWidth(int fallback)
    {
        try
        {
            return Console.IsOutputRedirected ? fallback : Math.Max(fallback, Console.WindowWidth - 4);
        }
        catch
        {
            return fallback;
        }
    }
}
