using System.Text.Json;

internal sealed class ProjectDaemonBridge
{
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public bool TryHandle(string? commandLine, out string response)
    {
        response = string.Empty;
        if (string.IsNullOrWhiteSpace(commandLine) || !commandLine.StartsWith("PROJECT_CMD ", StringComparison.Ordinal))
        {
            return false;
        }

        response = JsonSerializer.Serialize(
            new ProjectCommandResponseDto(false, "project commands require Unity editor bridge", null),
            _jsonOptions);
        return true;
    }
}
