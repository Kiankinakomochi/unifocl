using System.Text;
using Spectre.Console;
using Unifocl.Contracts;

internal sealed class HierarchyTui
{
    private const int DefaultFrameWidth = 78;
    private const int MinFrameWidth = 40;
    private const int MinTreeRows = 6;
    private const int MinCommandRows = 4;
    private const int ReservedPromptRows = 4;
    private const int FrameOverheadRows = 5;
    private const ConsoleKey FocusModeKey = ConsoleKey.F7;

    private static readonly List<CommandSpec> HierarchyCommands =
    [
        new("list", "Refresh hierarchy snapshot", "list"),
        new("ls", "Alias for list", "ls"),
        new("ref", "Alias for list", "ref"),
        new("enter <idx>", "Enter child object by visible index", "enter"),
        new("cd <idx>", "Alias for enter", "cd"),
        new("up", "Move to parent object", "up"),
        new("..", "Alias for up", ".."),
        new(":i", "Alias for up", ":i"),
        new("make --type <type> [--count <count>]", "Create typed objects under current object", "make"),
        new("mk <type> [count] [--name <name>|-n <name>]", "Create typed object(s) under current object", "mk"),
        new("remove <idx>", "Remove object by visible index", "remove"),
        new("rm <idx>", "Alias for remove", "rm"),
        new("toggle <idx>", "Toggle active state by index", "toggle"),
        new("t <idx>", "Alias for toggle", "t"),
        new("inspect <idx|name>", "Transition selected hierarchy object to inspector mode", "inspect"),
        new("ins <idx|name>", "Alias for inspect", "ins"),
        new("f <query>", "Fuzzy search hierarchy paths", "f"),
        new("ff <query>", "Alias for fuzzy search", "ff"),
        new("scroll [tree|log] <up|down> [count]", "Scroll tree or command stream", "scroll"),
        new("quit|exit|q|:q", "Exit hierarchy mode", "quit")
    ];

    private static readonly List<CommandSpec> MkTypeCommands =
    [
        new("mk Canvas [count]", "Root UI structural element", "mk Canvas"),
        new("mk Panel [count]", "Stretching UI panel with image background", "mk Panel"),
        new("mk Text [count]", "TextMeshPro UI text", "mk Text"),
        new("mk TMP [count]", "Alias for TextMeshPro UI text", "mk TMP"),
        new("mk Image [count]", "Standard UI image", "mk Image"),
        new("mk Button [count]", "Compound UI button with TMP label", "mk Button"),
        new("mk Toggle [count]", "Compound UI toggle", "mk Toggle"),
        new("mk Slider [count]", "UI slider", "mk Slider"),
        new("mk Scrollbar [count]", "UI scrollbar", "mk Scrollbar"),
        new("mk ScrollView [count]", "UI scroll view", "mk ScrollView"),
        new("mk EventSystem", "Standalone event system", "mk EventSystem"),
        new("mk Cube [count]", "3D primitive cube", "mk Cube"),
        new("mk Sphere [count]", "3D primitive sphere", "mk Sphere"),
        new("mk Capsule [count]", "3D primitive capsule", "mk Capsule"),
        new("mk Cylinder [count]", "3D primitive cylinder", "mk Cylinder"),
        new("mk Plane [count]", "3D primitive plane", "mk Plane"),
        new("mk Quad [count]", "3D primitive quad", "mk Quad"),
        new("mk DirLight [count]", "Directional light", "mk DirLight"),
        new("mk DirectionalLight [count]", "Directional light", "mk DirectionalLight"),
        new("mk PointLight [count]", "Point light", "mk PointLight"),
        new("mk SpotLight [count]", "Spot light", "mk SpotLight"),
        new("mk AreaLight [count]", "Area-light workflow helper", "mk AreaLight"),
        new("mk ReflectionProbe [count]", "Reflection probe", "mk ReflectionProbe"),
        new("mk Sprite [count]", "2D sprite renderer object", "mk Sprite"),
        new("mk SpriteMask [count]", "2D sprite mask", "mk SpriteMask"),
        new("mk Camera [count]", "Camera with audio listener", "mk Camera"),
        new("mk AudioSource [count]", "Audio source object", "mk AudioSource"),
        new("mk Empty [count]", "Empty object", "mk Empty"),
        new("mk EmptyParent", "Wrap current object in a new empty parent", "mk EmptyParent"),
        new("mk EmptyChild [count]", "Create empty child under current object", "mk EmptyChild")
    ];

    private static readonly Dictionary<string, HierarchyMkType> MkTypeLookup = BuildMkTypeLookup();

    private readonly HierarchyDaemonClient _daemonClient = new();
    private readonly HashSet<int> _collapsedNodeIds = [];
    private int _treeScrollOffset;
    private int _commandScrollOffset = int.MaxValue;
    private bool _followCommandScroll = true;

    private sealed record HierarchyTreeLine(int? EntryIndex, int? NodeId, bool HasChildren, string Text);
    private sealed record FocusTreeEntry(int EntryIndex, int NodeId, int Depth, bool HasChildren);

