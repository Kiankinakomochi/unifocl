using System.Net.Http;
using System.Text;
using System.Text.Json;

internal sealed class InspectorModeService
{
    private const int DefaultInnerWidth = 78;
    private const int MinInnerWidth = 40;
    private readonly InspectorTuiRenderer _renderer = new();
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };
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
            "toggle" or
            "t" or
            "f" or
            "ff" or
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
            case "f":
            case "ff":
                await HandleFuzzyFindAsync(input, tokens, session, log);
                return true;
            case "make":
            case "mk":
            case "remove":
            case "rm":
            case "rename":
            case "rn":
            case "move":
            case "mv":
                HandleUnsupportedInspectorMutation(input, session, log);
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
        Action<string> log)
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

        AddStream(context, "[i] inspector focus mode enabled (up/down select, tab inspect, shift+tab back, esc exit)");
        var selectedComponentPosition = 0;
        var selectedFieldPosition = 0;

        while (true)
        {
            if (context.Depth == InspectorDepth.ComponentList)
            {
                var components = context.Components.OrderBy(component => component.Index).ToList();
                if (components.Count == 0)
                {
                    _renderer.Render(context, null, null, focusModeEnabled: true);
                }
                else
                {
                    selectedComponentPosition = Math.Clamp(selectedComponentPosition, 0, components.Count - 1);
                    var selectedComponent = components[selectedComponentPosition];
                    _renderer.Render(context, selectedComponent.Index, null, focusModeEnabled: true);
                }
            }
            else
            {
                var fields = context.Fields;
                if (fields.Count == 0)
                {
                    _renderer.Render(context, null, null, focusModeEnabled: true);
                }
                else
                {
                    selectedFieldPosition = Math.Clamp(selectedFieldPosition, 0, fields.Count - 1);
                    _renderer.Render(context, null, fields[selectedFieldPosition].Name, focusModeEnabled: true);
                }
            }

            var intent = KeyboardIntentReader.ReadIntent();
            switch (intent)
            {
                case KeyboardIntent.Up:
                    if (context.Depth == InspectorDepth.ComponentList && context.Components.Count > 0)
                    {
                        selectedComponentPosition = selectedComponentPosition <= 0
                            ? context.Components.Count - 1
                            : selectedComponentPosition - 1;
                    }
                    else if (context.Depth == InspectorDepth.ComponentFields && context.Fields.Count > 0)
                    {
                        selectedFieldPosition = selectedFieldPosition <= 0
                            ? context.Fields.Count - 1
                            : selectedFieldPosition - 1;
                    }
                    break;
                case KeyboardIntent.Down:
                    if (context.Depth == InspectorDepth.ComponentList && context.Components.Count > 0)
                    {
                        selectedComponentPosition = selectedComponentPosition >= context.Components.Count - 1
                            ? 0
                            : selectedComponentPosition + 1;
                    }
                    else if (context.Depth == InspectorDepth.ComponentFields && context.Fields.Count > 0)
                    {
                        selectedFieldPosition = selectedFieldPosition >= context.Fields.Count - 1
                            ? 0
                            : selectedFieldPosition + 1;
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
                    break;
                case KeyboardIntent.ShiftTab:
                    if (context.Depth == InspectorDepth.ComponentFields)
                    {
                        context.Depth = InspectorDepth.ComponentList;
                        context.Fields.Clear();
                        context.SelectedComponentIndex = null;
                        context.SelectedComponentName = null;
                        context.BodyScrollOffset = 0;
                        context.FollowStreamScroll = true;
                        context.StreamScrollOffset = int.MaxValue;
                        AddStream(context, "[i] stepped up to component list");
                    }
                    else
                    {
                        AddStream(context, "[i] already at inspector root");
                    }

                    break;
                case KeyboardIntent.Escape:
                case KeyboardIntent.FocusInspector:
                    AddStream(context, "[i] inspector focus mode disabled");
                    _renderer.Render(context);
                    return;
                default:
                    break;
            }
        }
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

        var argument = tokens[1];
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
            var bridged = await TrySendInspectorMutationAsync(
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
            if (bridged)
            {
                context.Components.Remove(entry);
                context.Components.Add(entry with { Enabled = newEnabled });
                context.Components.Sort((a, b) => a.Index.CompareTo(b.Index));
                AddStream(context, $"[*] ok: {entry.Name} set to {(newEnabled ? "activated" : "deactivated")}");
            }
            else
            {
                AddStream(context, "[!] failed to toggle component in Unity bridge");
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
        var mutationOk = await TrySendInspectorMutationAsync(
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
        if (mutationOk)
        {
            ReplaceField(context, field with { Value = newValue });
            AddStream(context, $"[=] ok: {field.Name} updated to {newValue}");
        }
        else
        {
            AddStream(context, "[!] failed to toggle field in Unity bridge");
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
            _renderer.Render(context);
            return;
        }

        var fieldName = tokens[1];
        var newValue = string.Join(' ', tokens.Skip(2));

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
                AddStream(context, "[!] unable to fetch inspector fields from Unity bridge");
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

            var rootMutationOk = await TrySendInspectorMutationAsync(
                session,
                new InspectorBridgeRequest(
                    "set-field",
                    context.TargetPath,
                    component.Index,
                    component.Name,
                    rootField.Name,
                    newValue,
                    null));

            AddStream(context, $"{context.PromptLabel} > {input}");
            if (rootMutationOk)
            {
                AddStream(context, $"[=] ok: {component.Name}.{rootField.Name} updated to {newValue}");
            }
            else
            {
                AddStream(context, "[!] failed to set field in Unity bridge");
            }

            _renderer.Render(context);
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

        var mutationOk = await TrySendInspectorMutationAsync(
            session,
            new InspectorBridgeRequest(
                "set-field",
                context.TargetPath,
                context.SelectedComponentIndex,
                context.SelectedComponentName,
                field.Name,
                newValue,
                null));

        AddStream(context, $"{context.PromptLabel} > {input}");
        if (mutationOk)
        {
            ReplaceField(context, field with { Value = newValue });
            AddStream(context, $"[=] ok: {field.Name} updated to {newValue}");
        }
        else
        {
            AddStream(context, "[!] failed to set field in Unity bridge");
        }

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

        AddStream(context, "[!] failed to query fuzzy results from Unity bridge");
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
        context.Fields.Clear();
        context.SelectedComponentIndex = null;
        context.SelectedComponentName = null;
        context.BodyScrollOffset = 0;
        context.FollowStreamScroll = true;
        context.StreamScrollOffset = int.MaxValue;
        session.ContextMode = CliContextMode.Inspector;
        session.FocusPath = targetPath;
        await PopulateComponentsAsync(context, session, forceRefresh: true);
        AddStream(context, $"{context.PromptLabel} > {rawCommand}");
        AddStream(context, $"[i] entering inspector for: {context.TargetPath.TrimStart('/')}");
        if (context.Components.Count == 0)
        {
            AddStream(context, "[!] no inspector components returned (check Unity bridge attachment/target path)");
        }

        _renderer.Render(context);
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
        context.BodyScrollOffset = 0;
        context.FollowStreamScroll = true;
        context.StreamScrollOffset = int.MaxValue;
        await PopulateFieldsAsync(context, session, componentIndex, forceRefresh: true);
        AddStream(context, $"UnityCLI:{context.TargetPath} [inspect] > {rawCommand}");
        AddStream(context, $"[i] inspecting component: {component.Name}");
        if (context.Fields.Count == 0)
        {
            AddStream(context, "[!] no serializable fields returned for selected component");
        }

        _renderer.Render(context);
    }

    private async Task PopulateComponentsAsync(
        InspectorContext context,
        CliSessionState session,
        bool forceRefresh)
    {
        if (!forceRefresh && context.Components.Count > 0)
        {
            return;
        }

        var fromBridge = await TryFetchComponentsFromBridgeAsync(session, context.TargetPath);
        context.Components.Clear();
        if (fromBridge is not null)
        {
            context.Components.AddRange(fromBridge);
        }
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

    private async Task<List<InspectorComponentEntry>?> TryFetchComponentsFromBridgeAsync(CliSessionState session, string targetPath)
    {
        var payload = new InspectorBridgeRequest("list-components", targetPath, null, null, null, null, null);
        var responseJson = await SendBridgeRequestAsync(payload, session);
        if (string.IsNullOrWhiteSpace(responseJson))
        {
            return null;
        }

        try
        {
            var response = JsonSerializer.Deserialize<InspectorBridgeComponentsResponse>(responseJson, _jsonOptions);
            if (response?.Ok != true || response.Components is null)
            {
                return null;
            }

            return response.Components
                .Select(component => new InspectorComponentEntry(component.Index, component.Name, component.Enabled))
                .ToList();
        }
        catch
        {
            return null;
        }
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
                .Select(field => new InspectorFieldEntry(field.Name, field.Value, field.Type, field.IsBoolean))
                .ToList();
        }
        catch
        {
            return null;
        }
    }

    private async Task<bool> TrySendInspectorMutationAsync(CliSessionState session, InspectorBridgeRequest request)
    {
        var response = await SendBridgeRequestAsync(request, session);
        if (string.IsNullOrWhiteSpace(response))
        {
            return false;
        }

        try
        {
            var mutation = JsonSerializer.Deserialize<InspectorBridgeMutationResponse>(response, _jsonOptions);
            return mutation?.Ok == true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<string?> SendBridgeRequestAsync(InspectorBridgeRequest request, CliSessionState? session = null)
    {
        var port = session?.AttachedPort;
        if (port is null)
        {
            return null;
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            var json = JsonSerializer.Serialize(request, _jsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await Http.PostAsync($"http://127.0.0.1:{port.Value}/inspect", content, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return (await response.Content.ReadAsStringAsync(cts.Token)).Trim();
        }
        catch
        {
            return null;
        }
    }

    private static void ReplaceField(InspectorContext context, InspectorFieldEntry field)
    {
        var existingIndex = context.Fields.FindIndex(f => f.Name.Equals(field.Name, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
        {
            context.Fields[existingIndex] = field;
        }
    }

    private void HandleUnsupportedInspectorMutation(string input, CliSessionState session, Action<string> log)
    {
        var context = session.Inspector;
        if (context is null)
        {
            log("[grey]system[/]: inspector mode is required");
            return;
        }

        AddStream(context, $"{context.PromptLabel} > {input}");
        AddStream(context, "[!] command not implemented in inspector mode yet");
        _renderer.Render(context);
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

    private static void AppendInspectorFuzzyResults(InspectorContext context, string query, IEnumerable<string> paths)
    {
        var buffered = paths.ToList();
        if (buffered.Count == 0)
        {
            AddStream(context, $"[x] no fuzzy results for: {query}");
            return;
        }

        AddStream(context, $"[*] fuzzy results for: {query}");
        for (var i = 0; i < buffered.Count; i++)
        {
            AddStream(context, $"[{i}] {buffered[i]}");
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
        string? Query);

    private sealed record InspectorBridgeComponentsResponse(bool Ok, List<InspectorBridgeComponent>? Components);
    private sealed record InspectorBridgeFieldsResponse(bool Ok, List<InspectorBridgeField>? Fields);
    private sealed record InspectorBridgeSearchResponse(bool Ok, List<InspectorSearchResultDto>? Results);
    private sealed record InspectorBridgeMutationResponse(bool Ok);
    private sealed record InspectorBridgeComponent(int Index, string Name, bool Enabled);
    private sealed record InspectorBridgeField(string Name, string Value, string Type, bool IsBoolean);
}
