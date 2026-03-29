using Spectre.Console;

internal sealed partial class ProjectLifecycleService
{
    private async Task<bool> HandleRecentAsync(
        string input,
        CommandSpec matched,
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime,
        Action<string> log)
    {
        var args = ParseCommandArgs(input, matched.Trigger);
        if (!TryParseRecentArgs(args, out var indexRaw, out var allowUnsafe, out var pruneRequested, out var parseError))
        {
            log($"[red]error[/]: {Markup.Escape(parseError)}");
            return true;
        }

        if (pruneRequested)
        {
            if (!await HandleRecentPruneAsync(log))
            {
                return true;
            }
        }

        var recentResult = await RunWithStatusAsync("Loading recent projects...", () =>
        {
            var ok = _recentProjectHistoryService.TryGetRecentProjects(100, out var loadedEntries, out var loadError);
            return Task.FromResult((ok, loadedEntries, loadError));
        });

        if (!recentResult.ok)
        {
            log($"[red]error[/]: {Markup.Escape(recentResult.loadError ?? "failed to load recent projects")}");
            return true;
        }

        var entries = recentResult.loadedEntries;
        if (entries.Count == 0)
        {
            session.RecentProjectEntries.Clear();
            session.RecentSelectionAllowUnsafe = false;
            log("[grey]recent[/]: no recent projects found");
            return true;
        }

        LogRecentEntries(entries, log);
        session.RecentProjectEntries.Clear();
        session.RecentProjectEntries.AddRange(entries);
        session.RecentSelectionAllowUnsafe = allowUnsafe;

        if (!string.IsNullOrWhiteSpace(indexRaw))
        {
            if (!int.TryParse(indexRaw, out var idx) || idx <= 0)
            {
                log("[red]error[/]: idx must be a positive integer");
                return true;
            }

            if (idx > entries.Count)
            {
                log($"[red]error[/]: idx {idx} is out of range (1-{entries.Count})");
                return true;
            }

            var selectedEntry = entries[idx - 1];
            var confirmMessage = $"Open recent project [white]{idx}[/]: [white]{Markup.Escape(selectedEntry.ProjectPath)}[/]?";
            if (!CliTheme.ConfirmWithDividers(confirmMessage, defaultValue: true))
            {
                log("[grey]recent[/]: cancelled");
                return true;
            }

            return await OpenRecentSelectionAsync(selectedEntry, session, daemonControlService, daemonRuntime, allowUnsafe, log);
        }

        log("[grey]recent[/]: press [white]F7/F8[/] to enter selection mode ([white]↑/↓[/] move, [white]idx[/] jump, [white]Enter[/] open, [white]F7/F8[/] exit)");
        return true;
    }

    private static void LogRecentEntries(IReadOnlyList<RecentProjectEntry> entries, Action<string> log)
    {
        log("[grey]recent[/]: most recently opened projects");
        var visibleCount = ResolveVisibleRecentEntryCount(entries.Count, reservedRows: 2);
        for (var i = 0; i < visibleCount; i++)
        {
            var entry = entries[i];
            var opened = entry.LastOpenedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz");
            var plain = $"{entry.ProjectPath} ({opened})";
            var clamped = ClampToViewport(plain);
            log($"[grey]recent[/]: [white]{i + 1}[/]. [white]{Markup.Escape(clamped)}[/]");
        }

        var omittedCount = Math.Max(0, entries.Count - visibleCount);
        if (omittedCount > 0)
        {
            log($"[grey]recent[/]: showing [white]{visibleCount}[/] projects ([white]+{omittedCount}[/] more)");
        }
    }

