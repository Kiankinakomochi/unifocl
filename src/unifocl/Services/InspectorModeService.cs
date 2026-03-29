using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Globalization;
using Spectre.Console;

internal sealed partial class InspectorModeService
{
    private const int SceneRootNodeId = int.MaxValue;
    private const int DefaultInnerWidth = 78;
    private const int MinInnerWidth = 40;
    private readonly InspectorTuiRenderer _renderer = new();
    private readonly HierarchyDaemonClient _hierarchyDaemonClient = new();
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private static readonly HttpClient Http = new();

    public async Task<bool> TryHandleInspectorCommandAsync(
        string input,
        IReadOnlyList<string> tokens,
        CliSessionState session,
        Action<string> log)
    {
        if (tokens.Count == 0)
        {
            return false;
        }

        var normalizedTokens = tokens.ToList();
        CliDryRunDiffService.TryStripDryRunFlag(normalizedTokens, out var dryRunRequested);
        using var dryRunScope = CliDryRunScope.Push(dryRunRequested);
        if (normalizedTokens.Count == 0)
        {
            return false;
        }

        tokens = normalizedTokens;

        var command = tokens[0].ToLowerInvariant();
        var inInspector = session.Inspector is not null;
        var isInspectorCommand = command is
            "inspect" or
            "list" or
            "ls" or
            "enter" or
            "cd" or
            "up" or
            ".." or
            "set" or
            "s" or
            "edit" or
            "e" or
            "toggle" or
            "t" or
            "f" or
            "ff" or
            "component" or
            "comp" or
            "make" or
            "mk" or
            "remove" or
            "rm" or
            "rename" or
            "rn" or
            "move" or
            "mv" or
            "scroll" or
            ":i";

        if (!inInspector && !isInspectorCommand)
        {
            return false;
        }

        if (!isInspectorCommand)
        {
            return false;
        }

        switch (command)
        {
            case "inspect":
                await HandleInspectAsync(input, tokens, session);
                return true;
            case "list":
            case "ls":
                await HandleLsAsync(session, log);
                return true;
            case "enter":
            case "cd":
                await HandleInspectAsync(
                    $"inspect {string.Join(' ', tokens.Skip(1))}",
                    tokens.Count > 1 ? ["inspect", .. tokens.Skip(1)] : ["inspect"],
                    session);
                return true;
            case "up":
            case "..":
            case ":i":
                HandleStepUp(session, log);
                return true;
            case "toggle":
                await HandleToggleAsync(input, tokens, session, log);
                return true;
            case "t":
                await HandleToggleAsync(
                    $"toggle {string.Join(' ', tokens.Skip(1))}",
                    ["toggle", .. tokens.Skip(1)],
                    session,
                    log);
                return true;
            case "set":
                await HandleSetAsync(input, tokens, session, log);
                return true;
            case "s":
                await HandleSetAsync(
                    $"set {string.Join(' ', tokens.Skip(1))}",
                    ["set", .. tokens.Skip(1)],
                    session,
                    log);
                return true;
            case "edit":
                await HandleSetAsync(
                    $"set {string.Join(' ', tokens.Skip(1))}",
                    tokens.Count > 1 ? ["set", .. tokens.Skip(1)] : ["set"],
                    session,
                    log);
                return true;
            case "e":
                await HandleSetAsync(
                    $"set {string.Join(' ', tokens.Skip(1))}",
                    ["set", .. tokens.Skip(1)],
                    session,
                    log);
                return true;
            case "f":
            case "ff":
                await HandleFuzzyFindAsync(input, tokens, session, log);
                return true;
            case "component":
                await HandleComponentAsync(input, tokens, session, log);
                return true;
            case "comp":
                await HandleComponentAsync(
                    $"component {string.Join(' ', tokens.Skip(1))}",
                    ["component", .. tokens.Skip(1)],
                    session,
                    log);
                return true;
            case "make":
            case "mk":
                await HandleMakeAsync(input, tokens, session, log);
                return true;
            case "remove":
            case "rm":
                await HandleRemoveAsync(input, session, log);
                return true;
            case "rename":
            case "rn":
                await HandleRenameAsync(input, tokens, session, log);
                return true;
            case "move":
            case "mv":
                await HandleMoveAsync(input, tokens, session, log);
                return true;
            case "scroll":
                HandleScroll(input, tokens, session, log);
                return true;
            default:
                return false;
        }
    }

