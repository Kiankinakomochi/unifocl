internal static class ProjectViewTreeUtils
{
    public static void InitializeIfNeeded(ProjectViewState state, string projectPath)
    {
        if (state.Initialized)
        {
            RefreshTree(projectPath, state);
            return;
        }

        ResetToAssetsRoot(state, projectPath);
    }

    public static void ResetToAssetsRoot(ProjectViewState state, string projectPath)
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

    public static void RefreshTree(string projectPath, ProjectViewState state)
    {
        state.VisibleEntries.Clear();
        var cwdAbsolute = ProjectViewServiceUtils.ResolveAbsolutePath(projectPath, state.RelativeCwd);
        if (!Directory.Exists(cwdAbsolute))
        {
            return;
        }

        var index = 0;
        BuildEntries(state, cwdAbsolute, state.RelativeCwd, depth: 0, ref index);
    }

    public static bool HandleExpand(int index, ProjectViewState state, List<string> outputs)
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

    public static bool HandleNest(int index, ProjectViewState state, List<string> outputs)
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

    public static bool HandleUp(ProjectViewState state, List<string> outputs)
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

    public static bool IsExpandedDirectory(ProjectViewState state, ProjectTreeEntry entry)
    {
        return state.ExpandedDirectories.Contains(entry.RelativePath);
    }

    public static int? ResolveExpandedSelectionIndex(string projectPath, ProjectViewState state, string expandedRelativePath)
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

    private static void BuildEntries(
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
            var relativeChild = ProjectViewServiceUtils.CombineRelative(relativePath, file.Name);
            state.VisibleEntries.Add(new ProjectTreeEntry(index++, depth, file.Name, relativeChild, false));
        }

        foreach (var directory in directories)
        {
            var relativeChild = ProjectViewServiceUtils.CombineRelative(relativePath, directory.Name);
            state.VisibleEntries.Add(new ProjectTreeEntry(index++, depth, directory.Name, relativeChild, true));
            if (state.ExpandedDirectories.Contains(relativeChild))
            {
                BuildEntries(state, directory.FullName, relativeChild, depth + 1, ref index);
            }
        }
    }
}
