using Spectre.Console;

internal sealed partial class ProjectViewService
{
    private static readonly TimeSpan TrackableProgressRenderInterval = TimeSpan.FromMilliseconds(250);
    private readonly ProjectViewRenderer _renderer = new();
    private readonly HierarchyDaemonClient _daemonClient = new();
    private Action<string>? _projectLog;

    private enum ProjectFocusTabResult
    {
        ExpandedDirectory,
        NestedDirectory,
        OpenedAsset
    }

    public void OpenInitialView(CliSessionState session)
    {
        if (string.IsNullOrWhiteSpace(session.CurrentProjectPath))
        {
            return;
        }

        ProjectViewTreeUtils.ResetToAssetsRoot(session.ProjectView, session.CurrentProjectPath);
        RenderFrame(session.ProjectView);
    }

    public async Task<bool> TryHandleProjectViewCommandAsync(
        string input,
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime,
        Action<string>? log = null)
    {
        _projectLog = log;
        if (string.IsNullOrWhiteSpace(session.CurrentProjectPath))
        {
            return false;
        }

        ProjectViewTreeUtils.InitializeIfNeeded(session.ProjectView, session.CurrentProjectPath);
        var tokens = ProjectViewServiceUtils.Tokenize(input);
        CliDryRunDiffService.TryStripDryRunFlag(tokens, out var dryRunRequested);
        using var dryRunScope = CliDryRunScope.Push(dryRunRequested);
        if (tokens.Count == 0)
        {
            await SyncAssetIndexAsync(session);
            RenderFrame(session.ProjectView);
            return true;
        }

        var outputs = new List<string>();
        var handled = false;
        session.ProjectView.ExpandTranscriptForUpmList = false;

        if (tokens.Count >= 3
            && tokens[0].Equals("cd", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(tokens[1], out var index)
            && tokens[2].Equals("-long", StringComparison.OrdinalIgnoreCase))
        {
            handled = ProjectViewTreeUtils.HandleExpand(index, session.ProjectView, outputs);
        }
        else if (tokens.Count >= 2
                 && tokens[0].Equals("cd", StringComparison.OrdinalIgnoreCase)
                 && int.TryParse(tokens[1], out index))
        {
            handled = ProjectViewTreeUtils.HandleNest(index, session.ProjectView, outputs);
        }
        else if (tokens.Count >= 2
                 && (tokens[0].Equals("mk", StringComparison.OrdinalIgnoreCase)
                     || tokens[0].Equals("make", StringComparison.OrdinalIgnoreCase)))
        {
            await EnsureModeContextAsync(session, daemonControlService, daemonRuntime);
            handled = await HandleMkViaBridgeAsync(tokens, session, outputs);
        }
        else if (tokens.Count >= 2 && tokens[0].Equals("load", StringComparison.OrdinalIgnoreCase))
        {
            var selector = string.Join(' ', tokens.Skip(1));
            handled = await HandleLoadViaBridgeAsync(selector, session, outputs, daemonControlService, daemonRuntime);
        }
        else if (tokens.Count >= 3 && tokens[0].Equals("rename", StringComparison.OrdinalIgnoreCase) && int.TryParse(tokens[1], out index))
        {
            await EnsureModeContextAsync(session, daemonControlService, daemonRuntime);
            handled = await HandleRenameViaBridgeAsync(index, tokens[2], session, outputs);
        }
        else if (tokens.Count >= 2 && tokens[0].Equals("rm", StringComparison.OrdinalIgnoreCase))
        {
            await EnsureModeContextAsync(session, daemonControlService, daemonRuntime);
            handled = await HandleRemoveViaBridgeAsync(tokens[1], session, outputs);
        }
        else if (tokens[0].Equals("up", StringComparison.OrdinalIgnoreCase))
        {
            handled = ProjectViewTreeUtils.HandleUp(session.ProjectView, outputs);
        }
        else if (tokens[0].Equals("ls", StringComparison.OrdinalIgnoreCase)
                 || tokens[0].Equals("ref", StringComparison.OrdinalIgnoreCase))
        {
            ProjectViewTreeUtils.RefreshTree(session.CurrentProjectPath, session.ProjectView);
            await SyncAssetIndexAsync(session);
            outputs.Add("[i] refreshed project tree");
            handled = true;
        }
        else if (tokens[0].Equals("f", StringComparison.OrdinalIgnoreCase)
                 || tokens[0].Equals("ff", StringComparison.OrdinalIgnoreCase))
        {
            handled = await HandleFuzzyFindAsync(session, tokens, outputs);
        }
        else if (tokens[0].Equals("upm", StringComparison.OrdinalIgnoreCase))
        {
            handled = await HandleUpmCommandAsync(session, tokens, outputs, daemonControlService, daemonRuntime);
        }

        if (!handled)
        {
            return false;
        }

        EmitOutputs(session.ProjectView, outputs);
        RenderFrame(session.ProjectView);
        return true;
    }

    private async Task<ProjectCommandResponseDto> ExecuteProjectCommandAsync(
        CliSessionState session,
        ProjectCommandRequestDto request,
        Action<string>? onStatus = null)
    {
        if (session.AttachedPort is not int port)
        {
            return new ProjectCommandResponseDto(false, "daemon is not attached", null, null);
        }

        return await _daemonClient.ExecuteProjectCommandAsync(port, request, onStatus);
    }

    private static bool TryAppendDryRunDiff(List<string> outputs, string? content)
    {
        if (!CliDryRunDiffService.TryCaptureDiffFromContent(content, out var diff) || diff is null)
        {
            return false;
        }

        CliDryRunDiffService.AppendUnifiedDiffToLog(outputs, diff);
        return true;
    }

    private async Task<bool> HandleFuzzyFindAsync(CliSessionState session, IReadOnlyList<string> tokens, List<string> outputs)
    {
        if (tokens.Count < 2)
        {
            outputs.Add("[x] usage: f [--type <type>|t:<type>] <query>");
            return true;
        }

        await SyncAssetIndexAsync(session);
        var state = session.ProjectView;
        if (state.AssetPathByInstanceId.Count == 0)
        {
            outputs.Add("[x] asset index is empty; refresh with ls");
            return true;
        }

        var query = string.Join(' ', tokens.Skip(1));
        var (typeFilter, term) = ProjectViewServiceUtils.ParseProjectQuery(query);
        var matches = new List<ProjectFuzzyMatch>();

        foreach (var entry in state.AssetPathByInstanceId)
        {
            if (!ProjectViewServiceUtils.PassesTypeFilter(entry.Value, typeFilter))
            {
                continue;
            }

            var score = 1d;
            var matched = string.IsNullOrWhiteSpace(term)
                || FuzzyMatcher.TryScore(term, entry.Value, out score);
            if (!matched)
            {
                continue;
            }

            matches.Add(new ProjectFuzzyMatch(0, entry.Key, entry.Value, score));
        }

        var top = matches
            .OrderByDescending(m => m.Score)
            .ThenBy(m => m.Path, StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .Select((match, index) => match with { Index = index })
            .ToList();
        state.LastFuzzyMatches.Clear();
        state.LastFuzzyMatches.AddRange(top);

        if (top.Count == 0)
        {
            outputs.Add($"[x] no fuzzy results for: {query}");
            return true;
        }

        outputs.Add($"[*] fuzzy results for: {query}");
        foreach (var match in top)
        {
            outputs.Add($"[{match.Index}] {match.Path}");
        }

        return true;
    }

    private async Task<T> RunTrackableProgressAsync<T>(
        CliSessionState session,
        string activity,
        TimeSpan expectedDuration,
        Func<Task<T>> operation)
    {
        _ = expectedDuration;
        var startedAt = DateTime.UtcNow;
        _projectLog?.Invoke($"[grey]progress[/]: {Markup.Escape(activity)}...");
        try
        {
            var result = await operation();
            var total = DateTime.UtcNow - startedAt;
            _projectLog?.Invoke($"[grey]progress[/]: {Markup.Escape(activity)} done ({total.TotalSeconds:0.0}s)");
            return result;
        }
        catch
        {
            var total = DateTime.UtcNow - startedAt;
            _projectLog?.Invoke($"[red]progress[/]: {Markup.Escape(activity)} failed ({total.TotalSeconds:0.0}s)");
            throw;
        }
    }

    private void RenderFrame(ProjectViewState state, int? highlightedEntryIndex = null, bool focusModeEnabled = false)
    {
        if (CliRuntimeState.SuppressConsoleOutput)
        {
            return;
        }

        AnsiConsole.Clear();
        var lines = _renderer.Render(state, highlightedEntryIndex, focusModeEnabled);
        foreach (var line in lines)
        {
            CliTheme.MarkupLine(line);
        }
    }

    private void EmitOutputs(ProjectViewState state, IReadOnlyList<string> outputs)
    {
        if (outputs.Count == 0)
        {
            return;
        }

        if (_projectLog is null)
        {
            ProjectViewTranscriptUtils.Append(state, outputs);
            return;
        }

        foreach (var output in outputs)
        {
            _projectLog(output);
        }
    }

    private static async Task<bool> EnsureModeContextAsync(
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime,
        bool requireBridgeMode = false)
    {
        if (await daemonControlService.TouchAttachedDaemonAsync(session))
        {
            if (!requireBridgeMode)
            {
                return true;
            }
        }

        if (string.IsNullOrWhiteSpace(session.CurrentProjectPath))
        {
            return false;
        }

        if (!requireBridgeMode
            && DaemonControlService.IsUnityClientActiveForProject(session.CurrentProjectPath))
        {
            await daemonControlService.TryAttachProjectDaemonAsync(session.CurrentProjectPath, session);
            return true;
        }

        return await daemonControlService.EnsureProjectDaemonAsync(
            session.CurrentProjectPath,
            daemonRuntime,
            session,
            _ => { },
            requireBridgeMode);
    }

    private async Task SyncAssetIndexAsync(CliSessionState session)
    {
        if (session.AttachedPort is not int port)
        {
            return;
        }

        var state = session.ProjectView;
        var sync = await _daemonClient.SyncAssetIndexAsync(port, state.AssetIndexRevision);
        if (sync is null || sync.Unchanged)
        {
            return;
        }

        state.AssetIndexRevision = sync.Revision;
        state.AssetPathByInstanceId.Clear();
        foreach (var entry in sync.Entries)
        {
            state.AssetPathByInstanceId[entry.InstanceId] = entry.Path;
        }
    }
}
