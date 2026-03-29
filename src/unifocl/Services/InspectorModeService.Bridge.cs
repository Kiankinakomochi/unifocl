using System.Text;
using System.Text.Json;
using Spectre.Console;

internal sealed partial class InspectorModeService
{
    private async Task<InspectorComponentFetchResult> TryFetchComponentsFromBridgeAsync(CliSessionState session, string targetPath)
    {
        if (DaemonControlService.GetPort(session) is null)
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
            $"[x] inspector diagnostics: targetPath='{targetPath}', attachedPort={(DaemonControlService.GetPort(session)?.ToString() ?? "none")}"
        };

        if (!fetchResult.Success)
        {
            lines.Add($"[x] inspector diagnostics: bridge failure -> {fetchResult.FailureReason ?? "unknown"}");
        }

        if (!string.IsNullOrWhiteSpace(fetchResult.RawPayload))
        {
            lines.Add($"[x] inspector diagnostics: bridge payload: {Markup.Escape(fetchResult.RawPayload)}");
        }

        if (DaemonControlService.GetPort(session) is not int diagPort)
        {
            return lines;
        }

        var snapshot = await _hierarchyDaemonClient.GetSnapshotAsync(diagPort);
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

    private async Task<string> NormalizeInspectorTargetPathAsync(CliSessionState session, string rawTargetPath)
    {
        var normalized = NormalizeInspectorPath(rawTargetPath);
        if (DaemonControlService.GetPort(session) is not int normalizePort)
        {
            return normalized;
        }

        var snapshot = await _hierarchyDaemonClient.GetSnapshotAsync(normalizePort);
        if (snapshot is null)
        {
            return normalized;
        }

        var stripped = StripSceneRootPrefix(normalized, snapshot.Root.Name);
        return TryResolveHierarchyPathLenient(snapshot.Root, stripped, out var resolved)
            ? resolved
            : stripped;
    }

    private static string StripSceneRootPrefix(string normalizedPath, string sceneRootName)
    {
        if (string.IsNullOrWhiteSpace(normalizedPath)
            || normalizedPath == "/"
            || string.IsNullOrWhiteSpace(sceneRootName))
        {
            return normalizedPath;
        }

        var prefix = "/" + sceneRootName;
        if (normalizedPath.Equals(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return "/";
        }

        if (normalizedPath.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
        {
            return normalizedPath[prefix.Length..];
        }

        return normalizedPath;
    }

    private async Task<(InspectorComponentEntry Component, InspectorFieldEntry Field)?> TryResolveRootFieldByNameAsync(
        CliSessionState session,
        InspectorContext context,
        string fieldName)
    {
        await PopulateComponentsAsync(context, session, forceRefresh: context.Components.Count == 0);
        if (context.Components.Count == 0)
        {
            return null;
        }

        var matches = new List<(InspectorComponentEntry, InspectorFieldEntry)>();
        foreach (var component in context.Components)
        {
            var fields = await TryFetchFieldsFromBridgeAsync(session, context.TargetPath, component.Index);
            if (fields is null)
            {
                continue;
            }

            var field = fields.FirstOrDefault(f => f.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase));
            if (field is not null)
            {
                matches.Add((component, field));
            }
        }

        if (matches.Count == 1)
        {
            return matches[0];
        }

        if (matches.Count > 1)
        {
            AddStream(context, $"{context.PromptLabel} > set {fieldName} <value...>");
            AddStream(context, $"[!] ambiguous field '{fieldName}' at inspector root; use Component.{fieldName}");
            _renderer.Render(context);
        }

        return null;
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
        var port = session is not null ? DaemonControlService.GetPort(session) : null;
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

        // JsonUtility.FromJson (Unity side) deserializes JSON null as 0 for int fields,
        // but the Unity-side sentinel for "no component index specified" is -1.
        // Normalize null → -1 here so Unity's ResolveComponent falls through to name lookup.
        if (request.ComponentIndex is null)
        {
            request = request with { ComponentIndex = -1 };
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
}
