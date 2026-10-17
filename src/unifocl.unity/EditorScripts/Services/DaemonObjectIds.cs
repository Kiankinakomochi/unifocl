#if UNITY_EDITOR
#nullable enable
using UnityEditor;
using UnityEngine;
#if UNITY_6000_5_OR_NEWER
using System.Collections.Generic;
#endif

namespace UniFocl.EditorBridge
{
    /// <summary>
    /// Int object ids for the bridge protocol (hierarchy node ids, target/parent ids).
    /// Before Unity 6.5 these are plain instance ids. From 6.5 on, <c>GetInstanceID</c> and
    /// <c>InstanceIDToObject</c> are hard errors and <c>EntityId</c> is a 64-bit value with no int
    /// conversion (6.6 throws <c>NotImplementedException</c> from the cast), so the id is the low
    /// 32 bits of the EntityId raw data, with a per-domain map for the reverse lookup. Ids not in
    /// the map (e.g. issued before a domain reload) are recovered by re-registering every loaded
    /// GameObject, which yields the same low bits as long as the EntityId is unchanged.
    /// </summary>
    internal static class DaemonObjectIds
    {
#if UNITY_6000_5_OR_NEWER
        private static readonly object Sync = new();
        private static readonly Dictionary<int, EntityId> IdToEntity = new();
        private static readonly Dictionary<EntityId, int> EntityToId = new();
        private static int _nextFallbackId = int.MinValue;
#endif

        public static int GetId(Object obj)
        {
#if UNITY_6000_5_OR_NEWER
            lock (Sync)
            {
                return RegisterLocked(obj.GetEntityId());
            }
#else
#pragma warning disable CS0618 // obsolete (warning only) in 6.0-6.4; the EntityId path above covers 6.5+
            return obj.GetInstanceID();
#pragma warning restore CS0618
#endif
        }

        public static Object? FromId(int id)
        {
#if UNITY_6000_5_OR_NEWER
            if (TryLookup(id, out var entityId))
            {
                return EditorUtility.EntityIdToObject(entityId);
            }

            RegisterLoadedGameObjects();
            return TryLookup(id, out entityId) ? EditorUtility.EntityIdToObject(entityId) : null;
#else
#pragma warning disable CS0618 // obsolete (warning only) in 6.0-6.4; the EntityId path above covers 6.5+
            return EditorUtility.InstanceIDToObject(id);
#pragma warning restore CS0618
#endif
        }

        public static bool IsLoadingAssetPreview(Object asset)
        {
#if UNITY_6000_5_OR_NEWER
            return AssetPreview.IsLoadingAssetPreview(asset.GetEntityId());
#else
#pragma warning disable CS0618 // obsolete (warning only) in 6.0-6.4; the EntityId path above covers 6.5+
            return AssetPreview.IsLoadingAssetPreview(asset.GetInstanceID());
#pragma warning restore CS0618
#endif
        }

#if UNITY_6000_5_OR_NEWER
        private static bool TryLookup(int id, out EntityId entityId)
        {
            lock (Sync)
            {
                return IdToEntity.TryGetValue(id, out entityId);
            }
        }

        private static void RegisterLoadedGameObjects()
        {
            var gameObjects = Resources.FindObjectsOfTypeAll<GameObject>();
            lock (Sync)
            {
                foreach (var gameObject in gameObjects)
                {
                    RegisterLocked(gameObject.GetEntityId());
                }
            }
        }

        private static int RegisterLocked(EntityId entityId)
        {
            if (EntityToId.TryGetValue(entityId, out var known))
            {
                return known;
            }

            var id = unchecked((int)EntityId.ToULong(entityId));
            // 0 means "no id" and int.MaxValue is the hierarchy scene-root sentinel in the protocol;
            // a low-bits collision with another entity also takes the next free fallback id.
            while (id == 0 || id == int.MaxValue || IdToEntity.ContainsKey(id))
            {
                id = _nextFallbackId++;
            }

            IdToEntity[id] = entityId;
            EntityToId[entityId] = id;
            return id;
        }
#endif
    }
}
#endif
