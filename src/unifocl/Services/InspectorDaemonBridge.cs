using System.Text.Json;

internal sealed class InspectorDaemonBridge
{
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public bool TryHandle(string? commandLine, out string response)
    {
        response = string.Empty;
        if (string.IsNullOrWhiteSpace(commandLine) || !commandLine.StartsWith("INSPECT ", StringComparison.Ordinal))
        {
            return false;
        }

        var payload = commandLine["INSPECT ".Length..];
        try
        {
            var request = JsonSerializer.Deserialize<InspectorBridgeRequest>(payload, _jsonOptions);
            if (request is null || string.IsNullOrWhiteSpace(request.Action))
            {
                response = JsonSerializer.Serialize(new { ok = false }, _jsonOptions);
                return true;
            }

            response = request.Action switch
            {
                "list-components" => JsonSerializer.Serialize(new { ok = true, components = GetComponents() }, _jsonOptions),
                "list-fields" => JsonSerializer.Serialize(new { ok = true, fields = GetFields(request.ComponentName) }, _jsonOptions),
                "find" => JsonSerializer.Serialize(new { ok = true, results = Find(request.Query, request.ComponentName) }, _jsonOptions),
                "toggle-component" => JsonSerializer.Serialize(new { ok = true }, _jsonOptions),
                "toggle-field" => JsonSerializer.Serialize(new { ok = true }, _jsonOptions),
                "set-field" => JsonSerializer.Serialize(new { ok = true }, _jsonOptions),
                _ => JsonSerializer.Serialize(new { ok = false }, _jsonOptions)
            };
            return true;
        }
        catch
        {
            response = JsonSerializer.Serialize(new { ok = false }, _jsonOptions);
            return true;
        }
    }

    private static List<object> GetComponents()
    {
        return
        [
            new { index = 0, name = "Transform", enabled = true },
            new { index = 1, name = "Rigidbody", enabled = true },
            new { index = 2, name = "CapsuleCollider", enabled = true },
            new { index = 3, name = "PlayerController", enabled = true }
        ];
    }

    private static List<object> GetFields(string? componentName)
    {
        if (string.Equals(componentName, "PlayerController", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                new { name = "speed", value = "6.5", type = "float", isBoolean = false },
                new { name = "jumpForce", value = "12", type = "int", isBoolean = false },
                new { name = "grounded", value = "false", type = "bool", isBoolean = true },
                new { name = "playerColor", value = "RGBA(1.0, 0.0, 0.0, 1.0)", type = "Color", isBoolean = false },
                new { name = "startPos", value = "(0.0, 1.0, 0.0)", type = "Vector3", isBoolean = false }
            ];
        }

        return
        [
            new { name = "enabled", value = "true", type = "bool", isBoolean = true }
        ];
    }

    private static List<InspectorSearchResultDto> Find(string? query, string? componentName)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var results = new List<InspectorSearchResultDto>();
        foreach (var component in GetComponents())
        {
            var name = (string)component.GetType().GetProperty("name")!.GetValue(component)!;
            var index = (int)component.GetType().GetProperty("index")!.GetValue(component)!;
            if (FuzzyMatcher.TryScore(query, name, out var componentScore))
            {
                results.Add(new InspectorSearchResultDto("component", index, name, name, componentScore));
            }
        }

        foreach (var field in GetFields(componentName))
        {
            var fieldName = (string)field.GetType().GetProperty("name")!.GetValue(field)!;
            if (FuzzyMatcher.TryScore(query, fieldName, out var fieldScore))
            {
                var path = string.IsNullOrWhiteSpace(componentName) ? fieldName : $"{componentName}.{fieldName}";
                results.Add(new InspectorSearchResultDto("field", null, fieldName, path, fieldScore));
            }
        }

        return results
            .OrderByDescending(result => result.Score)
            .ThenBy(result => result.Path, StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList();
    }

    private sealed record InspectorBridgeRequest(
        string Action,
        string? TargetPath,
        int? ComponentIndex,
        string? ComponentName,
        string? FieldName,
        string? Value,
        string? Query);
}

