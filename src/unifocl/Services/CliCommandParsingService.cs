using System.Text;

internal static class CliCommandParsingService
{
    public static bool TryParseQuickLifecycleCommandText(
        string[] args,
        out string? commandText,
        out string? error)
    {
        commandText = null;
        error = null;
        if (args.Length == 0)
        {
            return false;
        }

        if (args[0].Equals("update", StringComparison.OrdinalIgnoreCase)
            || args[0].Equals("--update", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length > 1)
            {
                error = "usage: unifocl update";
                return true;
            }

            commandText = "/update";
            return true;
        }

        return false;
    }

    public static bool TryParseAgentInstallCommandText(string[] args, out string? commandText, out string? error)
    {
        commandText = null;
        error = null;

        if (args.Length == 0 || !args[0].Equals("agent", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (args.Length < 2 || !args[1].Equals("install", StringComparison.OrdinalIgnoreCase))
        {
            return false; // not "install" — let other agent subcommand parsers handle it
        }

        if (args.Length < 3)
        {
            error = "usage: unifocl agent install <codex|claude> [--workspace <path>] [--server-name <name>] [--config-root <path>] [--dry-run]";
            return true;
        }

        var remaining = args
            .Skip(2)
            .Select(QuoteShellLikeToken)
            .ToArray();
        commandText = "/agent install " + string.Join(' ', remaining);
        return true;
    }

    public static bool TryParseAgentSetupArgs(
        string[] args,
        out string? projectPath,
        out bool dryRun,
        out string? error)
    {
        projectPath = null;
        dryRun = false;
        error = null;

        if (args.Length == 0 || !args[0].Equals("agent", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (args.Length < 2 || !args[1].Equals("setup", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        for (var i = 2; i < args.Length; i++)
        {
            if (args[i].Equals("--dry-run", StringComparison.OrdinalIgnoreCase))
            {
                dryRun = true;
                continue;
            }

            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                error = $"unrecognized option {args[i]}; usage: unifocl agent setup [path-to-unity-project] [--dry-run]";
                return true;
            }

            if (projectPath is not null)
            {
                error = "too many arguments; usage: unifocl agent setup [path-to-unity-project] [--dry-run]";
                return true;
            }

            projectPath = args[i];
        }

        return true;
    }

    public static bool TryParseExecLaunchOptions(string[] args, out ExecLaunchOptions? options, out string? error)
    {
        options = null;
        error = null;
        if (args.Length == 0)
        {
            return false;
        }

        var hasAgenticFlag = args.Any(arg => arg.Equals("--agentic", StringComparison.OrdinalIgnoreCase));
        if (!args[0].Equals("exec", StringComparison.OrdinalIgnoreCase))
        {
            if (hasAgenticFlag)
            {
                error = "--agentic is supported with 'exec' only. Use: unifocl exec \"<command>\" --agentic";
                return true;
            }

            return false;
        }

        var agentic = false;
        var format = AgenticOutputFormat.Json;
        string? projectPath = null;
        CliContextMode? contextMode = null;
        int? attachPort = null;
        string? requestId = null;
        string? sessionSeed = null;
        var commandTokens = new List<string>();

        for (var i = 1; i < args.Length; i++)
        {
            var token = args[i];
            if (token.Equals("--agentic", StringComparison.OrdinalIgnoreCase))
            {
                agentic = true;
                continue;
            }

            if (token.Equals("--format", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    error = "missing value for --format (json|yaml)";
                    return true;
                }

                var raw = args[++i].Trim().ToLowerInvariant();
                if (raw == "json")
                {
                    format = AgenticOutputFormat.Json;
                }
                else if (raw == "yaml")
                {
                    format = AgenticOutputFormat.Yaml;
                }
                else
                {
                    error = "invalid --format value (use json|yaml)";
                    return true;
                }
                continue;
            }

            if (token.Equals("--project", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    error = "missing value for --project";
                    return true;
                }

                projectPath = args[++i];
                continue;
            }

            if (token.Equals("--mode", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    error = "missing value for --mode";
                    return true;
                }

                var rawMode = args[++i].Trim().ToLowerInvariant();
                contextMode = rawMode switch
                {
                    "project" => CliContextMode.Project,
                    "hierarchy" => CliContextMode.Hierarchy,
                    "inspector" => CliContextMode.Inspector,
                    _ => CliContextMode.None
                };
                if (contextMode == CliContextMode.None)
                {
                    error = "invalid --mode value (use project|hierarchy|inspector)";
                    return true;
                }
                continue;
            }

            if (token.Equals("--attach-port", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length || !int.TryParse(args[++i], out var parsedPort) || parsedPort is < 1 or > 65535)
                {
                    error = "invalid --attach-port value";
                    return true;
                }

                attachPort = parsedPort;
                continue;
            }

            if (token.Equals("--request-id", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    error = "missing value for --request-id";
                    return true;
                }

                requestId = args[++i];
                continue;
            }

            if (token.Equals("--session-seed", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    error = "missing value for --session-seed";
                    return true;
                }

                sessionSeed = args[++i];
                continue;
            }

            commandTokens.Add(token);
        }

        if (commandTokens.Count == 0)
        {
            error = "exec requires a command text";
            return true;
        }

        options = new ExecLaunchOptions(
            string.Join(' ', commandTokens),
            agentic,
            format,
            projectPath,
            contextMode,
            attachPort,
            requestId,
            sessionSeed);
        return true;
    }

    public static bool TryParseEvalLaunchOptions(string[] args, out EvalLaunchOptions? options, out string? error)
    {
        options = null;
        error = null;
        if (args.Length == 0)
        {
            return false;
        }

        if (!args[0].Equals("eval", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string? declarations = null;
        var timeoutMs = 10000;
        var dryRun = false;
        var json = false;
        string? projectPath = null;
        int? attachPort = null;
        string? requestId = null;
        string? sessionSeed = null;
        var codeTokens = new List<string>();

        for (var i = 1; i < args.Length; i++)
        {
            var token = args[i];
            if (token.Equals("--dry-run", StringComparison.OrdinalIgnoreCase))
            {
                dryRun = true;
                continue;
            }

            if (token.Equals("--json", StringComparison.OrdinalIgnoreCase))
            {
                json = true;
                continue;
            }

            if (token.Equals("--declarations", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    error = "missing value for --declarations";
                    return true;
                }

                declarations = args[++i];
                continue;
            }

            if (token.Equals("--timeout", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length || !int.TryParse(args[++i], out var parsedTimeout) || parsedTimeout <= 0)
                {
                    error = "invalid --timeout value (positive integer in ms)";
                    return true;
                }

                timeoutMs = parsedTimeout;
                continue;
            }

            if (token.Equals("--project", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    error = "missing value for --project";
                    return true;
                }

                projectPath = args[++i];
                continue;
            }

            if (token.Equals("--attach-port", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length || !int.TryParse(args[++i], out var parsedPort) || parsedPort is < 1 or > 65535)
                {
                    error = "invalid --attach-port value";
                    return true;
                }

                attachPort = parsedPort;
                continue;
            }

            if (token.Equals("--request-id", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    error = "missing value for --request-id";
                    return true;
                }

                requestId = args[++i];
                continue;
            }

            if (token.Equals("--session-seed", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    error = "missing value for --session-seed";
                    return true;
                }

                sessionSeed = args[++i];
                continue;
            }

            codeTokens.Add(token);
        }

        if (codeTokens.Count == 0)
        {
            error = "eval requires a code expression";
            return true;
        }

        options = new EvalLaunchOptions(
            string.Join(' ', codeTokens),
            declarations,
            timeoutMs,
            dryRun,
            json,
            projectPath,
            attachPort,
            requestId,
            sessionSeed);
        return true;
    }

    public static string ExtractActionLabel(string commandText)
    {
        var tokens = TokenizeComposerInput(commandText);
        if (tokens.Count == 0)
        {
            return "unknown";
        }

        return tokens[0].TrimStart('/').ToLowerInvariant();
    }

    public static string NormalizeSlashCommand(string input)
    {
        if (!input.StartsWith('/'))
        {
            return input;
        }

        var trimmed = input.Trim();
        var commandEnd = trimmed.IndexOf(' ');
        var commandToken = commandEnd >= 0 ? trimmed[..commandEnd] : trimmed;
        var rest = commandEnd >= 0 ? trimmed[commandEnd..] : string.Empty;

        var normalized = commandToken.ToLowerInvariant() switch
        {
            "/o" => "/open",
            "/c" => "/close",
            "/q" => "/quit",
            "/d" => "/daemon",
            "/cfg" => "/config",
            "/st" => "/status",
            "/?" => "/help",
            "/p" => "/project",
            "/h" => "/hierarchy",
            "/i" => "/inspect",
            "/b" => "/build run",
            "/bx" => "/build exec",
            "/ba" => "/build addressables",
            "/ev" => "/eval",
            "/exit" => "/quit",
            _ => commandToken
        };

        return normalized + rest;
    }

    public static CommandSpec? MatchCommand(string input, List<CommandSpec> commands)
    {
        var normalized = input.Trim().ToLowerInvariant();

        return commands
            .OrderByDescending(c => c.Trigger.Length)
            .FirstOrDefault(c => normalized == c.Trigger
                                 || normalized.StartsWith(c.Trigger + " ", StringComparison.OrdinalIgnoreCase));
    }

    public static bool TryNormalizeProjectBuildCommand(string input, out string normalizedBuildInput)
    {
        normalizedBuildInput = string.Empty;
        var trimmed = input.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return false;
        }

        var commandEnd = trimmed.IndexOf(' ');
        var head = commandEnd >= 0 ? trimmed[..commandEnd] : trimmed;
        var tail = commandEnd >= 0 ? trimmed[commandEnd..] : string.Empty;
        var normalizedHead = head.ToLowerInvariant() switch
        {
            "build" => "/build",
            "b" => "/build run",
            "bx" => "/build exec",
            "ba" => "/build addressables",
            _ => string.Empty
        };

        if (string.IsNullOrWhiteSpace(normalizedHead))
        {
            return false;
        }

        normalizedBuildInput = normalizedHead + tail;
        return true;
    }

    /// <summary>
    /// Returns true if the input is a bare "test ..." command (not slash-prefixed).
    /// Used to intercept test orchestration commands before project router.
    /// </summary>
    public static bool IsTestCommand(string input)
    {
        var trimmed = input.TrimStart();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return false;
        }

        var commandEnd = trimmed.IndexOf(' ');
        var head = commandEnd >= 0 ? trimmed[..commandEnd] : trimmed;
        return head.Equals("test", StringComparison.OrdinalIgnoreCase);
    }

    public static List<string> TokenizeComposerInput(string input)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        foreach (var ch in input)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(ch))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    public static string MergeAcceptedSuggestion(string currentInput, string acceptedCommand)
    {
        if (string.IsNullOrWhiteSpace(acceptedCommand))
        {
            return currentInput;
        }

        var trimmedCurrent = currentInput.TrimStart();
        if (string.IsNullOrWhiteSpace(trimmedCurrent))
        {
            return acceptedCommand;
        }

        var tokenCount = CountTokens(acceptedCommand);
        if (tokenCount <= 0)
        {
            return acceptedCommand;
        }

        var remainder = SliceAfterTokenCount(trimmedCurrent, tokenCount);
        if (string.IsNullOrEmpty(remainder))
        {
            return acceptedCommand;
        }

        if (acceptedCommand.EndsWith(' '))
        {
            return acceptedCommand + remainder.TrimStart();
        }

        if (char.IsWhiteSpace(remainder[0]))
        {
            return acceptedCommand + remainder;
        }

        return $"{acceptedCommand} {remainder}";
    }

    public static int CountTokens(string input)
    {
        var count = 0;
        var inQuotes = false;
        var inToken = false;
        foreach (var ch in input)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(ch))
            {
                if (inToken)
                {
                    count++;
                    inToken = false;
                }

                continue;
            }

            inToken = true;
        }

        if (inToken)
        {
            count++;
        }

        return count;
    }

    public static string SliceAfterTokenCount(string input, int tokenCount)
    {
        if (tokenCount <= 0)
        {
            return input;
        }

        var i = 0;
        var consumed = 0;
        var inQuotes = false;
        var inToken = false;
        while (i < input.Length)
        {
            var ch = input[i];
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                if (!inToken)
                {
                    inToken = true;
                }

                i++;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(ch))
            {
                if (inToken)
                {
                    consumed++;
                    inToken = false;
                    if (consumed == tokenCount)
                    {
                        return input[i..];
                    }
                }

                i++;
                continue;
            }

            inToken = true;
            i++;
        }

        if (inToken)
        {
            consumed++;
        }

        return consumed >= tokenCount ? string.Empty : input;
    }

    private static string QuoteShellLikeToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return "\"\"";
        }

