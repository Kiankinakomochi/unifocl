using System.Text;
using Spectre.Console;

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
        new("make <name>", "Create empty GameObject under current object", "make"),
        new("mk <name>", "Alias for make", "mk"),
        new("mk cube <name> [-p]", "Create primitive cube", "mk cube"),
        new("toggle <idx>", "Toggle active state by index", "toggle"),
        new("t <idx>", "Alias for toggle", "t"),
        new("f <query>", "Fuzzy search hierarchy paths", "f"),
        new("ff <query>", "Alias for fuzzy search", "ff"),
        new("scroll [tree|log] <up|down> [count]", "Scroll tree or command stream", "scroll"),
        new("quit|exit|q|:q", "Exit hierarchy mode", "quit")
    ];

    private readonly HierarchyDaemonClient _daemonClient = new();
    private int _treeScrollOffset;
    private int _commandScrollOffset = int.MaxValue;
    private bool _followCommandScroll = true;

    private sealed record HierarchyTreeLine(int? EntryIndex, int? NodeId, bool HasChildren, string Text);
    private sealed record FocusTreeEntry(int EntryIndex, int NodeId, int Depth, bool HasChildren);

    public async Task RunAsync(
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime,
        Action<string> log)
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

        while (true)
        {
            var treeLines = BuildTreeLines(snapshot, cwdId, out var indexMap, out var cwdPath, out _, null);
            RenderFrame(port, snapshot.Scene, treeLines.Select(line => line.Text).ToList(), commandLog, cwdPath);

            var firstKey = Console.ReadKey(intercept: true);
            var firstIntent = KeyboardIntentReader.FromConsoleKey(firstKey);
            if (firstIntent == KeyboardIntent.FocusProject
                || firstKey.Key == FocusModeKey)
            {
                commandLog.Add("[i] hierarchy focus mode enabled (up/down select, tab expand, shift+tab collapse, esc/F7 exit)");
                await RunKeyboardFocusModeAsync(port, snapshot, cwdId, commandLog, updatedSnapshot =>
                {
                    snapshot = updatedSnapshot;
                    if (!ContainsNode(snapshot.Root, cwdId))
                    {
                        cwdId = snapshot.Root.Id;
                    }
                });
                TrimCommandLog(commandLog);
                continue;
            }

            var input = ReadPromptInputWithIntellisenseFromFirstKey(cwdPath, firstKey);

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
            "make" => "mk",
            "t" => "toggle",
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

        if (tokens.Count >= 2 && tokens[0].Equals("mk", StringComparison.OrdinalIgnoreCase))
        {
            var primitiveKeyword = tokens[1].Equals("cube", StringComparison.OrdinalIgnoreCase);
            var name = primitiveKeyword && tokens.Count >= 3 ? tokens[2] : tokens[1];
            var primitive = primitiveKeyword || tokens.Skip(2).Any(t => t.Equals("-p", StringComparison.OrdinalIgnoreCase));
            var response = await _daemonClient.ExecuteAsync(
                port,
                new HierarchyCommandRequestDto("mk", cwdId, null, name, primitive));

            if (!response.Ok)
            {
                commandLog.Add($"[!] {FormatHierarchyCommandFailure(response.Message)}");
                return true;
            }

            var parentName = FindNode(snapshot.Root, cwdId)?.Name ?? "?";
            commandLog.Add($"[+] created: {name} [{(response.NodeId?.ToString() ?? "?")}] -> parent: {parentName}");
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

        if (tokens.Count >= 2 && tokens[0].Equals("f", StringComparison.OrdinalIgnoreCase))
        {
            var query = string.Join(' ', tokens.Skip(1));
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

    private async Task RunKeyboardFocusModeAsync(
        int port,
        HierarchySnapshotDto snapshot,
        int cwdId,
        List<string> commandLog,
        Action<HierarchySnapshotDto> setSnapshot)
    {
        var collapsedNodeIds = BuildDefaultCollapsedNodeSet(snapshot.Root, cwdId);
        var selectedEntryPosition = 0;
        int? highlightedNodeId = null;
        int? pendingExpandedNodeId = null;

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
                return;
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
                    return;
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
            ? " | FOCUS: ON (up/down, tab, shift+tab, esc)"
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

    private static HashSet<int> BuildDefaultCollapsedNodeSet(HierarchyNodeDto root, int cwdId)
    {
        var collapsed = new HashSet<int>();
        var cwdNode = FindNode(root, cwdId);
        if (cwdNode is null)
        {
            return collapsed;
        }

        AddCollapsedDescendantNodes(cwdNode, collapsed);
        return collapsed;
    }

    private static void AddCollapsedDescendantNodes(HierarchyNodeDto node, HashSet<int> collapsed)
    {
        foreach (var child in node.Children)
        {
            if (child.Children.Count > 0)
            {
                collapsed.Add(child.Id);
            }

            AddCollapsedDescendantNodes(child, collapsed);
        }
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

    private static string ReadPromptInputWithIntellisenseFromFirstKey(string cwdPath, ConsoleKeyInfo firstKey)
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
        var selectionArmed = false;
        var renderedLines = RenderPromptIntellisense(cwdPath, input.ToString(), selectedIndex, dismissed);

        while (true)
        {
            var allCandidates = GetHierarchyIntellisenseCandidates(input.ToString());
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
                if (selectionArmed
                    && candidates.Count > 0
                    && selectedIndex >= 0
                    && selectedIndex < candidates.Count
                    && !string.IsNullOrWhiteSpace(candidates[selectedIndex].CommitCommand))
                {
                    input.Clear();
                    input.Append(candidates[selectedIndex].CommitCommand);
                    dismissed = false;
                    selectionArmed = false;
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
                selectionArmed = false;
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
                selectionArmed = false;
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
                    selectionArmed = true;
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
                    selectionArmed = true;
                }
            }
            else if (TryAppendInputCharacter(key, input))
            {
                dismissed = false;
                selectionArmed = false;
            }

            ClearPromptFrame(renderedLines);
            renderedLines = RenderPromptIntellisense(cwdPath, input.ToString(), selectedIndex, dismissed);
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
        bool suppressIntellisense)
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
            var matches = GetHierarchySuggestionMatches(input);
            lines.Add("[grey]intellisense[/]: hierarchy commands [dim](up/down + enter to insert)[/]");
            if (matches.Count == 0)
            {
                lines.Add(string.IsNullOrWhiteSpace(input)
                    ? "[dim]no commands available[/]"
                    : $"[dim]no matches for {Markup.Escape(input)}[/]");
            }
            else
            {
                var selected = Math.Clamp(selectedSuggestionIndex, 0, matches.Count - 1);
                for (var i = 0; i < matches.Count; i++)
                {
                    var match = matches[i];
                    var isSelected = i == selected;
                    var prefix = isSelected
                        ? $"[{CliTheme.CursorForeground} on {CliTheme.CursorBackground}]>[/]"
                        : "[grey] [/]";
                    var signature = Markup.Escape(match.Signature);
                    var description = Markup.Escape(match.Description);
                    lines.Add(isSelected
                        ? $"{prefix} [{CliTheme.CursorForeground} on {CliTheme.CursorBackground}]{signature}[/] [dim]- {description}[/]"
                        : $"{prefix} [grey]{signature}[/] [dim]- {description}[/]");
                }
            }
        }

        foreach (var line in lines)
        {
            CliTheme.MarkupLine(line);
        }

        return lines.Count;
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
            .Where(command => !command.Description.StartsWith("Alias for", StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(normalized))
        {
            source = source.Where(command =>
                command.Signature.Contains(normalized, StringComparison.OrdinalIgnoreCase)
                || command.Description.Contains(normalized, StringComparison.OrdinalIgnoreCase)
                || command.Trigger.StartsWith(normalized, StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith(command.Trigger, StringComparison.OrdinalIgnoreCase));
        }

        return source.Take(12).ToList();
    }

    private static List<(string Label, string CommitCommand)> GetHierarchyIntellisenseCandidates(string query)
    {
        return GetHierarchySuggestionMatches(query)
            .Select(match => (match.Signature, match.Trigger))
            .ToList();
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