    private async Task<bool> HandleRecentPruneAsync(Action<string> log)
    {
        if (!TryLoadCliConfig(out var config, out var configError))
        {
            log($"[red]error[/]: {Markup.Escape(configError ?? "failed to read config")}");
            return false;
        }

        var staleDays = ResolveRecentPruneStaleDays(config);
        var pruneResult = await RunWithStatusAsync("Pruning recent projects...", () =>
        {
            var ok = _recentProjectHistoryService.TryPruneRecentProjects(
                staleDays,
                DateTimeOffset.UtcNow,
                out var summary,
                out var error);
            return Task.FromResult((ok, summary, error));
        });

        if (!pruneResult.ok)
        {
            log($"[red]error[/]: {Markup.Escape(pruneResult.error ?? "failed to prune recent projects")}");
            return false;
        }

        var summary = pruneResult.summary;
        if (summary.RemovedTotal == 0)
        {
            log($"[grey]recent[/]: prune complete (no entries removed, stale threshold: {staleDays} days)");
            return true;
        }

        log(
            $"[green]recent[/]: pruned [white]{summary.RemovedTotal}[/] entries " +
            $"(missing: [white]{summary.RemovedMissing}[/], stale: [white]{summary.RemovedStale}[/], " +
            $"remaining: [white]{summary.RemainingCount}[/], stale threshold: [white]{staleDays}[/] days)");
        return true;
    }

    private async Task<bool> OpenRecentSelectionAsync(
        RecentProjectEntry selectedEntry,
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime,
        bool allowUnsafe,
        Action<string> log)
    {
        log($"[grey]recent[/]: opening [white]{Markup.Escape(selectedEntry.ProjectPath)}[/]");
        return await TryOpenProjectAsync(
            selectedEntry.ProjectPath,
            session,
            daemonControlService,
            daemonRuntime,
            _editorDependencyInitializerService,
            promptForInitialization: true,
            ensureMcpHostDependencyCheck: true,
            allowUnsafe: allowUnsafe,
            daemonStartupTimeout: DefaultOpenDaemonStartupTimeout,
            log: log);
    }

    private static async Task<T> RunWithStatusAsync<T>(string statusText, Func<Task<T>> action)
    {
        if (Console.IsInputRedirected)
        {
            return await action();
        }

        var result = default(T);
        await AnsiConsole.Status()
            .Spinner(TuiTrackableProgress.StatusSpinner)
            .StartAsync(statusText, async _ =>
            {
                result = await action();
            });
        return result!;
    }

    private async Task RunRecentSelectionModeAsync(
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime,
        Action<string> log)
    {
        var entries = session.RecentProjectEntries;
        if (entries.Count == 0)
        {
            log("[yellow]recent[/]: no recent entries are available");
            return;
        }

        var selectedIndex = 0;
        var lastRenderedSelectedIndex = -1;
        var typedIndexBuffer = string.Empty;
        long typedIndexLastInputTick = 0;
        var (knownViewportWidth, knownViewportHeight) = TuiConsoleViewport.GetWindowSizeOrDefault();
        RenderRecentSelectionIfChanged(entries, selectedIndex, ref lastRenderedSelectedIndex);

        while (true)
        {
            if (!TuiConsoleViewport.WaitForKeyOrResize(ref knownViewportWidth, ref knownViewportHeight, out var key))
            {
                RenderRecentSelectionFrame(entries, selectedIndex);
                continue;
            }

            var intent = KeyboardIntentReader.ReadIntentFromFirstKey(key);
            if (SelectionIndexJumpHelper.TryApply(
                    intent,
                    index =>
                    {
                        // Recent list displays 1-based indices.
                        var target = index - 1;
                        if ((uint)target >= entries.Count)
                        {
                            return false;
                        }

                        selectedIndex = target;
                        RenderRecentSelectionIfChanged(entries, selectedIndex, ref lastRenderedSelectedIndex);
                        return true;
                    },
                    ref typedIndexBuffer,
                    ref typedIndexLastInputTick))
            {
                continue;
            }

            if (intent == KeyboardIntent.Up)
            {
                var nextSelectedIndex = selectedIndex <= 0 ? entries.Count - 1 : selectedIndex - 1;
                RenderRecentSelectionIfChanged(entries, nextSelectedIndex, ref lastRenderedSelectedIndex);
                selectedIndex = nextSelectedIndex;
                continue;
            }

            if (intent == KeyboardIntent.Down)
            {
                var nextSelectedIndex = selectedIndex >= entries.Count - 1 ? 0 : selectedIndex + 1;
                RenderRecentSelectionIfChanged(entries, nextSelectedIndex, ref lastRenderedSelectedIndex);
                selectedIndex = nextSelectedIndex;
                continue;
            }

            if (intent is KeyboardIntent.FocusProject or KeyboardIntent.Escape)
            {
                AnsiConsole.Clear();
                log("[i] recent selection mode disabled");
                return;
            }

            if (intent != KeyboardIntent.Enter)
            {
                continue;
            }

            var selectedEntry = entries[selectedIndex];
            AnsiConsole.Clear();
            await OpenRecentSelectionAsync(
                selectedEntry,
                session,
                daemonControlService,
                daemonRuntime,
                session.RecentSelectionAllowUnsafe,
                log);
            return;
        }
    }

