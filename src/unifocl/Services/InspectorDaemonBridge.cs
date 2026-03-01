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
                "list-components" => JsonSerializer.Serialize(new { ok = false, components = Array.Empty<object>() }, _jsonOptions),
                "list-fields" => JsonSerializer.Serialize(new { ok = false, fields = Array.Empty<object>() }, _jsonOptions),
                "find" => JsonSerializer.Serialize(new { ok = false, results = Array.Empty<object>() }, _jsonOptions),
                "toggle-component" => JsonSerializer.Serialize(new { ok = false }, _jsonOptions),
                "toggle-field" => JsonSerializer.Serialize(new { ok = false }, _jsonOptions),
                "set-field" => JsonSerializer.Serialize(new { ok = false }, _jsonOptions),
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

    private sealed record InspectorBridgeRequest(
        string Action,
        string? TargetPath,
        int? ComponentIndex,
        string? ComponentName,
        string? FieldName,
        string? Value,
        string? Query);
}
