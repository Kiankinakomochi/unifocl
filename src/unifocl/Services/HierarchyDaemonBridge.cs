using System.Text.Json;

internal sealed class HierarchyDaemonBridge
{
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public HierarchyDaemonBridge(string? projectPath)
    {
        _ = projectPath;
    }

    public bool TryHandle(string? commandLine, out string response)
    {
        response = string.Empty;
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return false;
        }

        var command = commandLine.Trim();
        if (string.Equals(command, "HIERARCHY_GET", StringComparison.Ordinal))
        {
            response = JsonSerializer.Serialize(
                new HierarchySnapshotDto("Unavailable", 1, new HierarchyNodeDto(0, "Unavailable", false, [])),
                _jsonOptions);
            return true;
        }

        if (command.StartsWith("HIERARCHY_FIND ", StringComparison.Ordinal))
        {
            response = JsonSerializer.Serialize(
                new HierarchySearchResponseDto(false, [], "hierarchy operations require Unity editor bridge"),
                _jsonOptions);
            return true;
        }

        if (command.StartsWith("HIERARCHY_CMD ", StringComparison.Ordinal))
        {
            response = JsonSerializer.Serialize(
                new HierarchyCommandResponseDto(false, "hierarchy operations require Unity editor bridge", null, null),
                _jsonOptions);
            return true;
        }

        return false;
    }
}
