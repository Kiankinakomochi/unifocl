using System.Text;

internal sealed class HierarchyTui
{
    private const int FrameWidth = 78;
    private const int MinTreeRows = 6;
    private const int MinCommandRows = 4;
    private const int ReservedPromptRows = 4;
    private const int FrameOverheadRows = 5;

    private readonly HierarchyDaemonClient _daemonClient = new();
    private int _treeScrollOffset;
    private int _commandScrollOffset = int.MaxValue;
    private bool _followCommandScroll = true;

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
            var treeLines = BuildTreeLines(snapshot, cwdId, out var indexMap, out var cwdPath);
            RenderFrame(port, snapshot.Scene, treeLines, commandLog, cwdPath);

            Console.Write($"UnityCLI:{cwdPath} > ");
            var input = Console.ReadLine()?.Trim() ?? string.Empty;
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

            if (input.Equals("ref", StringComparison.OrdinalIgnoreCase))
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
                commandLog.Add("[!] unknown command (supported: cd, up, mk, toggle, ref, scroll, quit)");
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
            var name = tokens[1];
            var primitive = tokens.Skip(2).Any(t => t.Equals("-p", StringComparison.OrdinalIgnoreCase));
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
            if (_followCommandScroll)
            {
                _commandScrollOffset = Math.Max(0, commandLog.Count - 1);
            }

            _followCommandScroll = false;
            _commandScrollOffset += delta;
            if (_commandScrollOffset >= commandLog.Count)
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

    private void RenderFrame(int daemonPort, string scene, List<string> treeLines, List<string> commandLog, string cwdPath)
    {
        Console.Clear();

        var borderTop = $"┌{new string('─', FrameWidth)}┐";
        var borderMid = $"├{new string('─', FrameWidth)}┤";
        var borderBottom = $"└{new string('─', FrameWidth)}┘";
        var availableRows = Math.Max(MinTreeRows + MinCommandRows, Console.WindowHeight - ReservedPromptRows);
        var dynamicRows = Math.Max(MinTreeRows + MinCommandRows, availableRows - FrameOverheadRows);
        var streamRows = commandLog
            .Where(line => !line.StartsWith("UnityCLI:", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var hasStreamPane = streamRows.Count > 0;
        var (treeRows, commandRows) = hasStreamPane
            ? AllocateViewportRows(dynamicRows, treeLines.Count, streamRows.Count)
            : (Math.Max(1, dynamicRows), 0);
        var visibleTree = SliceRows(treeLines, treeRows, ref _treeScrollOffset, followTail: false);
        var visibleCommandLog = hasStreamPane
            ? SliceRows(streamRows, commandRows, ref _commandScrollOffset, _followCommandScroll)
            : [];

        Console.WriteLine(borderTop);
        Console.WriteLine(ToFrameLine($" UnityCLI v0.1 | MODE: HIERARCHY | Daemon: 127.0.0.1:{daemonPort} | Scene: {scene}"));
        Console.WriteLine(borderMid);

        foreach (var line in visibleTree)
        {
            Console.WriteLine(ToFrameLine($" {line}"));
        }

        if (hasStreamPane)
        {
            Console.WriteLine(borderMid);
            for (var i = 0; i < visibleCommandLog.Count; i++)
            {
                var line = visibleCommandLog[i];
                Console.WriteLine(ToFrameLine($" {line}"));
            }
        }

        Console.WriteLine(borderBottom);
    }

    private static List<string> BuildTreeLines(HierarchySnapshotDto snapshot, int cwdId, out Dictionary<int, int> indexMap, out string cwdPath)
    {
        indexMap = new Dictionary<int, int>();
        var cwdNode = FindNode(snapshot.Root, cwdId) ?? snapshot.Root;
        cwdPath = BuildPath(snapshot.Root, cwdNode.Id);

        var lines = new List<string> { cwdPath };
        var index = 0;

        for (var i = 0; i < cwdNode.Children.Count; i++)
        {
            var child = cwdNode.Children[i];
            var isLast = i == cwdNode.Children.Count - 1;
            Traverse(child, string.Empty, isLast, lines, indexMap, ref index);
        }

        return lines;
    }

    private static void Traverse(
        HierarchyNodeDto node,
        string prefix,
        bool isLast,
        List<string> lines,
        Dictionary<int, int> indexMap,
        ref int index)
    {
        var branch = isLast ? "└──" : "├──";
        var label = node.Active ? node.Name : $"({node.Name})";
        lines.Add($"{prefix}{branch} [{index}] {label}");
        indexMap[index] = node.Id;
        index++;

        var nextPrefix = prefix + (isLast ? "    " : "│   ");
        for (var i = 0; i < node.Children.Count; i++)
        {
            var child = node.Children[i];
            var childLast = i == node.Children.Count - 1;
            Traverse(child, nextPrefix, childLast, lines, indexMap, ref index);
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

    private static string ToFrameLine(string raw)
    {
        var sanitized = raw.Replace('\t', ' ');
        var content = sanitized.Length > FrameWidth ? sanitized[..FrameWidth] : sanitized.PadRight(FrameWidth, ' ');
        return $"│{content}│";
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
}