    public async Task RunKeyboardFocusModeAsync(
        CliSessionState session,
        Action<string> log,
        bool returnToHierarchyInline = false)
    {
        var context = session.Inspector;
        if (context is null)
        {
            log("[yellow]inspector[/]: focus mode requires inspector context");
            return;
        }

        if (context.Depth == InspectorDepth.ComponentList)
        {
            await PopulateComponentsAsync(context, session, forceRefresh: context.Components.Count == 0);
        }
        else if (context.SelectedComponentIndex is int selectedComponentIndex)
        {
            await PopulateFieldsAsync(context, session, selectedComponentIndex, forceRefresh: context.Fields.Count == 0);
        }

        var selectedComponentPosition = 0;
        var selectedFieldPosition = 0;
        var typedIndexBuffer = string.Empty;
        long typedIndexLastInputTick = 0;
        var (knownViewportWidth, knownViewportHeight) = TuiConsoleViewport.GetWindowSizeOrDefault();

        while (true)
        {
            if (context.Depth == InspectorDepth.ComponentList)
            {
                var components = context.Components.OrderBy(component => component.Index).ToList();
                if (components.Count == 0)
                {
                    context.FocusHighlightedComponentIndex = null;
                    _renderer.Render(context, null, null, focusModeEnabled: true);
                }
                else
                {
                    if (context.FocusHighlightedComponentIndex is int highlightedComponentIndex)
                    {
                        var highlightedPosition = components.FindIndex(component => component.Index == highlightedComponentIndex);
                        if (highlightedPosition >= 0)
                        {
                            selectedComponentPosition = highlightedPosition;
                        }
                    }

                    selectedComponentPosition = Math.Clamp(selectedComponentPosition, 0, components.Count - 1);
                    var selectedComponent = components[selectedComponentPosition];
                    context.FocusHighlightedComponentIndex = selectedComponent.Index;
                    _renderer.Render(context, selectedComponent.Index, null, focusModeEnabled: true);
                }
            }
            else
            {
                var fields = context.Fields;
                if (fields.Count == 0)
                {
                    context.FocusHighlightedFieldName = null;
                    _renderer.Render(context, null, null, focusModeEnabled: true);
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(context.FocusHighlightedFieldName))
                    {
                        var highlightedPosition = fields.FindIndex(field =>
                            field.Name.Equals(context.FocusHighlightedFieldName, StringComparison.OrdinalIgnoreCase));
                        if (highlightedPosition >= 0)
                        {
                            selectedFieldPosition = highlightedPosition;
                        }
                    }

                    selectedFieldPosition = Math.Clamp(selectedFieldPosition, 0, fields.Count - 1);
                    context.FocusHighlightedFieldName = fields[selectedFieldPosition].Name;
                    _renderer.Render(context, null, context.FocusHighlightedFieldName, focusModeEnabled: true);
                }
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
                        if (context.Depth == InspectorDepth.ComponentList)
                        {
                            var orderedComponents = context.Components.OrderBy(component => component.Index).ToList();
                            var componentTargetPosition = orderedComponents.FindIndex(component => component.Index == index);
                            if (componentTargetPosition < 0)
                            {
                                return false;
                            }

                            selectedComponentPosition = componentTargetPosition;
                            context.FocusHighlightedComponentIndex = orderedComponents[componentTargetPosition].Index;
                            return true;
                        }

                        if ((uint)index >= context.Fields.Count)
                        {
                            return false;
                        }

                        selectedFieldPosition = index;
                        context.FocusHighlightedFieldName = context.Fields[selectedFieldPosition].Name;
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
                    if (context.Depth == InspectorDepth.ComponentList && context.Components.Count > 0)
                    {
                        var orderedComponentsForUp = context.Components.OrderBy(component => component.Index).ToList();
                        selectedComponentPosition = selectedComponentPosition <= 0
                            ? orderedComponentsForUp.Count - 1
                            : selectedComponentPosition - 1;
                        context.FocusHighlightedComponentIndex = orderedComponentsForUp[selectedComponentPosition].Index;
                    }
                    else if (context.Depth == InspectorDepth.ComponentFields && context.Fields.Count > 0)
                    {
                        selectedFieldPosition = selectedFieldPosition <= 0
                            ? context.Fields.Count - 1
                            : selectedFieldPosition - 1;
                        context.FocusHighlightedFieldName = context.Fields[selectedFieldPosition].Name;
                    }
                    break;
                case KeyboardIntent.Down:
                    if (context.Depth == InspectorDepth.ComponentList && context.Components.Count > 0)
                    {
                        var orderedComponentsForDown = context.Components.OrderBy(component => component.Index).ToList();
                        selectedComponentPosition = selectedComponentPosition >= orderedComponentsForDown.Count - 1
                            ? 0
                            : selectedComponentPosition + 1;
                        context.FocusHighlightedComponentIndex = orderedComponentsForDown[selectedComponentPosition].Index;
                    }
                    else if (context.Depth == InspectorDepth.ComponentFields && context.Fields.Count > 0)
                    {
                        selectedFieldPosition = selectedFieldPosition >= context.Fields.Count - 1
                            ? 0
                            : selectedFieldPosition + 1;
                        context.FocusHighlightedFieldName = context.Fields[selectedFieldPosition].Name;
                    }
                    break;
                case KeyboardIntent.Enter:
                    if (context.Depth == InspectorDepth.ComponentFields && context.Fields.Count > 0)
                    {
                        selectedFieldPosition = Math.Clamp(selectedFieldPosition, 0, context.Fields.Count - 1);
                        var fieldToEdit = context.Fields[selectedFieldPosition];
                        await RunInteractiveFieldEditAsync(context, session, fieldToEdit);
                    }

                    break;
                case KeyboardIntent.Tab:
                    if (context.Depth != InspectorDepth.ComponentList || context.Components.Count == 0)
                    {
                        break;
                    }

                    var orderedComponents = context.Components.OrderBy(component => component.Index).ToList();
                    selectedComponentPosition = Math.Clamp(selectedComponentPosition, 0, orderedComponents.Count - 1);
                    await EnterComponentAsync(
                        context,
                        session,
                        orderedComponents[selectedComponentPosition].Index,
                        $"inspect {orderedComponents[selectedComponentPosition].Index}");
                    selectedFieldPosition = 0;
                    context.FocusHighlightedFieldName = null;
                    break;
                case KeyboardIntent.ShiftTab:
                    if (context.Depth == InspectorDepth.ComponentFields)
                    {
                        StepUpToComponentList(context, "[i] stepped up to component list");
                    }
                    else
                    {
                        AddStream(context, "[i] already at inspector root");
                    }

                    break;
                case KeyboardIntent.FocusProject:
                    if (context.Depth == InspectorDepth.ComponentFields)
                    {
                        StepUpToComponentList(context, "[i] returned to component list");
                    }

                    await PopulateComponentsAsync(context, session, forceRefresh: context.Components.Count == 0);

                    if (context.FocusHighlightedComponentIndex is null && context.Components.Count > 0)
                    {
                        context.FocusHighlightedComponentIndex = context.Components
                            .OrderBy(component => component.Index)
                            .First()
                            .Index;
                    }

                    _renderer.Render(context, context.FocusHighlightedComponentIndex, null, focusModeEnabled: false);
                    return;
                case KeyboardIntent.Escape:
                    if (context.Depth == InspectorDepth.ComponentFields)
                    {
                        StepUpToComponentList(context, "[i] escaped to component list");
                        break;
                    }

                    session.Inspector = null;
                    if (returnToHierarchyInline)
                    {
                        session.ContextMode = CliContextMode.Hierarchy;
                        log("[i] inspector exited to hierarchy mode");
                    }
                    else
                    {
                        session.ContextMode = CliContextMode.Project;
                        session.AutoEnterHierarchyRequested = true;
                        log("[i] inspector exited; returning to hierarchy mode");
                    }

                    return;
                default:
                    break;
            }
        }
    }

    public void RenderCurrentFrame(CliSessionState session)
    {
        if (session.Inspector is null)
        {
            return;
        }

        _renderer.Render(session.Inspector);
    }

    private static void StepUpToComponentList(InspectorContext context, string message)
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
        AddStream(context, message);
    }

    private async Task HandleInspectAsync(
        string input,
        IReadOnlyList<string> tokens,
        CliSessionState session)
    {
        var context = session.Inspector ?? new InspectorContext();
        session.Inspector = context;

        if (tokens.Count == 1)
        {
            await EnterInspectorRootAsync(context, session, session.FocusPath, input);
            return;
        }

        var argument = string.Join(' ', tokens.Skip(1)).Trim();
        if (string.IsNullOrWhiteSpace(argument))
        {
            await EnterInspectorRootAsync(context, session, session.FocusPath, input);
            return;
        }

        if (int.TryParse(argument, out var componentIndex) && context.Components.Count > 0)
        {
            await EnterComponentAsync(context, session, componentIndex, input);
            return;
        }

        var rawTarget = argument.StartsWith('/') ? argument : "/" + argument;
        var target = await NormalizeInspectorTargetPathAsync(session, rawTarget);
        await EnterInspectorRootAsync(context, session, target, input);
    }

    private async Task HandleLsAsync(
        CliSessionState session,
        Action<string> log)
    {
        var context = session.Inspector;
        if (context is null)
        {
            log("[grey]system[/]: ls (inspector) requires inspect mode");
            return;
        }

        if (context.Depth == InspectorDepth.ComponentList)
        {
            await PopulateComponentsAsync(context, session, forceRefresh: true);
            AddStream(context, $"{context.PromptLabel} > ls");
            AddStream(context, "[i] refreshed components");
        }
        else
        {
            if (context.SelectedComponentIndex is null)
            {
                AddStream(context, $"{context.PromptLabel} > ls");
                AddStream(context, "[!] no component selected");
            }
            else
            {
                await PopulateFieldsAsync(context, session, context.SelectedComponentIndex.Value, forceRefresh: true);
                AddStream(context, $"{context.PromptLabel} > ls");
                AddStream(context, $"[i] refreshed fields for: {context.SelectedComponentName}");
            }
        }

        _renderer.Render(context);
    }

    private async Task HandleToggleAsync(
        string input,
        IReadOnlyList<string> tokens,
        CliSessionState session,
        Action<string> log)
    {
        var context = session.Inspector;
        if (context is null)
        {
            log("[grey]system[/]: toggle requires inspect mode");
            return;
        }

        if (tokens.Count < 2)
        {
            AddStream(context, $"{context.PromptLabel} > {input}");
            AddStream(context, "[!] usage: toggle <component-index|field>");
            _renderer.Render(context);
            return;
        }

        if (context.Depth == InspectorDepth.ComponentList)
        {
            if (!int.TryParse(tokens[1], out var index))
            {
                AddStream(context, $"{context.PromptLabel} > {input}");
                AddStream(context, "[!] component toggle requires an index");
                _renderer.Render(context);
                return;
            }

            var entry = context.Components.FirstOrDefault(c => c.Index == index);
            if (entry is null)
            {
                AddStream(context, $"{context.PromptLabel} > {input}");
                AddStream(context, $"[!] no component at index {index}");
                _renderer.Render(context);
                return;
            }

            var newEnabled = !entry.Enabled;
            var mutation = await TrySendInspectorMutationWithMessageAsync(
                session,
                new InspectorBridgeRequest(
                    "toggle-component",
                    context.TargetPath,
                    entry.Index,
                    entry.Name,
                    null,
                    null,
                    null));

            AddStream(context, $"{context.PromptLabel} > {input}");
        if (mutation.Ok)
        {
            context.Components.Remove(entry);
            context.Components.Add(entry with { Enabled = newEnabled });
            context.Components.Sort((a, b) => a.Index.CompareTo(b.Index));
            AddStream(context, $"[*] ok: {entry.Name} set to {(newEnabled ? "activated" : "deactivated")}");
            AppendDryRunDiffIfAny(context, mutation.Content);
        }
        else
        {
            AddStream(context, $"[!] {DescribeInspectorMutationFailure(mutation.Message)}");
        }

            _renderer.Render(context);
            return;
        }

        var fieldName = tokens[1];
        var field = context.Fields.FirstOrDefault(f => f.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase));
        if (field is null)
        {
            AddStream(context, $"{context.PromptLabel} > {input}");
            AddStream(context, $"[!] unknown field: {fieldName}");
            _renderer.Render(context);
            return;
        }

        if (!field.IsBoolean)
        {
            AddStream(context, $"{context.PromptLabel} > {input}");
            AddStream(context, $"[!] field {field.Name} is not toggleable");
            _renderer.Render(context);
            return;
        }

        var newValue = field.Value.Equals("true", StringComparison.OrdinalIgnoreCase) ? "false" : "true";
        var fieldMutation = await TrySendInspectorMutationWithMessageAsync(
            session,
            new InspectorBridgeRequest(
                "toggle-field",
                context.TargetPath,
                context.SelectedComponentIndex,
                context.SelectedComponentName,
                field.Name,
                null,
                null));
        AddStream(context, $"{context.PromptLabel} > {input}");
        if (fieldMutation.Ok)
        {
            ReplaceField(context, field with { Value = newValue });
            AddStream(context, $"[=] ok: {field.Name} updated to {newValue}");
            AppendDryRunDiffIfAny(context, fieldMutation.Content);
        }
        else
        {
            AddStream(context, $"[!] {DescribeInspectorMutationFailure(fieldMutation.Message)}");
        }

        _renderer.Render(context);
    }

    private async Task HandleSetAsync(
        string input,
        IReadOnlyList<string> tokens,
        CliSessionState session,
        Action<string> log)
    {
        var context = session.Inspector;
        if (context is null)
        {
            log("[grey]system[/]: set requires inspect mode");
            return;
        }

        if (tokens.Count < 3)
        {
            AddStream(context, $"{context.PromptLabel} > {input}");
            AddStream(context, "[!] usage: set <field> <value...>");
            AddStream(context, "[!] usage (ObjectReference): set <field> --search <query> [--scene|--project]");
            _renderer.Render(context);
            return;
        }

        var fieldName = tokens[1];
        var valueTokens = tokens.Skip(2).ToList();

        if (context.Depth != InspectorDepth.ComponentFields)
        {
            var separatorIndex = fieldName.IndexOf('.');
            if (separatorIndex <= 0 || separatorIndex >= fieldName.Length - 1)
            {
                var resolved = await TryResolveRootFieldByNameAsync(session, context, fieldName);
                if (resolved is null)
                {
                    AddStream(context, $"{context.PromptLabel} > {input}");
                    AddStream(context, "[!] usage at inspector root: set <Component>.<field> <value...>");
                    _renderer.Render(context);
                    return;
                }

                await ApplySetFieldAsync(
                    input,
                    session,
                    context,
                    resolved.Value.Component.Index,
                    resolved.Value.Component.Name,
                    resolved.Value.Field,
                    valueTokens,
                    successLabel: $"{resolved.Value.Component.Name}.{resolved.Value.Field.Name}",
                    updateSelectedComponentFields: false);
                return;
            }

            await PopulateComponentsAsync(context, session, forceRefresh: context.Components.Count == 0);
            var componentToken = fieldName[..separatorIndex];
            var nestedFieldName = fieldName[(separatorIndex + 1)..];
            var component = context.Components.FirstOrDefault(c => c.Name.Equals(componentToken, StringComparison.OrdinalIgnoreCase));
            if (component is null)
            {
                AddStream(context, $"{context.PromptLabel} > {input}");
                AddStream(context, $"[!] unknown component: {componentToken}");
                _renderer.Render(context);
                return;
            }

            var fetchedFields = await TryFetchFieldsFromBridgeAsync(session, context.TargetPath, component.Index);
            if (fetchedFields is null)
            {
                AddStream(context, $"{context.PromptLabel} > {input}");
                AddStream(context, "[!] unable to fetch inspector fields from daemon inspect endpoint (host/stub mode or transport failure)");
                _renderer.Render(context);
                return;
            }

            var fieldEntries = fetchedFields;
            var rootField = fieldEntries.FirstOrDefault(f => f.Name.Equals(nestedFieldName, StringComparison.OrdinalIgnoreCase));
            if (rootField is null)
            {
                AddStream(context, $"{context.PromptLabel} > {input}");
                AddStream(context, $"[!] unknown field: {component.Name}.{nestedFieldName}");
                _renderer.Render(context);
                return;
            }

            await ApplySetFieldAsync(
                input,
                session,
                context,
                component.Index,
                component.Name,
                rootField,
                valueTokens,
                successLabel: $"{component.Name}.{rootField.Name}",
                updateSelectedComponentFields: false);
            return;
        }

        var field = context.Fields.FirstOrDefault(f => f.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase));
        if (field is null)
        {
            AddStream(context, $"{context.PromptLabel} > {input}");
            AddStream(context, $"[!] unknown field: {fieldName}");
            _renderer.Render(context);
            return;
        }

        await ApplySetFieldAsync(
            input,
            session,
            context,
            context.SelectedComponentIndex!.Value,
            context.SelectedComponentName ?? string.Empty,
            field,
            valueTokens,
            successLabel: field.Name,
            updateSelectedComponentFields: true);
    }

    private async Task ApplySetFieldAsync(
        string input,
        CliSessionState session,
        InspectorContext context,
        int componentIndex,
        string componentName,
        InspectorFieldEntry field,
        IReadOnlyList<string> valueTokens,
        string successLabel,
        bool updateSelectedComponentFields)
    {
        if (valueTokens.Count == 0)
        {
            AddStream(context, $"{context.PromptLabel} > {input}");
            AddStream(context, "[!] usage: set <field> <value...>");
            _renderer.Render(context);
            return;
        }

        if (IsObjectReferenceField(field)
            && TryParseReferenceSearchArguments(valueTokens, out var query, out var includeScene, out var includeProject, out var searchError))
        {
            AddStream(context, $"{context.PromptLabel} > {input}");
            if (!string.IsNullOrWhiteSpace(searchError))
            {
                AddStream(context, $"[!] {searchError}");
                _renderer.Render(context);
                return;
            }

            await SearchReferenceCandidatesAsync(
                context,
                session,
                componentIndex,
                componentName,
                field.Name,
                query,
                includeScene,
                includeProject);
            _renderer.Render(context);
            return;
        }

        var newValue = string.Join(' ', valueTokens).Trim();
        if (IsObjectReferenceField(field)
            && TryResolveReferenceSelectionValue(newValue, context.LastReferenceSearchResults, out var resolvedValue, out var selectionError))
        {
            if (!string.IsNullOrWhiteSpace(selectionError))
            {
                AddStream(context, $"{context.PromptLabel} > {input}");
                AddStream(context, $"[!] {selectionError}");
                _renderer.Render(context);
                return;
            }

            newValue = resolvedValue;
        }

        var mutation = await TrySendInspectorMutationWithMessageAsync(
            session,
            new InspectorBridgeRequest(
                "set-field",
                context.TargetPath,
                componentIndex,
                componentName,
                field.Name,
                newValue,
                null));

        AddStream(context, $"{context.PromptLabel} > {input}");
        if (!mutation.Ok)
        {
            AddStream(context, $"[!] {DescribeInspectorMutationFailure(mutation.Message)}");
            _renderer.Render(context);
            return;
        }

        AppendDryRunDiffIfAny(context, mutation.Content);

        if (updateSelectedComponentFields)
        {
            await PopulateFieldsAsync(context, session, componentIndex, forceRefresh: true);
            var appliedValue = context.Fields
                .FirstOrDefault(f => f.Name.Equals(field.Name, StringComparison.OrdinalIgnoreCase))
                ?.Value ?? newValue;
            AddStream(context, $"[=] ok: {successLabel} updated to {appliedValue}");
            _renderer.Render(context);
            return;
        }

        var refreshedFields = await TryFetchFieldsFromBridgeAsync(session, context.TargetPath, componentIndex);
        var refreshedValue = refreshedFields?
            .FirstOrDefault(f => f.Name.Equals(field.Name, StringComparison.OrdinalIgnoreCase))
            ?.Value ?? newValue;
        AddStream(context, $"[=] ok: {successLabel} updated to {refreshedValue}");
        _renderer.Render(context);
    }

    private async Task HandleFuzzyFindAsync(
        string input,
        IReadOnlyList<string> tokens,
        CliSessionState session,
        Action<string> log)
    {
        var context = session.Inspector;
        if (context is null)
        {
            log("[grey]system[/]: fuzzy find requires inspect mode");
            return;
        }

        if (tokens.Count < 2)
        {
            AddStream(context, $"{context.PromptLabel} > {input}");
            AddStream(context, "[!] usage: f <query>");
            _renderer.Render(context);
            return;
        }

        var query = string.Join(' ', tokens.Skip(1));
        var payload = new InspectorBridgeRequest(
            "find",
            context.TargetPath,
            context.SelectedComponentIndex,
            context.SelectedComponentName,
            null,
            null,
            query);
        var responseJson = await SendBridgeRequestAsync(payload, session);

        AddStream(context, $"{context.PromptLabel} > {input}");

        if (!string.IsNullOrWhiteSpace(responseJson))
        {
            try
            {
                var response = JsonSerializer.Deserialize<InspectorBridgeSearchResponse>(responseJson, _jsonOptions);
                if (response?.Ok == true && response.Results is not null)
                {
                    AppendInspectorFuzzyResults(context, query, response.Results.Select(x => x.Path));
                    _renderer.Render(context);
                    return;
                }
            }
            catch
            {
            }
        }

        AddStream(context, "[!] failed to query fuzzy results from daemon inspect endpoint");
        _renderer.Render(context);
    }

    private async Task HandleMakeAsync(
        string input,
        IReadOnlyList<string> tokens,
        CliSessionState session,
        Action<string> log)
    {
        var context = session.Inspector;
        if (context is null)
        {
            log("[grey]system[/]: make requires inspect mode");
            return;
        }

        if (!TryParseInspectorCreateArguments(tokens, out var type, out var count, out var name, out var error))
        {
            AddStream(context, $"{context.PromptLabel} > {input}");
            AddStream(context, $"[!] {error}");
            _renderer.Render(context);
            return;
        }

        var targetNode = await TryResolveHierarchyNodeAsync(session, context.TargetPath, includeRoot: false);
        if (targetNode is null)
        {
            AddStream(context, $"{context.PromptLabel} > {input}");
            AddStream(context, "[!] unable to resolve inspected target in hierarchy");
            _renderer.Render(context);
            return;
        }

        var response = await _hierarchyDaemonClient.ExecuteAsync(
            DaemonControlService.GetPort(session)!.Value,
            new HierarchyCommandRequestDto(
                "mk",
                type.Equals("EmptyParent", StringComparison.OrdinalIgnoreCase) ? null : targetNode.Id,
                type.Equals("EmptyParent", StringComparison.OrdinalIgnoreCase) ? targetNode.Id : null,
                name,
                false,
                type,
                count));

        AddStream(context, $"{context.PromptLabel} > {input}");
        if (!response.Ok)
        {
            AddStream(context, $"[!] {response.Message}");
            _renderer.Render(context);
            return;
        }

        AddStream(context, $"[+] created: {type} x{count}");
        if (CliDryRunDiffService.TryCaptureDiffFromContent(response.Content, out var diff) && diff is not null)
        {
            CliDryRunDiffService.AppendUnifiedDiffToLog(context.CommandStream, diff);
        }
        _renderer.Render(context);
    }

    private async Task HandleComponentAsync(
        string input,
        IReadOnlyList<string> tokens,
        CliSessionState session,
        Action<string> log)
    {
        var context = session.Inspector;
        if (context is null)
        {
            log("[grey]system[/]: component requires inspect mode");
            return;
        }

        if (context.Depth != InspectorDepth.ComponentList)
        {
            AddStream(context, $"{context.PromptLabel} > {input}");
            AddStream(context, "[!] component add/remove is available from the component list view (use up to return)");
            _renderer.Render(context);
            return;
        }

        if (tokens.Count < 2)
        {
            AddStream(context, $"{context.PromptLabel} > {input}");
            AddStream(context, "[!] usage: component <add|remove> <type|index>");
            _renderer.Render(context);
            return;
        }

        var action = tokens[1].ToLowerInvariant();
        switch (action)
        {
            case "add":
                await HandleComponentAddAsync(input, tokens, session, context);
                return;
            case "remove":
            case "rm":
                await HandleComponentRemoveAsync(input, tokens, session, context);
                return;
            default:
                AddStream(context, $"{context.PromptLabel} > {input}");
                AddStream(context, "[!] usage: component <add|remove> <type|index>");
                _renderer.Render(context);
                return;
        }
    }

    private async Task HandleComponentAddAsync(
        string input,
        IReadOnlyList<string> tokens,
        CliSessionState session,
        InspectorContext context)
    {
        if (tokens.Count < 3)
        {
            AddStream(context, $"{context.PromptLabel} > {input}");
            AddStream(context, "[!] usage: component add <component-type>");
            _renderer.Render(context);
            return;
        }

        var rawType = string.Join(' ', tokens.Skip(2)).Trim();
        string displayName;
        string typeReference;
        if (InspectorComponentCatalog.TryResolve(rawType, out displayName, out typeReference, out _))
        {
            // Resolved from static catalog
        }
        else
        {
            // Pass through to daemon for TypeCache resolution
            displayName = rawType;
            typeReference = rawType;
            AddStream(context, $"[*] '{rawType}' is not a built-in type; resolving via TypeCache");
        }

        AddStream(context, $"{context.PromptLabel} > {input}");
        var mutation = await TrySendInspectorMutationWithMessageAsync(
            session,
            new InspectorBridgeRequest(
                "add-component",
                context.TargetPath,
                null,
                typeReference,
                null,
                null,
                null));

        if (!mutation.Ok)
        {
            var detail = string.IsNullOrWhiteSpace(mutation.Message)
                ? "failed to add component via daemon inspect endpoint"
                : DescribeInspectorMutationFailure(mutation.Message);
            AddStream(context, $"[!] {detail}");
            _renderer.Render(context);
            return;
        }

        await PopulateComponentsAsync(context, session, forceRefresh: true);
        AddStream(context, $"[+] added component: {displayName}");
        AppendDryRunDiffIfAny(context, mutation.Content);
        _renderer.Render(context);
    }

    private async Task HandleComponentRemoveAsync(
        string input,
        IReadOnlyList<string> tokens,
        CliSessionState session,
        InspectorContext context)
    {
        if (tokens.Count < 3)
        {
            AddStream(context, $"{context.PromptLabel} > {input}");
            AddStream(context, "[!] usage: component remove <index|component-name>");
            _renderer.Render(context);
            return;
        }

        var selector = string.Join(' ', tokens.Skip(2)).Trim();
        var target = ResolveComponentRemoveTarget(context, selector);
        if (target is null)
        {
            AddStream(context, $"{context.PromptLabel} > {input}");
            AddStream(context, $"[!] no component match for: {selector}");
            AddStream(context, "[i] use component remove <index> to disambiguate");
            _renderer.Render(context);
            return;
        }

        AddStream(context, $"{context.PromptLabel} > {input}");
        var mutation = await TrySendInspectorMutationWithMessageAsync(
            session,
            new InspectorBridgeRequest(
                "remove-component",
                context.TargetPath,
                target.Index,
                target.Name,
                null,
                null,
                null));

        if (!mutation.Ok)
        {
            var detail = string.IsNullOrWhiteSpace(mutation.Message)
                ? "failed to remove component via daemon inspect endpoint"
                : DescribeInspectorMutationFailure(mutation.Message);
            AddStream(context, $"[!] {detail}");
            _renderer.Render(context);
            return;
        }

        await PopulateComponentsAsync(context, session, forceRefresh: true);
        AddStream(context, $"[-] removed component: {target.Name}");
        AppendDryRunDiffIfAny(context, mutation.Content);
        _renderer.Render(context);
    }

    private async Task HandleRemoveAsync(
        string input,
        CliSessionState session,
        Action<string> log)
    {
        var context = session.Inspector;
        if (context is null)
        {
            log("[grey]system[/]: remove requires inspect mode");
            return;
        }

        var targetNode = await TryResolveHierarchyNodeAsync(session, context.TargetPath, includeRoot: false);
        if (targetNode is null)
        {
            AddStream(context, $"{context.PromptLabel} > {input}");
            AddStream(context, "[!] unable to resolve inspected target in hierarchy");
            _renderer.Render(context);
            return;
        }

        var response = await _hierarchyDaemonClient.ExecuteAsync(
            DaemonControlService.GetPort(session)!.Value,
            new HierarchyCommandRequestDto("rm", null, targetNode.Id, null, false));

        AddStream(context, $"{context.PromptLabel} > {input}");
        if (!response.Ok)
        {
            AddStream(context, $"[!] {response.Message}");
            _renderer.Render(context);
            return;
        }

        AddStream(context, $"[-] removed target: {targetNode.Name}");
        if (CliDryRunDiffService.TryCaptureDiffFromContent(response.Content, out var diff) && diff is not null)
        {
            CliDryRunDiffService.AppendUnifiedDiffToLog(context.CommandStream, diff);
            _renderer.Render(context);
            return;
        }
        session.Inspector = null;
        session.ContextMode = CliContextMode.Project;
        log("[i] inspector target removed; returned to project context");
    }

    private async Task HandleRenameAsync(
        string input,
        IReadOnlyList<string> tokens,
        CliSessionState session,
        Action<string> log)
    {
        var context = session.Inspector;
        if (context is null)
        {
            log("[grey]system[/]: rename requires inspect mode");
            return;
        }

        if (tokens.Count < 2)
        {
            AddStream(context, $"{context.PromptLabel} > {input}");
            AddStream(context, "[!] usage: rename <new-name>");
            _renderer.Render(context);
            return;
        }

        var targetNode = await TryResolveHierarchyNodeAsync(session, context.TargetPath, includeRoot: false);
        if (targetNode is null)
        {
            AddStream(context, $"{context.PromptLabel} > {input}");
            AddStream(context, "[!] unable to resolve inspected target in hierarchy");
            _renderer.Render(context);
            return;
        }

        var newName = string.Join(' ', tokens.Skip(1)).Trim();
        if (string.IsNullOrWhiteSpace(newName))
        {
            AddStream(context, $"{context.PromptLabel} > {input}");
            AddStream(context, "[!] usage: rename <new-name>");
            _renderer.Render(context);
            return;
        }

        var response = await _hierarchyDaemonClient.ExecuteAsync(
            DaemonControlService.GetPort(session)!.Value,
            new HierarchyCommandRequestDto("rename", null, targetNode.Id, newName, false));

        AddStream(context, $"{context.PromptLabel} > {input}");
        if (!response.Ok)
        {
            AddStream(context, $"[!] {response.Message}");
            _renderer.Render(context);
            return;
        }

        context.TargetPath = ReplaceLeafSegment(context.TargetPath, newName);
        session.FocusPath = context.TargetPath;
        AddStream(context, $"[=] renamed target to: {newName}");
        if (CliDryRunDiffService.TryCaptureDiffFromContent(response.Content, out var diff) && diff is not null)
        {
            CliDryRunDiffService.AppendUnifiedDiffToLog(context.CommandStream, diff);
        }
        _renderer.Render(context);
    }

    private async Task HandleMoveAsync(
        string input,
        IReadOnlyList<string> tokens,
        CliSessionState session,
        Action<string> log)
    {
        var context = session.Inspector;
        if (context is null)
        {
            log("[grey]system[/]: move requires inspect mode");
            return;
        }

        if (tokens.Count < 2)
        {
            AddStream(context, $"{context.PromptLabel} > {input}");
            AddStream(context, "[!] usage: move </path|..|/>");
            _renderer.Render(context);
            return;
        }

        var targetNode = await TryResolveHierarchyNodeAsync(session, context.TargetPath, includeRoot: false);
        if (targetNode is null)
        {
            AddStream(context, $"{context.PromptLabel} > {input}");
            AddStream(context, "[!] unable to resolve inspected target in hierarchy");
            _renderer.Render(context);
            return;
        }

        var destinationToken = string.Join(' ', tokens.Skip(1)).Trim();
        if (string.IsNullOrWhiteSpace(destinationToken))
        {
            AddStream(context, $"{context.PromptLabel} > {input}");
            AddStream(context, "[!] usage: move </path|..|/>");
            _renderer.Render(context);
            return;
        }

        var parentPath = ResolveMoveDestinationPath(context.TargetPath, destinationToken);
        int parentId;
        if (parentPath == "/")
        {
            parentId = SceneRootNodeId;
        }
        else
        {
            var parentNode = await TryResolveHierarchyNodeAsync(session, parentPath, includeRoot: false);
            if (parentNode is null)
            {
                AddStream(context, $"{context.PromptLabel} > {input}");
                AddStream(context, $"[!] destination not found: {parentPath}");
                _renderer.Render(context);
                return;
            }

            parentId = parentNode.Id;
        }

        var response = await _hierarchyDaemonClient.ExecuteAsync(
            DaemonControlService.GetPort(session)!.Value,
            new HierarchyCommandRequestDto("mv", parentId, targetNode.Id, null, false));

        AddStream(context, $"{context.PromptLabel} > {input}");
        if (!response.Ok)
        {
            AddStream(context, $"[!] {response.Message}");
            _renderer.Render(context);
            return;
        }

        var movedPath = parentPath == "/"
            ? "/" + targetNode.Name
            : $"{parentPath.TrimEnd('/')}/{targetNode.Name}";
        context.TargetPath = movedPath;
        session.FocusPath = movedPath;
        AddStream(context, $"[*] moved target to: {movedPath}");
        if (CliDryRunDiffService.TryCaptureDiffFromContent(response.Content, out var diff) && diff is not null)
        {
            CliDryRunDiffService.AppendUnifiedDiffToLog(context.CommandStream, diff);
        }
        _renderer.Render(context);
    }

    private void HandleScroll(
        string input,
        IReadOnlyList<string> tokens,
        CliSessionState session,
        Action<string> log)
    {
        var context = session.Inspector;
        if (context is null)
        {
            log("[grey]system[/]: scroll requires inspect mode");
            return;
        }

        if (tokens.Count < 2)
        {
            AddStream(context, $"{context.PromptLabel} > {input}");
            AddStream(context, "[!] usage: scroll [body|stream] <up|down> [count]");
            _renderer.Render(context);
            return;
        }

        var section = "body";
        var directionTokenIndex = 1;
        if (tokens[1].Equals("body", StringComparison.OrdinalIgnoreCase)
            || tokens[1].Equals("stream", StringComparison.OrdinalIgnoreCase))
        {
            section = tokens[1].ToLowerInvariant();
            directionTokenIndex = 2;
        }

        if (tokens.Count <= directionTokenIndex)
        {
            AddStream(context, $"{context.PromptLabel} > {input}");
            AddStream(context, "[!] usage: scroll [body|stream] <up|down> [count]");
            _renderer.Render(context);
            return;
        }

        var direction = tokens[directionTokenIndex].ToLowerInvariant();
        var amount = 1;
        if (tokens.Count > directionTokenIndex + 1
            && (!int.TryParse(tokens[directionTokenIndex + 1], out amount) || amount <= 0))
        {
            AddStream(context, $"{context.PromptLabel} > {input}");
            AddStream(context, "[!] count must be a positive integer");
            _renderer.Render(context);
            return;
        }

        if (direction is not ("up" or "down"))
        {
            AddStream(context, $"{context.PromptLabel} > {input}");
            AddStream(context, "[!] direction must be up or down");
            _renderer.Render(context);
            return;
        }

        var delta = direction == "up" ? -amount : amount;
        if (section == "stream")
        {
            var wrappedStreamCount = BuildWrappedStreamRows(context).Count;
            if (context.FollowStreamScroll)
            {
                context.StreamScrollOffset = Math.Max(0, wrappedStreamCount - 1);
            }

            context.FollowStreamScroll = false;
            context.StreamScrollOffset += delta;
            if (context.StreamScrollOffset >= wrappedStreamCount)
            {
                context.FollowStreamScroll = true;
                context.StreamScrollOffset = int.MaxValue;
            }
        }
        else
        {
            context.BodyScrollOffset += delta;
        }

        AddStream(context, $"{context.PromptLabel} > {input}");
        _renderer.Render(context);
    }

    private async Task<InspectorComponentFetchResult> PopulateComponentsAsync(
        InspectorContext context,
        CliSessionState session,
        bool forceRefresh)
    {
        if (!forceRefresh && context.Components.Count > 0)
        {
            return new InspectorComponentFetchResult(
                true,
                context.Components.ToList(),
                null,
                null,
                null);
        }

        var fromBridge = await TryFetchComponentsFromBridgeAsync(session, context.TargetPath);
        context.Components.Clear();
        if (fromBridge.Components.Count > 0)
        {
            context.Components.AddRange(fromBridge.Components);
        }

        return fromBridge;
    }

    private async Task PopulateFieldsAsync(
        InspectorContext context,
        CliSessionState session,
        int componentIndex,
        bool forceRefresh)
    {
        if (!forceRefresh && context.Fields.Count > 0 && context.SelectedComponentIndex == componentIndex)
        {
            return;
        }

        var fromBridge = await TryFetchFieldsFromBridgeAsync(session, context.TargetPath, componentIndex);
        context.Fields.Clear();
        if (fromBridge is not null)
        {
            context.Fields.AddRange(fromBridge);
        }
    }

    private static bool TryParseReferenceSearchArguments(
        IReadOnlyList<string> valueTokens,
        out string query,
        out bool includeScene,
        out bool includeProject,
        out string? error)
    {
        query = string.Empty;
        includeScene = false;
        includeProject = false;
        error = null;
        if (valueTokens.Count == 0
            || !valueTokens[0].Equals("--search", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var queryTokens = new List<string>();
        for (var i = 1; i < valueTokens.Count; i++)
        {
            var token = valueTokens[i];
            if (token.Equals("--scene", StringComparison.OrdinalIgnoreCase))
            {
                includeScene = true;
                continue;
            }

            if (token.Equals("--project", StringComparison.OrdinalIgnoreCase))
            {
                includeProject = true;
                continue;
            }

            queryTokens.Add(token);
        }

        if (!includeScene && !includeProject)
        {
            includeScene = true;
            includeProject = true;
        }

        query = string.Join(' ', queryTokens).Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            error = "usage: set <field> --search <query> [--scene|--project]";
        }

        return true;
    }

    private static bool TryResolveReferenceSelectionValue(
        string rawValue,
        IReadOnlyList<InspectorReferenceSearchEntry> searchResults,
        out string resolvedValue,
        out string? error)
    {
        resolvedValue = rawValue;
        error = null;
        if (!rawValue.StartsWith("@", StringComparison.Ordinal))
        {
            return false;
        }

        if (!int.TryParse(rawValue[1..], out var index) || index < 0 || index >= searchResults.Count)
        {
            error = "invalid reference index; run set <field> --search <query> first, then use @<index>";
            return true;
        }

        resolvedValue = searchResults[index].ValueToken;
        return true;
    }

    private static bool IsObjectReferenceField(InspectorFieldEntry field)
        => field.Type.Equals("ObjectReference", StringComparison.OrdinalIgnoreCase);

    private static void AppendInspectorReferenceSearchResults(
        InspectorContext context,
        string query,
        bool includeScene,
        bool includeProject,
        IReadOnlyList<InspectorReferenceSearchEntry> results)
    {
        if (results.Count == 0)
        {
            AddStream(context, $"[x] no reference results for: {query}");
            return;
        }

        var scopeLabel = includeScene && includeProject
            ? "scene+project"
            : includeScene
                ? "scene"
                : "project";
        const int maxLines = 10;
        AddStream(context, $"[*] reference results ({scopeLabel}) for: {query}");
        var shown = Math.Min(maxLines, results.Count);
        for (var i = 0; i < shown; i++)
        {
            var result = results[i];
            AddStream(context, $"[{i}] [{result.Scope}] {result.Path}");
        }

        if (results.Count > shown)
        {
            AddStream(context, $"[i] showing first {shown}/{results.Count} results");
        }

        AddStream(context, "[i] use set <field> @<index> to assign a listed reference");
    }

    private static bool TryParseInspectorCreateArguments(
        IReadOnlyList<string> tokens,
        out string type,
        out int count,
        out string? name,
        out string error)
    {
        type = string.Empty;
        count = 1;
        name = null;
        error = "usage: make --type <type> [--count <count>] | mk <type> [count] [--name <name>|-n <name>]";
        if (tokens.Count == 0)
        {
            return false;
        }

        if (tokens[0].Equals("make", StringComparison.OrdinalIgnoreCase)
            || (tokens[0].Equals("mk", StringComparison.OrdinalIgnoreCase) && tokens.Count >= 2 && tokens[1].StartsWith("--", StringComparison.Ordinal)))
        {
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

    private static void AddStream(InspectorContext context, string line)
    {
        context.CommandStream.Add(line);
        if (context.CommandStream.Count > 200)
        {
            context.CommandStream.RemoveRange(0, context.CommandStream.Count - 200);
        }

        if (context.FollowStreamScroll)
        {
            context.StreamScrollOffset = int.MaxValue;
        }
    }

    private static void AppendDryRunDiffIfAny(InspectorContext context, string? content)
    {
        if (!CliDryRunDiffService.TryCaptureDiffFromContent(content, out var diff) || diff is null)
        {
            return;
        }

        CliDryRunDiffService.AppendUnifiedDiffToLog(context.CommandStream, diff);
    }

    private static void AppendInspectorFuzzyResults(InspectorContext context, string query, IEnumerable<string> paths)
    {
        var buffered = paths.ToList();
        if (buffered.Count == 0)
        {
            AddStream(context, $"[x] no fuzzy results for: {query}");
            return;
        }

        const int maxLines = 10;
        AddStream(context, $"[*] fuzzy results for: {query}");
        var shown = Math.Min(maxLines, buffered.Count);
        for (var i = 0; i < shown; i++)
        {
            AddStream(context, $"[{i}] {buffered[i]}");
        }

        if (buffered.Count > shown)
        {
            AddStream(context, $"[i] showing first {shown}/{buffered.Count} results");
        }
    }

    private static List<string> BuildWrappedStreamRows(InspectorContext context)
    {
        var contentWidth = Math.Max(1, ResolveInnerWidth() - 1);
        return context.CommandStream
            .Where(line => !line.StartsWith("UnityCLI:", StringComparison.OrdinalIgnoreCase))
            .SelectMany(line => TuiTextWrap.WrapPlainText(line, contentWidth))
            .ToList();
    }

    private static int ResolveInnerWidth()
    {
        var windowWidth = Console.WindowWidth;
        if (windowWidth <= 2)
        {
            return DefaultInnerWidth;
        }

        return Math.Max(MinInnerWidth, windowWidth - 2);
    }

    private sealed record InspectorBridgeRequest(
        string Action,
        string TargetPath,
        int? ComponentIndex,
        string? ComponentName,
        string? FieldName,
        string? Value,
        string? Query,
        bool IncludeSceneReferences = true,
        bool IncludeProjectReferences = true,
        MutationIntentDto? Intent = null);

    private sealed record InspectorBridgeComponentsResponse(bool Ok, List<InspectorBridgeComponent>? Components);
    private sealed record InspectorBridgeFieldsResponse(bool Ok, List<InspectorBridgeField>? Fields);
    private sealed record InspectorBridgeSearchResponse(bool Ok, List<InspectorSearchResultDto>? Results);
    private sealed record InspectorBridgeMutationResponse(bool Ok, string? Message, string? Content);
    private sealed record InspectorBridgeComponent(int Index, string Name, bool Enabled);
    private sealed record InspectorBridgeField(string Name, string Value, string Type, bool IsBoolean, List<string>? EnumOptions);
    private sealed record InspectorComponentFetchResult(
        bool Success,
        List<InspectorComponentEntry> Components,
        string? FailureReason,
        bool? BridgeOk,
        string? RawPayload);
}
