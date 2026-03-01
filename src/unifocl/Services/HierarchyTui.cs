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
    private const ConsoleKey FocusModeKey = ConsoleKey.F6;

    private readonly HierarchyDaemonClient _daemonClient = new();
    private int _treeScrollOffset;
    private int _commandScrollOffset = int.MaxValue;
    private bool _followCommandScroll = true;

    private sealed record HierarchyTreeLine(int? EntryIndex, int? NodeId, bool HasChildren, string Text);
    private sealed record FocusTreeEntry(int EntryIndex, int NodeId, bool HasChildren);

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

            Console.Write($"UnityCLI:{cwdPath} > ");
            var firstKey = Console.ReadKey(intercept: true);
            var firstIntent = KeyboardIntentReader.FromConsoleKey(firstKey);
            if (firstIntent == KeyboardIntent.FocusHierarchy || firstKey.Key == FocusModeKey)
            {
                commandLog.Add("[i] hierarchy focus mode enabled (up/down select, tab expand, shift+tab collapse, esc exit)");
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

            string input;
            if (firstKey.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                input = string.Empty;
            }
            else
            {
                var firstChar = firstKey.KeyChar;
                if (!char.IsControl(firstChar))
                {
                    Console.Write(firstChar);
                    var tail = Console.ReadLine() ?? string.Empty;
                    input = (firstChar + tail).Trim();
                }
                else
                {
                    Console.WriteLine();
                    input = string.Empty;
                }
            }

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
                commandLog.Add("[!] unknown command (supported: list, enter, up, make, toggle, f, scroll, quit)");
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
                commandLog.Add($"[!] {response.Message}");
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
                commandLog.Add($"[!] {response.Message}");
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
                commandLog.Add($"[!] {(response?.Message ?? "hierarchy fuzzy search failed")}");
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

            selectedEntryPosition = Math.Clamp(selectedEntryPosition, 0, visibleEntries.Count - 1);
            var selectedEntry = visibleEntries[selectedEntryPosition];
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
                    break;
                case KeyboardIntent.Down:
                    selectedEntryPosition = selectedEntryPosition >= visibleEntries.Count - 1
                        ? 0
                        : selectedEntryPosition + 1;
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
                case KeyboardIntent.FocusHierarchy:
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
        WriteFrameLine(ToFrameLine($" UnityCLI v0.1 | MODE: HIERARCHY | Daemon: 127.0.0.1:{daemonPort} | Scene: {scene}{focusLabel}", frameWidth));
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
                WriteFrameLine(ToFrameLine($" {line}", frameWidth));
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
            Traverse(child, string.Empty, isLast, lines, indexMap, visibleEntries, ref index, collapsedNodeIds);
        }

        return lines;
    }

    private static void Traverse(
        HierarchyNodeDto node,
        string prefix,
        bool isLast,
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
        visibleEntries.Add(new FocusTreeEntry(index, node.Id, hasChildren));
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
            Traverse(child, nextPrefix, childLast, lines, indexMap, visibleEntries, ref index, collapsedNodeIds);
        }
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

    private static string ToFrameLine(string raw, int frameWidth)
    {
        var sanitized = raw.Replace('\t', ' ');
        var content = sanitized.Length > frameWidth ? sanitized[..frameWidth] : sanitized.PadRight(frameWidth, ' ');
        return $"│{content}│";
    }

    private static void WriteFrameLine(string line, bool highlight = false)
    {
        var escaped = Markup.Escape(line);
        CliTheme.MarkupLine(highlight ? CliTheme.CursorWrapEscaped(escaped) : escaped);
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
        return commandLog
            .Where(line => !line.StartsWith("UnityCLI:", StringComparison.OrdinalIgnoreCase))
            .SelectMany(line => TuiTextWrap.WrapPlainText(line, contentWidth))
            .ToList();
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