        if (!token.Any(char.IsWhiteSpace) && !token.Contains('"', StringComparison.Ordinal))
        {
            return token;
        }

        return $"\"{token.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }

    /// <summary>
    /// Tokenizes like <see cref="TokenizeComposerInput"/> but also records each
    /// token's raw span in the input (quote characters included), so callers can
    /// rewrite individual tokens without destroying the quoting of the rest.
    /// </summary>
    public static List<RawToken> TokenizeWithSpans(string input)
    {
        var tokens = new List<RawToken>();
        var current = new StringBuilder();
        var inQuotes = false;
        var start = -1;
        for (var i = 0; i < input.Length; i++)
        {
            var ch = input[i];
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                if (start < 0)
                {
                    start = i;
                }

                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(ch))
            {
                if (start >= 0)
                {
                    tokens.Add(new RawToken(current.ToString(), start, i - start));
                    current.Clear();
                    start = -1;
                }

                continue;
            }

            if (start < 0)
            {
                start = i;
            }

            current.Append(ch);
        }

        if (start >= 0)
        {
            tokens.Add(new RawToken(current.ToString(), start, input.Length - start));
        }

        return tokens;
    }

    /// <summary>
    /// Parses an interactive/one-shot "/eval" command. The code snippet is taken
    /// verbatim from the raw input (string literals, quotes and spaces intact);
    /// tokenization is used only to locate the flags. One symmetric pair of outer
    /// quotes (single or double) around the whole snippet is stripped, and a bare
    /// expression (no top-level ';') is wrapped in "return (...);" so it produces
    /// a value inside the daemon's statement-body wrapper.
    /// </summary>
    public static bool TryParseEvalCommand(string input, out EvalCommandArguments? arguments, out string? error)
    {
        const string Usage = "usage: /eval '<code>' [--declarations '<decl>'] [--timeout <ms>] [--dry-run]  (code is a C# statement body; a bare expression is auto-returned)";
        arguments = null;
        error = null;

        var spans = TokenizeWithSpans(input);
        if (spans.Count < 2)
        {
            error = Usage;
            return false;
        }

        var declarations = string.Empty;
        var timeoutMs = 10000;
        var dryRun = false;
        var codeStart = -1;
        var codeEnd = -1;
        var flagIndexes = new List<int>();

        for (var i = 1; i < spans.Count; i++)
        {
            var raw = input.Substring(spans[i].Start, spans[i].Length);
            if (raw.Equals("--dry-run", StringComparison.OrdinalIgnoreCase))
            {
                dryRun = true;
                flagIndexes.Add(i);
                continue;
            }

            if (raw.Equals("--timeout", StringComparison.OrdinalIgnoreCase) && i + 1 < spans.Count)
            {
                if (!int.TryParse(spans[i + 1].Value, out var parsedTimeout) || parsedTimeout <= 0)
                {
                    error = "invalid --timeout value (positive integer in ms)";
                    return false;
                }

                timeoutMs = parsedTimeout;
                flagIndexes.Add(i);
                i++;
                continue;
            }

            if (raw.Equals("--declarations", StringComparison.OrdinalIgnoreCase) && i + 1 < spans.Count)
            {
                var declSpan = spans[i + 1];
                declarations = StripSymmetricOuterQuotes(input.Substring(declSpan.Start, declSpan.Length));
                flagIndexes.Add(i);
                i++;
                continue;
            }

            if (codeStart < 0)
            {
                codeStart = i;
            }

            codeEnd = i;
        }

        if (codeStart < 0)
        {
            error = "eval requires a code snippet. " + Usage;
            return false;
        }

        if (flagIndexes.Any(idx => idx > codeStart && idx < codeEnd))
        {
            error = "eval flags must come before or after the code snippet, not inside it — quote the snippet if it contains flag-like tokens";
            return false;
        }

        var rawStart = spans[codeStart].Start;
        var rawEnd = spans[codeEnd].Start + spans[codeEnd].Length;
        var code = StripSymmetricOuterQuotes(input[rawStart..rawEnd].Trim());
        if (string.IsNullOrWhiteSpace(code))
        {
            error = "eval requires a code snippet. " + Usage;
            return false;
        }

        if (!ContainsTopLevelSemicolon(code))
        {
            code = $"return ({code});";
        }

        arguments = new EvalCommandArguments(code, declarations, timeoutMs, dryRun);
        return true;
    }

    /// <summary>
    /// Strips exactly one pair of matching outer quote characters, but only when
    /// the same quote character does not also occur inside — so a snippet that
    /// merely starts and ends with a literal (e.g. "a" + "b") is left verbatim.
    /// </summary>
    public static string StripSymmetricOuterQuotes(string text)
    {
        if (text.Length >= 2)
        {
            var first = text[0];
            if ((first == '\'' || first == '"') && text[^1] == first)
            {
                var inner = text[1..^1];
                if (!inner.Contains(first))
                {
                    return inner;
                }
            }
        }

        return text;
    }

    private static bool ContainsTopLevelSemicolon(string code)
    {
        var inString = false;
        var inChar = false;
        var escaped = false;
        foreach (var ch in code)
        {
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if ((inString || inChar) && ch == '\\')
            {
                escaped = true;
                continue;
            }

            if (inString)
            {
                inString = ch != '"';
                continue;
            }

            if (inChar)
            {
                inChar = ch != '\'';
                continue;
            }

            if (ch == '"')
            {
                inString = true;
            }
            else if (ch == '\'')
            {
                inChar = true;
            }
            else if (ch == ';')
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>A token value plus the raw span it occupies in the original input (quotes included).</summary>
internal readonly record struct RawToken(string Value, int Start, int Length);

/// <summary>Arguments extracted from a "/eval" command by <see cref="CliCommandParsingService.TryParseEvalCommand"/>.</summary>
internal sealed record EvalCommandArguments(string Code, string Declarations, int TimeoutMs, bool DryRun);
