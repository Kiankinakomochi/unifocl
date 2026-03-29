internal sealed partial class InspectorModeService
{
    private void HandleStepUp(CliSessionState session, Action<string> log)
    {
        var context = session.Inspector;
        if (context is null)
        {
            log("[grey]system[/]: not in inspector mode");
            return;
        }

        if (context.Depth == InspectorDepth.ComponentFields)
        {
            context.FocusHighlightedComponentIndex = context.SelectedComponentIndex;
            context.FocusHighlightedFieldName = null;
            context.Depth = InspectorDepth.ComponentList;
            context.Fields.Clear();
            context.SelectedComponentIndex = null;
            context.SelectedComponentName = null;
            context.BodyScrollOffset = 0;
            context.FollowStreamScroll = true;
            context.StreamScrollOffset = int.MaxValue;
            AddStream(context, $"{context.PromptLabel} > :i");
            AddStream(context, "[i] stepped up to component list");
            _renderer.Render(context);
            return;
        }

        session.Inspector = null;
        session.ContextMode = CliContextMode.Project;
        log("[i] inspector exited to project context");
    }

    private async Task EnterInspectorRootAsync(
        InspectorContext context,
        CliSessionState session,
        string targetPath,
        string rawCommand)
    {
        context.TargetPath = targetPath;
        context.Depth = InspectorDepth.ComponentList;
        context.FocusHighlightedComponentIndex = null;
        context.FocusHighlightedFieldName = null;
        context.Fields.Clear();
        context.SelectedComponentIndex = null;
        context.SelectedComponentName = null;
        context.BodyScrollOffset = 0;
        context.FollowStreamScroll = true;
        context.StreamScrollOffset = int.MaxValue;
        context.LastReferenceSearchResults.Clear();
        session.ContextMode = CliContextMode.Inspector;
        session.FocusPath = targetPath;

        // Right after scene/prefab transitions, Bridge callbacks can be briefly delayed on Unity main thread.
        // Probe hierarchy snapshot readiness first to reduce false-empty inspector fetches.
        await WaitForHierarchySnapshotReadyAsync(session);

        var componentFetch = await PopulateComponentsAsync(context, session, forceRefresh: true);
        AddStream(context, $"{context.PromptLabel} > {rawCommand}");
        AddStream(context, $"[i] entering inspector for: {context.TargetPath.TrimStart('/')}");
        if (context.Components.Count == 0)
        {
            AddStream(context, "[!] no inspector components returned");
            var diagnostics = await BuildInspectorComponentFetchDiagnosticsAsync(session, context.TargetPath, componentFetch);
            foreach (var line in diagnostics)
            {
                AddStream(context, line);
            }
        }

        _renderer.Render(context);
    }

    private async Task WaitForHierarchySnapshotReadyAsync(CliSessionState session)
    {
        if (DaemonControlService.GetPort(session) is not int waitPort)
        {
            return;
        }

        var deadline = DateTime.UtcNow.AddSeconds(12);
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = await _hierarchyDaemonClient.GetSnapshotAsync(waitPort);
            if (snapshot is not null)
            {
                return;
            }

            await Task.Delay(160);
        }
    }

    private async Task EnterComponentAsync(
        InspectorContext context,
        CliSessionState session,
        int componentIndex,
        string rawCommand)
    {
        var component = context.Components.FirstOrDefault(c => c.Index == componentIndex);
        if (component is null)
        {
            AddStream(context, $"{context.PromptLabel} > {rawCommand}");
            AddStream(context, $"[!] invalid component index: {componentIndex}");
            _renderer.Render(context);
            return;
        }

        context.Depth = InspectorDepth.ComponentFields;
        context.SelectedComponentIndex = componentIndex;
        context.SelectedComponentName = component.Name;
        context.FocusHighlightedComponentIndex = componentIndex;
        context.FocusHighlightedFieldName = null;
        context.BodyScrollOffset = 0;
        context.FollowStreamScroll = true;
        context.StreamScrollOffset = int.MaxValue;
        context.LastReferenceSearchResults.Clear();
        await PopulateFieldsAsync(context, session, componentIndex, forceRefresh: true);
        AddStream(context, $"UnityCLI:{context.TargetPath} [[inspect]] > {rawCommand}");
        AddStream(context, $"[i] inspecting component: {component.Name}");
        if (context.Fields.Count == 0)
        {
            AddStream(context, "[!] no serializable fields returned for selected component");
        }

        _renderer.Render(context);
    }

    private async Task<HierarchyNodeDto?> TryResolveHierarchyNodeAsync(
        CliSessionState session,
        string targetPath,
        bool includeRoot)
    {
        if (DaemonControlService.GetPort(session) is not int snapshotPort)
        {
            return null;
        }

        var snapshot = await _hierarchyDaemonClient.GetSnapshotAsync(snapshotPort);
        if (snapshot is null)
        {
            return null;
        }

        if (TryFindNodeByInspectorPath(snapshot.Root, targetPath, includeRoot, out var resolved))
        {
            return resolved;
        }

        var stripped = StripSceneRootPrefix(NormalizeInspectorPath(targetPath), snapshot.Root.Name);
        if (TryResolveHierarchyPathLenient(snapshot.Root, stripped, out var lenient))
        {
            stripped = lenient;
        }

        return TryFindNodeByInspectorPath(snapshot.Root, stripped, includeRoot, out resolved)
            ? resolved
            : null;
    }

    private static bool TryFindNodeByInspectorPath(
        HierarchyNodeDto root,
        string targetPath,
        bool includeRoot,
        out HierarchyNodeDto node)
    {
        node = root;
        var normalized = NormalizeInspectorPath(targetPath);
        if (normalized == "/")
        {
            return includeRoot;
        }

        var segments = normalized
            .Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var current = root;
        foreach (var segment in segments)
        {
            var next = current.Children.FirstOrDefault(child =>
                child.Name.Equals(segment, StringComparison.OrdinalIgnoreCase));
            if (next is null)
            {
                return false;
            }

            current = next;
        }

        node = current;
        return true;
    }

    private static string ResolveMoveDestinationPath(string currentTargetPath, string destinationToken)
    {
        var normalizedCurrent = NormalizeInspectorPath(currentTargetPath);
        if (destinationToken.Equals("/", StringComparison.Ordinal)
            || destinationToken.Equals("root", StringComparison.OrdinalIgnoreCase))
        {
            return "/";
        }

        if (destinationToken.Equals("..", StringComparison.Ordinal))
        {
            return GetParentPath(normalizedCurrent);
        }

        return NormalizeInspectorPath(destinationToken.StartsWith('/') ? destinationToken : "/" + destinationToken);
    }

    private static string ReplaceLeafSegment(string path, string newLeafName)
    {
        var normalized = NormalizeInspectorPath(path);
        if (normalized == "/")
        {
            return normalized;
        }

        var segments = normalized
            .Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        segments[^1] = newLeafName;
        return "/" + string.Join('/', segments);
    }

    private static string GetParentPath(string path)
    {
        var normalized = NormalizeInspectorPath(path);
        if (normalized == "/")
        {
            return "/";
        }

        var segments = normalized
            .Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length <= 1)
        {
            return "/";
        }

        return "/" + string.Join('/', segments.Take(segments.Length - 1));
    }

    private static string NormalizeInspectorPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "/";
        }

        var normalized = path.Trim();
        if (!normalized.StartsWith('/'))
        {
            normalized = "/" + normalized;
        }

        while (normalized.Contains("//", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
        }

        return normalized.Length > 1 ? normalized.TrimEnd('/') : normalized;
    }

    private static bool TryResolveHierarchyPathLenient(HierarchyNodeDto root, string normalizedPath, out string resolvedPath)
    {
        resolvedPath = "/";
        if (string.IsNullOrWhiteSpace(normalizedPath) || normalizedPath == "/")
        {
            return true;
        }

        var segments = normalizedPath
            .Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
        {
            return true;
        }

        var current = root;
        var resolvedSegments = new List<string>(segments.Length);
        foreach (var segment in segments)
        {
            var exact = current.Children
                .Where(child => child.Name.Equals(segment, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (exact.Count == 1)
            {
                current = exact[0];
                resolvedSegments.Add(current.Name);
                continue;
            }

            if (exact.Count > 1)
            {
                return false;
            }

            var prefixed = current.Children
                .Where(child => child.Name.StartsWith(segment + " (", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (prefixed.Count != 1)
            {
                return false;
            }

            current = prefixed[0];
            resolvedSegments.Add(current.Name);
        }

        resolvedPath = "/" + string.Join('/', resolvedSegments);
        return true;
    }
}
