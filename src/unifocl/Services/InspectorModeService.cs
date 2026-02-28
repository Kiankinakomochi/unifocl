using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

internal sealed class InspectorModeService
{
    private readonly InspectorTuiRenderer _renderer = new();
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

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

            context.Components.Remove(entry);
            context.Components.Add(entry with { Enabled = newEnabled });
            context.Components.Sort((a, b) => a.Index.CompareTo(b.Index));

            AddStream(context, $"{context.PromptLabel} > {input}");
            AddStream(context, $"[*] ok: {entry.Name} set to {(newEnabled ? "activated" : "deactivated")}");
            if (!bridged)
            {
                AddStream(context, "[~] daemon bridge unavailable; applied to local inspector cache");
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
        ReplaceField(context, field with { Value = newValue });
        AddStream(context, $"{context.PromptLabel} > {input}");
        AddStream(context, $"[=] ok: {field.Name} updated to {newValue}");
        if (!mutationOk)
        {
            AddStream(context, "[~] daemon bridge unavailable; applied to local inspector cache");
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
            var fieldEntries = fetchedFields ?? GetMockFields(component.Name);
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
            AddStream(context, $"[=] ok: {component.Name}.{rootField.Name} updated to {newValue}");
            if (!rootMutationOk)
            {
                AddStream(context, "[~] daemon bridge unavailable; applied to local inspector cache");
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

        ReplaceField(context, field with { Value = newValue });
        AddStream(context, $"{context.PromptLabel} > {input}");
        AddStream(context, $"[=] ok: {field.Name} updated to {newValue}");
        if (!mutationOk)
        {
            AddStream(context, "[~] daemon bridge unavailable; applied to local inspector cache");
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

        var localPaths = context.Depth == InspectorDepth.ComponentList
            ? context.Components
                .Select(c => c.Name)
                .Where(name => FuzzyMatcher.TryScore(query, name, out _))
                .Take(20)
                .ToList()
            : context.Fields
                .Select(f => $"{context.SelectedComponentName}.{f.Name}")
                .Where(path => FuzzyMatcher.TryScore(query, path, out _))
                .Take(20)
                .ToList();

        AppendInspectorFuzzyResults(context, query, localPaths);
        AddStream(context, "[~] daemon bridge unavailable; used local inspector cache");
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
            if (context.FollowStreamScroll)
            {
                context.StreamScrollOffset = Math.Max(0, context.CommandStream.Count - 1);
            }

            context.FollowStreamScroll = false;
            context.StreamScrollOffset += delta;
            if (context.StreamScrollOffset >= context.CommandStream.Count)
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
        context.Components.AddRange(fromBridge ?? GetMockComponents());
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
        context.Fields.AddRange(fromBridge ?? GetMockFields(context.SelectedComponentName ?? "Component"));
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
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await client.ConnectAsync(IPAddress.Loopback, port.Value, cts.Token);
            await using var stream = client.GetStream();
            using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            var json = JsonSerializer.Serialize(request, _jsonOptions);
            await writer.WriteLineAsync($"INSPECT {json}");
            var response = await reader.ReadLineAsync();
            return response;
        }
        catch
        {
            return null;
        }
    }

    private static List<InspectorComponentEntry> GetMockComponents()
    {
        return
        [
            new InspectorComponentEntry(0, "Transform", true),
            new InspectorComponentEntry(1, "Rigidbody", true),
            new InspectorComponentEntry(2, "CapsuleCollider", true),
            new InspectorComponentEntry(3, "PlayerController", true)
        ];
    }

    private static List<InspectorFieldEntry> GetMockFields(string componentName)
    {
        if (!componentName.Equals("PlayerController", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                new InspectorFieldEntry("enabled", "true", "bool", true)
            ];
        }

        return
        [
            new InspectorFieldEntry("speed", "6.5", "float", false),
            new InspectorFieldEntry("jumpForce", "12", "int", false),
            new InspectorFieldEntry("grounded", "false", "bool", true),
            new InspectorFieldEntry("playerColor", "RGBA(1.0, 0.0, 0.0, 1.0)", "Color", false),
            new InspectorFieldEntry("startPos", "(0.0, 1.0, 0.0)", "Vector3", false)
        ];
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
