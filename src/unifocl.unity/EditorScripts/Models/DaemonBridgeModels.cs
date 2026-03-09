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
    }

    [Serializable]
    internal sealed class HierarchyCommandResponse
    {
        public bool ok;
        public string message = string.Empty;
        public int nodeId;
        public bool isActive;
    }

    [Serializable]
    internal sealed class HierarchySearchRequest
    {
        public string query = string.Empty;
        public int limit = 20;
        public int parentId;
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
    }

    [Serializable]
    internal sealed class InspectorSearchResult
    {
        public string scope = string.Empty;
        public int componentIndex = -1;
        public string name = string.Empty;
        public string path = string.Empty;
        public double score;
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
    }

    [Serializable]
    internal sealed class ProjectCommandRequest
    {
        public string action = string.Empty;
        public string assetPath = string.Empty;
        public string newAssetPath = string.Empty;
        public string content = string.Empty;
        public string requestId = string.Empty;
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
}
#endif
