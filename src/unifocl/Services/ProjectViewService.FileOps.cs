using System.Text.Json;
using Spectre.Console;

internal sealed partial class ProjectViewService
{
    private async Task<bool> HandleMkViaBridgeAsync(
        IReadOnlyList<string> tokens,
        CliSessionState session,
        List<string> outputs)
    {
        if (!ProjectViewMkCommandUtils.TryParseProjectMkArguments(tokens, out var mkTypeRaw, out var count, out var name, out var parentSelector, out var error))
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

        if (!ProjectViewMkCommandUtils.TryResolveMkParentPath(session, parentSelector, out var parentPath, out var parentError))
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
                outputs.Add(ProjectViewServiceUtils.FormatProjectCommandFailure("create", response.Message));
                return true;
            }

            if (response.Kind?.Equals("dry-run", StringComparison.OrdinalIgnoreCase) == true
                && TryAppendDryRunDiff(outputs, response.Content))
            {
                return true;
            }

            var createdPaths = ProjectViewServiceUtils.ParseMkAssetCreatedPaths(response.Content);
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
                ProjectViewTreeUtils.RefreshTree(session.CurrentProjectPath, state);
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
            var template = ProjectViewServiceUtils.ResolveTemplate(projectPath);
            outputs.Add($"[*] template: found '{template.TemplateName}' in {template.TemplateSource}");
            for (var i = 0; i < count; i++)
            {
                var rawName = ProjectViewServiceUtils.ResolveScriptCreateName(canonicalType, name, i, count);
                var typeName = ProjectViewServiceUtils.SanitizeTypeName(rawName);
                if (string.IsNullOrWhiteSpace(typeName))
                {
                    outputs.Add("[x] invalid script name");
                    return true;
                }

                var uniqueTypeName = ProjectViewServiceUtils.ResolveUniqueScriptTypeName(projectPath, parentPath, typeName);
                var targetRelative = ProjectViewServiceUtils.CombineRelative(parentPath, $"{uniqueTypeName}.cs");

                var content = canonicalType.Equals("ScriptableObjectScript", StringComparison.OrdinalIgnoreCase)
                    ? ProjectViewServiceUtils.BuildScriptableObjectTemplate(uniqueTypeName)
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
                    outputs.Add(ProjectViewServiceUtils.FormatProjectCommandFailure("create", response.Message));
                    return true;
                }

                if (response.Kind?.Equals("dry-run", StringComparison.OrdinalIgnoreCase) == true
                    && TryAppendDryRunDiff(outputs, response.Content))
                {
                    return true;
                }

                created.Add(targetRelative);
            }

            foreach (var path in created)
            {
                outputs.Add($"[+] created: {path}");
            }

            await SyncAssetIndexAsync(session);
            ProjectViewTreeUtils.RefreshTree(projectPath, state);
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
        if (ProjectViewServiceUtils.TryParseRemoveIndexRange(selector, out var startIndex, out var endIndex, out var rangeError))
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
                if (!response.Ok && ProjectViewServiceUtils.IsAssetNotFoundFailure(response.Message))
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
                    outputs.Add(ProjectViewServiceUtils.FormatProjectCommandFailure("remove", response.Message));
                    if (targets.Count > 1)
                    {
                        outputs.Add($"[i] removed {removedPaths.Count}/{targets.Count} before failure");
                    }

                    return true;
                }

                if (response.Kind?.Equals("dry-run", StringComparison.OrdinalIgnoreCase) == true
                    && TryAppendDryRunDiff(outputs, response.Content))
                {
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
                ProjectViewTreeUtils.RefreshTree(session.CurrentProjectPath, state);
            }

            await SyncAssetIndexAsync(session);
            return true;
        }
        finally
        {
            state.DbState = ProjectDbState.IdleSafe;
        }
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

        var target = ProjectViewServiceUtils.FindEntryBySelector(state, selector);
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
        var isHierarchyAssetLoad = ProjectViewServiceUtils.IsHierarchyAssetExtension(extension);
        var loadAssetKind = ProjectViewServiceUtils.ResolveLoadAssetKind(extension);
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
        if (!response.Ok && ProjectViewServiceUtils.IsAssetNotFoundFailure(response.Message))
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

            outputs.Add(ProjectViewServiceUtils.FormatProjectCommandFailure("load", response.Message));
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
        var destinationRelative = ProjectViewServiceUtils.ComputeRenameDestinationPath(sourceRelativePath, target.IsDirectory, newName);

        state.DbState = ProjectDbState.LockedImporting;
        try
        {
            var response = await ExecuteProjectCommandAsync(
                session,
                new ProjectCommandRequestDto("rename-asset", sourceRelativePath, destinationRelative, null));
            if (!response.Ok && ProjectViewServiceUtils.IsAssetNotFoundFailure(response.Message))
            {
                var fallbackPath = await ResolveAssetFallbackPathAsync(session, sourceRelativePath, allowDirectoryFallback: target.IsDirectory);
                if (!string.IsNullOrWhiteSpace(fallbackPath)
                    && !fallbackPath.Equals(sourceRelativePath, StringComparison.OrdinalIgnoreCase))
                {
                    sourceRelativePath = fallbackPath;
                    destinationRelative = ProjectViewServiceUtils.ComputeRenameDestinationPath(sourceRelativePath, target.IsDirectory, newName);
                    response = await ExecuteProjectCommandAsync(
                        session,
                        new ProjectCommandRequestDto("rename-asset", sourceRelativePath, destinationRelative, null));
                }
            }

            if (!response.Ok)
            {
                outputs.Add(ProjectViewServiceUtils.FormatProjectCommandFailure("rename", response.Message));
                return true;
            }

            if (response.Kind?.Equals("dry-run", StringComparison.OrdinalIgnoreCase) == true
                && TryAppendDryRunDiff(outputs, response.Content))
            {
                return true;
            }

            outputs.Add("[=] rename complete. .meta file updated successfully.");
            if (!string.IsNullOrWhiteSpace(session.CurrentProjectPath))
            {
                ProjectViewTreeUtils.RefreshTree(session.CurrentProjectPath, state);
            }

            await SyncAssetIndexAsync(session);
            return true;
        }
        finally
        {
            state.DbState = ProjectDbState.IdleSafe;
        }
    }

    private async Task<string?> ResolveAssetFallbackPathAsync(CliSessionState session, string targetRelativePath, bool allowDirectoryFallback)
    {
        var targetPath = targetRelativePath.Replace('\\', '/');
        if (!string.IsNullOrWhiteSpace(session.CurrentProjectPath))
        {
            var targetAbsolutePath = ProjectViewServiceUtils.ResolveAbsolutePath(session.CurrentProjectPath, targetPath);
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

    private void EmitImmediateLoadFeedback(ProjectViewState state, string targetName)
    {
        var prefix = ProjectViewServiceUtils.ResolveLoadAssetKind(Path.GetExtension(targetName));
        ProjectViewTranscriptUtils.Append(state, [$"[*] loading {prefix}: {targetName}"]);
        RenderFrame(state);
    }

    private void EmitLoadDiagnostic(ProjectViewState state, string message)
    {
        ProjectViewTranscriptUtils.Append(state, [$"[grey]load[/]: {Markup.Escape(message)}"]);
        RenderFrame(state);
    }
}
