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
}
#endif
