#if UNITY_EDITOR
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UniFocl.EditorBridge
{
    /// <summary>
    /// Validates Unity editor state before a compile request is dispatched.
    ///
    /// The goal is to replace silent failure modes with structured rejections
    /// the CLI can surface to the caller. Without this gate, requesting compile
    /// while the editor is already busy (in another compile, an asset import,
    /// or domain reload) silently no-ops and leaves the caller waiting forever.
    /// </summary>
    internal static class CompilationStateValidationService
    {
        public readonly struct ValidationResult
        {
            public ValidationResult(bool isValid, string errorCode, string errorMessage)
            {
                IsValid = isValid;
                ErrorCode = errorCode ?? string.Empty;
                ErrorMessage = errorMessage ?? string.Empty;
            }

            public bool IsValid { get; }
            public string ErrorCode { get; }
            public string ErrorMessage { get; }

            public static ValidationResult Success() => new(true, string.Empty, string.Empty);
            public static ValidationResult Failure(string code, string message) => new(false, code, message);
        }

        public static ValidationResult Validate()
        {
            if (EditorApplication.isCompiling)
            {
                return ValidationResult.Failure(
                    "already-compiling",
                    "Compilation is already in progress. Wait for it to finish before requesting another compile.");
            }

            if (EditorApplication.isUpdating)
            {
                return ValidationResult.Failure(
                    "editor-updating",
                    "Editor is updating (asset import in progress). Wait for the update to complete before requesting compile.");
            }

            if (CompileDomainReloadState.IsDomainReloadInProgress())
            {
                return ValidationResult.Failure(
                    "domain-reload-in-progress",
                    "Domain reload is in progress. Wait for the editor to settle before requesting compile.");
            }

            string[] duplicateAsmdefs = FindDuplicateAsmdefNames();
            if (duplicateAsmdefs.Length > 0)
            {
                return ValidationResult.Failure(
                    "duplicate-asmdef",
                    "Duplicate Assembly Definition names will block compilation: "
                    + string.Join(", ", duplicateAsmdefs)
                    + ". Resolve the duplicates before requesting compile.");
            }

            return ValidationResult.Success();
        }

        // ── Duplicate asmdef detection ────────────────────────────────────────
        //
        // Two .asmdef files with the same `name` field are silent-fatal: Unity
        // skips compilation of both assemblies and surfaces only a console
        // warning. We catch this case before requesting compile so the CLI
        // gets an actionable error instead of "compile did not start".

        [Serializable]
        private sealed class AsmdefStub
        {
            public string name = string.Empty;
        }

        private static string[] FindDuplicateAsmdefNames()
        {
            string assetsPath = Application.dataPath;
            if (string.IsNullOrEmpty(assetsPath) || !Directory.Exists(assetsPath))
            {
                return Array.Empty<string>();
            }

            string[] paths;
            try
            {
                paths = Directory.GetFiles(assetsPath, "*.asmdef", SearchOption.AllDirectories);
            }
            catch (Exception)
            {
                return Array.Empty<string>();
            }

            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string path in paths)
            {
                string? name = TryReadAsmdefName(path);
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                seen[name!] = seen.TryGetValue(name!, out int prior) ? prior + 1 : 1;
            }

            return seen
                .Where(kv => kv.Value > 1)
                .Select(kv => kv.Key)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();
        }

        private static string? TryReadAsmdefName(string path)
        {
            try
            {
                string json = File.ReadAllText(path);
                var stub = JsonUtility.FromJson<AsmdefStub>(json);
                return stub == null ? null : stub.name;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Tracks whether a domain reload is in flight using <see cref="SessionState"/>
    /// so the flag survives the reload itself. <see cref="AssemblyReloadEvents"/>
    /// callbacks straddle the reload boundary, so we set the flag before the
    /// reload begins and clear it once managed code is back online.
    /// </summary>
    internal static class CompileDomainReloadState
    {
        private const string DomainReloadKey = "unifocl.compile.domainReloadInProgress";

        private static bool _registered;

        [InitializeOnLoadMethod]
        private static void Register()
        {
            if (_registered)
            {
                return;
            }

            _registered = true;

            // afterAssemblyReload fires once the new domain is fully online,
            // including [InitializeOnLoadMethod] static constructors. Clear the
            // flag now so anyone polling SessionState sees us as ready again.
            AssemblyReloadEvents.afterAssemblyReload += () =>
            {
                SessionState.SetBool(DomainReloadKey, false);
            };

            AssemblyReloadEvents.beforeAssemblyReload += () =>
            {
                SessionState.SetBool(DomainReloadKey, true);
            };

            // If the editor was still mid-reload when this static constructor
            // runs (e.g. the previous AppDomain hadn't yet returned from
            // beforeAssemblyReload before being torn down), the flag may be
            // stuck true. Clear it once on init so we never carry stale state.
            SessionState.SetBool(DomainReloadKey, false);
        }

        public static bool IsDomainReloadInProgress() => SessionState.GetBool(DomainReloadKey, false);
    }
}
#endif