    public async Task RunAsync(
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime,
        Action<string> log,
        Func<string, Task<bool>>? transitionToInspectorAsync = null)
    {
        if (string.IsNullOrWhiteSpace(session.CurrentProjectPath))
        {
            log("[yellow]hierarchy[/]: open a project first with /open");
            return;
        }

        var touched = await daemonControlService.TouchAttachedDaemonAsync(session);
        if (!touched)
        {
            await daemonControlService.EnsureProjectDaemonAsync(session.CurrentProjectPath, daemonRuntime, session, log);
        }

        if (session.AttachedPort is null)
        {
            log("[red]hierarchy[/]: daemon is not attached");
            return;
        }

        var port = session.AttachedPort.Value;
        var snapshot = await _daemonClient.GetSnapshotAsync(port);
        if (snapshot is null)
        {
            log("[red]hierarchy[/]: failed to load hierarchy snapshot from daemon");
            return;
        }

        var cwdId = snapshot.Root.Id;
        var commandLog = new List<string>();
        _treeScrollOffset = 0;
        _commandScrollOffset = int.MaxValue;
        _followCommandScroll = true;
        _collapsedNodeIds.Clear();

        while (true)
        {
            var treeLines = BuildTreeLines(snapshot, cwdId, out var indexMap, out var cwdPath, out _, _collapsedNodeIds);
            RenderFrame(port, snapshot.Scene, treeLines.Select(line => line.Text).ToList(), commandLog, cwdPath);

            var firstKey = Console.ReadKey(intercept: true);
            var firstIntent = KeyboardIntentReader.FromConsoleKey(firstKey);
            if (firstIntent == KeyboardIntent.FocusProject
                || firstKey.Key == FocusModeKey)
            {
                commandLog.Add("[i] hierarchy focus mode enabled (up/down select, idx jump, tab expand, enter inspect, shift+tab collapse, esc/F7 exit)");
                var focusInspectTarget = await RunKeyboardFocusModeAsync(port, snapshot, cwdId, commandLog, updatedSnapshot =>
                {
                    snapshot = updatedSnapshot;
                    if (!ContainsNode(snapshot.Root, cwdId))
                    {
                        cwdId = snapshot.Root.Id;
                    }
                });
                TrimCommandLog(commandLog);

                if (!string.IsNullOrWhiteSpace(focusInspectTarget)
                    && transitionToInspectorAsync is not null
                    && await transitionToInspectorAsync(focusInspectTarget))
                {
                    Console.Clear();
                    log($"[grey]hierarchy[/]: transitioned to inspector -> [white]{Markup.Escape(focusInspectTarget)}[/]");
                    return;
                }

                continue;
            }

            var input = ReadPromptInputWithIntellisenseFromFirstKey(cwdPath, firstKey, snapshot, cwdId);

            if (string.IsNullOrWhiteSpace(input))
            {
                continue;
            }

            if (input.Equals("q", StringComparison.OrdinalIgnoreCase)
                || input.Equals("quit", StringComparison.OrdinalIgnoreCase)
                || input.Equals("exit", StringComparison.OrdinalIgnoreCase)
                || input.Equals(":q", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            var (inspectCommandHandled, inspectTransitioned) = await TryHandleInspectTransitionCommandAsync(
                input,
                indexMap,
                cwdId,
                snapshot,
                commandLog,
                transitionToInspectorAsync);
            if (inspectCommandHandled)
            {
                TrimCommandLog(commandLog);
                if (inspectTransitioned)
                {
                    Console.Clear();
                    log("[grey]hierarchy[/]: transitioned to inspector mode");
                    return;
                }

                continue;
            }

            if (input.Equals("ref", StringComparison.OrdinalIgnoreCase)
                || input.Equals("list", StringComparison.OrdinalIgnoreCase)
                || input.Equals("ls", StringComparison.OrdinalIgnoreCase))
            {
                var refreshed = await _daemonClient.GetSnapshotAsync(port);
                if (refreshed is null)
                {
                    commandLog.Add("[!] failed to refresh hierarchy snapshot");
                    TrimCommandLog(commandLog);
                    continue;
                }

                snapshot = refreshed;
                if (!ContainsNode(snapshot.Root, cwdId))
                {
                    cwdId = snapshot.Root.Id;
                }

                commandLog.Add("[*] ok: hierarchy snapshot refreshed");
                TrimCommandLog(commandLog);
                continue;
            }

            var handled = await TryHandleInFrameCommandAsync(
                input,
                indexMap,
                cwdId,
                port,
                snapshot,
                commandLog,
                newCwdId => cwdId = newCwdId);

            if (!handled)
            {
                commandLog.Add("[!] unknown command (type any text to view hierarchy intellisense)");
            }

            var nextSnapshot = await _daemonClient.GetSnapshotAsync(port);
            if (nextSnapshot is not null)
            {
                snapshot = nextSnapshot;
                if (!ContainsNode(snapshot.Root, cwdId))
                {
                    cwdId = snapshot.Root.Id;
                }

                PruneCollapsedNodeSet(snapshot.Root, _collapsedNodeIds);
            }

            TrimCommandLog(commandLog);
        }

        Console.Clear();
        log("[grey]hierarchy[/]: exited hierarchy mode");
    }

    private async Task<bool> TryHandleInFrameCommandAsync(
        string input,
        IReadOnlyDictionary<int, int> indexMap,
        int cwdId,
        int port,
        HierarchySnapshotDto snapshot,
        List<string> commandLog,
        Action<int> setCwd)
    {
        var tokens = Tokenize(input);
        if (tokens.Count == 0)
        {
            return true;
        }

        tokens[0] = tokens[0].ToLowerInvariant() switch
        {
            "enter" => "cd",
            ".." => "up",
            "remove" => "rm",
            "t" => "toggle",
            "ins" => "inspect",
            "ff" => "f",
            _ => tokens[0]
        };

        if (tokens[0].Equals("up", StringComparison.OrdinalIgnoreCase) || tokens[0].Equals(":i", StringComparison.OrdinalIgnoreCase))
        {
            var parentId = FindParentId(snapshot.Root, cwdId);
            setCwd(parentId ?? snapshot.Root.Id);
            _treeScrollOffset = 0;
            commandLog.Add("[*] ok: moved to parent");
            return true;
        }

        if (TryHandleScrollCommand(tokens, commandLog))
        {
            return true;
        }

        if (tokens.Count == 2 && tokens[0].Equals("cd", StringComparison.OrdinalIgnoreCase) && int.TryParse(tokens[1], out var cdIndex))
        {
            if (!indexMap.TryGetValue(cdIndex, out var targetId))
            {
                commandLog.Add($"[!] invalid index: {cdIndex}");
                return true;
            }

            setCwd(targetId);
            _treeScrollOffset = 0;
            commandLog.Add("[*] ok: changed current object");
            return true;
        }

        if (tokens[0].Equals("make", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseMakeArguments(tokens, out var makeType, out var makeCount, out var makeError))
            {
                commandLog.Add($"[!] {makeError}");
                return true;
            }

            if (!TryNormalizeMkType(makeType, out var normalizedMakeType, out makeError))
            {
                commandLog.Add($"[!] {makeError}");
                return true;
            }

            var targetId = normalizedMakeType.Equals("EmptyParent", StringComparison.OrdinalIgnoreCase) ? cwdId : (int?)null;
            if (normalizedMakeType.Equals("EmptyParent", StringComparison.OrdinalIgnoreCase) && cwdId == snapshot.Root.Id)
            {
                commandLog.Add("[!] EmptyParent requires a non-root current object");
                return true;
            }

            var parentId = normalizedMakeType.Equals("EmptyParent", StringComparison.OrdinalIgnoreCase)
                ? (FindParentId(snapshot.Root, cwdId) ?? snapshot.Root.Id)
                : cwdId;

            var response = await _daemonClient.ExecuteAsync(
                port,
                new HierarchyCommandRequestDto("mk", parentId, targetId, null, false, normalizedMakeType, makeCount));
            if (!response.Ok)
            {
                commandLog.Add($"[!] {FormatHierarchyCommandFailure(response.Message)}");
                return true;
            }

            commandLog.Add($"[+] created: {normalizedMakeType} x{makeCount}");
            return true;
        }

        if (tokens.Count >= 2 && tokens[0].Equals("mk", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseMkArguments(tokens, out var mkType, out var mkCount, out var mkName, out var mkError))
            {
                commandLog.Add($"[!] {mkError}");
                return true;
            }

            if (!TryNormalizeMkType(mkType, out var normalizedMkType, out mkError))
            {
                commandLog.Add($"[!] {mkError}");
                return true;
            }

            var targetId = normalizedMkType.Equals("EmptyParent", StringComparison.OrdinalIgnoreCase) ? cwdId : (int?)null;
            if (normalizedMkType.Equals("EmptyParent", StringComparison.OrdinalIgnoreCase) && cwdId == snapshot.Root.Id)
            {
                commandLog.Add("[!] EmptyParent requires a non-root current object");
                return true;
            }

            var parentId = normalizedMkType.Equals("EmptyParent", StringComparison.OrdinalIgnoreCase)
                ? (FindParentId(snapshot.Root, cwdId) ?? snapshot.Root.Id)
                : cwdId;

            var response = await _daemonClient.ExecuteAsync(
                port,
                new HierarchyCommandRequestDto("mk", parentId, targetId, mkName, false, normalizedMkType, mkCount));

            if (!response.Ok)
            {
                commandLog.Add($"[!] {FormatHierarchyCommandFailure(response.Message)}");
                return true;
            }

            commandLog.Add($"[+] created: {normalizedMkType} x{mkCount}");
            return true;
        }

        if (tokens.Count == 2 && tokens[0].Equals("toggle", StringComparison.OrdinalIgnoreCase) && int.TryParse(tokens[1], out var toggleIndex))
        {
            if (!indexMap.TryGetValue(toggleIndex, out var targetId))
            {
                commandLog.Add($"[!] invalid index: {toggleIndex}");
                return true;
            }

            var response = await _daemonClient.ExecuteAsync(
                port,
                new HierarchyCommandRequestDto("toggle", null, targetId, null, false));
            if (!response.Ok)
            {
                commandLog.Add($"[!] {FormatHierarchyCommandFailure(response.Message)}");
                return true;
            }

            var targetName = FindNode(snapshot.Root, targetId)?.Name ?? "Object";
            var activeState = response.IsActive == true ? "active" : "inactive";
            commandLog.Add($"[*] ok: {targetName} set to {activeState}");
            return true;
        }

        if (tokens.Count == 2 && tokens[0].Equals("rm", StringComparison.OrdinalIgnoreCase) && int.TryParse(tokens[1], out var rmIndex))
        {
            if (!indexMap.TryGetValue(rmIndex, out var targetId))
            {
                commandLog.Add($"[!] invalid index: {rmIndex}");
                return true;
            }

            var targetName = FindNode(snapshot.Root, targetId)?.Name ?? "Object";
            var response = await _daemonClient.ExecuteAsync(
                port,
                new HierarchyCommandRequestDto("rm", null, targetId, null, false));
            if (!response.Ok)
            {
                commandLog.Add($"[!] {FormatHierarchyCommandFailure(response.Message)}");
                return true;
            }

            commandLog.Add($"[-] removed: {targetName} [{targetId}]");
            return true;
        }

        if (tokens.Count >= 1 && tokens[0].Equals("f", StringComparison.OrdinalIgnoreCase))
        {
            var query = tokens.Count >= 2
                ? string.Join(' ', tokens.Skip(1))
                : string.Empty;
            var response = await _daemonClient.SearchAsync(port, new HierarchySearchRequestDto(query, 20, cwdId));
            if (response?.Ok != true)
            {
                commandLog.Add($"[!] {FormatHierarchyCommandFailure(response?.Message, "hierarchy fuzzy search failed")}");
                return true;
            }

            if (response.Results.Count == 0)
            {
                commandLog.Add($"[x] no fuzzy results for: {query}");
                return true;
            }

            commandLog.Add($"[*] fuzzy results for: {query}");
            for (var i = 0; i < response.Results.Count; i++)
            {
                var result = response.Results[i];
                var activeLabel = result.Active ? string.Empty : " (inactive)";
                commandLog.Add($"[{i}] {result.Path}{activeLabel}");
            }

            return true;
        }

        return false;
    }

    private async Task<(bool Handled, bool Transitioned)> TryHandleInspectTransitionCommandAsync(
        string input,
        IReadOnlyDictionary<int, int> indexMap,
        int cwdId,
        HierarchySnapshotDto snapshot,
        List<string> commandLog,
        Func<string, Task<bool>>? transitionToInspectorAsync)
    {
        var tokens = Tokenize(input);
        if (tokens.Count == 0)
        {
            return (false, false);
        }

        var command = tokens[0].ToLowerInvariant();
        if (command is not ("inspect" or "ins"))
        {
            return (false, false);
        }

        if (transitionToInspectorAsync is null)
        {
            commandLog.Add("[!] inspect transition is unavailable in this context");
            return (true, false);
        }

        if (!TryResolveInspectTargetPath(tokens, indexMap, cwdId, snapshot, out var targetPath, out var error))
        {
            commandLog.Add($"[!] {error}");
            return (true, false);
        }

        var transitioned = await transitionToInspectorAsync(targetPath);
        if (transitioned)
        {
            commandLog.Add($"[*] switched to inspector: {targetPath}");
            return (true, true);
        }

        commandLog.Add("[!] failed to switch to inspector");
        return (true, false);
    }

    private bool TryHandleScrollCommand(IReadOnlyList<string> tokens, List<string> commandLog)
    {
        if (tokens.Count < 2 || !tokens[0].Equals("scroll", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var section = "tree";
        var directionIndex = 1;
        if (tokens[1].Equals("tree", StringComparison.OrdinalIgnoreCase)
            || tokens[1].Equals("log", StringComparison.OrdinalIgnoreCase))
        {
            section = tokens[1].ToLowerInvariant();
            directionIndex = 2;
        }

        if (tokens.Count <= directionIndex)
        {
            commandLog.Add("[!] usage: scroll [tree|log] <up|down> [count]");
            return true;
        }

        var direction = tokens[directionIndex].ToLowerInvariant();
        if (direction is not ("up" or "down"))
        {
            commandLog.Add("[!] direction must be up or down");
            return true;
        }

        var amount = 1;
        if (tokens.Count > directionIndex + 1 && (!int.TryParse(tokens[directionIndex + 1], out amount) || amount <= 0))
        {
            commandLog.Add("[!] count must be a positive integer");
            return true;
        }

        var delta = direction == "up" ? -amount : amount;
        if (section == "log")
        {
            var wrappedStreamRowCount = BuildWrappedStreamRows(commandLog, Math.Max(1, ResolveFrameWidth() - 1)).Count;
            if (_followCommandScroll)
            {
                _commandScrollOffset = Math.Max(0, wrappedStreamRowCount - 1);
            }

            _followCommandScroll = false;
            _commandScrollOffset += delta;
            if (_commandScrollOffset >= wrappedStreamRowCount)
            {
                _followCommandScroll = true;
                _commandScrollOffset = int.MaxValue;
            }

            commandLog.Add($"[*] log scrolled {direction} by {amount}");
            return true;
        }

        _treeScrollOffset += delta;
        commandLog.Add($"[*] tree scrolled {direction} by {amount}");
        return true;
    }

    private async Task<string?> RunKeyboardFocusModeAsync(
        int port,
        HierarchySnapshotDto snapshot,
        int cwdId,
        List<string> commandLog,
        Action<HierarchySnapshotDto> setSnapshot)
    {
        var collapsedNodeIds = _collapsedNodeIds;
        var selectedEntryPosition = 0;
        int? highlightedNodeId = null;
        int? pendingExpandedNodeId = null;
        var typedIndexBuffer = string.Empty;
        long typedIndexLastInputTick = 0;

        while (true)
        {
            var treeLines = BuildTreeLines(
                snapshot,
                cwdId,
                out _,
                out var cwdPath,
                out var visibleEntries,
                collapsedNodeIds);

            if (visibleEntries.Count == 0)
            {
                commandLog.Add("[i] hierarchy has no children to focus");
                return null;
            }

            if (pendingExpandedNodeId is int expandedNodeId)
            {
                highlightedNodeId = ResolveExpandedSelectionNodeId(visibleEntries, expandedNodeId);
                pendingExpandedNodeId = null;
            }

            if (highlightedNodeId is int selectedNodeId)
            {
                var highlightedPosition = visibleEntries.FindIndex(entry => entry.NodeId == selectedNodeId);
                if (highlightedPosition >= 0)
                {
                    selectedEntryPosition = highlightedPosition;
                }
            }

            selectedEntryPosition = Math.Clamp(selectedEntryPosition, 0, visibleEntries.Count - 1);
            var selectedEntry = visibleEntries[selectedEntryPosition];
            highlightedNodeId = selectedEntry.NodeId;
            var highlightedTree = treeLines
                .Select(line =>
                {
                    if (line.EntryIndex == selectedEntry.EntryIndex)
                    {
                        return $"> {line.Text}";
                    }

                    return $"  {line.Text}";
                })
                .ToList();

            var wrappedStreamLineCount = BuildWrappedStreamRows(commandLog, Math.Max(1, ResolveFrameWidth() - 1)).Count;
            EnsureSelectedTreeVisibility(highlightedTree.Count, wrappedStreamLineCount, selectedEntryPosition + 1);
            RenderFrame(port, snapshot.Scene, highlightedTree, commandLog, cwdPath, focusModeEnabled: true);

            var intent = KeyboardIntentReader.ReadIntent();
            if (SelectionIndexJumpHelper.TryApply(
                    intent,
                    index =>
                    {
                        var targetPosition = visibleEntries.FindIndex(entry => entry.EntryIndex == index);
                        if (targetPosition < 0)
                        {
                            return false;
                        }

                        selectedEntryPosition = targetPosition;
                        highlightedNodeId = visibleEntries[targetPosition].NodeId;
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
                    selectedEntryPosition = selectedEntryPosition <= 0
                        ? visibleEntries.Count - 1
                        : selectedEntryPosition - 1;
                    highlightedNodeId = visibleEntries[selectedEntryPosition].NodeId;
                    break;
                case KeyboardIntent.Down:
                    selectedEntryPosition = selectedEntryPosition >= visibleEntries.Count - 1
                        ? 0
                        : selectedEntryPosition + 1;
                    highlightedNodeId = visibleEntries[selectedEntryPosition].NodeId;
                    break;
                case KeyboardIntent.Tab:
                    selectedEntry = visibleEntries[selectedEntryPosition];
                    if (!selectedEntry.HasChildren)
                    {
                        commandLog.Add($"[i] {selectedEntry.EntryIndex} has no children");
                        break;
                    }

                    if (collapsedNodeIds.Remove(selectedEntry.NodeId))
                    {
                        pendingExpandedNodeId = selectedEntry.NodeId;
                        commandLog.Add($"[*] expanded [{selectedEntry.EntryIndex}]");
                        break;
                    }

                    commandLog.Add($"[i] already expanded [{selectedEntry.EntryIndex}]");
                    break;
                case KeyboardIntent.Enter:
                    selectedEntry = visibleEntries[selectedEntryPosition];
                    var inspectTargetPath = BuildPath(snapshot.Root, selectedEntry.NodeId);
                    return inspectTargetPath;

                case KeyboardIntent.ShiftTab:
                    selectedEntry = visibleEntries[selectedEntryPosition];
                    if (!selectedEntry.HasChildren)
                    {
                        commandLog.Add($"[i] {selectedEntry.EntryIndex} has no children");
                        break;
                    }

                    if (collapsedNodeIds.Add(selectedEntry.NodeId))
                    {
                        commandLog.Add($"[*] collapsed [{selectedEntry.EntryIndex}]");
                        break;
                    }

                    commandLog.Add($"[i] already collapsed [{selectedEntry.EntryIndex}]");
                    break;
                case KeyboardIntent.Escape:
                case KeyboardIntent.FocusProject:
                    commandLog.Add("[i] hierarchy focus mode disabled");
                    return null;
                default:
                    break;
            }

            TrimCommandLog(commandLog);
            var refreshed = await _daemonClient.GetSnapshotAsync(port);
            if (refreshed is not null)
            {
                snapshot = refreshed;
                if (!ContainsNode(snapshot.Root, cwdId))
                {
                    cwdId = snapshot.Root.Id;
                }

                setSnapshot(snapshot);
                PruneCollapsedNodeSet(snapshot.Root, collapsedNodeIds);
            }
        }
    }

    private void RenderFrame(
        int daemonPort,
        string scene,
        List<string> treeLines,
        List<string> commandLog,
        string cwdPath,
        bool focusModeEnabled = false)
    {
        Console.Clear();

        var frameWidth = ResolveFrameWidth();
        var borderTop = $"┌{new string('─', frameWidth)}┐";
        var borderMid = $"├{new string('─', frameWidth)}┤";
        var borderBottom = $"└{new string('─', frameWidth)}┘";
        var availableRows = Math.Max(MinTreeRows + MinCommandRows, Console.WindowHeight - ReservedPromptRows);
        var dynamicRows = Math.Max(MinTreeRows + MinCommandRows, availableRows - FrameOverheadRows);
        var streamRows = BuildWrappedStreamRows(commandLog, Math.Max(1, frameWidth - 1));
        var hasStreamPane = streamRows.Count > 0;
        var (treeRows, commandRows) = hasStreamPane
            ? AllocateViewportRows(dynamicRows, treeLines.Count, streamRows.Count)
            : (Math.Max(1, dynamicRows), 0);
        var visibleTree = SliceRows(treeLines, treeRows, ref _treeScrollOffset, followTail: false);
        var visibleCommandLog = hasStreamPane
            ? SliceRows(streamRows, commandRows, ref _commandScrollOffset, _followCommandScroll)
            : [];

        var focusLabel = focusModeEnabled
            ? " | FOCUS: ON (up/down, idx jump, tab expand, enter inspect, shift+tab collapse, esc)"
            : $" | Focus Key: {FocusModeKey}";

        WriteFrameLine(borderTop);
        WriteFrameLine(ToFrameLine($" UnityCLI v{CliVersion.SemVer} | MODE: HIERARCHY | Daemon: 127.0.0.1:{daemonPort} | Scene: {scene}{focusLabel}", frameWidth));
        WriteFrameLine(borderMid);

        foreach (var line in visibleTree)
        {
            var selected = focusModeEnabled && line.StartsWith("> ", StringComparison.Ordinal);
            WriteFrameLine(ToFrameLine($" {line}", frameWidth), selected);
        }

        if (hasStreamPane)
        {
            WriteFrameLine(borderMid);
            for (var i = 0; i < visibleCommandLog.Count; i++)
            {
                var line = visibleCommandLog[i];
                WriteFrameLine(ToFrameLine($" {line}", frameWidth, allowMarkup: true));
            }
        }

        WriteFrameLine(borderBottom);
    }

    private static List<HierarchyTreeLine> BuildTreeLines(
        HierarchySnapshotDto snapshot,
        int cwdId,
        out Dictionary<int, int> indexMap,
        out string cwdPath,
        out List<FocusTreeEntry> visibleEntries,
        HashSet<int>? collapsedNodeIds = null)
    {
        indexMap = new Dictionary<int, int>();
        visibleEntries = new List<FocusTreeEntry>();
        var cwdNode = FindNode(snapshot.Root, cwdId) ?? snapshot.Root;
        cwdPath = BuildPath(snapshot.Root, cwdNode.Id);

        var lines = new List<HierarchyTreeLine> { new HierarchyTreeLine(null, null, false, cwdPath) };
        var index = 0;

        for (var i = 0; i < cwdNode.Children.Count; i++)
        {
            var child = cwdNode.Children[i];
            var isLast = i == cwdNode.Children.Count - 1;
            Traverse(child, string.Empty, isLast, depth: 0, lines, indexMap, visibleEntries, ref index, collapsedNodeIds);
        }

        return lines;
    }

    private static void Traverse(
        HierarchyNodeDto node,
        string prefix,
        bool isLast,
        int depth,
        List<HierarchyTreeLine> lines,
        Dictionary<int, int> indexMap,
        List<FocusTreeEntry> visibleEntries,
        ref int index,
        HashSet<int>? collapsedNodeIds)
    {
        var branch = isLast ? "└──" : "├──";
        var label = node.Active ? node.Name : $"({node.Name})";
        var hasChildren = node.Children.Count > 0;
        var collapsedMarker = hasChildren && collapsedNodeIds?.Contains(node.Id) == true ? " [+]" : string.Empty;
        lines.Add(new HierarchyTreeLine(index, node.Id, hasChildren, $"{prefix}{branch} [{index}] {label}{collapsedMarker}"));
        indexMap[index] = node.Id;
        visibleEntries.Add(new FocusTreeEntry(index, node.Id, depth, hasChildren));
        index++;

        if (hasChildren && collapsedNodeIds?.Contains(node.Id) == true)
        {
            return;
        }

        var nextPrefix = prefix + (isLast ? "    " : "│   ");
        for (var i = 0; i < node.Children.Count; i++)
        {
            var child = node.Children[i];
            var childLast = i == node.Children.Count - 1;
            Traverse(child, nextPrefix, childLast, depth + 1, lines, indexMap, visibleEntries, ref index, collapsedNodeIds);
        }
    }

    private static int ResolveExpandedSelectionNodeId(List<FocusTreeEntry> visibleEntries, int expandedNodeId)
    {
        var expandedPosition = visibleEntries.FindIndex(entry => entry.NodeId == expandedNodeId);
        if (expandedPosition < 0)
        {
            return expandedNodeId;
        }

        var expandedEntry = visibleEntries[expandedPosition];
        var firstChildPosition = expandedPosition + 1;
        if (firstChildPosition < visibleEntries.Count)
        {
            var firstChild = visibleEntries[firstChildPosition];
            if (firstChild.Depth == expandedEntry.Depth + 1)
            {
                return firstChild.NodeId;
            }
        }

        return expandedNodeId;
    }

    private static void PruneCollapsedNodeSet(HierarchyNodeDto root, HashSet<int> collapsedNodeIds)
    {
        if (collapsedNodeIds.Count == 0)
        {
            return;
        }

        var toRemove = collapsedNodeIds.Where(id => !ContainsNode(root, id)).ToList();
        foreach (var id in toRemove)
        {
            collapsedNodeIds.Remove(id);
        }
    }

    private void EnsureSelectedTreeVisibility(int treeLineCount, int commandLogCount, int selectedTreeRow)
    {
        var streamRows = commandLogCount;
        var hasStreamPane = streamRows > 0;
        var availableRows = Math.Max(MinTreeRows + MinCommandRows, Console.WindowHeight - ReservedPromptRows);
        var dynamicRows = Math.Max(MinTreeRows + MinCommandRows, availableRows - FrameOverheadRows);
        var treeRows = hasStreamPane
            ? AllocateViewportRows(dynamicRows, treeLineCount, streamRows).TreeRows
            : Math.Max(1, dynamicRows);

        if (selectedTreeRow < _treeScrollOffset)
        {
            _treeScrollOffset = selectedTreeRow;
            return;
        }

        var viewportBottom = _treeScrollOffset + treeRows - 1;
        if (selectedTreeRow > viewportBottom)
        {
            _treeScrollOffset = selectedTreeRow - treeRows + 1;
        }
    }

    private static string BuildPath(HierarchyNodeDto root, int targetId)
    {
        var segments = new List<string>();
        if (!TryCollectPath(root, targetId, segments))
        {
            return $"/{root.Name}";
        }

        segments.Reverse();
        return "/" + string.Join('/', segments);
    }

    private static bool TryCollectPath(HierarchyNodeDto node, int targetId, List<string> segments)
    {
        if (node.Id == targetId)
        {
            segments.Add(node.Name);
            return true;
        }

        foreach (var child in node.Children)
        {
            if (TryCollectPath(child, targetId, segments))
            {
                segments.Add(node.Name);
                return true;
            }
        }

        return false;
    }

    private static HierarchyNodeDto? FindNode(HierarchyNodeDto node, int id)
    {
        if (node.Id == id)
        {
            return node;
        }

        foreach (var child in node.Children)
        {
            var found = FindNode(child, id);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static bool ContainsNode(HierarchyNodeDto node, int id) => FindNode(node, id) is not null;

    private static bool TryResolveInspectTargetPath(
        IReadOnlyList<string> tokens,
        IReadOnlyDictionary<int, int> indexMap,
        int cwdId,
        HierarchySnapshotDto snapshot,
        out string targetPath,
        out string error)
    {
        targetPath = string.Empty;
        error = string.Empty;

        if (tokens.Count < 2)
        {
            error = "usage: inspect <idx|name>";
            return false;
        }

        var argument = string.Join(' ', tokens.Skip(1)).Trim();
        if (string.IsNullOrWhiteSpace(argument))
        {
            error = "usage: inspect <idx|name>";
            return false;
        }

        if (int.TryParse(argument, out var index))
        {
            if (!indexMap.TryGetValue(index, out var nodeId))
            {
                error = $"invalid index: {index}";
                return false;
            }

            targetPath = BuildPath(snapshot.Root, nodeId);
            return true;
        }

        var cwdPath = BuildPath(snapshot.Root, cwdId);
        var byName = indexMap
            .Values
            .Distinct()
            .Select(nodeId => FindNode(snapshot.Root, nodeId))
            .OfType<HierarchyNodeDto>()
            .Where(node => node.Name.Equals(argument, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (byName.Count == 1)
        {
            targetPath = BuildPath(snapshot.Root, byName[0].Id);
            return true;
        }

        if (byName.Count > 1)
        {
            error = $"ambiguous name: {argument} (use inspect <idx>)";
            return false;
        }

        var normalizedAbsolute = argument.StartsWith("/", StringComparison.Ordinal)
            ? argument
            : $"{cwdPath.TrimEnd('/')}/{argument.TrimStart('/')}";
        var byPath = indexMap
            .Values
            .Distinct()
            .Select(nodeId => BuildPath(snapshot.Root, nodeId))
            .FirstOrDefault(path => path.Equals(normalizedAbsolute, StringComparison.OrdinalIgnoreCase));
        if (byPath is not null)
        {
            targetPath = byPath;
            return true;
        }

        error = $"unable to resolve hierarchy target: {argument}";
        return false;
    }

    private static int? FindParentId(HierarchyNodeDto root, int id)
    {
        foreach (var child in root.Children)
        {
            if (child.Id == id)
            {
                return root.Id;
            }

            var nested = FindParentId(child, id);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static string ReadPromptInputWithIntellisenseFromFirstKey(
        string cwdPath,
        ConsoleKeyInfo firstKey,
        HierarchySnapshotDto snapshot,
        int cwdId)
    {
        var input = new StringBuilder();
        if (firstKey.Key == ConsoleKey.Enter)
        {
            Console.WriteLine();
            return string.Empty;
        }

        if (firstKey.Key == ConsoleKey.Escape)
        {
            Console.WriteLine();
            return string.Empty;
        }

        if (!TryAppendInputCharacter(firstKey, input))
        {
            Console.WriteLine();
            return string.Empty;
        }

        var selectedIndex = 0;
        var dismissed = false;
        var renderedLines = RenderPromptIntellisense(cwdPath, input.ToString(), selectedIndex, dismissed, snapshot, cwdId);

        while (true)
        {
            var allCandidates = GetHierarchyIntellisenseCandidates(input.ToString(), snapshot, cwdId);
            var candidates = dismissed ? [] : allCandidates;
            if (candidates.Count == 0)
            {
                selectedIndex = 0;
            }
            else
            {
                selectedIndex = Math.Clamp(selectedIndex, 0, candidates.Count - 1);
            }

            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                if (IsHierarchyCatalogCommandInput(input.ToString()))
                {
                    ClearPromptFrame(renderedLines);
                    Console.WriteLine($"UnityCLI:{cwdPath} > {input}");
                    return input.ToString().Trim();
                }

                if (candidates.Count > 0
                    && selectedIndex >= 0
                    && selectedIndex < candidates.Count
                    && !string.IsNullOrWhiteSpace(candidates[selectedIndex].CommitCommand))
                {
                    var currentInput = input.ToString();
                    input.Clear();
                    input.Append(MergeAcceptedSuggestion(currentInput, candidates[selectedIndex].CommitCommand));
                    // Accepting a suggestion exits IntelliSense so next Enter executes.
                    dismissed = true;
                }
                else
                {
                    ClearPromptFrame(renderedLines);
                    Console.WriteLine($"UnityCLI:{cwdPath} > {input}");
                    return input.ToString().Trim();
                }
            }
            else if (key.Key == ConsoleKey.Backspace)
            {
                if (input.Length > 0)
                {
                    input.Length--;
                }

                dismissed = false;
            }
            else if (key.Key == ConsoleKey.Escape)
            {
                if (!dismissed && allCandidates.Count > 0)
                {
                    dismissed = true;
                }
                else
                {
                    input.Clear();
                    dismissed = false;
                }

                selectedIndex = 0;
            }
            else if (key.Key == ConsoleKey.UpArrow)
            {
                if (dismissed && allCandidates.Count > 0)
                {
                    dismissed = false;
                    candidates = allCandidates;
                }

                if (candidates.Count > 0)
                {
                    selectedIndex = selectedIndex <= 0 ? candidates.Count - 1 : selectedIndex - 1;
                }
            }
            else if (key.Key == ConsoleKey.DownArrow)
            {
                if (dismissed && allCandidates.Count > 0)
                {
                    dismissed = false;
                    candidates = allCandidates;
                }

                if (candidates.Count > 0)
                {
                    selectedIndex = selectedIndex >= candidates.Count - 1 ? 0 : selectedIndex + 1;
                }
            }
            else if (TryAppendInputCharacter(key, input))
            {
                dismissed = false;
            }

            ClearPromptFrame(renderedLines);
            renderedLines = RenderPromptIntellisense(cwdPath, input.ToString(), selectedIndex, dismissed, snapshot, cwdId);
        }
    }

    private static bool TryAppendInputCharacter(ConsoleKeyInfo key, StringBuilder input)
    {
        if (char.IsControl(key.KeyChar))
        {
            return false;
        }

        input.Append(key.KeyChar);
        return true;
    }

    private static int RenderPromptIntellisense(
        string cwdPath,
        string input,
        int selectedSuggestionIndex,
        bool suppressIntellisense,
        HierarchySnapshotDto snapshot,
        int cwdId)
    {
        var lines = new List<string>
        {
            $"UnityCLI:{Markup.Escape(cwdPath)} > [bold white]{Markup.Escape(input)}[/]"
        };

        if (suppressIntellisense)
        {
            lines.Add("[dim]intellisense dismissed (Esc). Type or use ↑/↓ to reopen suggestions.[/]");
        }
        else
        {
            var candidates = GetHierarchyIntellisenseCandidates(input, snapshot, cwdId);
            var isFuzzyPreview = TryParseFuzzyQuery(input, out _);
            lines.Add(isFuzzyPreview
                ? "[grey]fuzzy[/]: hierarchy path suggestions [dim](up/down to browse, Enter to execute current input)[/]"
                : "[grey]intellisense[/]: hierarchy commands [dim](up/down + enter to insert)[/]");
            if (candidates.Count == 0)
            {
                lines.Add(isFuzzyPreview
                    ? "[dim]no fuzzy matches[/]"
                    : (string.IsNullOrWhiteSpace(input)
                        ? "[dim]no commands available[/]"
                        : $"[dim]no matches for {Markup.Escape(input)}[/]"));
            }
            else
            {
                var selected = Math.Clamp(selectedSuggestionIndex, 0, candidates.Count - 1);
                const int maxVisible = 12;
                var visibleCount = Math.Min(maxVisible, candidates.Count);
                var windowStart = Math.Clamp(selected - (visibleCount / 2), 0, Math.Max(0, candidates.Count - visibleCount));
                var windowEnd = windowStart + visibleCount;
                for (var i = windowStart; i < windowEnd; i++)
                {
                    var candidate = candidates[i];
                    var isSelected = i == selected;
                    var prefix = isSelected
                        ? $"[{CliTheme.CursorForeground} on {CliTheme.CursorBackground}]>[/]"
                        : "[grey] [/]";
                    var label = isFuzzyPreview
                        ? FormatHierarchyFuzzyCandidateLabel(candidate.Label, isSelected)
                        : Markup.Escape(candidate.Label);
                    lines.Add(isSelected
                        ? (isFuzzyPreview
                            ? $"{prefix} {label}"
                            : $"{prefix} [{CliTheme.CursorForeground} on {CliTheme.CursorBackground}]{label}[/]")
                        : (isFuzzyPreview
                            ? $"{prefix} {label}"
                            : $"{prefix} [grey]{label}[/]"));
                }

                if (candidates.Count > visibleCount)
                {
                    lines.Add($"[dim]showing {windowStart + 1}-{windowEnd}/{candidates.Count}[/]");
                }
            }
        }

        foreach (var line in lines)
        {
            CliTheme.MarkupLine(line);
        }

        return lines.Count;
    }

    private static bool IsHierarchyCatalogCommandInput(string input)
    {
        var trimmed = input.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return false;
        }

        var head = trimmed.Split(' ', '\t', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(head))
        {
            return false;
        }

        return HierarchyCommands.Any(command => command.Trigger.Equals(head, StringComparison.OrdinalIgnoreCase))
            || MkTypeCommands.Any(command => command.Trigger.Equals(head, StringComparison.OrdinalIgnoreCase));
    }

    private static string MergeAcceptedSuggestion(string currentInput, string acceptedCommand)
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

    private static int CountTokens(string input)
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

    private static string SliceAfterTokenCount(string input, int tokenCount)
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

    private static void ClearPromptFrame(int renderedLines)
    {
        for (var i = 0; i < renderedLines; i++)
        {
            Console.Write("\u001b[1A");
            Console.Write("\r\u001b[2K");
        }
    }

    private static List<CommandSpec> GetHierarchySuggestionMatches(string query)
    {
        var normalized = query.Trim().ToLowerInvariant();
        IEnumerable<CommandSpec> source = HierarchyCommands
            .Concat(MkTypeCommands)
            .Where(command => !command.Description.StartsWith("Alias for", StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(normalized))
        {
            source = source.Where(command =>
                command.Signature.Contains(normalized, StringComparison.OrdinalIgnoreCase)
                || command.Description.Contains(normalized, StringComparison.OrdinalIgnoreCase)
                || command.Trigger.StartsWith(normalized, StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith(command.Trigger, StringComparison.OrdinalIgnoreCase));
        }

        return source.ToList();
    }

    private static List<(string Label, string CommitCommand)> GetHierarchyIntellisenseCandidates(string query, HierarchySnapshotDto snapshot, int cwdId)
    {
        if (TryParseFuzzyQuery(query, out var fuzzyQuery))
        {
            return GetHierarchyFuzzyPreviewCandidates(snapshot, cwdId, fuzzyQuery);
        }

        return GetHierarchySuggestionMatches(query)
            .Select(match => (match.Signature, string.IsNullOrWhiteSpace(match.Trigger) ? match.Signature : match.Trigger))
            .ToList();
    }

    private static bool TryParseFuzzyQuery(string input, out string query)
    {
        query = string.Empty;
        var trimmed = input.TrimStart();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return false;
        }

        if (trimmed.Equals("f", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("ff", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (trimmed.StartsWith("f ", StringComparison.OrdinalIgnoreCase))
        {
            query = trimmed[2..].Trim();
            return true;
        }

        if (trimmed.StartsWith("ff ", StringComparison.OrdinalIgnoreCase))
        {
            query = trimmed[3..].Trim();
            return true;
        }

        return false;
    }

    private static List<(string Label, string CommitCommand)> GetHierarchyFuzzyPreviewCandidates(
        HierarchySnapshotDto snapshot,
        int cwdId,
        string query)
    {
        var cwdNode = FindNode(snapshot.Root, cwdId) ?? snapshot.Root;
        var seedPath = BuildPath(snapshot.Root, cwdNode.Id);
        var paths = new List<string>();
        CollectHierarchyPaths(cwdNode, seedPath, paths);

        IEnumerable<(string Path, double Score)> ranked;
        if (string.IsNullOrWhiteSpace(query))
        {
            ranked = paths.Select(path => (path, 1d));
        }
        else
        {
            var scored = new List<(string Path, double Score)>();
            foreach (var path in paths)
            {
                if (path.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    scored.Add((path, 0.9d));
                    continue;
                }

                if (FuzzyMatcher.TryScore(query, path, out var score))
                {
                    scored.Add((path, score));
                }
            }

            ranked = scored;
        }

        return ranked
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .Take(30)
            .Select(entry => (entry.Path, $"f {entry.Path}"))
            .ToList();
    }

    private static void CollectHierarchyPaths(HierarchyNodeDto node, string currentPath, List<string> output)
    {
        foreach (var child in node.Children)
        {
            var childPath = $"{currentPath}/{child.Name}";
            output.Add(childPath);
            CollectHierarchyPaths(child, childPath, output);
        }
    }

    private static string FormatHierarchyFuzzyCandidateLabel(string path, bool selectedLine)
    {
        var normalizedPath = path.Replace('\\', '/');
        var separatorIndex = normalizedPath.LastIndexOf('/');
        if (separatorIndex < 0 || separatorIndex >= normalizedPath.Length - 1)
        {
            var escaped = Markup.Escape(path);
            return selectedLine
                ? $"[bold white]{escaped}[/]"
                : $"[white]{escaped}[/]";
        }

        var context = Markup.Escape(normalizedPath[..(separatorIndex + 1)]);
        var leaf = Markup.Escape(normalizedPath[(separatorIndex + 1)..]);
        return selectedLine
            ? $"[grey58]{context}[/][bold white]{leaf}[/]"
            : $"[grey58]{context}[/][bold deepskyblue1]{leaf}[/]";
    }

    private static bool TryParseMakeArguments(
        IReadOnlyList<string> tokens,
        out string type,
        out int count,
        out string error)
    {
        type = string.Empty;
        count = 1;
        error = "usage: make --type <type> [--count <count>]";
        if (tokens.Count < 3)
        {
            return false;
        }

        for (var i = 1; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Equals("--type", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= tokens.Count)
                {
                    error = "usage: make --type <type> [--count <count>]";
                    return false;
                }

                type = tokens[++i];
                continue;
            }

            if (token.StartsWith("--type=", StringComparison.OrdinalIgnoreCase))
            {
                type = token["--type=".Length..];
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

            error = $"unsupported option: {token}";
            return false;
        }

        if (string.IsNullOrWhiteSpace(type))
        {
            error = "missing --type <type>";
            return false;
        }

        return true;
    }

    private static bool TryParseMkArguments(
        IReadOnlyList<string> tokens,
        out string type,
        out int count,
        out string? name,
        out string error)
    {
        type = string.Empty;
        count = 1;
        name = null;
        error = "usage: mk <type> [count] [--name <name>|-n <name>]";
        if (tokens.Count < 2)
        {
            return false;
        }

        type = tokens[1];
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
                    error = "usage: mk <type> [count] [--name <name>|-n <name>]";
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

    private static Dictionary<string, HierarchyMkType> BuildMkTypeLookup()
    {
        var lookup = new Dictionary<string, HierarchyMkType>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in Enum.GetValues<HierarchyMkType>())
        {
            if (value == HierarchyMkType.Unspecified)
            {
                continue;
            }

            var key = NormalizeMkTypeKey(value.ToString());
            if (!lookup.ContainsKey(key))
            {
                lookup[key] = value;
            }
        }

        lookup[NormalizeMkTypeKey("ScrollView")] = HierarchyMkType.ScrollView;
        lookup[NormalizeMkTypeKey("EventSystem")] = HierarchyMkType.EventSystem;
        lookup[NormalizeMkTypeKey("DirLight")] = HierarchyMkType.DirLight;
        lookup[NormalizeMkTypeKey("DirectionalLight")] = HierarchyMkType.DirectionalLight;
        lookup[NormalizeMkTypeKey("PointLight")] = HierarchyMkType.PointLight;
        lookup[NormalizeMkTypeKey("SpotLight")] = HierarchyMkType.SpotLight;
        lookup[NormalizeMkTypeKey("AreaLight")] = HierarchyMkType.AreaLight;
        lookup[NormalizeMkTypeKey("ReflectionProbe")] = HierarchyMkType.ReflectionProbe;
        lookup[NormalizeMkTypeKey("SpriteMask")] = HierarchyMkType.SpriteMask;
        lookup[NormalizeMkTypeKey("AudioSource")] = HierarchyMkType.AudioSource;
        lookup[NormalizeMkTypeKey("EmptyParent")] = HierarchyMkType.EmptyParent;
        lookup[NormalizeMkTypeKey("EmptyChild")] = HierarchyMkType.EmptyChild;
        lookup[NormalizeMkTypeKey("TMP")] = HierarchyMkType.Tmp;
        return lookup;
    }

    private static bool TryNormalizeMkType(string raw, out string canonical, out string error)
    {
        canonical = string.Empty;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "mk type is required";
            return false;
        }

        var key = NormalizeMkTypeKey(raw);
        if (!MkTypeLookup.TryGetValue(key, out var mkType))
        {
            var known = string.Join(", ", MkTypeLookup.Values.Distinct().OrderBy(v => v.ToString()).Select(v => v.ToString()));
            error = $"unsupported mk type: {raw}. supported types: {known}";
            return false;
        }

        canonical = mkType.ToString();
        return true;
    }

    private static string NormalizeMkTypeKey(string raw)
    {
        return raw.Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
    }

    private static string FormatHierarchyCommandFailure(string? message, string fallback = "hierarchy command failed")
    {
        var normalized = string.IsNullOrWhiteSpace(message) ? fallback : message!;
        if (IsHierarchyStubbedMessage(normalized))
        {
            return $"hierarchy command is stubbed without Bridge mode: {normalized}";
        }

        return normalized;
    }

    private static bool IsHierarchyStubbedMessage(string message)
    {
        return message.Contains("require Bridge mode", StringComparison.OrdinalIgnoreCase)
               || message.Contains("stubbed bridge", StringComparison.OrdinalIgnoreCase)
               || message.Contains("stubbed", StringComparison.OrdinalIgnoreCase);
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

    private static void TrimCommandLog(List<string> commandLog)
    {
        const int maxLines = 120;
        if (commandLog.Count <= maxLines)
        {
            return;
        }

        commandLog.RemoveRange(0, commandLog.Count - maxLines);
    }

    private static string ToFrameLine(string raw, int frameWidth, bool allowMarkup = false)
    {
        var sanitized = raw.Replace('\t', ' ');
        var content = allowMarkup
            ? FitMarkup(sanitized, frameWidth)
            : Markup.Escape(FitPlain(sanitized, frameWidth));
        return $"│{content}│";
    }

    private static void WriteFrameLine(string line, bool highlight = false)
    {
        CliTheme.MarkupLine(highlight ? CliTheme.CursorWrapEscaped(line) : line);
    }

    private static (int TreeRows, int CommandRows) AllocateViewportRows(int dynamicRows, int treeCount, int commandCount)
    {
        var preferredTree = Math.Max(MinTreeRows, treeCount);
        var preferredCommand = Math.Max(MinCommandRows, commandCount);
        var preferredTotal = preferredTree + preferredCommand;
        if (preferredTotal <= dynamicRows)
        {
            return (preferredTree, preferredCommand);
        }

        var commandRows = Math.Min(preferredCommand, Math.Max(MinCommandRows, dynamicRows / 3));
        var treeRows = dynamicRows - commandRows;
        if (treeRows < MinTreeRows)
        {
            treeRows = MinTreeRows;
            commandRows = Math.Max(MinCommandRows, dynamicRows - treeRows);
        }

        return (Math.Max(1, treeRows), Math.Max(1, commandRows));
    }

    private static List<string> SliceRows(IReadOnlyList<string> source, int viewportRows, ref int offset, bool followTail)
    {
        viewportRows = Math.Max(1, viewportRows);
        var rows = source.Count == 0 ? new List<string> { string.Empty } : source.ToList();
        var maxOffset = Math.Max(0, rows.Count - viewportRows);
        if (followTail)
        {
            offset = maxOffset;
        }

        offset = Math.Clamp(offset, 0, maxOffset);
        var visible = rows.Skip(offset).Take(viewportRows).ToList();
        while (visible.Count < viewportRows)
        {
            visible.Add(string.Empty);
        }

        var hiddenAbove = offset;
        var hiddenBelow = Math.Max(0, rows.Count - (offset + visible.Count));
        if (hiddenAbove > 0 && visible.Count > 0)
        {
            visible[0] = $"... {hiddenAbove} line(s) above ...";
        }

        if (hiddenBelow > 0 && visible.Count > 1)
        {
            visible[^1] = $"... {hiddenBelow} line(s) below ...";
        }

        return visible;
    }

    private static List<string> BuildWrappedStreamRows(IReadOnlyList<string> commandLog, int contentWidth)
    {
        _ = contentWidth;
        return commandLog
            .Where(line => !line.StartsWith("UnityCLI:", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static string FitPlain(string text, int width)
    {
        var content = text.Length > width ? text[..width] : text;
        return content.PadRight(width, ' ');
    }

    private static string FitMarkup(string markup, int width)
    {
        try
        {
            var plain = Markup.Remove(markup);
            if (plain.Length > width)
            {
                return Markup.Escape(plain[..Math.Max(0, width - 1)] + "…");
            }

            if (plain.Length < width)
            {
                return markup + new string(' ', width - plain.Length);
            }

            return markup;
        }
        catch
        {
            return Markup.Escape(FitPlain(markup, width));
        }
    }

    private static int ResolveFrameWidth()
    {
        var windowWidth = Console.WindowWidth;
        if (windowWidth <= 2)
        {
            return DefaultFrameWidth;
        }

        return Math.Max(MinFrameWidth, windowWidth - 2);
    }
}
