using System.Text.Json;
using System.Text.Json.Serialization;

public sealed class UnifoclManifest
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = string.Empty;

    [JsonPropertyName("generatedAtUtc")]
    public string GeneratedAtUtc { get; set; } = string.Empty;

    [JsonPropertyName("categories")]
    public List<UnifoclCategoryManifest> Categories { get; set; } = [];
}

public sealed class UnifoclCategoryManifest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("tools")]
    public List<UnifoclToolManifest> Tools { get; set; } = [];
}

public sealed class UnifoclToolManifest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("declaringType")]
    public string DeclaringType { get; set; } = string.Empty;

    [JsonPropertyName("methodName")]
    public string MethodName { get; set; } = string.Empty;

    [JsonPropertyName("inputSchema")]
    public JsonElement InputSchema { get; set; }
}
