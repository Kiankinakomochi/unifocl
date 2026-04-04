#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace UniFocl.EditorBridge
{
    [Serializable]
    internal sealed class RuntimeSimpleResponse
    {
        public bool ok;
        public string message = string.Empty;
    }

    [Serializable]
    internal sealed class RuntimeStatusResponse
    {
        public bool ok;
        public string state = string.Empty;
        public string targetAddress = string.Empty;
        public int playerId = -1;
    }

    [Serializable]
    internal sealed class RuntimeTargetListResponse
    {
        public bool ok;
        public string targets = "[]";
    }

    [Serializable]
    internal sealed class RuntimeManifestResponse
    {
        public bool ok;
        public string manifest = "{}";
        public string error = string.Empty;
    }

    [Serializable]
    internal sealed class RuntimeExecRequest
    {
        public string command = string.Empty;
        public string argsJson = "{}";
    }

    [Serializable]
    internal sealed class RuntimeExecResponse
    {
        public bool ok;
        public string requestId = string.Empty;
        public bool success;
        public string message = string.Empty;
        public string resultJson = "{}";
    }

    [Serializable]
    internal sealed class RuntimeJobSubmitRequest
    {
        public string command = string.Empty;
        public string argsJson = "{}";
        public int timeoutMs = 60000;
    }

    [Serializable]
    internal sealed class RuntimeJobStatusResponse
    {
        public bool ok;
        public string jobId = string.Empty;
        public string state = string.Empty;
        public float progress;
        public string message = string.Empty;
        public string resultJson = "{}";
    }

    [Serializable]
    internal sealed class RuntimeJobListResponse
    {
        public bool ok;
        public string jobs = "[]";
    }

    [Serializable]
    internal sealed class RuntimeStreamSubscribeRequest
    {
        public string channel = string.Empty;
        public string filterJson = "{}";
    }

    [Serializable]
    internal sealed class RuntimeStreamResponse
    {
        public bool ok;
        public string subscriptionId = string.Empty;
        public string message = string.Empty;
    }

    [Serializable]
    internal sealed class RuntimeWatchRequest
    {
        public string expression = string.Empty;
        public string target = string.Empty;
        public int intervalMs = 1000;
    }

    [Serializable]
    internal sealed class RuntimeWatchResponse
    {
        public bool ok;
        public string watchId = string.Empty;
        public string message = string.Empty;
    }

    [Serializable]
    internal sealed class RuntimeWatchListResponse
    {
        public bool ok;
        public string watches = "[]";
    }

    [Serializable]
    internal sealed class RuntimeWatchSnapshotEntry
    {
        public string watchId = string.Empty;
        public string expression = string.Empty;
        public string valueJson = "{}";
        public long timestampUtcMs;
    }
}
#endif
