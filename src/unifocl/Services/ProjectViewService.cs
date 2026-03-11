using System.Text;
using System.Text.Json;
using Spectre.Console;

internal sealed class ProjectViewService
{
    private const int MaxTranscriptEntries = 5000;
    private readonly ProjectViewRenderer _renderer = new();
    private readonly HierarchyDaemonClient _daemonClient = new();
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

        ResetToAssetsRoot(session.ProjectView, session.CurrentProjectPath);
        RenderFrame(session.ProjectView);
    }

    public async Task<bool> TryHandleProjectViewCommandAsync(
        string input,
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime)
    {
        if (string.IsNullOrWhiteSpace(session.CurrentProjectPath))
        {
            return false;
        }

        InitializeIfNeeded(session.ProjectView, session.CurrentProjectPath);
        var tokens = Tokenize(input);
        if (tokens.Count == 0)
        {
            await SyncAssetIndexAsync(session);
            RenderFrame(session.ProjectView);
            return true;
        }

        var outputs = new List<string>();
        var handled = false;
        if (!tokens[0].Equals("upm", StringComparison.OrdinalIgnoreCase))
        {
            session.ProjectView.ExpandTranscriptForUpmList = false;
        }

        if (tokens.Count >= 3
            && tokens[0].Equals("cd", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(tokens[1], out var index)
            && tokens[2].Equals("-long", StringComparison.OrdinalIgnoreCase))
        {
            handled = HandleExpand(index, session.ProjectView, outputs);
        }
        else if (tokens.Count >= 2
                 && tokens[0].Equals("cd", StringComparison.OrdinalIgnoreCase)
                 && int.TryParse(tokens[1], out index))
        {
            handled = HandleNest(index, session.ProjectView, outputs);
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
            handled = HandleUp(session.ProjectView, outputs);
        }
        else if (tokens[0].Equals("ls", StringComparison.OrdinalIgnoreCase)
                 || tokens[0].Equals("ref", StringComparison.OrdinalIgnoreCase))
        {
            RefreshTree(session.CurrentProjectPath, session.ProjectView);
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

        AppendTranscript(session.ProjectView, outputs);
        RenderFrame(session.ProjectView);
        return true;
    }

    public async Task RunKeyboardFocusModeAsync(
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime)
    {
        if (string.IsNullOrWhiteSpace(session.CurrentProjectPath))
        {
            return;
        }

        InitializeIfNeeded(session.ProjectView, session.CurrentProjectPath);
        if (ShouldRunUpmFocusMode(session.ProjectView))
        {
            await RunUpmPackageFocusModeAsync(session, daemonControlService, daemonRuntime);
            return;
        }

        var outputs = new List<string>
        {
            "[i] project focus mode enabled (up/down select, idx jump, tab open/reveal, shift+tab back, esc exit)"
        };
        AppendTranscript(session.ProjectView, outputs);
        outputs.Clear();

        var selectedEntryPosition = 0;
        var typedIndexBuffer = string.Empty;
        long typedIndexLastInputTick = 0;
        while (true)
        {
            RefreshTree(session.CurrentProjectPath, session.ProjectView);
            var entries = session.ProjectView.VisibleEntries;
            if (entries.Count == 0)
            {
                session.ProjectView.FocusHighlightedEntryIndex = null;
                RenderFrame(session.ProjectView, null, focusModeEnabled: true);
            }
            else
            {
                if (session.ProjectView.FocusHighlightedEntryIndex is int highlightedIndex)
                {
                    var highlightedPosition = entries.FindIndex(entry => entry.Index == highlightedIndex);
                    if (highlightedPosition >= 0)
                    {
                        selectedEntryPosition = highlightedPosition;
                    }
                }

                selectedEntryPosition = Math.Clamp(selectedEntryPosition, 0, entries.Count - 1);
                session.ProjectView.FocusHighlightedEntryIndex = entries[selectedEntryPosition].Index;
                RenderFrame(session.ProjectView, entries[selectedEntryPosition].Index, focusModeEnabled: true);
            }

            var intent = KeyboardIntentReader.ReadIntent();
            if (SelectionIndexJumpHelper.TryApply(
                    intent,
                    index =>
                    {
                        var targetPosition = entries.FindIndex(entry => entry.Index == index);
                        if (targetPosition < 0)
                        {
                            return false;
                        }

                        selectedEntryPosition = targetPosition;
                        session.ProjectView.FocusHighlightedEntryIndex = entries[targetPosition].Index;
                        return true;
                    },
                    ref typedIndexBuffer,
                    ref typedIndexLastInputTick))
            {
                continue;
            }

            switch (intent)
            {
                case KeyboardIntent.Up:
                    if (entries.Count > 0)
                    {
                        selectedEntryPosition = selectedEntryPosition <= 0
                            ? entries.Count - 1
                            : selectedEntryPosition - 1;
                        session.ProjectView.FocusHighlightedEntryIndex = entries[selectedEntryPosition].Index;
                    }
                    break;
                case KeyboardIntent.Down:
                    if (entries.Count > 0)
                    {
                        selectedEntryPosition = selectedEntryPosition >= entries.Count - 1
                            ? 0
                            : selectedEntryPosition + 1;
                        session.ProjectView.FocusHighlightedEntryIndex = entries[selectedEntryPosition].Index;
                    }
                    break;
                case KeyboardIntent.Tab:
                    if (entries.Count == 0)
                    {
                        break;
                    }

                    var selectedEntry = entries[selectedEntryPosition];
                    var tabResult = await HandleProjectFocusTabAsync(selectedEntry, session, daemonControlService, daemonRuntime, outputs);
                    if (session.ContextMode != CliContextMode.Project)
                    {
                        AppendTranscript(session.ProjectView, outputs);
                        RenderFrame(session.ProjectView);
                        return;
                    }

                    if (tabResult == ProjectFocusTabResult.ExpandedDirectory)
                    {
                        session.ProjectView.FocusHighlightedEntryIndex = ResolveExpandedSelectionIndex(
                            session.CurrentProjectPath,
                            session.ProjectView,
                            selectedEntry.RelativePath);
                    }
                    else
                    {
                        selectedEntryPosition = 0;
                        session.ProjectView.FocusHighlightedEntryIndex = null;
                    }
                    break;
                case KeyboardIntent.ShiftTab:
                    HandleUp(session.ProjectView, outputs);
                    selectedEntryPosition = 0;
                    session.ProjectView.FocusHighlightedEntryIndex = null;
                    break;
                case KeyboardIntent.Escape:
                case KeyboardIntent.FocusProject:
                    outputs.Add("[i] project focus mode disabled");
                    session.ProjectView.FocusHighlightedEntryIndex = null;
                    AppendTranscript(session.ProjectView, outputs);
                    RenderFrame(session.ProjectView);
                    return;
                default:
                    break;
            }

            if (outputs.Count > 0)
            {
                AppendTranscript(session.ProjectView, outputs);
                outputs.Clear();
            }
        }
    }

    private static bool ShouldRunUpmFocusMode(ProjectViewState state)
    {
        return state.ExpandTranscriptForUpmList && state.LastUpmPackages.Count > 0;
    }

    private async Task RunUpmPackageFocusModeAsync(
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime)
    {
        var state = session.ProjectView;
        state.UpmFocusModeEnabled = true;
        state.UpmActionMenuVisible = false;
        state.UpmActionSelectedIndex = 0;
        state.UpmFocusSelectedIndex = Math.Clamp(state.UpmFocusSelectedIndex, 0, Math.Max(0, state.LastUpmPackages.Count - 1));
        AppendTranscript(state, ["[i] upm selection mode enabled (up/down select, idx jump, enter action menu, esc/F7 exit)"]);
        RenderFrame(state);

        var typedIndexBuffer = string.Empty;
        long typedIndexLastInputTick = 0;
        while (true)
        {
            if (state.LastUpmPackages.Count == 0)
            {
                AppendTranscript(state, ["[i] upm selection mode disabled (no packages to select)"]);
                state.UpmFocusModeEnabled = false;
                state.UpmActionMenuVisible = false;
                RenderFrame(state);
                return;
            }

            state.UpmFocusSelectedIndex = Math.Clamp(state.UpmFocusSelectedIndex, 0, state.LastUpmPackages.Count - 1);
            RenderFrame(state);

            var key = Console.ReadKey(intercept: true);
            var intent = KeyboardIntentReader.FromConsoleKey(key);
            if (intent is KeyboardIntent.Escape or KeyboardIntent.FocusProject)
            {
                if (state.UpmActionMenuVisible)
                {
                    state.UpmActionMenuVisible = false;
                    continue;
                }

                AppendTranscript(state, ["[i] upm selection mode disabled"]);
                state.UpmFocusModeEnabled = false;
                state.UpmActionMenuVisible = false;
                RenderFrame(state);
                return;
            }

            if (SelectionIndexJumpHelper.TryApply(
                    intent,
                    index =>
                    {
                        if (state.UpmActionMenuVisible)
                        {
                            if ((uint)index > 2)
                            {
                                return false;
                            }

                            state.UpmActionSelectedIndex = index;
                            return true;
                        }

                        if ((uint)index >= state.LastUpmPackages.Count)
                        {
                            return false;
                        }

                        state.UpmFocusSelectedIndex = index;
                        return true;
                    },
                    ref typedIndexBuffer,
                    ref typedIndexLastInputTick))
            {
                continue;
            }

            if (intent == KeyboardIntent.Up)
            {
                if (state.UpmActionMenuVisible)
                {
                    state.UpmActionSelectedIndex = state.UpmActionSelectedIndex <= 0 ? 2 : state.UpmActionSelectedIndex - 1;
                }
                else
                {
                    state.UpmFocusSelectedIndex = state.UpmFocusSelectedIndex <= 0
                        ? state.LastUpmPackages.Count - 1
                        : state.UpmFocusSelectedIndex - 1;
                }

                continue;
            }

            if (intent == KeyboardIntent.Down)
            {
                if (state.UpmActionMenuVisible)
                {
                    state.UpmActionSelectedIndex = state.UpmActionSelectedIndex >= 2 ? 0 : state.UpmActionSelectedIndex + 1;
                }
                else
                {
                    state.UpmFocusSelectedIndex = state.UpmFocusSelectedIndex >= state.LastUpmPackages.Count - 1
                        ? 0
                        : state.UpmFocusSelectedIndex + 1;
                }

                continue;
            }

            if (intent != KeyboardIntent.Enter)
            {
                continue;
            }

            if (!state.UpmActionMenuVisible)
            {
                state.UpmActionMenuVisible = true;
                state.UpmActionSelectedIndex = 0;
                continue;
            }

            var selectedPackage = state.LastUpmPackages[state.UpmFocusSelectedIndex];
            var keepSelectionMode = await ExecuteUpmFocusedActionAsync(
                selectedPackage,
                state.UpmActionSelectedIndex,
                session,
                daemonControlService,
                daemonRuntime);
            state.UpmActionMenuVisible = false;
            state.UpmActionSelectedIndex = 0;
            if (!keepSelectionMode)
            {
                state.UpmFocusModeEnabled = false;
                RenderFrame(state);
                return;
            }

            state.UpmFocusSelectedIndex = Math.Clamp(state.UpmFocusSelectedIndex, 0, Math.Max(0, state.LastUpmPackages.Count - 1));
        }
    }

    private async Task<bool> ExecuteUpmFocusedActionAsync(
        UpmPackageEntry selectedPackage,
        int actionIndex,
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime)
    {
        var packageId = selectedPackage.PackageId;
        switch (actionIndex)
        {
            case 0:
                await TryHandleProjectViewCommandAsync($"upm update {packageId}", session, daemonControlService, daemonRuntime);
                break;
            case 1:
                await TryHandleProjectViewCommandAsync($"upm remove {packageId}", session, daemonControlService, daemonRuntime);
                break;
            default:
                if (!selectedPackage.Source.Equals("Registry", StringComparison.OrdinalIgnoreCase))
                {
                    AppendTranscript(session.ProjectView, ["[x] clean install supports registry packages only"]);
                    RenderFrame(session.ProjectView);
                    return true;
                }

                var installTarget = string.IsNullOrWhiteSpace(selectedPackage.Version)
                    ? selectedPackage.PackageId
                    : $"{selectedPackage.PackageId}@{selectedPackage.Version}";
                await TryHandleProjectViewCommandAsync($"upm remove {packageId}", session, daemonControlService, daemonRuntime);
                await TryHandleProjectViewCommandAsync($"upm install {installTarget}", session, daemonControlService, daemonRuntime);
                break;
        }

        await TryHandleProjectViewCommandAsync("upm ls", session, daemonControlService, daemonRuntime);
        return session.ProjectView.LastUpmPackages.Count > 0;
    }

    private static void InitializeIfNeeded(ProjectViewState state, string projectPath)
    {
        if (state.Initialized)
        {
            RefreshTree(projectPath, state);
            return;
        }

        ResetToAssetsRoot(state, projectPath);
    }

    private static void ResetToAssetsRoot(ProjectViewState state, string projectPath)
    {
        var defaultCwd = "Assets";
        var defaultPath = Path.Combine(projectPath, "Assets");
        if (!Directory.Exists(defaultPath))
        {
            Directory.CreateDirectory(defaultPath);
        }

        state.RelativeCwd = defaultCwd;
        state.ExpandedDirectories.Clear();
        state.VisibleEntries.Clear();
        state.CommandTranscript.Clear();
        state.CommandTranscript.Add("[i] project view ready");
        state.DbState = ProjectDbState.IdleSafe;
        state.Initialized = true;
        state.FocusHighlightedEntryIndex = null;
        state.AssetIndexRevision = 0;
        state.AssetPathByInstanceId.Clear();
        state.LastFuzzyMatches.Clear();
        state.LastUpmPackages.Clear();
        state.ExpandTranscriptForUpmList = false;
        state.UpmFocusModeEnabled = false;
        state.UpmFocusSelectedIndex = 0;
        state.UpmActionMenuVisible = false;
        state.UpmActionSelectedIndex = 0;
        RefreshTree(projectPath, state);
    }

    private static void RefreshTree(string projectPath, ProjectViewState state)
    {
        state.VisibleEntries.Clear();
        var cwdAbsolute = ResolveAbsolutePath(projectPath, state.RelativeCwd);
        if (!Directory.Exists(cwdAbsolute))
        {
            return;
        }

        var index = 0;
        BuildEntries(projectPath, state, cwdAbsolute, state.RelativeCwd, depth: 0, ref index);
    }

    private static void BuildEntries(
        string projectPath,
        ProjectViewState state,
        string absolutePath,
        string relativePath,
        int depth,
        ref int index)
    {
        var directories = Directory.EnumerateDirectories(absolutePath)
            .Select(path => new DirectoryInfo(path))
            .OrderBy(dir => dir.Name, StringComparer.OrdinalIgnoreCase);
        var files = Directory.EnumerateFiles(absolutePath)
            .Select(path => new FileInfo(path))
            .Where(file => !file.Extension.Equals(".meta", StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            var relativeChild = CombineRelative(relativePath, file.Name);
            state.VisibleEntries.Add(new ProjectTreeEntry(index++, depth, file.Name, relativeChild, false));
        }

        foreach (var directory in directories)
        {
            var relativeChild = CombineRelative(relativePath, directory.Name);
            state.VisibleEntries.Add(new ProjectTreeEntry(index++, depth, directory.Name, relativeChild, true));
            if (state.ExpandedDirectories.Contains(relativeChild))
            {
                BuildEntries(projectPath, state, directory.FullName, relativeChild, depth + 1, ref index);
            }
        }
    }

    private static bool HandleExpand(int index, ProjectViewState state, List<string> outputs)
    {
        var target = state.VisibleEntries.FirstOrDefault(entry => entry.Index == index);
        if (target is null)
        {
            outputs.Add($"[x] invalid index: {index}");
            return true;
        }

        if (!target.IsDirectory)
        {
            outputs.Add($"[x] index {index} is not a directory");
            return true;
        }

        state.ExpandedDirectories.Add(target.RelativePath);
        outputs.Add($"[i] expanded: {target.Name}/");
        return true;
    }

    private static bool HandleNest(int index, ProjectViewState state, List<string> outputs)
    {
        var target = state.VisibleEntries.FirstOrDefault(entry => entry.Index == index);
        if (target is null)
        {
            outputs.Add($"[x] invalid index: {index}");
            return true;
        }

        if (!target.IsDirectory)
        {
            outputs.Add($"[x] index {index} is not a directory");
            return true;
        }

        state.RelativeCwd = target.RelativePath;
        state.ExpandedDirectories.Clear();
        outputs.Add($"[i] nested into: {target.Name}/");
        return true;
    }

    private static bool HandleUp(ProjectViewState state, List<string> outputs)
    {
        var cwd = state.RelativeCwd.Replace('\\', '/').Trim('/');
        if (cwd.Equals("Assets", StringComparison.OrdinalIgnoreCase))
        {
            outputs.Add("[i] already at Assets root");
            return true;
        }

        var parent = Path.GetDirectoryName(cwd)?.Replace('\\', '/');
        state.RelativeCwd = string.IsNullOrWhiteSpace(parent) ? "Assets" : parent;
        state.ExpandedDirectories.Clear();
        outputs.Add($"[i] moved up to: {state.RelativeCwd}/");
        return true;
    }

    private static bool IsExpandedDirectory(ProjectViewState state, ProjectTreeEntry entry)
    {
        return state.ExpandedDirectories.Contains(entry.RelativePath);
    }

    private async Task<ProjectFocusTabResult> HandleProjectFocusTabAsync(
        ProjectTreeEntry entry,
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime,
        List<string> outputs)
    {
        if (entry.IsDirectory)
        {
            if (!IsExpandedDirectory(session.ProjectView, entry))
            {
                HandleExpand(entry.Index, session.ProjectView, outputs);
                return ProjectFocusTabResult.ExpandedDirectory;
            }

            HandleNest(entry.Index, session.ProjectView, outputs);
            return ProjectFocusTabResult.NestedDirectory;
        }

        await HandleLoadViaBridgeAsync(entry.Index.ToString(), session, outputs, daemonControlService, daemonRuntime);
        return ProjectFocusTabResult.OpenedAsset;
    }

    private static int? ResolveExpandedSelectionIndex(string projectPath, ProjectViewState state, string expandedRelativePath)
    {
        RefreshTree(projectPath, state);
        var entries = state.VisibleEntries;
        if (entries.Count == 0)
        {
            return null;
        }

        var expandedIndex = entries.FindIndex(entry =>
            entry.RelativePath.Equals(expandedRelativePath, StringComparison.OrdinalIgnoreCase));
        if (expandedIndex < 0)
        {
            return null;
        }

        var expandedEntry = entries[expandedIndex];
        var firstChildIndex = expandedIndex + 1;
        if (firstChildIndex < entries.Count)
        {
            var firstChild = entries[firstChildIndex];
            if (firstChild.Depth == expandedEntry.Depth + 1
                && firstChild.RelativePath.StartsWith(expandedEntry.RelativePath + "/", StringComparison.OrdinalIgnoreCase))
            {
                return firstChild.Index;
            }
        }

        return expandedEntry.Index;
    }

    private async Task<bool> HandleMkViaBridgeAsync(
        IReadOnlyList<string> tokens,
        CliSessionState session,
        List<string> outputs)
    {
        if (!TryParseProjectMkArguments(tokens, out var mkTypeRaw, out var count, out var name, out var parentSelector, out var error))
        {
            outputs.Add($"[x] {error}");
            outputs.Add("[x] usage: make --type <type> [--count <count>] [--name <name>|-n <name>] [--parent <idx|name>]");
            outputs.Add("[x] usage: mk <type> [count] [--name <name>|-n <name>] [--parent <idx|name>]");
            return true;
        }

        if (!ProjectMkCatalog.TryNormalizeType(mkTypeRaw, out var canonicalType, out var typeError))
        {
            outputs.Add($"[x] {typeError}");
            outputs.Add($"[i] supported mk types: {string.Join(", ", ProjectMkCatalog.KnownTypes)}");
            return true;
        }

        if (!TryResolveMkParentPath(session, parentSelector, out var parentPath, out var parentError))
        {
            outputs.Add($"[x] {parentError}");
            return true;
        }

        if (canonicalType.Equals("CSharpScript", StringComparison.OrdinalIgnoreCase)
            || canonicalType.Equals("ScriptableObjectScript", StringComparison.OrdinalIgnoreCase))
        {
            return await HandleScriptMkViaBridgeAsync(canonicalType, count, name, parentPath, session, outputs);
        }

        var state = session.ProjectView;
        state.DbState = ProjectDbState.LockedImporting;
        try
        {
            var payload = JsonSerializer.Serialize(
                new MkAssetRequestPayload(canonicalType, count, name),
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            var response = await RunTrackableProgressAsync(
                session,
                $"creating {canonicalType} asset(s)",
                TimeSpan.FromSeconds(12),
                () => ExecuteProjectCommandAsync(
                    session,
                    new ProjectCommandRequestDto(
                        "mk-asset",
                        parentPath,
                        null,
                        payload)));

            if (!response.Ok)
            {
                outputs.Add(FormatProjectCommandFailure("create", response.Message));
                return true;
            }

            var createdPaths = ParseMkAssetCreatedPaths(response.Content);
            if (createdPaths.Count == 0)
            {
                outputs.Add($"[+] created: {canonicalType}");
            }
            else
            {
                outputs.Add($"[+] created {createdPaths.Count} {canonicalType} asset(s)");
                foreach (var path in createdPaths)
                {
                    outputs.Add($"    - {path}");
                }
            }

            await SyncAssetIndexAsync(session);
            if (!string.IsNullOrWhiteSpace(session.CurrentProjectPath))
            {
                RefreshTree(session.CurrentProjectPath, state);
            }

            return true;
        }
        finally
        {
            state.DbState = ProjectDbState.IdleSafe;
        }
    }

    private async Task<bool> HandleScriptMkViaBridgeAsync(
        string canonicalType,
        int count,
        string? name,
        string parentPath,
        CliSessionState session,
        List<string> outputs)
    {
        if (string.IsNullOrWhiteSpace(session.CurrentProjectPath))
        {
            outputs.Add("[x] no active project");
            return true;
        }

        var projectPath = session.CurrentProjectPath!;
        var state = session.ProjectView;
        var created = new List<string>();
        state.DbState = ProjectDbState.LockedImporting;
        try
        {
            var template = ResolveTemplate(projectPath);
            outputs.Add($"[*] template: found '{template.TemplateName}' in {template.TemplateSource}");
            for (var i = 0; i < count; i++)
            {
                var rawName = ResolveScriptCreateName(canonicalType, name, i, count);
                var typeName = SanitizeTypeName(rawName);
                if (string.IsNullOrWhiteSpace(typeName))
                {
                    outputs.Add("[x] invalid script name");
                    return true;
                }

                var uniqueTypeName = ResolveUniqueScriptTypeName(projectPath, parentPath, typeName);
                var targetRelative = CombineRelative(parentPath, $"{uniqueTypeName}.cs");

                var content = canonicalType.Equals("ScriptableObjectScript", StringComparison.OrdinalIgnoreCase)
                    ? BuildScriptableObjectTemplate(uniqueTypeName)
                    : template.Content.Replace("#NAME#", uniqueTypeName);

                var response = await RunTrackableProgressAsync(
                    session,
                    $"creating script {uniqueTypeName}.cs",
                    TimeSpan.FromSeconds(8),
                    () => ExecuteProjectCommandAsync(
                        session,
                        new ProjectCommandRequestDto(
                            "mk-script",
                            targetRelative,
                            null,
                            content)));
                if (!response.Ok)
                {
                    outputs.Add(FormatProjectCommandFailure("create", response.Message));
                    return true;
                }

                created.Add(targetRelative);
            }

            foreach (var path in created)
            {
                outputs.Add($"[+] created: {path}");
            }

            await SyncAssetIndexAsync(session);
            RefreshTree(projectPath, state);
            return true;
        }
        finally
        {
            state.DbState = ProjectDbState.IdleSafe;
        }
    }

    private async Task<bool> HandleRemoveViaBridgeAsync(string selector, CliSessionState session, List<string> outputs)
    {
        var state = session.ProjectView;
        if (string.IsNullOrWhiteSpace(selector))
        {
            outputs.Add("[x] usage: remove <idx|start:end>");
            return true;
        }

        var targets = new List<ProjectTreeEntry>();
        if (TryParseRemoveIndexRange(selector, out var startIndex, out var endIndex, out var rangeError))
        {
            if (!string.IsNullOrWhiteSpace(rangeError))
            {
                outputs.Add($"[x] {rangeError}");
                return true;
            }

            var selected = state.VisibleEntries
                .Where(entry => entry.Index >= startIndex && entry.Index <= endIndex)
                .OrderByDescending(entry => entry.Index)
                .ToList();
            if (selected.Count == 0)
            {
                outputs.Add($"[x] no entries in range: {startIndex}:{endIndex}");
                return true;
            }

            targets.AddRange(selected);
        }
        else if (int.TryParse(selector, out var singleIndex))
        {
            var target = state.VisibleEntries.FirstOrDefault(entry => entry.Index == singleIndex);
            if (target is null)
            {
                outputs.Add($"[x] invalid index: {singleIndex}");
                return true;
            }

            targets.Add(target);
        }
        else
        {
            outputs.Add("[x] usage: remove <idx|start:end>");
            return true;
        }

        var removedPaths = new List<string>();
        state.DbState = ProjectDbState.LockedImporting;
        try
        {
            foreach (var target in targets)
            {
                var sourcePath = target.RelativePath;
                var response = await RunTrackableProgressAsync(
                    session,
                    $"removing {Path.GetFileName(sourcePath)}",
                    TimeSpan.FromSeconds(6),
                    () => ExecuteProjectCommandAsync(
                        session,
                        new ProjectCommandRequestDto("remove-asset", sourcePath, null, null)));
                if (!response.Ok && IsAssetNotFoundFailure(response.Message))
                {
                    var fallbackPath = await ResolveAssetFallbackPathAsync(session, sourcePath, allowDirectoryFallback: target.IsDirectory);
                    if (!string.IsNullOrWhiteSpace(fallbackPath)
                        && !fallbackPath.Equals(sourcePath, StringComparison.OrdinalIgnoreCase))
                    {
                        sourcePath = fallbackPath;
                        response = await RunTrackableProgressAsync(
                            session,
                            $"removing {Path.GetFileName(sourcePath)}",
                            TimeSpan.FromSeconds(6),
                            () => ExecuteProjectCommandAsync(
                                session,
                                new ProjectCommandRequestDto("remove-asset", sourcePath, null, null)));
                    }
                }

                if (!response.Ok)
                {
                    outputs.Add(FormatProjectCommandFailure("remove", response.Message));
                    if (targets.Count > 1)
                    {
                        outputs.Add($"[i] removed {removedPaths.Count}/{targets.Count} before failure");
                    }

                    return true;
                }

                removedPaths.Add(sourcePath);
            }

            if (removedPaths.Count == 1)
            {
                outputs.Add($"[=] removed: {removedPaths[0]}");
            }
            else
            {
                outputs.Add($"[=] removed {removedPaths.Count} assets");
                foreach (var path in removedPaths)
                {
                    outputs.Add($"    - {path}");
                }
            }

            if (!string.IsNullOrWhiteSpace(session.CurrentProjectPath))
            {
                RefreshTree(session.CurrentProjectPath, state);
            }

            await SyncAssetIndexAsync(session);
            return true;
        }
        finally
        {
            state.DbState = ProjectDbState.IdleSafe;
        }
    }

    private static bool TryParseRemoveIndexRange(
        string selector,
        out int startIndex,
        out int endIndex,
        out string? error)
    {
        startIndex = 0;
        endIndex = 0;
        error = null;
        var trimmed = selector.Trim();
        var separator = trimmed.IndexOf(':');
        if (separator < 0)
        {
            return false;
        }

        var startRaw = trimmed[..separator];
        var endRaw = separator + 1 < trimmed.Length ? trimmed[(separator + 1)..] : string.Empty;
        if (!int.TryParse(startRaw, out startIndex) || !int.TryParse(endRaw, out endIndex))
        {
            error = "range must be numeric: <start:end>";
            return true;
        }

        if (startIndex < 0 || endIndex < 0)
        {
            error = "range indices must be non-negative";
            return true;
        }

        if (startIndex > endIndex)
        {
            error = "range start must be <= end";
            return true;
        }

        return true;
    }

    private async Task<bool> HandleLoadViaBridgeAsync(
        string selector,
        CliSessionState session,
        List<string> outputs,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime)
    {
        var state = session.ProjectView;
        if (string.IsNullOrWhiteSpace(selector))
        {
            outputs.Add("[x] usage: load <idx|name>");
            return true;
        }

        var target = FindEntryBySelector(state, selector);
        if (target is null)
        {
            outputs.Add($"[x] no entry matches: {selector}");
            return true;
        }

        if (target.IsDirectory)
        {
            outputs.Add("[x] load expects a scene (.unity), prefab (.prefab), or script (.cs), not a directory");
            return true;
        }

        var extension = Path.GetExtension(target.Name);
        var isHierarchyAssetLoad = IsHierarchyAssetExtension(extension);
        var loadAssetKind = ResolveLoadAssetKind(extension);
        EmitImmediateLoadFeedback(state, target.Name);
        EmitLoadDiagnostic(state, $"selector '{selector}' resolved to '{target.RelativePath}'");
        EmitLoadDiagnostic(state, "ensuring Bridge mode context");
        var bridgeModeReady = await (isHierarchyAssetLoad
            ? RunTrackableProgressAsync(
                session,
                $"preparing {loadAssetKind} load context",
                TimeSpan.FromSeconds(6),
                () => EnsureModeContextAsync(
                    session,
                    daemonControlService,
                    daemonRuntime,
                    requireBridgeMode: true))
            : EnsureModeContextAsync(
                session,
                daemonControlService,
                daemonRuntime,
                requireBridgeMode: false));
        if (!bridgeModeReady && isHierarchyAssetLoad)
        {
            outputs.Add($"[x] {loadAssetKind} load failed: Bridge mode is unavailable; set UNITY_PATH or start Unity editor for this project");
            return true;
        }
        if (isHierarchyAssetLoad)
        {
            EmitLoadDiagnostic(state, "Bridge mode context ready");
        }

        var response = await (isHierarchyAssetLoad
            ? RunTrackableProgressAsync(
                session,
                $"loading {loadAssetKind} {target.Name}",
                TimeSpan.FromSeconds(20),
                () => ExecuteProjectCommandAsync(
                    session,
                    new ProjectCommandRequestDto("load-asset", target.RelativePath, null, null),
                    status => EmitLoadDiagnostic(state, status)))
            : ExecuteProjectCommandAsync(
                session,
                new ProjectCommandRequestDto("load-asset", target.RelativePath, null, null),
                status => EmitLoadDiagnostic(state, status)));
        if (!response.Ok && IsAssetNotFoundFailure(response.Message))
        {
            var fallbackPath = await ResolveAssetFallbackPathAsync(session, target.RelativePath, allowDirectoryFallback: false);
            if (!string.IsNullOrWhiteSpace(fallbackPath)
                && !fallbackPath.Equals(target.RelativePath, StringComparison.OrdinalIgnoreCase))
            {
                EmitLoadDiagnostic(state, $"asset fallback path resolved: '{fallbackPath}'");
                response = await (isHierarchyAssetLoad
                    ? RunTrackableProgressAsync(
                        session,
                        $"retrying {loadAssetKind} load {target.Name}",
                        TimeSpan.FromSeconds(20),
                        () => ExecuteProjectCommandAsync(
                            session,
                            new ProjectCommandRequestDto("load-asset", fallbackPath, null, null),
                            status => EmitLoadDiagnostic(state, status)))
                    : ExecuteProjectCommandAsync(
                        session,
                        new ProjectCommandRequestDto("load-asset", fallbackPath, null, null),
                        status => EmitLoadDiagnostic(state, status)));
            }
        }

        EmitLoadDiagnostic(state, $"daemon result: ok={response.Ok}, kind={response.Kind ?? "null"}");

        if (!response.Ok)
        {
            if (isHierarchyAssetLoad)
            {
                outputs.Add($"[x] {loadAssetKind} load failed: {response.Message ?? "unknown error"}");
                return true;
            }

            outputs.Add(FormatProjectCommandFailure("load", response.Message));
            return true;
        }

        if (isHierarchyAssetLoad
            || response.Kind?.Equals("scene", StringComparison.OrdinalIgnoreCase) == true
            || response.Kind?.Equals("prefab", StringComparison.OrdinalIgnoreCase) == true)
        {
            outputs.Add($"[=] loaded {loadAssetKind}: {target.Name}");
            outputs.Add("[i] switched to hierarchy mode");
            session.ContextMode = CliContextMode.Hierarchy;
            session.AutoEnterHierarchyRequested = true;
            return true;
        }

        outputs.Add($"[=] opened script: {target.Name}");
        return true;
    }

    private async Task<bool> HandleRenameViaBridgeAsync(int index, string newName, CliSessionState session, List<string> outputs)
    {
        var state = session.ProjectView;
        var target = state.VisibleEntries.FirstOrDefault(entry => entry.Index == index);
        if (target is null)
        {
            outputs.Add($"[x] invalid index: {index}");
            return true;
        }

        if (string.IsNullOrWhiteSpace(newName))
        {
            outputs.Add("[x] new name cannot be empty");
            return true;
        }

        var sourceRelativePath = target.RelativePath;
        var destinationRelative = ComputeRenameDestinationPath(sourceRelativePath, target.IsDirectory, newName);

        state.DbState = ProjectDbState.LockedImporting;
        try
        {
            var response = await ExecuteProjectCommandAsync(
                session,
                new ProjectCommandRequestDto("rename-asset", sourceRelativePath, destinationRelative, null));
            if (!response.Ok && IsAssetNotFoundFailure(response.Message))
            {
                var fallbackPath = await ResolveAssetFallbackPathAsync(session, sourceRelativePath, allowDirectoryFallback: target.IsDirectory);
                if (!string.IsNullOrWhiteSpace(fallbackPath)
                    && !fallbackPath.Equals(sourceRelativePath, StringComparison.OrdinalIgnoreCase))
                {
                    sourceRelativePath = fallbackPath;
                    destinationRelative = ComputeRenameDestinationPath(sourceRelativePath, target.IsDirectory, newName);
                    response = await ExecuteProjectCommandAsync(
                        session,
                        new ProjectCommandRequestDto("rename-asset", sourceRelativePath, destinationRelative, null));
                }
            }

            if (!response.Ok)
            {
                outputs.Add(FormatProjectCommandFailure("rename", response.Message));
                return true;
            }

            outputs.Add("[=] rename complete. .meta file updated successfully.");
            if (!string.IsNullOrWhiteSpace(session.CurrentProjectPath))
            {
                RefreshTree(session.CurrentProjectPath, state);
            }

            await SyncAssetIndexAsync(session);
            return true;
        }
        finally
        {
            state.DbState = ProjectDbState.IdleSafe;
        }
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

    private async Task<ProjectCommandResponseDto> ExecuteUpmMutationWithRecoveryAsync(
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime,
        ProjectCommandRequestDto request,
        List<string> outputs)
    {
        var response = await ExecuteProjectCommandAsync(session, request);
        if (response.Ok)
        {
            return response;
        }

        if (DidResponseChannelInterruptAfterCompletion(response.Message))
        {
            outputs.Add("[yellow]upm[/]: command completed in daemon, but response channel was interrupted; verifying package state");
            return response;
        }

        if (!ShouldRecoverUpmTimeout(response.Message))
        {
            return response;
        }

        if (DidDaemonRuntimeRestart(response.Message))
        {
            outputs.Add("[yellow]upm[/]: daemon runtime restarted during command; retrying once on refreshed runtime");
        }
        else
        {
            outputs.Add("[yellow]upm[/]: timed out while daemon is reachable; restarting bridge and retrying once");
            await daemonControlService.HandleDaemonCommandAsync(
                input: "/daemon restart",
                trigger: "/daemon restart",
                runtime: daemonRuntime,
                session: session,
                log: line => outputs.Add(line),
                streamLog: outputs);
        }

        var bridgeReady = await EnsureModeContextAsync(session, daemonControlService, daemonRuntime, requireBridgeMode: true);
        if (!bridgeReady)
        {
            return new ProjectCommandResponseDto(
                false,
                "bridge restart after UPM timeout did not recover project command endpoint",
                null,
                null);
        }

        var retried = await ExecuteProjectCommandAsync(session, request);
        if (!retried.Ok && !string.IsNullOrWhiteSpace(retried.Message))
        {
            return new ProjectCommandResponseDto(
                false,
                $"{retried.Message} (after automatic bridge restart retry)",
                retried.Kind,
                retried.Content);
        }

        return retried;
    }

    private static bool ShouldRecoverUpmTimeout(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        return message.Contains("daemon project command timed out", StringComparison.OrdinalIgnoreCase)
               && message.Contains("daemonPing=ok", StringComparison.OrdinalIgnoreCase)
               || DidDaemonRuntimeRestart(message);
    }

    private static bool DidDaemonRuntimeRestart(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        return message.Contains("daemon runtime restarted during project command", StringComparison.OrdinalIgnoreCase);
    }

    private static bool DidResponseChannelInterruptAfterCompletion(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        return message.Contains("completed successfully but response channel was interrupted", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<(bool Confirmed, string? Version)> TryConfirmUpmUpdateSucceededAsync(
        CliSessionState session,
        string packageId,
        string? expectedVersion)
    {
        if (string.IsNullOrWhiteSpace(packageId) || !IsRegistryPackageId(packageId))
        {
            return (false, null);
        }

        var payload = JsonSerializer.Serialize(
            new UpmListRequestPayload(false, true, false),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(1200);
            }

            var response = await ExecuteProjectCommandAsync(
                session,
                new ProjectCommandRequestDto("upm-list", null, null, payload));
            if (!response.Ok)
            {
                continue;
            }

            var current = TryFindUpmPackageById(response.Content, packageId);
            if (current is null)
            {
                continue;
            }

            var installedVersion = string.IsNullOrWhiteSpace(current.Version) ? null : current.Version!;
            if (!string.IsNullOrWhiteSpace(expectedVersion)
                && string.Equals(installedVersion, expectedVersion, StringComparison.OrdinalIgnoreCase))
            {
                return (true, installedVersion);
            }

            if (!current.IsOutdated)
            {
                return (true, installedVersion);
            }
        }

        return (false, null);
    }

    private static ProjectTreeEntry? FindEntryBySelector(ProjectViewState state, string selector)
    {
        var normalizedSelector = NormalizeLoadSelector(selector);
        if (int.TryParse(normalizedSelector, out var index))
        {
            var visible = state.VisibleEntries.FirstOrDefault(entry => entry.Index == index);
            if (visible is not null)
            {
                return visible;
            }

            var fuzzy = state.LastFuzzyMatches.FirstOrDefault(entry => entry.Index == index);
            if (fuzzy is not null)
            {
                var name = Path.GetFileName(fuzzy.Path);
                return new ProjectTreeEntry(fuzzy.Index, 0, name, fuzzy.Path, false);
            }

            return null;
        }

        var normalized = normalizedSelector.Replace('\\', '/');
        return state.VisibleEntries.FirstOrDefault(entry =>
            entry.Name.Equals(normalized, StringComparison.OrdinalIgnoreCase)
            || entry.RelativePath.Equals(normalized, StringComparison.OrdinalIgnoreCase)
            || entry.RelativePath.EndsWith("/" + normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeLoadSelector(string selector)
    {
        var normalized = selector.Trim();
        if (normalized.Length >= 2
            && ((normalized.StartsWith('<') && normalized.EndsWith('>'))
                || (normalized.StartsWith('"') && normalized.EndsWith('"'))
                || (normalized.StartsWith('\'') && normalized.EndsWith('\''))))
        {
            normalized = normalized[1..^1].Trim();
        }

        return normalized;
    }

    private static string ComputeRenameDestinationPath(string sourceRelativePath, bool isDirectory, string newName)
    {
        var parentRelative = Path.GetDirectoryName(sourceRelativePath)?.Replace('\\', '/') ?? string.Empty;
        var sourceName = Path.GetFileName(sourceRelativePath);
        var finalName = isDirectory
            ? newName
            : (Path.HasExtension(newName) ? newName : $"{newName}{Path.GetExtension(sourceName)}");
        return string.IsNullOrEmpty(parentRelative) ? finalName : $"{parentRelative}/{finalName}";
    }

    private async Task<string?> ResolveAssetFallbackPathAsync(CliSessionState session, string targetRelativePath, bool allowDirectoryFallback)
    {
        var targetPath = targetRelativePath.Replace('\\', '/');
        if (!string.IsNullOrWhiteSpace(session.CurrentProjectPath))
        {
            var targetAbsolutePath = ResolveAbsolutePath(session.CurrentProjectPath, targetPath);
            if (File.Exists(targetAbsolutePath) || Directory.Exists(targetAbsolutePath))
            {
                return targetPath;
            }
        }

        await SyncAssetIndexAsync(session);
        var paths = session.ProjectView.AssetPathByInstanceId.Values
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (paths.Count > 0)
        {
            var exact = paths.FirstOrDefault(path => path.Equals(targetPath, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(exact))
            {
                return exact;
            }

            var fileName = Path.GetFileName(targetPath);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                var sameFileName = paths
                    .Where(path => Path.GetFileName(path).Equals(fileName, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (sameFileName.Count == 1)
                {
                    return sameFileName[0];
                }

                var extension = Path.GetExtension(fileName);
                if (!string.IsNullOrWhiteSpace(extension))
                {
                    var stem = Path.GetFileNameWithoutExtension(fileName);
                    var sameStem = paths
                        .Where(path => Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase))
                        .Where(path => Path.GetFileNameWithoutExtension(path).Equals(stem, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (sameStem.Count == 1)
                    {
                        return sameStem[0];
                    }
                }
            }
        }

        if (!allowDirectoryFallback || string.IsNullOrWhiteSpace(session.CurrentProjectPath))
        {
            return null;
        }

        var projectAssetsPath = Path.Combine(session.CurrentProjectPath, "Assets");
        if (!Directory.Exists(projectAssetsPath))
        {
            return null;
        }

        var targetDirectoryName = Path.GetFileName(targetPath.TrimEnd('/', '\\'));
        if (string.IsNullOrWhiteSpace(targetDirectoryName))
        {
            return null;
        }

        var matchingDirectories = Directory
            .EnumerateDirectories(projectAssetsPath, targetDirectoryName, SearchOption.AllDirectories)
            .Select(path => "Assets/" + Path.GetRelativePath(projectAssetsPath, path).Replace('\\', '/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return matchingDirectories.Count == 1 ? matchingDirectories[0] : null;
    }

    private static bool IsAssetNotFoundFailure(string? message)
    {
        return !string.IsNullOrWhiteSpace(message)
               && message.Contains("asset not found", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolvePackageDisplayName(string? displayName, string packageId)
    {
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            return displayName.Trim();
        }

        var tail = packageId.Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? packageId;
        return string.Join(' ', tail
            .Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token.Length == 0
                ? token
                : char.ToUpperInvariant(token[0]) + token[1..]));
    }

    private static string ResolveUpmStatusColor(UpmPackageEntry package)
    {
        if (package.IsDeprecated)
        {
            return CliTheme.Error;
        }

        if (package.IsOutdated)
        {
            return CliTheme.Warning;
        }

        if (package.IsPreview)
        {
            return CliTheme.Info;
        }

        return CliTheme.Success;
    }

    private static string ResolveUpmStatusLabel(UpmPackageEntry package)
    {
        if (package.IsDeprecated)
        {
            return "deprecated";
        }

        if (package.IsOutdated)
        {
            return "update available";
        }

        if (package.IsPreview)
        {
            return "preview";
        }

        return "stable";
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
        var (typeFilter, term) = ParseProjectQuery(query);
        var matches = new List<ProjectFuzzyMatch>();

        foreach (var entry in state.AssetPathByInstanceId)
        {
            if (!PassesTypeFilter(entry.Value, typeFilter))
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

    private async Task<bool> HandleUpmCommandAsync(
        CliSessionState session,
        IReadOnlyList<string> tokens,
        List<string> outputs,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime)
    {
        if (tokens.Count < 2)
        {
            outputs.Add("[x] usage: upm <list|ls> [--outdated] [--builtin] [--git]");
            outputs.Add("[x] usage: upm <install|add|i> <target>");
            outputs.Add("[x] usage: upm <remove|rm|uninstall> <id>");
            outputs.Add("[x] usage: upm <update|u> [id]");
            return true;
        }

        var subcommand = tokens[1];
        session.ProjectView.ExpandTranscriptForUpmList =
            subcommand.Equals("list", StringComparison.OrdinalIgnoreCase)
            || subcommand.Equals("ls", StringComparison.OrdinalIgnoreCase);

        if (subcommand.Equals("install", StringComparison.OrdinalIgnoreCase)
            || subcommand.Equals("add", StringComparison.OrdinalIgnoreCase)
            || subcommand.Equals("i", StringComparison.OrdinalIgnoreCase))
        {
            var rawTarget = tokens.Count >= 3
                ? string.Join(' ', tokens.Skip(2))
                : string.Empty;
            if (!TryNormalizeUpmInstallTarget(rawTarget, out var target, out var targetType, out var validationError))
            {
                outputs.Add($"[x] upm install failed: {validationError}");
                outputs.Add("accepted targets:");
                outputs.Add("- registry ID (com.unity.addressables)");
                outputs.Add("- git URL (https://github.com/user/repo.git?path=/subfolder#v1.0.0)");
                outputs.Add("- local path (file:../local-pkg)");
                return true;
            }

            var installBridgeReady = await RunTrackableProgressAsync(
                session,
                "preparing package manager",
                TimeSpan.FromSeconds(6),
                () => EnsureModeContextAsync(session, daemonControlService, daemonRuntime, requireBridgeMode: true));
            if (!installBridgeReady)
            {
                outputs.Add("[x] upm install failed: Bridge mode is unavailable; set UNITY_PATH or open Unity editor for this project");
                return true;
            }

            if (targetType.Equals("registry", StringComparison.OrdinalIgnoreCase))
            {
                var (registryPackageId, _) = SplitRegistryTarget(target);
                var listResponse = await RunTrackableProgressAsync(
                    session,
                    "checking installed packages",
                    TimeSpan.FromSeconds(8),
                    () => ExecuteProjectCommandAsync(
                        session,
                        new ProjectCommandRequestDto(
                            "upm-list",
                            null,
                            null,
                            JsonSerializer.Serialize(
                                new UpmListRequestPayload(false, true, false),
                                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }))));
                if (!listResponse.Ok)
                {
                    outputs.Add(FormatProjectCommandFailure("upm install", listResponse.Message));
                    return true;
                }

                var existingPackage = TryFindUpmPackageById(listResponse.Content, registryPackageId);
                if (existingPackage is not null)
                {
                    var existingPackageId = string.IsNullOrWhiteSpace(existingPackage.PackageId) ? registryPackageId : existingPackage.PackageId!;
                    var existingPackageVersion = string.IsNullOrWhiteSpace(existingPackage.Version) ? "-" : existingPackage.Version!;
                    RenderFrame(session.ProjectView);
                    var cleanInstallRequested = Console.IsInputRedirected
                        ? false
                        : AnsiConsole.Confirm(
                            $"Package [white]{Markup.Escape(existingPackageId)}[/] is already installed ([white]{Markup.Escape(existingPackageVersion)}[/]). Run clean install (remove then install)?",
                            defaultValue: false);
                    if (!cleanInstallRequested)
                    {
                        outputs.Add($"[i] install skipped: {Markup.Escape(existingPackageId)} is already installed");
                        return true;
                    }

                    var removePayload = JsonSerializer.Serialize(
                        new UpmRemoveRequestPayload(existingPackageId),
                        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                    var removeResponse = await RunTrackableProgressAsync(
                        session,
                        $"clean uninstalling {existingPackageId}",
                        TimeSpan.FromSeconds(25),
                        () => ExecuteUpmMutationWithRecoveryAsync(
                            session,
                            daemonControlService,
                            daemonRuntime,
                            new ProjectCommandRequestDto("upm-remove", null, null, removePayload),
                            outputs));
                    if (!removeResponse.Ok)
                    {
                        outputs.Add(FormatProjectCommandFailure("upm clean remove", removeResponse.Message));
                        return true;
                    }

                    outputs.Add($"[i] clean remove complete: {Markup.Escape(existingPackageId)}");
                }
            }

            var installPayload = JsonSerializer.Serialize(
                new UpmInstallRequestPayload(target),
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            var installResponse = await RunTrackableProgressAsync(
                session,
                $"installing {target}",
                TimeSpan.FromSeconds(35),
                () => ExecuteUpmMutationWithRecoveryAsync(
                    session,
                    daemonControlService,
                    daemonRuntime,
                    new ProjectCommandRequestDto("upm-install", null, null, installPayload),
                    outputs));

            if (!installResponse.Ok)
            {
                outputs.Add(FormatProjectCommandFailure("upm install", installResponse.Message));
                return true;
            }

            UpmInstallResponsePayload? installParsed = null;
            if (!string.IsNullOrWhiteSpace(installResponse.Content))
            {
                try
                {
                    installParsed = JsonSerializer.Deserialize<UpmInstallResponsePayload>(
                        installResponse.Content,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch
                {
                }
            }

            var installedId = string.IsNullOrWhiteSpace(installParsed?.PackageId) ? target : installParsed.PackageId!;
            var installedVersion = string.IsNullOrWhiteSpace(installParsed?.Version) ? null : installParsed.Version!;
            var source = string.IsNullOrWhiteSpace(installParsed?.Source) ? null : installParsed.Source!;
            var resolvedTargetType = string.IsNullOrWhiteSpace(installParsed?.TargetType) ? targetType : installParsed.TargetType!;
            outputs.Add(installedVersion is null
                ? $"[+] installed package: {Markup.Escape(installedId)}"
                : $"[+] installed package: {Markup.Escape(installedId)} v{Markup.Escape(installedVersion)}");
            if (!string.IsNullOrWhiteSpace(source))
            {
                outputs.Add($"[i] source: {Markup.Escape(source)}");
            }

            outputs.Add($"[i] target type: {Markup.Escape(resolvedTargetType)}");
            return true;
        }

        if (subcommand.Equals("remove", StringComparison.OrdinalIgnoreCase)
            || subcommand.Equals("rm", StringComparison.OrdinalIgnoreCase)
            || subcommand.Equals("uninstall", StringComparison.OrdinalIgnoreCase))
        {
            var rawId = tokens.Count >= 3
                ? string.Join(' ', tokens.Skip(2))
                : string.Empty;
            var packageId = NormalizeLoadSelector(rawId);
            if (!IsRegistryPackageId(packageId))
            {
                outputs.Add("[x] upm remove failed: package id is required (e.g., com.unity.addressables)");
                return true;
            }

            var removeBridgeReady = await RunTrackableProgressAsync(
                session,
                "preparing package manager",
                TimeSpan.FromSeconds(6),
                () => EnsureModeContextAsync(session, daemonControlService, daemonRuntime, requireBridgeMode: true));
            if (!removeBridgeReady)
            {
                outputs.Add("[x] upm remove failed: Bridge mode is unavailable; set UNITY_PATH or open Unity editor for this project");
                return true;
            }

            var removePayload = JsonSerializer.Serialize(
                new UpmRemoveRequestPayload(packageId),
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            var removeResponse = await RunTrackableProgressAsync(
                session,
                $"removing {packageId}",
                TimeSpan.FromSeconds(20),
                () => ExecuteUpmMutationWithRecoveryAsync(
                    session,
                    daemonControlService,
                    daemonRuntime,
                    new ProjectCommandRequestDto("upm-remove", null, null, removePayload),
                    outputs));
            if (!removeResponse.Ok)
            {
                outputs.Add(FormatProjectCommandFailure("upm remove", removeResponse.Message));
                return true;
            }

            outputs.Add($"[+] removed package: {Markup.Escape(packageId)}");
            return true;
        }

        if (subcommand.Equals("update", StringComparison.OrdinalIgnoreCase)
            || subcommand.Equals("u", StringComparison.OrdinalIgnoreCase))
        {
            var updateBridgeReady = await RunTrackableProgressAsync(
                session,
                "preparing package manager",
                TimeSpan.FromSeconds(6),
                () => EnsureModeContextAsync(session, daemonControlService, daemonRuntime, requireBridgeMode: true));
            if (!updateBridgeReady)
            {
                outputs.Add("[x] upm update failed: Bridge mode is unavailable; set UNITY_PATH or open Unity editor for this project");
                return true;
            }

            var updateListPayload = JsonSerializer.Serialize(
                new UpmListRequestPayload(false, true, false),
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            var updateListResponse = await RunTrackableProgressAsync(
                session,
                "reading package state",
                TimeSpan.FromSeconds(10),
                () => ExecuteProjectCommandAsync(
                    session,
                    new ProjectCommandRequestDto("upm-list", null, null, updateListPayload)));
            if (!updateListResponse.Ok)
            {
                outputs.Add(FormatProjectCommandFailure("upm update", updateListResponse.Message));
                return true;
            }

            var updatePackages = TryParseUpmPackages(updateListResponse.Content);
            if (updatePackages is null)
            {
                outputs.Add("[x] upm update failed: invalid package payload");
                return true;
            }

            var requestedId = tokens.Count >= 3
                ? NormalizeLoadSelector(string.Join(' ', tokens.Skip(2)))
                : string.Empty;

            if (!string.IsNullOrWhiteSpace(requestedId))
            {
                var target = updatePackages.FirstOrDefault(p =>
                    !string.IsNullOrWhiteSpace(p.PackageId)
                    && p.PackageId.Equals(requestedId, StringComparison.OrdinalIgnoreCase));
                if (target is null)
                {
                    outputs.Add($"[x] upm update failed: package not installed: {Markup.Escape(requestedId)}");
                    return true;
                }

                if (target.Source?.Equals("BuiltIn", StringComparison.OrdinalIgnoreCase) == true)
                {
                    outputs.Add($"[i] update skipped: built-in package cannot be updated: {Markup.Escape(requestedId)}");
                    return true;
                }

                if (!target.IsOutdated)
                {
                    outputs.Add($"[i] already up to date: {Markup.Escape(requestedId)}");
                    return true;
                }

                var singleUpdatePayload = JsonSerializer.Serialize(
                    new UpmInstallRequestPayload(ComposeRegistryInstallTarget(requestedId, target.LatestCompatibleVersion)),
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                var singleUpdateResponse = await RunTrackableProgressAsync(
                    session,
                    $"updating {requestedId}",
                    TimeSpan.FromSeconds(30),
                    () => ExecuteUpmMutationWithRecoveryAsync(
                        session,
                        daemonControlService,
                        daemonRuntime,
                        new ProjectCommandRequestDto("upm-install", null, null, singleUpdatePayload),
                        outputs));
                if (!singleUpdateResponse.Ok)
                {
                    var verified = await TryConfirmUpmUpdateSucceededAsync(
                        session,
                        requestedId,
                        target.LatestCompatibleVersion);
                    if (verified.Confirmed)
                    {
                        var confirmedOldVersion = string.IsNullOrWhiteSpace(target.Version) ? "-" : target.Version!;
                        var confirmedVersion = string.IsNullOrWhiteSpace(verified.Version)
                            ? ResolveUpmUpdatedVersion(singleUpdateResponse.Content, target.LatestCompatibleVersion)
                            : verified.Version!;
                        outputs.Add("[i] update command timed out, but package state confirms success");
                        outputs.Add($"[+] updated package: {Markup.Escape(requestedId)} [grey]v{Markup.Escape(confirmedOldVersion)} -> v{Markup.Escape(confirmedVersion)}[/]");
                        return true;
                    }

                    outputs.Add(FormatProjectCommandFailure("upm update", singleUpdateResponse.Message));
                    return true;
                }

                var oldVersion = string.IsNullOrWhiteSpace(target.Version) ? "-" : target.Version!;
                var newVersion = ResolveUpmUpdatedVersion(singleUpdateResponse.Content, target.LatestCompatibleVersion);
                outputs.Add($"[+] updated package: {Markup.Escape(requestedId)} [grey]v{Markup.Escape(oldVersion)} -> v{Markup.Escape(newVersion)}[/]");
                return true;
            }

            var outdated = updatePackages
                .Where(p => p.IsOutdated
                            && !string.IsNullOrWhiteSpace(p.PackageId)
                            && !string.Equals(p.Source, "BuiltIn", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p.PackageId, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (outdated.Count == 0)
            {
                outputs.Add("[i] all packages are already up to date");
                return true;
            }

            outputs.Add($"[*] updating {outdated.Count} outdated package(s) safely");
            var successCount = 0;
            var failureCount = 0;
            foreach (var package in outdated)
            {
                var packageId = package.PackageId!;
                var bulkPayload = JsonSerializer.Serialize(
                    new UpmInstallRequestPayload(ComposeRegistryInstallTarget(packageId, package.LatestCompatibleVersion)),
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                var bulkResponse = await RunTrackableProgressAsync(
                    session,
                    $"updating {packageId}",
                    TimeSpan.FromSeconds(30),
                    () => ExecuteUpmMutationWithRecoveryAsync(
                        session,
                        daemonControlService,
                        daemonRuntime,
                        new ProjectCommandRequestDto("upm-install", null, null, bulkPayload),
                        outputs));
                if (bulkResponse.Ok)
                {
                    successCount++;
                    var oldVersion = string.IsNullOrWhiteSpace(package.Version) ? "-" : package.Version!;
                    var newVersion = ResolveUpmUpdatedVersion(bulkResponse.Content, package.LatestCompatibleVersion);
                    outputs.Add($"[+] updated: {Markup.Escape(packageId)} [grey]v{Markup.Escape(oldVersion)} -> v{Markup.Escape(newVersion)}[/]");
                }
                else
                {
                    var verified = await TryConfirmUpmUpdateSucceededAsync(
                        session,
                        packageId,
                        package.LatestCompatibleVersion);
                    if (verified.Confirmed)
                    {
                        successCount++;
                        var oldVersion = string.IsNullOrWhiteSpace(package.Version) ? "-" : package.Version!;
                        var confirmedVersion = string.IsNullOrWhiteSpace(verified.Version)
                            ? ResolveUpmUpdatedVersion(bulkResponse.Content, package.LatestCompatibleVersion)
                            : verified.Version!;
                        outputs.Add("[i] update command timed out, but package state confirms success");
                        outputs.Add($"[+] updated: {Markup.Escape(packageId)} [grey]v{Markup.Escape(oldVersion)} -> v{Markup.Escape(confirmedVersion)}[/]");
                    }
                    else
                    {
                        failureCount++;
                        outputs.Add(FormatProjectCommandFailure($"upm update {packageId}", bulkResponse.Message));
                    }
                }
            }

            outputs.Add($"[i] update summary: success={successCount}, failed={failureCount}");
            return true;
        }

        if (!subcommand.Equals("list", StringComparison.OrdinalIgnoreCase)
            && !subcommand.Equals("ls", StringComparison.OrdinalIgnoreCase))
        {
            outputs.Add($"[x] unsupported upm subcommand: {subcommand}");
            outputs.Add("supported: upm list (alias: upm ls), upm install (aliases: upm add, upm i), upm remove (aliases: upm rm, upm uninstall), upm update (alias: upm u)");
            return true;
        }

        var includeOutdatedOnly = false;
        var includeBuiltin = false;
        var includeGitOnly = false;
        for (var i = 2; i < tokens.Count; i++)
        {
            var option = tokens[i];
            if (option.Equals("--outdated", StringComparison.OrdinalIgnoreCase))
            {
                includeOutdatedOnly = true;
                continue;
            }

            if (option.Equals("--builtin", StringComparison.OrdinalIgnoreCase))
            {
                includeBuiltin = true;
                continue;
            }

            if (option.Equals("--git", StringComparison.OrdinalIgnoreCase))
            {
                includeGitOnly = true;
                continue;
            }

            outputs.Add($"[x] unsupported flag: {option}");
            outputs.Add("supported flags: --outdated --builtin --git");
            return true;
        }

        var bridgeModeReady = await RunTrackableProgressAsync(
            session,
            "preparing package manager",
            TimeSpan.FromSeconds(6),
            () => EnsureModeContextAsync(session, daemonControlService, daemonRuntime, requireBridgeMode: true));
        if (!bridgeModeReady)
        {
            outputs.Add("[x] upm list failed: Bridge mode is unavailable; set UNITY_PATH or open Unity editor for this project");
            return true;
        }

        var payload = JsonSerializer.Serialize(
            new UpmListRequestPayload(includeOutdatedOnly, includeBuiltin, includeGitOnly),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var response = await RunTrackableProgressAsync(
            session,
            "loading package information",
            TimeSpan.FromSeconds(12),
            () => ExecuteProjectCommandAsync(
                session,
                new ProjectCommandRequestDto("upm-list", null, null, payload)));

        if (!response.Ok)
        {
            outputs.Add(FormatProjectCommandFailure("upm list", response.Message));
            return true;
        }

        if (string.IsNullOrWhiteSpace(response.Content))
        {
            outputs.Add("[x] upm list failed: daemon returned empty package payload");
            return true;
        }

        UpmListResponsePayload? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<UpmListResponsePayload>(
                response.Content,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            outputs.Add($"[x] upm list failed: invalid package payload ({ex.Message})");
            return true;
        }

        var indexedPackages = (parsed?.Packages ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p.PackageId))
            .OrderBy(p => ResolvePackageDisplayName(p.DisplayName, p.PackageId!), StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.PackageId, StringComparer.OrdinalIgnoreCase)
            .Select((package, index) => new UpmPackageEntry(
                index,
                package.PackageId!,
                ResolvePackageDisplayName(package.DisplayName, package.PackageId!),
                string.IsNullOrWhiteSpace(package.Version) ? "-" : package.Version!,
                string.IsNullOrWhiteSpace(package.Source) ? "unknown" : package.Source!,
                string.IsNullOrWhiteSpace(package.LatestCompatibleVersion) ? null : package.LatestCompatibleVersion,
                package.IsOutdated,
                package.IsDeprecated,
                package.IsPreview))
            .ToList();

        session.ProjectView.LastUpmPackages.Clear();
        session.ProjectView.LastUpmPackages.AddRange(indexedPackages);

        outputs.Add($"{indexedPackages.Count} package(s)");
        if (indexedPackages.Count == 0)
        {
            outputs.Add("no packages matched the selected filters");
            return true;
        }

        foreach (var package in indexedPackages)
        {
            var statusColor = ResolveUpmStatusColor(package);
            var statusLabel = ResolveUpmStatusLabel(package);
            outputs.Add(
                $"[{CliTheme.TextSecondary}]{package.Index}.[/] {Markup.Escape(package.DisplayName)} ({Markup.Escape(package.PackageId)}) v{Markup.Escape(package.Version)} [{CliTheme.TextSecondary}]{Markup.Escape(package.Source)}[/] [{statusColor}]{Markup.Escape(statusLabel)}[/]");
        }
        return true;
    }

    private static bool TryNormalizeUpmInstallTarget(
        string rawTarget,
        out string normalizedTarget,
        out string targetType,
        out string error)
    {
        normalizedTarget = NormalizeLoadSelector(rawTarget);
        targetType = string.Empty;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedTarget))
        {
            error = "missing target";
            return false;
        }

        if (IsRegistryPackageId(normalizedTarget))
        {
            targetType = "registry";
            return true;
        }

        if (IsGitPackageUrl(normalizedTarget))
        {
            targetType = "git";
            return true;
        }

        if (IsLocalFilePackagePath(normalizedTarget))
        {
            targetType = "file";
            return true;
        }

        error = "target must be registry ID, Git URL, or file: path";
        return false;
    }

    private static bool IsRegistryPackageId(string value)
    {
        var (packageId, version) = SplitRegistryTarget(value);
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return false;
        }

        if (version is not null && string.IsNullOrWhiteSpace(version))
        {
            return false;
        }

        var segments = packageId.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
        {
            return false;
        }

        foreach (var segment in segments)
        {
            if (segment.Length == 0)
            {
                return false;
            }

            if (!char.IsLetterOrDigit(segment[0]))
            {
                return false;
            }

            foreach (var ch in segment)
            {
                if (!char.IsLetterOrDigit(ch) && ch != '-' && ch != '_')
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static (string PackageId, string? Version) SplitRegistryTarget(string value)
    {
        var at = value.IndexOf('@');
        if (at < 0)
        {
            return (value, null);
        }

        var packageId = value[..at];
        var version = at + 1 < value.Length ? value[(at + 1)..] : string.Empty;
        return (packageId, version);
    }

    private static string ComposeRegistryInstallTarget(string packageId, string? latestCompatibleVersion)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return packageId;
        }

        if (string.IsNullOrWhiteSpace(latestCompatibleVersion))
        {
            return packageId;
        }

        return $"{packageId}@{latestCompatibleVersion}";
    }

    private static bool IsGitPackageUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var isHttp = uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                     || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        if (!isHttp)
        {
            return false;
        }

        var withoutQuery = value.Split('?', '#')[0];
        return withoutQuery.EndsWith(".git", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLocalFilePackagePath(string value)
    {
        if (!value.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var pathPart = value["file:".Length..].Trim();
        return !string.IsNullOrWhiteSpace(pathPart);
    }

    private async Task<T> RunTrackableProgressAsync<T>(
        CliSessionState session,
        string activity,
        TimeSpan expectedDuration,
        Func<Task<T>> operation)
    {
        var state = session.ProjectView;
        var markerIndex = state.CommandTranscript.Count;
        state.CommandTranscript.Add(string.Empty);

        var startedAt = DateTime.UtcNow;
        var tick = 0;
        var task = operation();

        while (!task.IsCompleted)
        {
            var elapsed = DateTime.UtcNow - startedAt;
            var progress = TuiTrackableProgress.ComputeExpectedDurationProgress(elapsed, expectedDuration);
            state.CommandTranscript[markerIndex] = TuiTrackableProgress.BuildTrackableLine(activity, tick, progress, elapsed);
            RenderFrame(state);
            tick++;
            await Task.Delay(120);
        }

        try
        {
            var result = await task;
            var total = DateTime.UtcNow - startedAt;
            state.CommandTranscript[markerIndex] = TuiTrackableProgress.BuildTrackableLine(activity, tick, 1d, total, done: true);
            RenderFrame(state);
            return result;
        }
        catch
        {
            var total = DateTime.UtcNow - startedAt;
            state.CommandTranscript[markerIndex] = TuiTrackableProgress.BuildTrackableLine(activity, tick, 1d, total, failed: true);
            RenderFrame(state);
            throw;
        }
    }

    private static UpmListPackagePayload? TryFindUpmPackageById(string? rawContent, string packageId)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return null;
        }

        var packages = TryParseUpmPackages(rawContent);
        return packages?.FirstOrDefault(p =>
            !string.IsNullOrWhiteSpace(p.PackageId)
            && p.PackageId.Equals(packageId, StringComparison.OrdinalIgnoreCase));
    }

    private static List<UpmListPackagePayload>? TryParseUpmPackages(string? rawContent)
    {
        if (string.IsNullOrWhiteSpace(rawContent))
        {
            return null;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<UpmListResponsePayload>(
                rawContent,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return parsed?.Packages;
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveUpmUpdatedVersion(string? installResponseContent, string? fallbackLatestCompatibleVersion)
    {
        if (!string.IsNullOrWhiteSpace(installResponseContent))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<UpmInstallResponsePayload>(
                    installResponseContent,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (!string.IsNullOrWhiteSpace(parsed?.Version))
                {
                    return parsed.Version!;
                }
            }
            catch
            {
            }
        }

        return string.IsNullOrWhiteSpace(fallbackLatestCompatibleVersion)
            ? "unknown"
            : fallbackLatestCompatibleVersion!;
    }

    private void RenderFrame(ProjectViewState state, int? highlightedEntryIndex = null, bool focusModeEnabled = false)
    {
        AnsiConsole.Clear();
        var lines = _renderer.Render(state, highlightedEntryIndex, focusModeEnabled);
        foreach (var line in lines)
        {
            CliTheme.MarkupLine(line);
        }
    }

    private static void AppendTranscript(ProjectViewState state, IReadOnlyList<string> outputs)
    {
        if (outputs.Count == 0)
        {
            return;
        }

        state.CommandTranscript.AddRange(outputs);
        if (state.CommandTranscript.Count <= MaxTranscriptEntries)
        {
            return;
        }

        var overflow = state.CommandTranscript.Count - MaxTranscriptEntries;
        state.CommandTranscript.RemoveRange(0, overflow);
    }

    private static bool IsHierarchyAssetExtension(string extension)
    {
        return extension.Equals(".unity", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".prefab", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveLoadAssetKind(string extension)
    {
        if (extension.Equals(".unity", StringComparison.OrdinalIgnoreCase))
        {
            return "scene";
        }

        if (extension.Equals(".prefab", StringComparison.OrdinalIgnoreCase))
        {
            return "prefab";
        }

        if (extension.Equals(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return "script";
        }

        return "asset";
    }

    private void EmitImmediateLoadFeedback(ProjectViewState state, string targetName)
    {
        var prefix = ResolveLoadAssetKind(Path.GetExtension(targetName));
        AppendTranscript(state, [$"[*] loading {prefix}: {targetName}"]);
        RenderFrame(state);
    }

    private void EmitLoadDiagnostic(ProjectViewState state, string message)
    {
        AppendTranscript(state, [$"[grey]load[/]: {Markup.Escape(message)}"]);
        RenderFrame(state);
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

    private static bool TryResolveMkParentPath(
        CliSessionState session,
        string? parentSelector,
        out string parentPath,
        out string error)
    {
        parentPath = "Assets";
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(parentSelector))
        {
            return true;
        }

        var selector = parentSelector.Trim();
        if (selector.Equals("Assets", StringComparison.OrdinalIgnoreCase)
            || selector.Equals("/", StringComparison.OrdinalIgnoreCase)
            || selector.Equals("root", StringComparison.OrdinalIgnoreCase))
        {
            parentPath = "Assets";
            return true;
        }

        var state = session.ProjectView;
        var entry = FindEntryBySelector(state, selector);
        if (entry is not null)
        {
            if (!entry.IsDirectory)
            {
                error = $"--parent target is not a folder: {selector}";
                return false;
            }

            parentPath = entry.RelativePath;
            return true;
        }

        var normalized = selector.Replace('\\', '/').Trim('/');
        var relative = normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : $"Assets/{normalized}";
        if (string.IsNullOrWhiteSpace(session.CurrentProjectPath))
        {
            error = $"parent folder not found: {selector}";
            return false;
        }

        var absolute = ResolveAbsolutePath(session.CurrentProjectPath!, relative);
        if (!Directory.Exists(absolute))
        {
            error = $"parent folder not found: {selector}";
            return false;
        }

        parentPath = relative;
        return true;
    }

    private static bool TryParseProjectMkArguments(
        IReadOnlyList<string> tokens,
        out string mkType,
        out int count,
        out string? name,
        out string? parent,
        out string error)
    {
        mkType = string.Empty;
        count = 1;
        name = null;
        parent = null;
        error = "usage: make --type <type> [--count <count>] [--name <name>|-n <name>] [--parent <idx|name>] | mk <type> [count] [--name <name>|-n <name>] [--parent <idx|name>]";
        if (tokens.Count == 0)
        {
            return false;
        }

        var isMake = tokens[0].Equals("make", StringComparison.OrdinalIgnoreCase)
                     || (tokens[0].Equals("mk", StringComparison.OrdinalIgnoreCase)
                         && tokens.Count >= 2
                         && (tokens[1].StartsWith("--type", StringComparison.OrdinalIgnoreCase)
                             || tokens[1].StartsWith("-t", StringComparison.OrdinalIgnoreCase)));
        if (isMake)
        {
            if (tokens.Count < 3)
            {
                return false;
            }

            for (var i = 1; i < tokens.Count; i++)
            {
                var token = tokens[i];
                if (token.Equals("--type", StringComparison.OrdinalIgnoreCase) || token.Equals("-t", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= tokens.Count)
                    {
                        error = "usage: make --type <type> [--count <count>] [--name <name>|-n <name>] [--parent <idx|name>]";
                        return false;
                    }

                    mkType = tokens[++i];
                    continue;
                }

                if (token.StartsWith("--type=", StringComparison.OrdinalIgnoreCase))
                {
                    mkType = token["--type=".Length..];
                    continue;
                }

                if (token.Equals("--count", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= tokens.Count || !int.TryParse(tokens[++i], out count) || count <= 0)
                    {
                        error = "count must be a positive integer";
                        return false;
                    }

                    continue;
                }

                if (token.StartsWith("--count=", StringComparison.OrdinalIgnoreCase))
                {
                    var raw = token["--count=".Length..];
                    if (!int.TryParse(raw, out count) || count <= 0)
                    {
                        error = "count must be a positive integer";
                        return false;
                    }

                    continue;
                }

                if (token.StartsWith("--name=", StringComparison.OrdinalIgnoreCase))
                {
                    name = token["--name=".Length..].Trim();
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        error = "name must not be empty";
                        return false;
                    }

                    continue;
                }

                if (token.StartsWith("-n=", StringComparison.OrdinalIgnoreCase))
                {
                    name = token["-n=".Length..].Trim();
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        error = "name must not be empty";
                        return false;
                    }

                    continue;
                }

                if (token.Equals("--name", StringComparison.OrdinalIgnoreCase) || token.Equals("-n", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= tokens.Count)
                    {
                        error = "usage: make --type <type> [--count <count>] [--name <name>|-n <name>] [--parent <idx|name>]";
                        return false;
                    }

                    name = tokens[++i].Trim();
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        error = "name must not be empty";
                        return false;
                    }

                    continue;
                }

                if (token.StartsWith("--parent=", StringComparison.OrdinalIgnoreCase))
                {
                    parent = token["--parent=".Length..].Trim();
                    if (string.IsNullOrWhiteSpace(parent))
                    {
                        error = "parent must not be empty";
                        return false;
                    }

                    continue;
                }

                if (token.Equals("--parent", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= tokens.Count)
                    {
                        error = "usage: make --type <type> [--count <count>] [--name <name>|-n <name>] [--parent <idx|name>]";
                        return false;
                    }

                    parent = tokens[++i].Trim();
                    if (string.IsNullOrWhiteSpace(parent))
                    {
                        error = "parent must not be empty";
                        return false;
                    }

                    continue;
                }

                error = $"unsupported option: {token}";
                return false;
            }

            if (string.IsNullOrWhiteSpace(mkType))
            {
                error = "missing --type <type>";
                return false;
            }

            return true;
        }

        if (tokens.Count < 2)
        {
            error = "usage: mk <type> [count] [--name <name>|-n <name>] [--parent <idx|name>]";
            return false;
        }

        mkType = tokens[1];
        var countSpecified = false;
        for (var i = 2; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.StartsWith("--count=", StringComparison.OrdinalIgnoreCase))
            {
                var raw = token["--count=".Length..];
                if (!int.TryParse(raw, out count) || count <= 0)
                {
                    error = "count must be a positive integer";
                    return false;
                }

                countSpecified = true;
                continue;
            }

            if (token.Equals("--count", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= tokens.Count || !int.TryParse(tokens[++i], out count) || count <= 0)
                {
                    error = "count must be a positive integer";
                    return false;
                }

                countSpecified = true;
                continue;
            }

            if (token.StartsWith("--name=", StringComparison.OrdinalIgnoreCase))
            {
                name = token["--name=".Length..].Trim();
                if (string.IsNullOrWhiteSpace(name))
                {
                    error = "name must not be empty";
                    return false;
                }

                continue;
            }

            if (token.StartsWith("-n=", StringComparison.OrdinalIgnoreCase))
            {
                name = token["-n=".Length..].Trim();
                if (string.IsNullOrWhiteSpace(name))
                {
                    error = "name must not be empty";
                    return false;
                }

                continue;
            }

                if (token.Equals("--name", StringComparison.OrdinalIgnoreCase) || token.Equals("-n", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= tokens.Count)
                    {
                        error = "usage: mk <type> [count] [--name <name>|-n <name>] [--parent <idx|name>]";
                        return false;
                    }

                name = tokens[++i].Trim();
                if (string.IsNullOrWhiteSpace(name))
                {
                    error = "name must not be empty";
                    return false;
                }

                    continue;
                }

                if (token.StartsWith("--parent=", StringComparison.OrdinalIgnoreCase))
                {
                    parent = token["--parent=".Length..].Trim();
                    if (string.IsNullOrWhiteSpace(parent))
                    {
                        error = "parent must not be empty";
                        return false;
                    }

                    continue;
                }

                if (token.Equals("--parent", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= tokens.Count)
                    {
                        error = "usage: mk <type> [count] [--name <name>|-n <name>] [--parent <idx|name>]";
                        return false;
                    }

                    parent = tokens[++i].Trim();
                    if (string.IsNullOrWhiteSpace(parent))
                    {
                        error = "parent must not be empty";
                        return false;
                    }

                    continue;
                }

            if (!countSpecified && int.TryParse(token, out var parsedCount) && parsedCount > 0)
            {
                count = parsedCount;
                countSpecified = true;
                continue;
            }

            error = $"unsupported mk argument: {token}";
            return false;
        }

        return true;
    }

    private static (string TemplateName, string TemplateSource, string Content) ResolveTemplate(string projectPath)
    {
        var templatesJsonPath = Path.Combine(projectPath, "templates.json");
        if (File.Exists(templatesJsonPath))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(templatesJsonPath));
                if (document.RootElement.TryGetProperty("templates", out var templates)
                    && templates.ValueKind == JsonValueKind.Object)
                {
                    foreach (var key in new[] { "CustomScript", "script" })
                    {
                        if (templates.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                        {
                            var templateRelative = value.GetString();
                            if (string.IsNullOrWhiteSpace(templateRelative))
                            {
                                continue;
                            }

                            var templatePath = ResolveAbsolutePath(projectPath, templateRelative);
                            if (File.Exists(templatePath))
                            {
                                return (key, "templates.json", File.ReadAllText(templatePath));
                            }
                        }
                    }
                }
            }
            catch
            {
            }
        }

        var defaultTemplate =
$"using UnityEngine;{Environment.NewLine}{Environment.NewLine}public class #NAME# : MonoBehaviour{Environment.NewLine}{{{Environment.NewLine}    private void Start(){{ }}{Environment.NewLine}{Environment.NewLine}    private void Update(){{ }}{Environment.NewLine}}}{Environment.NewLine}";
        return ("ProjectDefault", "default template", defaultTemplate);
    }

    private static string ResolveAbsolutePath(string projectPath, string relativeOrAbsolute)
    {
        if (Path.IsPathRooted(relativeOrAbsolute))
        {
            return relativeOrAbsolute;
        }

        return Path.GetFullPath(Path.Combine(projectPath, relativeOrAbsolute));
    }

    private static string CombineRelative(string parent, string child)
    {
        if (string.IsNullOrWhiteSpace(parent))
        {
            return child.Replace('\\', '/');
        }

        return $"{parent.TrimEnd('/', '\\')}/{child}".Replace('\\', '/');
    }

    private static string SanitizeTypeName(string raw)
    {
        var builder = new StringBuilder();
        foreach (var ch in raw)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_')
            {
                builder.Append(ch);
            }
        }

        var value = builder.ToString();
        if (value.Length == 0)
        {
            return string.Empty;
        }

        if (!char.IsLetter(value[0]) && value[0] != '_')
        {
            value = "_" + value;
        }

        return value;
    }

    private static string ResolveScriptCreateName(string canonicalType, string? baseName, int index, int count)
    {
        var defaultBase = canonicalType.Equals("ScriptableObjectScript", StringComparison.OrdinalIgnoreCase)
            ? "NewScriptableObject"
            : "NewScript";
        var resolvedBase = string.IsNullOrWhiteSpace(baseName) ? defaultBase : baseName.Trim();
        return count > 1 ? $"{resolvedBase}_{index + 1}" : resolvedBase;
    }

    private static string ResolveUniqueScriptTypeName(string projectPath, string parentPath, string baseTypeName)
    {
        var candidate = baseTypeName;
        var suffix = 1;
        while (true)
        {
            var candidateRelative = CombineRelative(parentPath, $"{candidate}.cs");
            var candidateAbsolute = ResolveAbsolutePath(projectPath, candidateRelative);
            if (!File.Exists(candidateAbsolute))
            {
                return candidate;
            }

            candidate = $"{baseTypeName}_{suffix++}";
        }
    }

    private static string BuildScriptableObjectTemplate(string typeName)
    {
        return
$"using UnityEngine;{Environment.NewLine}{Environment.NewLine}[CreateAssetMenu(fileName = \"{typeName}\", menuName = \"ScriptableObjects/{typeName}\")]{Environment.NewLine}public class {typeName} : ScriptableObject{Environment.NewLine}{{{Environment.NewLine}}}{Environment.NewLine}";
    }

    private static List<string> ParseMkAssetCreatedPaths(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<MkAssetResponsePayload>(
                content,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (parsed?.CreatedPaths is null || parsed.CreatedPaths.Count == 0)
            {
                return [];
            }

            return parsed.CreatedPaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path!)
                .ToList();
        }
        catch
        {
            return [];
        }
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

    private static (string? TypeFilter, string Query) ParseProjectQuery(string query)
        => ProjectMkCatalog.ParseFuzzyQuery(query);

    private static bool PassesTypeFilter(string path, string? typeFilter)
        => ProjectMkCatalog.PassesFuzzyTypeFilter(path, typeFilter);

    private static string FormatProjectCommandFailure(string action, string? message)
    {
        var (category, hint) = ClassifyProjectBridgeFailure(message);
        var details = string.IsNullOrWhiteSpace(message) ? "unknown error" : message;
        return string.IsNullOrWhiteSpace(hint)
            ? $"[x] {action} failed ({category}): {details}"
            : $"[x] {action} failed ({category}): {details} [grey]{hint}[/]";
    }

    private static (string Category, string Hint) ClassifyProjectBridgeFailure(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return ("bridge runtime error", "Command is implemented; inspect bridge logs for details.");
        }

        if (message.StartsWith(ProjectDaemonBridge.StubbedBridgePrefix, StringComparison.Ordinal))
        {
            return ("stubbed bridge", "This daemon path is not implemented; run with Bridge mode attached.");
        }

        if (message.Contains("daemon did not return", StringComparison.OrdinalIgnoreCase)
            || message.Contains("daemon returned", StringComparison.OrdinalIgnoreCase)
            || message.Contains("daemon is not attached", StringComparison.OrdinalIgnoreCase))
        {
            return ("bridge transport error", "Bridge connection failed; ensure daemon/editor bridge is running and attached.");
        }

        return ("bridge runtime error", "Command is implemented; bridge returned an operational error.");
    }

    private static List<string> Tokenize(string input)
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

    private sealed record UpmListRequestPayload(
        bool IncludeOutdated,
        bool IncludeBuiltin,
        bool IncludeGit);

    private sealed record MkAssetRequestPayload(
        string Type,
        int Count,
        string? Name);

    private sealed record MkAssetResponsePayload(
        List<string>? CreatedPaths);

    private sealed record UpmInstallRequestPayload(
        string Target);

    private sealed record UpmRemoveRequestPayload(
        string PackageId);

    private sealed record UpmListResponsePayload(
        List<UpmListPackagePayload>? Packages);

    private sealed record UpmInstallResponsePayload(
        string? PackageId,
        string? Version,
        string? Source,
        string? TargetType);

    private sealed record UpmListPackagePayload(
        string? PackageId,
        string? DisplayName,
        string? Version,
        string? Source,
        string? LatestCompatibleVersion,
        bool IsOutdated,
        bool IsDeprecated,
        bool IsPreview);
}
