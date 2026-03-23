#if UNITY_EDITOR
using System;
using System.IO;
using UnityEngine;

namespace UniFocl.EditorBridge
{
    /// <summary>
    /// Persisted per-project editor configuration for the unifocl bridge.
    /// Stored at &lt;projectRoot&gt;/.unifocl/editor-config.json.
    /// </summary>
    [Serializable]
    internal sealed class UnifoclEditorConfig
    {
        // ── Fields (camelCase so the JSON file is human-friendly) ─────────────

        /// <summary>
        /// When true, <see cref="UnifoclCompilationService.RequestRecompile"/> will
        /// attempt to bring the Unity editor window to the foreground before requesting
        /// script compilation, ensuring the file-watcher ticks immediately.
        /// Set to false on headless CI runners or when the window grab causes issues.
        /// </summary>
        public bool allowWindowGrab = true;

        // ── Persistence ───────────────────────────────────────────────────────

        private static string ConfigPath
        {
            get
            {
                var root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                return Path.Combine(root, ".unifocl", "editor-config.json");
            }
        }

        /// <summary>Loads config from disk, returning defaults if the file is absent or unreadable.</summary>
        public static UnifoclEditorConfig Load()
        {
            try
            {
                var path = ConfigPath;
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    var cfg  = JsonUtility.FromJson<UnifoclEditorConfig>(json);
                    if (cfg is not null)
                        return cfg;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[unifocl] editor-config load failed (using defaults): {ex.Message}");
            }
            return new UnifoclEditorConfig();
        }

        /// <summary>Persists this config to disk.</summary>
        public void Save()
        {
            try
            {
                var path = ConfigPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonUtility.ToJson(this, prettyPrint: true));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[unifocl] editor-config save failed: {ex.Message}");
            }
        }
    }
}
#endif
