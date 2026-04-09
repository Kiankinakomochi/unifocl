using Spectre.Console;
using System.Text;

internal sealed partial class ProjectLifecycleService
{
    private static readonly string[] UnifoclGitignoreEntries = [".unifocl/", ".unifocl-runtime/"];
    private const string UnifoclGitignoreComment = "# unifocl runtime and bridge state";

    /// <summary>
    /// Checks whether the Unity project's .gitignore covers the standard unifocl
    /// directories (.unifocl/ and .unifocl-runtime/). When entries are missing the
    /// behaviour is governed by the <c>setup.gitignore</c> config key:
    /// <list type="bullet">
    ///   <item><term>auto</term><description>Adds missing entries silently.</description></item>
    ///   <item><term>off</term><description>Does nothing.</description></item>
    ///   <item><term>not set (default)</term><description>
    ///     Interactive sessions prompt the user once and save the answer.
    ///     Agentic sessions emit a hint instead so the agent can request consent.
    ///   </description></item>
    /// </list>
    /// </summary>
    private static void CheckAndApplyGitignoreEntries(string projectPath, Action<string> log)
    {
        var missing = DetectMissingGitignoreEntries(projectPath);
        if (missing.Count == 0)
        {
            return;
        }

        if (!TryLoadCliConfig(out var config, out _))
        {
            config = new CliConfig();
        }

        // Agentic / MCP mode: never prompt interactively — emit a structured hint.
        if (CliRuntimeState.SuppressConsoleOutput)
        {
            if (config.GitignoreAutoIgnore != false)
            {
                var entriesList = string.Join(", ", missing);
                log($"[grey]hint[/]: the project .gitignore is missing unifocl entries ({entriesList}). " +
                    "Ask the user for consent before adding them. " +
                    "Once the user agrees, either write the entries to .gitignore directly " +
                    "or run [white]/config set setup.gitignore auto[/] so unifocl handles it on future opens.");
            }

            return;
        }

        // Explicit auto-add (user previously said yes, or /config set setup.gitignore auto).
        if (config.GitignoreAutoIgnore == true)
        {
            ApplyGitignoreEntries(projectPath, missing, log);
            return;
        }

        // Explicit opt-out (/config set setup.gitignore off).
        if (config.GitignoreAutoIgnore == false)
        {
            return;
        }

        // Not configured yet: in a non-interactive context (piped stdin) skip without saving
        // so the next interactive session still gets the prompt.
        if (Console.IsInputRedirected)
        {
            log("[grey]gitignore[/]: non-interactive mode — skipped " +
                "(run [white]/config set setup.gitignore auto[/] to enable auto-add)");
            return;
        }

        // Interactive: prompt once, persist the answer.
        log($"[yellow]gitignore[/]: the project [white].gitignore[/] is missing unifocl entries: " +
            $"{string.Join(", ", missing)}");
        var consent = AnsiConsole.Confirm("Add unifocl entries to .gitignore? (you won't be asked again)");
        config.GitignoreAutoIgnore = consent;
        TrySaveCliConfig(config, out _);

        if (consent)
        {
            ApplyGitignoreEntries(projectPath, missing, log);
        }
        else
        {
            log("[grey]gitignore[/]: skipped — " +
                "run [white]/config set setup.gitignore auto[/] to enable auto-add, " +
                "or [white]/config reset setup.gitignore[/] to be asked again");
        }
    }

    /// <summary>Returns the subset of <see cref="UnifoclGitignoreEntries"/> not present in the project's .gitignore.</summary>
    private static IReadOnlyList<string> DetectMissingGitignoreEntries(string projectPath)
    {
        var gitignorePath = Path.Combine(projectPath, ".gitignore");
        HashSet<string> presentPatterns;

        if (File.Exists(gitignorePath))
        {
            var lines = File.ReadAllLines(gitignorePath);
            presentPatterns = new HashSet<string>(
                lines.Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')),
                StringComparer.Ordinal);
        }
        else
        {
            presentPatterns = [];
        }

        return UnifoclGitignoreEntries.Where(e => !presentPatterns.Contains(e)).ToList();
    }

    /// <summary>Appends <paramref name="entries"/> to the project's .gitignore, creating it if absent.</summary>
    private static void ApplyGitignoreEntries(
        string projectPath,
        IReadOnlyList<string> entries,
        Action<string> log)
    {
        try
        {
            var gitignorePath = Path.Combine(projectPath, ".gitignore");
            var existingContent = File.Exists(gitignorePath)
                ? File.ReadAllText(gitignorePath)
                : string.Empty;

            var sb = new StringBuilder(existingContent);

            // Ensure there is exactly one blank line before the appended block.
            if (sb.Length > 0 && sb[^1] != '\n')
            {
                sb.AppendLine();
            }

            if (sb.Length > 0)
            {
                sb.AppendLine();
            }

            sb.AppendLine(UnifoclGitignoreComment);
            foreach (var entry in entries)
            {
                sb.AppendLine(entry);
            }

            File.WriteAllText(gitignorePath, sb.ToString());
            var noun = entries.Count == 1 ? "entry" : "entries";
            log($"[green]gitignore[/]: added {entries.Count} {noun} to [white].gitignore[/]: " +
                $"{string.Join(", ", entries)}");
        }
        catch (Exception ex)
        {
            log($"[yellow]gitignore[/]: could not update .gitignore ({Markup.Escape(ex.Message)})");
        }
    }
}
