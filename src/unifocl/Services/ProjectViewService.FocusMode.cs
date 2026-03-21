internal sealed partial class ProjectViewService
{
    public async Task RunKeyboardFocusModeAsync(
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime,
        Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(session.CurrentProjectPath))
        {
            return;
        }

        ProjectViewTreeUtils.InitializeIfNeeded(session.ProjectView, session.CurrentProjectPath);
        if (ShouldRunUpmFocusMode(session.ProjectView))
        {
            await RunUpmPackageFocusModeAsync(session, daemonControlService, daemonRuntime);
            return;
        }

        var outputs = new List<string>
        {
            "[i] project focus mode enabled (up/down select, idx jump, tab open/reveal, shift+tab back, esc exit)"
        };
        EmitOutputs(session.ProjectView, outputs);
        outputs.Clear();

        var selectedEntryPosition = 0;
        var typedIndexBuffer = string.Empty;
        long typedIndexLastInputTick = 0;
        var (knownViewportWidth, knownViewportHeight) = TuiConsoleViewport.GetWindowSizeOrDefault();
        while (true)
        {
            ProjectViewTreeUtils.RefreshTree(session.CurrentProjectPath, session.ProjectView);
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

            if (!TuiConsoleViewport.WaitForKeyOrResize(ref knownViewportWidth, ref knownViewportHeight, out var key))
            {
                continue;
            }

            var intent = KeyboardIntentReader.ReadIntentFromFirstKey(key);
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
                        EmitOutputs(session.ProjectView, outputs);
                        RenderFrame(session.ProjectView);
                        return;
                    }

                    if (tabResult == ProjectFocusTabResult.ExpandedDirectory)
                    {
                        session.ProjectView.FocusHighlightedEntryIndex = ProjectViewTreeUtils.ResolveExpandedSelectionIndex(
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
                    ProjectViewTreeUtils.HandleUp(session.ProjectView, outputs);
                    selectedEntryPosition = 0;
                    session.ProjectView.FocusHighlightedEntryIndex = null;
                    break;
                case KeyboardIntent.Escape:
                case KeyboardIntent.FocusProject:
                    outputs.Add("[i] project focus mode disabled");
                    session.ProjectView.FocusHighlightedEntryIndex = null;
                    EmitOutputs(session.ProjectView, outputs);
                    RenderFrame(session.ProjectView);
                    return;
                default:
                    break;
            }

            if (outputs.Count > 0)
            {
                EmitOutputs(session.ProjectView, outputs);
                outputs.Clear();
            }
        }
    }

    private static bool ShouldRunUpmFocusMode(ProjectViewState state)
    {
        _ = state;
        return false;
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
        EmitOutputs(state, ["[i] upm selection mode enabled (up/down select, idx jump, enter action menu, esc/F7 exit)"]);
        RenderFrame(state);

        var typedIndexBuffer = string.Empty;
        long typedIndexLastInputTick = 0;
        var (knownViewportWidth, knownViewportHeight) = TuiConsoleViewport.GetWindowSizeOrDefault();
        while (true)
        {
            if (state.LastUpmPackages.Count == 0)
            {
                EmitOutputs(state, ["[i] upm selection mode disabled (no packages to select)"]);
                state.UpmFocusModeEnabled = false;
                state.UpmActionMenuVisible = false;
                RenderFrame(state);
                return;
            }

            state.UpmFocusSelectedIndex = Math.Clamp(state.UpmFocusSelectedIndex, 0, state.LastUpmPackages.Count - 1);
            RenderFrame(state);

            if (!TuiConsoleViewport.WaitForKeyOrResize(ref knownViewportWidth, ref knownViewportHeight, out var key))
            {
                continue;
            }

            var intent = KeyboardIntentReader.ReadIntentFromFirstKey(key);
            if (intent is KeyboardIntent.Escape or KeyboardIntent.FocusProject)
            {
                if (state.UpmActionMenuVisible)
                {
                    state.UpmActionMenuVisible = false;
                    continue;
                }

                EmitOutputs(state, ["[i] upm selection mode disabled"]);
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
        DaemonRuntime daemonRuntime,
        Action<string>? log = null)
    {
        var packageId = selectedPackage.PackageId;
        switch (actionIndex)
        {
            case 0:
                await TryHandleProjectViewCommandAsync($"upm update {packageId}", session, daemonControlService, daemonRuntime, log);
                break;
            case 1:
                await TryHandleProjectViewCommandAsync($"upm remove {packageId}", session, daemonControlService, daemonRuntime, log);
                break;
            default:
                if (!selectedPackage.Source.Equals("Registry", StringComparison.OrdinalIgnoreCase))
                {
                    EmitOutputs(session.ProjectView, ["[x] clean install supports registry packages only"]);
                    RenderFrame(session.ProjectView);
                    return true;
                }

                var installTarget = string.IsNullOrWhiteSpace(selectedPackage.Version)
                    ? selectedPackage.PackageId
                    : $"{selectedPackage.PackageId}@{selectedPackage.Version}";
                await TryHandleProjectViewCommandAsync($"upm remove {packageId}", session, daemonControlService, daemonRuntime, log);
                await TryHandleProjectViewCommandAsync($"upm install {installTarget}", session, daemonControlService, daemonRuntime, log);
                break;
        }

        await TryHandleProjectViewCommandAsync("upm ls", session, daemonControlService, daemonRuntime, log);
        return session.ProjectView.LastUpmPackages.Count > 0;
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
            if (!ProjectViewTreeUtils.IsExpandedDirectory(session.ProjectView, entry))
            {
                ProjectViewTreeUtils.HandleExpand(entry.Index, session.ProjectView, outputs);
                return ProjectFocusTabResult.ExpandedDirectory;
            }

            ProjectViewTreeUtils.HandleNest(entry.Index, session.ProjectView, outputs);
            return ProjectFocusTabResult.NestedDirectory;
        }

        await HandleLoadViaBridgeAsync(entry.Index.ToString(), session, outputs, daemonControlService, daemonRuntime);
        return ProjectFocusTabResult.OpenedAsset;
    }
}
