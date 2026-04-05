#if UNITY_EDITOR
using System;
using System.Collections.Generic;

namespace UniFocl.EditorBridge
{
    [Serializable]
    internal sealed class AssetIndexEntry
    {
        public int instanceId;
        public string path = string.Empty;
    }

    [Serializable]
    internal sealed class AssetIndexSyncResponse
    {
        public int revision;
        public bool unchanged;
        public AssetIndexEntry[] entries = Array.Empty<AssetIndexEntry>();
    }

    internal sealed class HierarchyNodeState
    {
        public HierarchyNodeState(int id, string name, bool active, List<HierarchyNodeState> children)
        {
            this.id = id;
            this.name = name;
            this.active = active;
            this.children = children;
        }

        public int id;
        public string name;
        public bool active;
        public List<HierarchyNodeState> children;
    }

    [Serializable]
    internal sealed class HierarchyNodeDto
    {
        public int id;
        public string name = string.Empty;
        public bool active;
        public HierarchyNodeDto[] children = Array.Empty<HierarchyNodeDto>();
    }

    [Serializable]
    internal sealed class HierarchySnapshotResponse
    {
        public string scene = string.Empty;
        public int snapshotVersion;
        public HierarchyNodeDto root = new();
    }

    [Serializable]
    internal sealed class HierarchyCommandRequest
    {
        public string action = string.Empty;
        public int parentId;
        public int targetId;
        public string name = string.Empty;
        public bool primitive;
        public string type = string.Empty;
        public int count;
        public MutationIntentEnvelope intent = new();
    }

    [Serializable]
    internal sealed class HierarchyCommandResponse
    {
        public bool ok;
        public string message = string.Empty;
        public int nodeId;
        public bool isActive;
        public string content = string.Empty;
        /// <summary>
        /// The name Unity actually assigned to the affected object.
        /// Populated for create, rename, and move ops.
        /// May differ from the requested name when Unity's duplicate-name
        /// resolution appends " (1)", " (2)", etc.
        /// </summary>
        public string assignedName = string.Empty;
    }

    [Serializable]
    internal sealed class HierarchySearchRequest
    {
        public string query = string.Empty;
        public int limit = 20;
        public int parentId;
        public string tag = string.Empty;
        public string layer = string.Empty;
        public string component = string.Empty;
    }

    [Serializable]
    internal sealed class HierarchySearchResult
    {
        public int nodeId;
        public string path = string.Empty;
        public bool active;
        public double score;
    }

    [Serializable]
    internal sealed class HierarchySearchResponse
    {
        public bool ok;
        public HierarchySearchResult[] results = Array.Empty<HierarchySearchResult>();
        public string message = string.Empty;
    }

    [Serializable]
    internal sealed class InspectorBridgeRequest
    {
        public string action = string.Empty;
        public string targetPath = string.Empty;
        public int componentIndex = -1;
        public string componentName = string.Empty;
        public string fieldName = string.Empty;
        public string value = string.Empty;
        public string query = string.Empty;
        public bool includeSceneReferences = true;
        public bool includeProjectReferences = true;
        public MutationIntentEnvelope intent = new();
    }

    [Serializable]
    internal sealed class InspectorComponentEntry
    {
        public int index;
        public string name = string.Empty;
        public bool enabled;
    }

    [Serializable]
    internal sealed class InspectorFieldEntry
    {
        public string name = string.Empty;
        public string value = string.Empty;
        public string type = string.Empty;
        public bool isBoolean;
        public string[] enumOptions = Array.Empty<string>();
    }

    [Serializable]
    internal sealed class InspectorSearchResult
    {
        public string scope = string.Empty;
        public int componentIndex = -1;
        public string name = string.Empty;
        public string path = string.Empty;
        public double score;
        public string valueToken = string.Empty;
    }

    [Serializable]
    internal sealed class InspectorComponentsResponse
    {
        public bool ok;
        public InspectorComponentEntry[] components = Array.Empty<InspectorComponentEntry>();
    }

    [Serializable]
    internal sealed class InspectorFieldsResponse
    {
        public bool ok;
        public InspectorFieldEntry[] fields = Array.Empty<InspectorFieldEntry>();
    }

    [Serializable]
    internal sealed class InspectorSearchResponse
    {
        public bool ok;
        public InspectorSearchResult[] results = Array.Empty<InspectorSearchResult>();
    }

    [Serializable]
    internal sealed class InspectorMutationResponse
    {
        public bool ok;
        public string message = string.Empty;
        public string content = string.Empty;
        /// <summary>
        /// The 0-based index of the component in the target object's component list.
        /// Populated only for successful add_component ops; -1 otherwise.
        /// Agents should use this index for subsequent set_field / toggle_component
        /// calls instead of relying on the component type name, which is ambiguous
        /// when the same component type appears more than once.
        /// </summary>
        public int assignedIndex = -1;
    }

    [Serializable]
    internal sealed class ProjectCommandRequest
    {
        public string action = string.Empty;
        public string assetPath = string.Empty;
        public string newAssetPath = string.Empty;
        public string content = string.Empty;
        public string requestId = string.Empty;
        public MutationIntentEnvelope intent = new();
    }

    [Serializable]
    internal sealed class MutationIntentEnvelope
    {
        public string transactionId = string.Empty;
        public string target = string.Empty;
        public string property = string.Empty;
        public string oldValue = string.Empty;
        public string newValue = string.Empty;
        public MutationIntentFlags flags = new();
    }

