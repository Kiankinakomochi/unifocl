using System.Text.Json.Serialization;

/// <summary>Addressing scheme for runtime targets: <c>platform:name</c>.</summary>
internal sealed record RuntimeTargetAddress(string Platform, string Name)
{
    /// <summary>Parse an address string like "android:pixel-7" or "editor:playmode".</summary>
    public static RuntimeTargetAddress Parse(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return new RuntimeTargetAddress("editor", "playmode");
        }

        var colonIdx = address.IndexOf(':');
        if (colonIdx < 0)
        {
            return new RuntimeTargetAddress(address.Trim().ToLowerInvariant(), "*");
        }

        return new RuntimeTargetAddress(
            address[..colonIdx].Trim().ToLowerInvariant(),
            address[(colonIdx + 1)..].Trim());
    }

    public override string ToString() => $"{Platform}:{Name}";
}

/// <summary>A discovered runtime target (player instance).</summary>
internal sealed record RuntimeTarget(
    int PlayerId,
    string Name,
    string Platform,
    string DeviceId,
    bool IsConnected)
{
    public RuntimeTargetAddress Address => new(Platform, Name);
}

/// <summary>Connection status for the currently attached runtime target.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum RuntimeConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Error
}

/// <summary>Status snapshot for the active runtime connection.</summary>
internal sealed record RuntimeConnectionStatus(
    RuntimeConnectionState State,
    string? TargetAddress,
    int PlayerId,
    string? Message);