    private static void RenderRecentSelectionIfChanged(
        IReadOnlyList<RecentProjectEntry> entries,
        int selectedIndex,
        ref int lastRenderedSelectedIndex)
    {
        if (selectedIndex == lastRenderedSelectedIndex)
        {
            return;
        }

        lastRenderedSelectedIndex = selectedIndex;
        RenderRecentSelectionFrame(entries, selectedIndex);
    }

    private static void RenderRecentSelectionFrame(IReadOnlyList<RecentProjectEntry> entries, int selectedIndex)
    {
        AnsiConsole.Clear();
        CliTheme.MarkupLine(CliTheme.PromptDividerMarkup);
        CliTheme.MarkupLine("[grey]recent[/]: selection mode ([white]↑/↓[/] move, [white]idx[/] jump, [white]Enter[/] open, [white]Esc/F7/F8[/] exit)");
        var (windowStart, visibleCount, omittedCount) = ResolveVisibleRecentEntryWindow(entries.Count, selectedIndex);
        for (var i = windowStart; i < windowStart + visibleCount; i++)
        {
            var entry = entries[i];
            var opened = entry.LastOpenedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz");
            var plain = ClampToViewport($"recent: {i + 1}. {entry.ProjectPath} ({opened})");
            if (i == selectedIndex)
            {
                CliTheme.MarkupLine(CliTheme.CursorWrapEscaped(Markup.Escape($"> {plain}")));
                continue;
            }

            CliTheme.MarkupLine($"[grey]{Markup.Escape(plain)}[/]");
        }

        if (omittedCount > 0)
        {
            CliTheme.MarkupLine($"[grey]recent[/]: showing [white]{visibleCount}[/] projects ([white]+{omittedCount}[/] more)");
        }
        CliTheme.MarkupLine(CliTheme.PromptDividerMarkup);
    }

    private static int ResolveVisibleRecentEntryCount(int totalEntries, int reservedRows)
    {
        var intendedRows = reservedRows + totalEntries;
        var excessRows = TuiConsoleViewport.GetExcessRows(intendedRows);
        var visibleCount = Math.Max(1, totalEntries - excessRows);
        if (visibleCount < totalEntries)
        {
            // Reserve one line for a truncation summary footer.
            visibleCount = Math.Max(1, visibleCount - 1);
        }

        return Math.Min(totalEntries, visibleCount);
    }

    private static (int WindowStart, int VisibleCount, int OmittedCount) ResolveVisibleRecentEntryWindow(int totalEntries, int selectedIndex)
    {
        var visibleCount = ResolveVisibleRecentEntryCount(totalEntries, reservedRows: 4);
        if (visibleCount >= totalEntries)
        {
            return (0, totalEntries, 0);
        }

        var maxWindowStart = Math.Max(0, totalEntries - visibleCount);
        var centeredWindowStart = selectedIndex - (visibleCount / 2);
        var windowStart = Math.Clamp(centeredWindowStart, 0, maxWindowStart);
        var omittedCount = totalEntries - visibleCount;
        return (windowStart, visibleCount, omittedCount);
    }

    private static string ClampToViewport(string value)
    {
        var excessColumns = TuiConsoleViewport.GetExcessColumns(value.Length);
        if (excessColumns <= 0)
        {
            return value;
        }

        var keep = Math.Max(1, value.Length - excessColumns - 1);
        return $"{value[..keep]}…";
    }
}