    [Serializable]
    internal sealed class MutationIntentFlags
    {
        public bool dryRun;
        public bool requireRollback = true;
        public string vcsMode = string.Empty;
        public MutationVcsOwnedPath[] vcsOwnedPaths = Array.Empty<MutationVcsOwnedPath>();
    }

    [Serializable]
    internal sealed class MutationVcsOwnedPath
    {
        public string path = string.Empty;
        public string owner = string.Empty;
        public bool requiresCheckout;
    }

    [Serializable]
    internal sealed class MutationPathChange
    {
        public string action = string.Empty;
        public string path = string.Empty;
        public string nextPath = string.Empty;
        public string metaPath = string.Empty;
        public string owner = string.Empty;
        public bool requiresCheckout;
    }

    [Serializable]
    internal sealed class MutationDryRunDiffPayload
    {
        public string summary = string.Empty;
        public string format = "unified";
        public string before = string.Empty;
        public string after = string.Empty;
        public string[] lines = Array.Empty<string>();
        public MutationPathChange[] changes = Array.Empty<MutationPathChange>();
    }

    [Serializable]
    internal sealed class ProjectCommandResponse
    {
        public bool ok;
        public string message = string.Empty;
        public string kind = string.Empty;
        public string content = string.Empty;
    }

    [Serializable]
    internal sealed class ProjectCommandStatusResponse
    {
        public string requestId = string.Empty;
        public string action = string.Empty;
        public bool active;
        public bool success;
        public string stage = string.Empty;
        public string detail = string.Empty;
        public string startedAtUtc = string.Empty;
        public string lastUpdatedAtUtc = string.Empty;
        public string finishedAtUtc = string.Empty;
        public bool isCompiling;
        public bool isUpdating;
        public bool isDurable;
        public string state = string.Empty;
        public bool cancelRequested;
    }

    [Serializable]
    internal sealed class ProjectCommandAcceptedResponse
    {
        public bool ok;
        public string requestId = string.Empty;
        public string action = string.Empty;
        public bool duplicated;
        public string stage = string.Empty;
        public string message = string.Empty;
    }

    [Serializable]
    internal sealed class ProjectCommandResultResponse
    {
        public bool found;
        public bool completed;
        public bool success;
        public string requestId = string.Empty;
        public string action = string.Empty;
        public string state = string.Empty;
        public string message = string.Empty;
        public string responsePayload = string.Empty;
    }

    [Serializable]
    internal sealed class UpmListRequestOptions
    {
        public bool includeOutdated;
        public bool includeBuiltin;
        public bool includeGit;
    }

    [Serializable]
    internal sealed class UpmInstallRequestOptions
    {
        public string target = string.Empty;
    }

    [Serializable]
    internal sealed class UpmRemoveRequestOptions
    {
        public string packageId = string.Empty;
    }

    [Serializable]
    internal sealed class UpmPackageEntry
    {
        public string packageId = string.Empty;
        public string displayName = string.Empty;
        public string version = string.Empty;
        public string source = string.Empty;
        public string latestCompatibleVersion = string.Empty;
        public bool isOutdated;
        public bool isDeprecated;
        public bool isPreview;
    }

    [Serializable]
    internal sealed class UpmListResponse
    {
        public UpmPackageEntry[] packages = Array.Empty<UpmPackageEntry>();
    }

    [Serializable]
    internal sealed class UpmInstallResponse
    {
        public string packageId = string.Empty;
        public string version = string.Empty;
        public string source = string.Empty;
        public string targetType = string.Empty;
    }

    // ── Custom tool bridge ───────────────────────────────────────────────────────

    [Serializable]
    internal sealed class CustomToolBridgeRequest
    {
        public string toolName = string.Empty;
        public string argsJson = string.Empty;
    }

    [Serializable]
    internal sealed class CustomToolBridgeResponse
    {
        public bool ok;
        public string result = string.Empty;
        public string message = string.Empty;
    }

    // ── Eval bridge ────────────────────────────────────────────────────────────

    [Serializable]
    internal sealed class EvalRequestPayload
    {
        public string code = string.Empty;
        public string declarations = string.Empty;
        public int timeoutMs = 10000;
    }

    // ── ExecV2 adapter ──────────────────────────────────────────────────────────

    /// <summary>Incoming ExecV2Request JSON as parsed by JsonUtility on the Unity side.</summary>
    [Serializable]
    internal sealed class ExecV2AdapterRequest
    {
        public string operation = string.Empty;
        public string requestId = string.Empty;
        public ExecV2AdapterArgs args = new();
    }

    /// <summary>Flat union of all per-operation arg fields for ExecV2 adapter dispatch.</summary>
    [Serializable]
    internal sealed class ExecV2AdapterArgs
    {
        // asset.rename / asset.remove / asset.create / asset.create_script
        public string assetPath = string.Empty;
        public string newAssetPath = string.Empty;
        public string content = string.Empty;
        // hierarchy / scene utility selectors
        public string query = string.Empty;
        public int limit = 20;
        public int parentId;
        public int targetId;
        public string parent = string.Empty;
        public string tag = string.Empty;
        public string layer = string.Empty;
        public string component = string.Empty;
        public string name = string.Empty;
        public string scenePath = string.Empty;
        // build.exec
        public string method = string.Empty;
        // upm.remove
        public string packageId = string.Empty;
        // build.scenes.set
        public string[] scenes = Array.Empty<string>();
    }
}
#endif
