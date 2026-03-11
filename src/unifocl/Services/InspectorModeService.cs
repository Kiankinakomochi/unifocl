using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Globalization;
using Spectre.Console;

internal sealed class InspectorModeService
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

            var intent = KeyboardIntentReader.ReadIntent();
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

        var target = argument.StartsWith('/') ? argument : "/" + argument;
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
                AddStream(context, $"{context.PromptLabel} > {input}");
                AddStream(context, "[!] usage at inspector root: set <Component>.<field> <value...>");
                _renderer.Render(context);
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

    private async Task SearchReferenceCandidatesAsync(
        InspectorContext context,
        CliSessionState session,
        int componentIndex,
        string componentName,
        string fieldName,
        string query,
        bool includeScene,
        bool includeProject)
    {
        var payload = new InspectorBridgeRequest(
            "find-reference",
            context.TargetPath,
            componentIndex,
            componentName,
            fieldName,
            null,
            query,
            includeScene,
            includeProject);
        var responseJson = await SendBridgeRequestAsync(payload, session);
        if (string.IsNullOrWhiteSpace(responseJson))
        {
            AddStream(context, "[!] failed to query reference candidates from Bridge mode");
            return;
        }

        try
        {
            var response = JsonSerializer.Deserialize<InspectorBridgeSearchResponse>(responseJson, _jsonOptions);
            if (response?.Ok != true || response.Results is null)
            {
                AddStream(context, "[!] failed to query reference candidates from Bridge mode");
                return;
            }

            var referenceResults = response.Results
                .Where(result => !string.IsNullOrWhiteSpace(result.Path) && !string.IsNullOrWhiteSpace(result.ValueToken))
                .Select(result => new InspectorReferenceSearchEntry(
                    string.IsNullOrWhiteSpace(result.Scope) ? "reference" : result.Scope,
                    result.Path,
                    result.ValueToken!))
                .ToList();

            context.LastReferenceSearchResults.Clear();
            context.LastReferenceSearchResults.AddRange(referenceResults);
            AppendInspectorReferenceSearchResults(context, query, includeScene, includeProject, referenceResults);
        }
        catch
        {
            AddStream(context, "[!] failed to parse reference candidates from Bridge mode");
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
            session.AttachedPort!.Value,
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
        if (!InspectorComponentCatalog.TryResolve(rawType, out var displayName, out var typeReference, out var error))
        {
            AddStream(context, $"{context.PromptLabel} > {input}");
            AddStream(context, $"[!] {error}");
            AddStream(context, "[i] use fuzzy suggestions: component add <partial-name>");
            _renderer.Render(context);
            return;
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
            session.AttachedPort!.Value,
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
            session.AttachedPort!.Value,
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
            session.AttachedPort!.Value,
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
        if (session.AttachedPort is null)
        {
            return;
        }

        var deadline = DateTime.UtcNow.AddSeconds(12);
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = await _hierarchyDaemonClient.GetSnapshotAsync(session.AttachedPort.Value);
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

    private async Task RunInteractiveFieldEditAsync(
        InspectorContext context,
        CliSessionState session,
        InspectorFieldEntry field)
    {
        if (context.SelectedComponentIndex is null)
        {
            AddStream(context, "[!] no selected component for interactive edit");
            _renderer.Render(context, null, field.Name, focusModeEnabled: true);
            return;
        }

        if (TryParseVectorValues(field.Value, out var vectorValues))
        {
            await RunVectorFieldEditAsync(context, session, field, vectorValues);
            return;
        }

        if (field.Type.Equals("String", StringComparison.OrdinalIgnoreCase))
        {
            await RunTextFieldEditAsync(context, session, field);
            return;
        }

        if (IsObjectReferenceField(field))
        {
            await RunObjectReferenceFieldEditAsync(context, session, field);
            return;
        }

        if (IsNumericFieldType(field.Type))
        {
            await RunNumericFieldEditAsync(context, session, field);
            return;
        }

        if (field.IsBoolean)
        {
            await RunEnumFieldEditAsync(context, session, field, ["false", "true"], "bool");
            return;
        }

        var enumOptions = field.EnumOptions?.Where(option => !string.IsNullOrWhiteSpace(option)).Distinct(StringComparer.Ordinal).ToList() ?? [];
        if (enumOptions.Count > 0)
        {
            await RunEnumFieldEditAsync(context, session, field, enumOptions, "enum");
            return;
        }

        AddStream(context, $"[i] interactive edit not available for {field.Type}; use set/edit command");
        _renderer.Render(context, null, field.Name, focusModeEnabled: true);
    }

    private async Task RunObjectReferenceFieldEditAsync(
        InspectorContext context,
        CliSessionState session,
        InspectorFieldEntry field)
    {
        if (context.SelectedComponentIndex is null || string.IsNullOrWhiteSpace(context.SelectedComponentName))
        {
            AddStream(context, "[!] no selected component for reference edit");
            _renderer.Render(context, null, field.Name, focusModeEnabled: true);
            return;
        }

        var query = string.Empty;
        var includeScene = true;
        var includeProject = true;
        var selectedIndex = 0;
        AddStream(context, "[i] reference edit: type query, Up/Down select, Enter apply, Tab scope(scene/project/all), Esc cancel");
        BeginInteractiveEdit(context, field.Name, "reference", 0, 0);

        try
        {
            while (true)
            {
                var results = await FetchReferenceSearchEntriesAsync(
                    context,
                    session,
                    context.SelectedComponentIndex.Value,
                    context.SelectedComponentName,
                    field.Name,
                    query,
                    includeScene,
                    includeProject);

                context.LastReferenceSearchResults.Clear();
                context.LastReferenceSearchResults.AddRange(results);
                selectedIndex = results.Count == 0
                    ? 0
                    : Math.Clamp(selectedIndex, 0, Math.Min(7, results.Count - 1));
                context.InteractiveSearchPreviewRows.Clear();
                context.InteractiveSearchPreviewRows.AddRange(
                    BuildReferencePreviewRows(query, includeScene, includeProject, results, selectedIndex));
                _renderer.Render(context, null, field.Name, focusModeEnabled: true);

                var key = Console.ReadKey(intercept: true);
                switch (key.Key)
                {
                    case ConsoleKey.Escape:
                        context.InteractiveSearchPreviewRows.Clear();
                        AddStream(context, "[i] reference edit cancelled");
                        _renderer.Render(context, null, field.Name, focusModeEnabled: true);
                        return;

                    case ConsoleKey.Enter:
                        if (results.Count == 0)
                        {
                            AddStream(context, "[!] no reference result selected");
                            continue;
                        }

                        var selected = results[Math.Clamp(selectedIndex, 0, results.Count - 1)];
                        if (await ApplyInteractiveFieldValueAsync(context, session, field, selected.ValueToken))
                        {
                            context.InteractiveSearchPreviewRows.Clear();
                            return;
                        }

                        context.InteractiveSearchPreviewRows.Clear();
                        _renderer.Render(context, null, field.Name, focusModeEnabled: true);
                        return;

                    case ConsoleKey.UpArrow:
                        if (results.Count > 0)
                        {
                            var cap = Math.Min(8, results.Count);
                            selectedIndex = selectedIndex <= 0 ? cap - 1 : selectedIndex - 1;
                        }

                        break;

                    case ConsoleKey.DownArrow:
                        if (results.Count > 0)
                        {
                            var cap = Math.Min(8, results.Count);
                            selectedIndex = (selectedIndex + 1) % cap;
                        }

                        break;

                    case ConsoleKey.Tab:
                        if (includeScene && includeProject)
                        {
                            includeScene = true;
                            includeProject = false;
                        }
                        else if (includeScene)
                        {
                            includeScene = false;
                            includeProject = true;
                        }
                        else
                        {
                            includeScene = true;
                            includeProject = true;
                        }

                        selectedIndex = 0;
                        break;

                    case ConsoleKey.Backspace:
                        if (query.Length > 0)
                        {
                            query = query[..^1];
                            selectedIndex = 0;
                        }

                        break;

                    default:
                        if (!char.IsControl(key.KeyChar))
                        {
                            query += key.KeyChar;
                            selectedIndex = 0;
                        }

                        break;
                }
            }
        }
        finally
        {
            context.InteractiveSearchPreviewRows.Clear();
            EndInteractiveEdit(context);
        }
    }

    private async Task<List<InspectorReferenceSearchEntry>> FetchReferenceSearchEntriesAsync(
        InspectorContext context,
        CliSessionState session,
        int componentIndex,
        string componentName,
        string fieldName,
        string query,
        bool includeScene,
        bool includeProject)
    {
        var payload = new InspectorBridgeRequest(
            "find-reference",
            context.TargetPath,
            componentIndex,
            componentName,
            fieldName,
            null,
            query,
            includeScene,
            includeProject);
        var responseJson = await SendBridgeRequestAsync(payload, session);
        if (string.IsNullOrWhiteSpace(responseJson))
        {
            return [];
        }

        try
        {
            var response = JsonSerializer.Deserialize<InspectorBridgeSearchResponse>(responseJson, _jsonOptions);
            if (response?.Ok != true || response.Results is null)
            {
                return [];
            }

            return response.Results
                .Where(result => !string.IsNullOrWhiteSpace(result.Path) && !string.IsNullOrWhiteSpace(result.ValueToken))
                .Select(result => new InspectorReferenceSearchEntry(
                    string.IsNullOrWhiteSpace(result.Scope) ? "reference" : result.Scope,
                    result.Path,
                    result.ValueToken!))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static IReadOnlyList<string> BuildReferencePreviewRows(
        string query,
        bool includeScene,
        bool includeProject,
        IReadOnlyList<InspectorReferenceSearchEntry> results,
        int selectedIndex)
    {
        var scopeLabel = includeScene && includeProject
            ? "all"
            : includeScene
                ? "scene"
                : "project";
        var lines = new List<string>
        {
            $"[grey]reference fuzzy[/] | query: {Markup.Escape(query)} | scope: {Markup.Escape(scopeLabel)}"
        };

        if (results.Count == 0)
        {
            lines.Add("no matches");
            lines.Add("[grey]type to search references[/]");
            return lines;
        }

        var shown = Math.Min(8, results.Count);
        for (var i = 0; i < shown; i++)
        {
            var result = results[i];
            var indexCell = Markup.Escape($"[{i}]");
            var scopeCell = Markup.Escape($"[{result.Scope}]");
            var escapedScope = Markup.Escape(result.Scope);
            var escapedPath = Markup.Escape(result.Path);
            if (i == selectedIndex)
            {
                lines.Add(
                    $"[{CliTheme.CursorForeground} on {CliTheme.CursorBackground}]▸ {indexCell} {scopeCell} {escapedPath}[/]");
                continue;
            }

            lines.Add($"[grey] {indexCell} {scopeCell} {escapedPath}[/]");
        }

        if (results.Count > shown)
        {
            lines.Add($"... {results.Count - shown} more");
        }

        lines.Add("[grey]Enter apply | Tab scope | Esc cancel[/]");
        return lines;
    }

    private async Task RunVectorFieldEditAsync(
        InspectorContext context,
        CliSessionState session,
        InspectorFieldEntry field,
        List<float> values)
    {
        var original = field;
        var selectedIndex = 0;
        var componentTexts = values
            .Select(value => value.ToString("0.###", CultureInfo.InvariantCulture))
            .ToList();
        AddStream(context, "[i] vector edit: type number, Backspace/Delete edit, Tab next component, Left/Right adjust, Enter apply, Esc cancel");
        BeginInteractiveEdit(context, field.Name, "vector", selectedIndex, values.Count);

        try
        {
            while (true)
            {
                UpdateInteractiveEditCursor(context, selectedIndex, componentTexts.Count);
                var preview = FormatVector(componentTexts);
                ReplaceField(context, field with { Value = preview });
                _renderer.Render(context, null, field.Name, focusModeEnabled: true);

                var key = Console.ReadKey(intercept: true);
                switch (key.Key)
                {
                    case ConsoleKey.Tab:
                        selectedIndex = (selectedIndex + 1) % componentTexts.Count;
                        break;
                    case ConsoleKey.LeftArrow:
                        if (TryParseFloatInvariant(componentTexts[selectedIndex], out var leftValue))
                        {
                            leftValue -= key.Modifiers.HasFlag(ConsoleModifiers.Shift) ? 1f : 0.1f;
                            componentTexts[selectedIndex] = leftValue.ToString("0.###", CultureInfo.InvariantCulture);
                        }

                        break;
                    case ConsoleKey.RightArrow:
                        if (TryParseFloatInvariant(componentTexts[selectedIndex], out var rightValue))
                        {
                            rightValue += key.Modifiers.HasFlag(ConsoleModifiers.Shift) ? 1f : 0.1f;
                            componentTexts[selectedIndex] = rightValue.ToString("0.###", CultureInfo.InvariantCulture);
                        }

                        break;
                    case ConsoleKey.Backspace:
                        if (componentTexts[selectedIndex].Length > 0)
                        {
                            componentTexts[selectedIndex] = componentTexts[selectedIndex][..^1];
                        }

                        break;
                    case ConsoleKey.Delete:
                        componentTexts[selectedIndex] = string.Empty;
                        break;
                    case ConsoleKey.Enter:
                        if (!TryValidateVectorComponentTexts(componentTexts))
                        {
                            AddStream(context, "[!] invalid numeric input in vector components");
                            break;
                        }

                        if (await ApplyInteractiveFieldValueAsync(context, session, field, preview))
                        {
                            return;
                        }

                        ReplaceField(context, original);
                        _renderer.Render(context, null, field.Name, focusModeEnabled: true);
                        return;
                    case ConsoleKey.Escape:
                        ReplaceField(context, original);
                        AddStream(context, "[i] vector edit cancelled");
                        _renderer.Render(context, null, field.Name, focusModeEnabled: true);
                        return;
                    default:
                        if (IsAllowedNumericInputChar(key.KeyChar, "Float"))
                        {
                            componentTexts[selectedIndex] += key.KeyChar;
                        }

                        break;
                }
            }
        }
        finally
        {
            EndInteractiveEdit(context);
        }
    }

    private async Task RunEnumFieldEditAsync(
        InspectorContext context,
        CliSessionState session,
        InspectorFieldEntry field,
        IReadOnlyList<string> enumOptions,
        string mode)
    {
        var original = field;
        var selectedIndex = Math.Max(0, enumOptions.ToList().FindIndex(option => option.Equals(field.Value, StringComparison.OrdinalIgnoreCase)));
        AddStream(context, mode == "bool"
            ? "[i] bool edit: Enter apply, Esc cancel, Left/Right/Tab cycle values"
            : "[i] enum edit: Enter apply, Esc cancel, Left/Right/Tab cycle options");
        AddStream(context, $"[i] {mode} options: {string.Join(" | ", enumOptions)}");
        BeginInteractiveEdit(context, field.Name, mode, selectedIndex, enumOptions.Count);

        try
        {
            while (true)
            {
                UpdateInteractiveEditCursor(context, selectedIndex, enumOptions.Count);
                var preview = enumOptions[selectedIndex];
                ReplaceField(context, field with { Value = preview });
                _renderer.Render(context, null, field.Name, focusModeEnabled: true);

                var key = Console.ReadKey(intercept: true);
                switch (key.Key)
                {
                    case ConsoleKey.Tab:
                    case ConsoleKey.RightArrow:
                        selectedIndex = (selectedIndex + 1) % enumOptions.Count;
                        break;
                    case ConsoleKey.LeftArrow:
                        selectedIndex = selectedIndex <= 0 ? enumOptions.Count - 1 : selectedIndex - 1;
                        break;
                    case ConsoleKey.Enter:
                        if (await ApplyInteractiveFieldValueAsync(context, session, field, preview))
                        {
                            return;
                        }

                        ReplaceField(context, original);
                        _renderer.Render(context, null, field.Name, focusModeEnabled: true);
                        return;
                    case ConsoleKey.Escape:
                        ReplaceField(context, original);
                        AddStream(context, "[i] enum edit cancelled");
                        _renderer.Render(context, null, field.Name, focusModeEnabled: true);
                        return;
                    default:
                        break;
                }
            }
        }
        finally
        {
            EndInteractiveEdit(context);
        }
    }

    private async Task RunNumericFieldEditAsync(
        InspectorContext context,
        CliSessionState session,
        InspectorFieldEntry field)
    {
        var original = field;
        var buffer = field.Value;
        AddStream(context, "[i] numeric edit: type value, Enter apply, Esc cancel, Backspace delete");
        BeginInteractiveEdit(context, field.Name, "number", 0, 1);

        try
        {
            while (true)
            {
                ReplaceField(context, field with { Value = buffer });
                _renderer.Render(context, null, field.Name, focusModeEnabled: true);

                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Escape)
                {
                    ReplaceField(context, original);
                    AddStream(context, "[i] numeric edit cancelled");
                    _renderer.Render(context, null, field.Name, focusModeEnabled: true);
                    return;
                }

                if (key.Key == ConsoleKey.Enter)
                {
                    if (!IsValidNumericText(field.Type, buffer))
                    {
                        AddStream(context, $"[!] invalid {field.Type.ToLowerInvariant()} value: {buffer}");
                        continue;
                    }

                    if (await ApplyInteractiveFieldValueAsync(context, session, field, buffer))
                    {
                        return;
                    }

                    ReplaceField(context, original);
                    _renderer.Render(context, null, field.Name, focusModeEnabled: true);
                    return;
                }

                if (key.Key == ConsoleKey.Backspace)
                {
                    if (buffer.Length > 0)
                    {
                        buffer = buffer[..^1];
                    }

                    continue;
                }

                if (IsAllowedNumericInputChar(key.KeyChar, field.Type))
                {
                    buffer += key.KeyChar;
                }
            }
        }
        finally
        {
            EndInteractiveEdit(context);
        }
    }

    private async Task RunTextFieldEditAsync(
        InspectorContext context,
        CliSessionState session,
        InspectorFieldEntry field)
    {
        var original = field;
        var buffer = field.Value;
        var cursor = buffer.Length;
        AddStream(context, "[i] text edit: type value, Left/Right move cursor, Enter apply, Esc cancel, Backspace/Delete edit");
        BeginInteractiveEdit(context, field.Name, "text", 0, 1);

        try
        {
            while (true)
            {
                SetInteractiveOverlay(context, field.Name, BuildTextOverlayValue(buffer, cursor));
                ReplaceField(context, field with { Value = buffer });
                _renderer.Render(context, null, field.Name, focusModeEnabled: true);

                var key = Console.ReadKey(intercept: true);
                switch (key.Key)
                {
                    case ConsoleKey.Escape:
                        ReplaceField(context, original);
                        AddStream(context, "[i] text edit cancelled");
                        _renderer.Render(context, null, field.Name, focusModeEnabled: true);
                        return;
                    case ConsoleKey.Enter:
                        if (await ApplyInteractiveFieldValueAsync(context, session, field, buffer))
                        {
                            return;
                        }

                        ReplaceField(context, original);
                        _renderer.Render(context, null, field.Name, focusModeEnabled: true);
                        return;
                    case ConsoleKey.LeftArrow:
                        if (cursor > 0)
                        {
                            cursor--;
                        }

                        break;
                    case ConsoleKey.RightArrow:
                        if (cursor < buffer.Length)
                        {
                            cursor++;
                        }

                        break;
                    case ConsoleKey.Backspace:
                        if (cursor > 0)
                        {
                            buffer = buffer.Remove(cursor - 1, 1);
                            cursor--;
                        }

                        break;
                    case ConsoleKey.Delete:
                        if (cursor < buffer.Length)
                        {
                            buffer = buffer.Remove(cursor, 1);
                        }

                        break;
                    default:
                        if (!char.IsControl(key.KeyChar))
                        {
                            buffer = buffer.Insert(cursor, key.KeyChar.ToString());
                            cursor++;
                        }

                        break;
                }
            }
        }
        finally
        {
            EndInteractiveEdit(context);
        }
    }

    private static void BeginInteractiveEdit(
        InspectorContext context,
        string fieldName,
        string mode,
        int partIndex,
        int partCount)
    {
        context.InteractiveEditActive = true;
        context.InteractiveEditFieldName = fieldName;
        context.InteractiveEditMode = mode;
        context.InteractiveEditPartIndex = partIndex;
        context.InteractiveEditPartCount = partCount;
        context.InteractiveOverlayActive = false;
        context.InteractiveOverlayTitle = null;
        context.InteractiveOverlayValue = null;
    }

    private static void UpdateInteractiveEditCursor(InspectorContext context, int partIndex, int partCount)
    {
        context.InteractiveEditPartIndex = partIndex;
        context.InteractiveEditPartCount = partCount;
    }

    private static void EndInteractiveEdit(InspectorContext context)
    {
        context.InteractiveEditActive = false;
        context.InteractiveEditFieldName = null;
        context.InteractiveEditMode = null;
        context.InteractiveEditPartIndex = 0;
        context.InteractiveEditPartCount = 0;
        context.InteractiveOverlayActive = false;
        context.InteractiveOverlayTitle = null;
        context.InteractiveOverlayValue = null;
        context.InteractiveSearchPreviewRows.Clear();
    }

    private static void SetInteractiveOverlay(InspectorContext context, string title, string value)
    {
        context.InteractiveOverlayActive = true;
        context.InteractiveOverlayTitle = title;
        context.InteractiveOverlayValue = value;
    }

    private static string BuildTextOverlayValue(string text, int cursorIndex)
    {
        var clamped = Math.Clamp(cursorIndex, 0, text.Length);
        var withCursor = text.Insert(clamped, "|");
        return withCursor.Length == 0 ? "|" : withCursor;
    }

    private async Task<bool> ApplyInteractiveFieldValueAsync(
        InspectorContext context,
        CliSessionState session,
        InspectorFieldEntry field,
        string value)
    {
        var mutation = await TrySendInspectorMutationWithMessageAsync(
            session,
            new InspectorBridgeRequest(
                "set-field",
                context.TargetPath,
                context.SelectedComponentIndex,
                context.SelectedComponentName,
                field.Name,
                value,
                null));
        if (!mutation.Ok)
        {
            AddStream(context, $"[!] {DescribeInspectorMutationFailure(mutation.Message)}: {field.Name}");
            return false;
        }

        await PopulateFieldsAsync(context, session, context.SelectedComponentIndex!.Value, forceRefresh: true);
        var appliedValue = context.Fields
            .FirstOrDefault(f => f.Name.Equals(field.Name, StringComparison.OrdinalIgnoreCase))
            ?.Value ?? value;
        AddStream(context, $"[=] ok: {field.Name} updated to {appliedValue}");
        _renderer.Render(context, null, field.Name, focusModeEnabled: true);
        return true;
    }

    private static bool TryParseVectorValues(string value, out List<float> parsed)
    {
        parsed = [];
        var normalized = value.Trim();
        if (!normalized.StartsWith("(", StringComparison.Ordinal)
            || !normalized.EndsWith(")", StringComparison.Ordinal))
        {
            return false;
        }

        normalized = normalized[1..^1];
        var tokens = normalized
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Trim())
            .Where(part => part.Length > 0)
            .ToArray();
        if (tokens.Length is < 2 or > 4)
        {
            return false;
        }

        foreach (var token in tokens)
        {
            if (!float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                return false;
            }

            parsed.Add(number);
        }

        return true;
    }

    private static string FormatVector(IReadOnlyList<float> values)
    {
        var formatted = values.Select(value => value.ToString("0.###", CultureInfo.InvariantCulture));
        return $"({string.Join(", ", formatted)})";
    }

    private static string FormatVector(IReadOnlyList<string> values)
    {
        var normalized = values.Select(value => value.Trim());
        return $"({string.Join(", ", normalized)})";
    }

    private static bool IsNumericFieldType(string type)
    {
        return type.Equals("Integer", StringComparison.OrdinalIgnoreCase)
               || type.Equals("Float", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsValidNumericText(string type, string text)
    {
        if (type.Equals("Integer", StringComparison.OrdinalIgnoreCase))
        {
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
        }

        if (type.Equals("Float", StringComparison.OrdinalIgnoreCase))
        {
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
        }

        return false;
    }

    private static bool IsAllowedNumericInputChar(char keyChar, string type)
    {
        if (char.IsControl(keyChar))
        {
            return false;
        }

        if (char.IsDigit(keyChar))
        {
            return true;
        }

        if (type.Equals("Integer", StringComparison.OrdinalIgnoreCase))
        {
            return keyChar is '-' or '+';
        }

        if (type.Equals("Float", StringComparison.OrdinalIgnoreCase))
        {
            return keyChar is '-' or '+' or '.' or 'e' or 'E';
        }

        return false;
    }

    private static bool TryValidateVectorComponentTexts(IReadOnlyList<string> values)
    {
        if (values.Count is < 2 or > 4)
        {
            return false;
        }

        foreach (var value in values)
        {
            if (!TryParseFloatInvariant(value, out _))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryParseFloatInvariant(string value, out float result)
    {
        return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
    }

    private async Task<InspectorComponentFetchResult> TryFetchComponentsFromBridgeAsync(CliSessionState session, string targetPath)
    {
        if (session.AttachedPort is null)
        {
            return new InspectorComponentFetchResult(
                false,
                [],
                "no attached daemon port in current session",
                null,
                null);
        }

        var payload = new InspectorBridgeRequest("list-components", targetPath, null, null, null, null, null);
        var responseJson = await SendBridgeRequestAsync(payload, session);
        if (string.IsNullOrWhiteSpace(responseJson))
        {
            return new InspectorComponentFetchResult(
                false,
                [],
                "bridge request returned empty payload (timeout, transport failure, or non-success status)",
                null,
                null);
        }

        try
        {
            var response = JsonSerializer.Deserialize<InspectorBridgeComponentsResponse>(responseJson, _jsonOptions);
            if (response is null)
            {
                return new InspectorComponentFetchResult(
                    false,
                    [],
                    "bridge payload deserialized to null response object",
                    null,
                    ClipDiagnosticPayload(responseJson));
            }

            if (!response.Ok)
            {
                return new InspectorComponentFetchResult(
                    false,
                    [],
                    "bridge responded with ok=false for list-components",
                    false,
                    ClipDiagnosticPayload(responseJson));
            }

            if (response.Components is null)
            {
                return new InspectorComponentFetchResult(
                    false,
                    [],
                    "bridge responded with ok=true but components payload is missing",
                    true,
                    ClipDiagnosticPayload(responseJson));
            }

            var components = response.Components
                .Select(component => new InspectorComponentEntry(component.Index, component.Name, component.Enabled))
                .ToList();
            return new InspectorComponentFetchResult(
                true,
                components,
                null,
                true,
                ClipDiagnosticPayload(responseJson));
        }
        catch
        {
            return new InspectorComponentFetchResult(
                false,
                [],
                "bridge returned non-parseable JSON payload for list-components",
                null,
                ClipDiagnosticPayload(responseJson));
        }
    }

    private async Task<List<string>> BuildInspectorComponentFetchDiagnosticsAsync(
        CliSessionState session,
        string targetPath,
        InspectorComponentFetchResult fetchResult)
    {
        var lines = new List<string>
        {
            $"[x] inspector diagnostics: targetPath='{targetPath}', attachedPort={(session.AttachedPort?.ToString() ?? "none")}"
        };

        if (!fetchResult.Success)
        {
            lines.Add($"[x] inspector diagnostics: bridge failure -> {fetchResult.FailureReason ?? "unknown"}");
        }

        if (!string.IsNullOrWhiteSpace(fetchResult.RawPayload))
        {
            lines.Add($"[x] inspector diagnostics: bridge payload: {Markup.Escape(fetchResult.RawPayload)}");
        }

        if (session.AttachedPort is null)
        {
            return lines;
        }

        var snapshot = await _hierarchyDaemonClient.GetSnapshotAsync(session.AttachedPort.Value);
        if (snapshot is null)
        {
            lines.Add("[x] inspector diagnostics: hierarchy snapshot fetch failed on attached daemon");
            return lines;
        }

        lines.Add($"[x] inspector diagnostics: hierarchy scene/root='{snapshot.Scene}'");
        if (TryFindNodeByInspectorPath(snapshot.Root, targetPath, includeRoot: false, out var resolvedNode))
        {
            lines.Add($"[x] inspector diagnostics: target resolved in hierarchy (node id {resolvedNode.Id}, name '{resolvedNode.Name}')");
            if (fetchResult.Success && fetchResult.Components.Count == 0)
            {
                lines.Add("[x] inspector diagnostics: hierarchy target exists but bridge returned 0 components; this indicates inspector-side target resolution mismatch");
            }
        }
        else
        {
            var candidateRoots = snapshot.Root.Children
                .Take(3)
                .Select(node => node.Name)
                .ToList();
            var hint = candidateRoots.Count == 0
                ? "no hierarchy root children are available"
                : $"top-level candidates: {string.Join(", ", candidateRoots)}";
            lines.Add($"[x] inspector diagnostics: target path does not resolve in current hierarchy snapshot ({hint})");
        }

        return lines;
    }

    private static string ClipDiagnosticPayload(string payload)
    {
        const int maxLength = 220;
        if (string.IsNullOrWhiteSpace(payload))
        {
            return string.Empty;
        }

        var compact = payload.Replace(Environment.NewLine, " ", StringComparison.Ordinal).Trim();
        if (compact.Length <= maxLength)
        {
            return compact;
        }

        return compact[..maxLength] + "...";
    }

    private async Task<List<InspectorFieldEntry>?> TryFetchFieldsFromBridgeAsync(CliSessionState session, string targetPath, int componentIndex)
    {
        var payload = new InspectorBridgeRequest("list-fields", targetPath, componentIndex, null, null, null, null);
        var responseJson = await SendBridgeRequestAsync(payload, session);
        if (string.IsNullOrWhiteSpace(responseJson))
        {
            return null;
        }

        try
        {
            var response = JsonSerializer.Deserialize<InspectorBridgeFieldsResponse>(responseJson, _jsonOptions);
            if (response?.Ok != true || response.Fields is null)
            {
                return null;
            }

            return response.Fields
                .Select(field => new InspectorFieldEntry(field.Name, field.Value, field.Type, field.IsBoolean, field.EnumOptions))
                .ToList();
        }
        catch
        {
            return null;
        }
    }

    private static InspectorComponentEntry? ResolveComponentRemoveTarget(InspectorContext context, string selector)
    {
        if (int.TryParse(selector, out var index))
        {
            return context.Components.FirstOrDefault(component => component.Index == index);
        }

        var matches = context.Components
            .Where(component => component.Name.Equals(selector, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count == 1)
        {
            return matches[0];
        }

        if (InspectorComponentCatalog.TryResolve(selector, out _, out var typeReference, out _))
        {
            var simpleName = typeReference.Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if (!string.IsNullOrWhiteSpace(simpleName))
            {
                var typedMatches = context.Components
                    .Where(component => component.Name.Equals(simpleName, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (typedMatches.Count == 1)
                {
                    return typedMatches[0];
                }
            }
        }

        return null;
    }

    private async Task<(bool Ok, string? Message, string? Content)> TrySendInspectorMutationWithMessageAsync(CliSessionState session, InspectorBridgeRequest request)
    {
        var response = await SendBridgeRequestAsync(request, session);
        if (string.IsNullOrWhiteSpace(response))
        {
            return (false, "inspector mutation failed (daemon inspect endpoint request failed or returned empty response)", null);
        }

        try
        {
            var mutation = JsonSerializer.Deserialize<InspectorBridgeMutationResponse>(response, _jsonOptions);
            if (mutation?.Ok == true)
            {
                if (CliDryRunDiffService.TryCaptureDiffFromContent(mutation.Content, out _))
                {
                }

                return (true, mutation.Message, mutation.Content);
            }

            if (string.IsNullOrWhiteSpace(mutation?.Message))
            {
                return (false, "inspector mutation failed (daemon inspect endpoint returned ok=false without message)", mutation?.Content);
            }

            return (false, mutation.Message, mutation.Content);
        }
        catch
        {
            return (false, $"inspector mutation failed (daemon inspect endpoint returned invalid payload: {response})", null);
        }
    }

    private static string DescribeInspectorMutationFailure(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "inspector mutation failed (daemon inspect endpoint returned no detail)";
        }

        if (message.Contains("requires Bridge mode", StringComparison.OrdinalIgnoreCase))
        {
            return "inspector mutation requires Unity Bridge mode, but current daemon is Host/stub mode. Re-run /init and reattach the project daemon";
        }

        return message;
    }

    private async Task<string?> SendBridgeRequestAsync(InspectorBridgeRequest request, CliSessionState? session = null)
    {
        var port = session?.AttachedPort;
        if (port is null)
        {
            return null;
        }

        if (DaemonMutationActionCatalog.IsInspectorMutation(request.Action) && request.Intent is null)
        {
            request = request with
            {
                Intent = MutationIntentFactory.CreateInspectorIntent(
                    request.Action,
                    request.TargetPath,
                    request.ComponentIndex,
                    request.ComponentName,
                    request.FieldName,
                    request.Value)
            };
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var cts = new CancellationTokenSource(ResolveInspectRequestTimeout(request.Action));
                var json = JsonSerializer.Serialize(request, _jsonOptions);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await Http.PostAsync($"http://127.0.0.1:{port.Value}/inspect", content, cts.Token);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                return (await response.Content.ReadAsStringAsync(cts.Token)).Trim();
            }
            catch when (attempt < 2)
            {
                await Task.Delay(180);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private static TimeSpan ResolveInspectRequestTimeout(string action)
    {
        // Mutation requests can include scene/prefab persistence and legitimately take longer.
        if (action is "set-field" or "toggle-field" or "toggle-component" or "add-component" or "remove-component")
        {
            return TimeSpan.FromSeconds(10);
        }

        // Read actions are used immediately after load transitions and may need extra bridge settle time.
        return TimeSpan.FromSeconds(8);
    }

    private static void ReplaceField(InspectorContext context, InspectorFieldEntry field)
    {
        var existingIndex = context.Fields.FindIndex(f => f.Name.Equals(field.Name, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
        {
            context.Fields[existingIndex] = field;
        }
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

    private async Task<HierarchyNodeDto?> TryResolveHierarchyNodeAsync(
        CliSessionState session,
        string targetPath,
        bool includeRoot)
    {
        if (session.AttachedPort is null)
        {
            return null;
        }

        var snapshot = await _hierarchyDaemonClient.GetSnapshotAsync(session.AttachedPort.Value);
        if (snapshot is null)
        {
            return null;
        }

        return TryFindNodeByInspectorPath(snapshot.Root, targetPath, includeRoot, out var resolved)
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
