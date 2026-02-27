using System;

namespace UniFocl.SharedModels
{
    [Serializable]
    public sealed class BridgeConfig
    {
        public string projectPath = string.Empty;
        public DaemonEndpoint daemon = new();
        public string protocol = "v1";
        public string updatedAtUtc = string.Empty;
    }

    [Serializable]
    public sealed class DaemonEndpoint
    {
        public string host = "127.0.0.1";
        public int port = 18080;
    }

    [Serializable]
    public sealed class DaemonServiceArgs
    {
        public int port = 8080;
        public string projectPath = string.Empty;
        public int ttlSeconds = 600;
        public bool headless = false;
    }
}
