#if UNITY_EDITOR
#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.Compilation;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using UpmPackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace UniFocl.EditorBridge
{
    internal static partial class DaemonProjectService
    {
        private static readonly object BuildStateLock = new();
        private static readonly object CompilationStateLock = new();
        private static readonly Regex PercentRegex = new(@"(?<!\d)(\d{1,3})\s*%", RegexOptions.Compiled);
        private static readonly SemaphoreSlim FileSystemMutationSemaphore = new(1, 1);
        private static readonly Lazy<Dictionary<string, Type>> ScriptableObjectTypeLookup = new(BuildScriptableObjectTypeLookup);
        private static BuildRuntimeState _buildState = new();
        private static CompilationRuntimeState _compilationState = new();
        private static int _unityMainThreadId;
        private static bool _buildInProgress;
        private static bool _cancelRequested;
        private static string _activeBuildAction = string.Empty;
        private const string VcsModeUvcsAll = "uvcs_all";
        private const string VcsModeUvcsHybridGitIgnore = "uvcs_hybrid_gitignore";
        private const string VcsOwnerUvcs = "uvcs";
        private const string LastOpenedSceneMarkerRelativePath = ".unifocl/last-opened-scene.txt";

        [Serializable]
        private sealed class HierarchyFindPayload
        {
            public string query = string.Empty;
            public int limit = 20;
            public int parentId;
            public string tag = string.Empty;
            public string layer = string.Empty;
            public string component = string.Empty;
        }

        [Serializable]
        private sealed class SceneCommandPayload
        {
            public string scenePath = string.Empty;
        }

        [Serializable]
        private sealed class HierarchyDuplicatePayload
        {
            public int targetId;
            public int parentId;
            public string name = string.Empty;
        }

        [InitializeOnLoadMethod]
        private static void InitializeCompilationTracking()
        {
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
        }

        private static void OnCompilationStarted(object obj)
        {
            lock (CompilationStateLock)
            {
                _compilationState = new CompilationRuntimeState
                {
                    running = true,
                    succeeded = false,
                    errors = Array.Empty<string>(),
                    startedAtUtc = DateTime.UtcNow.ToString("O"),
                    finishedAtUtc = string.Empty
                };
            }
        }

        private static void OnAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] messages)
        {
            var errorMessages = messages
                .Where(m => m.type == CompilerMessageType.Error)
                .Select(m => m.message)
                .ToArray();

            if (errorMessages.Length == 0)
            {
                return;
            }

            lock (CompilationStateLock)
            {
                var existing = _compilationState.errors ?? Array.Empty<string>();
                var combined = new string[existing.Length + errorMessages.Length];
                Array.Copy(existing, combined, existing.Length);
                Array.Copy(errorMessages, 0, combined, existing.Length, errorMessages.Length);
                _compilationState = new CompilationRuntimeState
                {
                    running = _compilationState.running,
                    succeeded = _compilationState.succeeded,
                    errors = combined,
                    startedAtUtc = _compilationState.startedAtUtc,
                    finishedAtUtc = _compilationState.finishedAtUtc
                };
            }
        }

        private static void OnCompilationFinished(object obj)
        {
            lock (CompilationStateLock)
            {
                _compilationState = new CompilationRuntimeState
                {
                    running = false,
                    succeeded = _compilationState.errors == null || _compilationState.errors.Length == 0,
                    errors = _compilationState.errors ?? Array.Empty<string>(),
                    startedAtUtc = _compilationState.startedAtUtc,
                    finishedAtUtc = DateTime.UtcNow.ToString("O")
                };
            }
        }

        public static string GetCompilationStatusPayload()
        {
            lock (CompilationStateLock)
            {
                return JsonUtility.ToJson(new CompilationStatusResponse
                {
                    running = _compilationState.running,
                    succeeded = _compilationState.succeeded,
                    errors = _compilationState.errors ?? Array.Empty<string>(),
                    startedAtUtc = _compilationState.startedAtUtc,
                    finishedAtUtc = _compilationState.finishedAtUtc
                });
            }
        }

        public static string Execute(string payload)
        {
            return ExecuteAsync(payload).GetAwaiter().GetResult();
        }

        public static Task<string> ExecuteAsync(string payload)
        {
            return ExecuteAsync(payload, durableDispatch: true);
        }

        public static string SubmitMutationPayload(string payload)
        {
            ProjectCommandRequest? request;
            try
            {
                request = JsonUtility.FromJson<ProjectCommandRequest>(payload);
            }
            catch
            {
                request = null;
            }

            if (request is null || string.IsNullOrWhiteSpace(request.action))
            {
                return JsonUtility.ToJson(new ProjectCommandAcceptedResponse
                {
                    ok = false,
                    requestId = string.Empty,
                    action = string.Empty,
                    stage = "error",
                    message = "missing project command payload"
                });
            }

            if (string.IsNullOrWhiteSpace(request.requestId))
            {
                request.requestId = Guid.NewGuid().ToString("N");
            }

            try
            {
                var accepted = DaemonMutationCommandDispatcher.Submit(request);
                return JsonUtility.ToJson(accepted);
            }
            catch (Exception ex)
            {
                return JsonUtility.ToJson(new ProjectCommandAcceptedResponse
                {
                    ok = false,
                    requestId = request.requestId ?? string.Empty,
                    action = request.action,
                    stage = "error",
                    message = $"failed to queue mutation command: {ex.GetType().Name}: {ex.Message}"
                });
            }
        }

        public static string GetMutationStatusPayload(string requestId)
        {
            if (!DaemonMutationCommandDispatcher.TryGetStatus(requestId, out var status))
            {
                return JsonUtility.ToJson(new ProjectCommandStatusResponse
                {
                    requestId = requestId ?? string.Empty,
                    action = string.Empty,
                    active = false,
                    success = false,
                    stage = "not-found",
                    detail = "project command request id not found",
                    startedAtUtc = string.Empty,
                    lastUpdatedAtUtc = DateTime.UtcNow.ToString("O"),
                    finishedAtUtc = string.Empty,
                    isDurable = true,
                    state = "not-found"
                });
            }

            return JsonUtility.ToJson(status);
        }

        public static string GetMutationResultPayload(string requestId)
        {
            return JsonUtility.ToJson(DaemonMutationCommandDispatcher.GetResult(requestId));
        }

        public static string CancelMutationPayload(string requestId)
        {
            return JsonUtility.ToJson(DaemonMutationCommandDispatcher.Cancel(requestId));
        }

        public static bool TryGetDurableMutationStatus(string? requestId, out ProjectCommandStatusResponse status)
        {
            status = new ProjectCommandStatusResponse();
            if (string.IsNullOrWhiteSpace(requestId))
            {
                return false;
            }

            return DaemonMutationCommandDispatcher.TryGetStatus(requestId, out status);
        }

        internal static Task<string> ExecuteMutationWorkerAsync(ProjectCommandRequest request)
        {
            return ExecuteCommandCoreAsync(request, durableDispatch: false);
        }

        private static Task<string> ExecuteAsync(string payload, bool durableDispatch)
        {
            if (_unityMainThreadId == 0)
            {
                _unityMainThreadId = Environment.CurrentManagedThreadId;
            }

            DaemonMutationCommandDispatcher.Initialize();

            ProjectCommandRequest? request;
            try
            {
                request = JsonUtility.FromJson<ProjectCommandRequest>(payload);
            }
            catch
            {
                return Task.FromResult(JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = "invalid project command payload" }));
            }

            if (request is null || string.IsNullOrWhiteSpace(request.action))
            {
                return Task.FromResult(JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = "missing project command payload" }));
            }

            if (DaemonMutationTransactionCoordinator.IsProjectMutation(request.action))
            {
                var decision = DaemonMutationTransactionCoordinator.ValidateProjectIntent(request.action, request.intent);
                if (!decision.Accepted)
                {
                    return Task.FromResult(JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = decision.Message }));
                }

                if (!decision.ShouldExecute)
                {
                    return Task.FromResult(JsonUtility.ToJson(new ProjectCommandResponse
                    {
                        ok = true,
                        message = decision.Message,
                        kind = "dry-run"
                    }));
                }

                if (decision.IsDryRun && !SupportsFileDryRunPreview(request.action))
                {
                    return Task.FromResult(JsonUtility.ToJson(new ProjectCommandResponse
                    {
                        ok = true,
                        message = $"dry-run preview is not implemented for action: {request.action}",
                        kind = "dry-run",
                        content = DaemonDryRunDiffService.BuildFileDiffPayload(
                            $"no-op preview for {request.action}",
                            new[]
                            {
                                new MutationPathChange
                                {
                                    action = request.action,
                                    path = request.assetPath,
                                    nextPath = request.newAssetPath,
                                    metaPath = string.IsNullOrWhiteSpace(request.assetPath) ? string.Empty : request.assetPath + ".meta"
                                }
                            })
                    }));
                }
            }

            try
            {
                return ExecuteCommandCoreAsync(request, durableDispatch);
            }
            catch (Exception ex)
            {
                return Task.FromResult(JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = false,
                    message = $"project command exception: {ex.GetType().Name}: {ex.Message}"
                }));
            }
        }

        private static Task<string> ExecuteCommandCoreAsync(ProjectCommandRequest request, bool durableDispatch)
        {
            var isDryRun = request.intent is not null && request.intent.flags is not null && request.intent.flags.dryRun;
            if (durableDispatch && !isDryRun)
            {
                return Task.FromResult(SubmitMutationPayload(JsonUtility.ToJson(request)));
            }

            return request.action switch
            {
                "healthcheck" => Task.FromResult(JsonUtility.ToJson(new ProjectCommandResponse { ok = true, message = "project command endpoint ready", kind = "healthcheck" })),
                "mk-script" => Task.FromResult(ExecuteCreateScript(request)),
                "mk-asset" => Task.FromResult(ExecuteCreateAsset(request)),
                "rename-asset" => Task.FromResult(ExecuteRenameAsset(request)),
                "duplicate-asset" => Task.FromResult(ExecuteDuplicateAsset(request)),
                "remove-asset" => Task.FromResult(ExecuteRemoveAsset(request)),
                "load-asset" => Task.FromResult(ExecuteLoadAsset(request)),
                "upm-list" => Task.FromResult(ExecuteUpmList(request)),
                "upm-remove" => ExecuteUpmRemove(request),
                "build-run" => Task.FromResult(ExecuteBuildRun(request)),
                "build-exec" => Task.FromResult(ExecuteBuildExec(request)),
                "build-scenes-get" => Task.FromResult(ExecuteBuildScenesGet()),
                "build-scenes-set" => Task.FromResult(ExecuteBuildScenesSet(request)),
                "build-addressables" => Task.FromResult(ExecuteBuildAddressables(request)),
                "addressables-cli" => Task.FromResult(ExecuteAddressablesCommand(request)),
                "build-cancel" => Task.FromResult(ExecuteBuildCancel()),
                "build-targets" => Task.FromResult(ExecuteBuildTargets()),
                "compile-request" => Task.FromResult(ExecuteCompileRequest()),
                "compile-status" => Task.FromResult(ExecuteCompileStatus()),
                "hierarchy-find" => Task.FromResult(ExecuteHierarchyFind(request)),
                "settings-inspect" => Task.FromResult(ExecuteSettingsInspect()),
                "console-clear" => Task.FromResult(ExecuteConsoleClear()),
                "prefab-create" => Task.FromResult(ExecutePrefabCreate(request)),
                "prefab-apply" => Task.FromResult(ExecutePrefabApply(request)),
                "prefab-revert" => Task.FromResult(ExecutePrefabRevert(request)),
                "prefab-unpack" => Task.FromResult(ExecutePrefabUnpack(request)),
                "prefab-variant" => Task.FromResult(ExecutePrefabVariant(request)),
                "scene-load" => Task.FromResult(ExecuteSceneLoad(request, additive: false)),
                "scene-add" => Task.FromResult(ExecuteSceneLoad(request, additive: true)),
                "scene-unload" => Task.FromResult(ExecuteSceneUnload(request)),
                "scene-remove" => Task.FromResult(ExecuteSceneUnload(request)),
                "hierarchy-duplicate" => Task.FromResult(ExecuteHierarchyDuplicate(request)),
                "eval-code" => DaemonEvalService.ExecuteAsync(request, isDryRun),
                "validate-scene-list" => Task.FromResult(DaemonValidateService.ExecuteValidateSceneList()),
                "validate-missing-scripts" => Task.FromResult(DaemonValidateService.ExecuteValidateMissingScripts()),
                "validate-build-settings" => Task.FromResult(DaemonValidateService.ExecuteValidateBuildSettings()),
                "validate-asset-refs" => Task.FromResult(DaemonValidateService.ExecuteValidateAssetRefs()),
                "validate-addressables" => Task.FromResult(DaemonValidateService.ExecuteValidateAddressables()),
                "build-artifact-metadata" => Task.FromResult(DaemonBuildReportService.ExecuteBuildArtifactMetadata()),
                "build-failure-classify" => Task.FromResult(DaemonBuildReportService.ExecuteBuildFailureClassify()),
                "diag-script-defines" => Task.FromResult(DaemonDiagService.ExecuteDiagScriptDefines()),
                "diag-compile-errors" => Task.FromResult(DaemonDiagService.ExecuteDiagCompileErrors()),
                "diag-assembly-graph" => Task.FromResult(DaemonDiagService.ExecuteDiagAssemblyGraph()),
                "diag-scene-deps" => Task.FromResult(DaemonDiagService.ExecuteDiagSceneDeps()),
                "diag-prefab-deps" => Task.FromResult(DaemonDiagService.ExecuteDiagPrefabDeps()),
                "diag-asset-size" => Task.FromResult(DaemonDiagService.ExecuteDiagAssetSize()),
                "diag-import-hotspots" => Task.FromResult(DaemonDiagService.ExecuteDiagImportHotspots()),
                "query-mk-types" => Task.FromResult(ExecuteQueryMkTypes()),
                "query-hierarchy-mk-types" => Task.FromResult(ExecuteQueryHierarchyMkTypes()),
                "query-component-types" => Task.FromResult(ExecuteQueryComponentTypes()),
                _ => Task.FromResult(JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = $"unsupported action: {request.action}" }))
            };
        }

        public static string GetBuildStatusPayload()
        {
            lock (BuildStateLock)
            {
                return JsonUtility.ToJson(new BuildStatusResponse
                {
                    running = _buildState.running,
                    cancelRequested = _buildState.cancelRequested,
                    progress01 = _buildState.progress01,
                    step = _buildState.step,
                    kind = _buildState.kind,
                    logPath = _buildState.logPath,
                    outputPath = _buildState.outputPath,
                    startedAtUtc = _buildState.startedAtUtc,
                    finishedAtUtc = _buildState.finishedAtUtc,
                    success = _buildState.success,
                    message = _buildState.message,
                    lastHeartbeatUtc = _buildState.lastHeartbeatUtc,
                    lastDiagnostic = _buildState.lastDiagnostic,
                    lastException = _buildState.lastException
                });
            }
        }

        public static string ReadBuildLogPayload(long offset, int limit, bool errorsOnly)
        {
            string? logPath;
            lock (BuildStateLock)
            {
                logPath = _buildState.logPath;
            }

            if (string.IsNullOrWhiteSpace(logPath) || !File.Exists(logPath))
            {
                return JsonUtility.ToJson(new BuildLogChunkResponse
                {
                    nextOffset = Math.Max(0, offset),
                    lines = Array.Empty<BuildLogLine>()
                });
            }

            var safeOffset = Math.Max(0, offset);
            var safeLimit = Math.Clamp(limit, 1, 400);
            var lines = new List<BuildLogLine>(safeLimit);
            long nextOffset = safeOffset;
            using (var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (safeOffset > stream.Length)
                {
                    safeOffset = stream.Length;
                }

                stream.Seek(safeOffset, SeekOrigin.Begin);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                while (!reader.EndOfStream && lines.Count < safeLimit)
                {
                    var line = reader.ReadLine();
                    if (line is null)
                    {
                        continue;
                    }

                    var parsed = ParseLogLine(line);
                    if (errorsOnly && !parsed.level.Equals("error", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    lines.Add(parsed);
                }

                nextOffset = stream.Position;
            }

            return JsonUtility.ToJson(new BuildLogChunkResponse
            {
                nextOffset = nextOffset,
                lines = lines.ToArray()
            });
        }

        private static string ExecuteRenameAsset(ProjectCommandRequest request)
        {
            return ExecuteWithRollbackStash(
                request,
                mutationName: "rename-asset",
                executeMutation: () =>
                {
                    if (!IsValidAssetPath(request.assetPath) || !IsValidAssetPath(request.newAssetPath))
                    {
                        return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = "rename-asset requires assetPath and newAssetPath" });
                    }

                    if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(request.assetPath) is null)
                    {
                        return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = $"asset not found: {request.assetPath}" });
                    }

                    var error = AssetDatabase.MoveAsset(request.assetPath, request.newAssetPath);
                    if (!string.IsNullOrWhiteSpace(error))
                    {
                        return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = error });
                    }

                    AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                    return JsonUtility.ToJson(new ProjectCommandResponse { ok = true, message = "asset renamed" });
                },
                preMutationTargets: new[] { request.assetPath },
                rollbackCleanupTargets: new[] { request.newAssetPath });
        }

        private static string ExecuteRemoveAsset(ProjectCommandRequest request)
        {
            return ExecuteWithRollbackStash(
                request,
                mutationName: "remove-asset",
                executeMutation: () =>
                {
                    if (!IsValidAssetPath(request.assetPath))
                    {
                        return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = "remove-asset requires assetPath" });
                    }

                    if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(request.assetPath) is null
                        && !AssetDatabase.IsValidFolder(request.assetPath))
                    {
                        return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = $"asset not found: {request.assetPath}" });
                    }

                    if (!AssetDatabase.MoveAssetToTrash(request.assetPath))
                    {
                        return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = $"failed to remove asset: {request.assetPath}" });
                    }

                    AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                    return JsonUtility.ToJson(new ProjectCommandResponse { ok = true, message = "asset removed" });
                },
                preMutationTargets: new[] { request.assetPath });
        }

        private static string ExecuteDuplicateAsset(ProjectCommandRequest request)
        {
            return ExecuteWithRollbackStash(
                request,
                mutationName: "duplicate-asset",
                executeMutation: () =>
                {
                    if (!IsValidAssetPath(request.assetPath) || !IsValidAssetPath(request.newAssetPath))
                    {
                        return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = "duplicate-asset requires assetPath and newAssetPath" });
                    }

                    if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(request.assetPath) is null
                        && !AssetDatabase.IsValidFolder(request.assetPath))
                    {
                        return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = $"asset not found: {request.assetPath}" });
                    }

                    if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(request.newAssetPath) is not null
                        || AssetDatabase.IsValidFolder(request.newAssetPath))
                    {
                        return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = $"target already exists: {request.newAssetPath}" });
                    }

                    if (!AssetDatabase.CopyAsset(request.assetPath, request.newAssetPath))
                    {
                        return JsonUtility.ToJson(new ProjectCommandResponse
                        {
                            ok = false,
                            message = $"failed to duplicate asset: {request.assetPath} -> {request.newAssetPath}"
                        });
                    }

                    AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                    return JsonUtility.ToJson(new ProjectCommandResponse { ok = true, message = "asset duplicated" });
                },
                preMutationTargets: new[] { request.assetPath },
                rollbackCleanupTargets: new[] { request.newAssetPath });
        }

        private static string ExecuteLoadAsset(ProjectCommandRequest request)
        {
            if (!IsValidAssetPath(request.assetPath))
            {
                return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = "load-asset requires assetPath" });
            }

            var extension = Path.GetExtension(request.assetPath);
            if (extension.Equals(".unity", StringComparison.OrdinalIgnoreCase))
            {
                var requestedScenePath = request.assetPath.Replace('\\', '/');
                if (!File.Exists(Path.Combine(GetProjectRoot(), requestedScenePath.Replace('/', Path.DirectorySeparatorChar))))
                {
                    return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = $"scene not found: {request.assetPath}" });
                }

                try
                {
                    if (DaemonSceneManager.TryGetActiveScene(out var activeScene))
                    {
                        DaemonScenePersistenceService.SaveScenesWithoutMarkDirty(
                            "project scene-switch preflight",
                            activeScene);
                    }

                    DaemonHierarchyService.ClearLoadedPrefabSnapshotRoot();

                    if (!DaemonSceneManager.TryLoadSceneSingleAndActivate(requestedScenePath, out _, out var loadError))
                    {
                        return JsonUtility.ToJson(new ProjectCommandResponse
                        {
                            ok = false,
                            message = loadError ?? $"scene load failed: {requestedScenePath}"
                        });
                    }

                    TryPersistLastOpenedScenePath(requestedScenePath);
                    return JsonUtility.ToJson(new ProjectCommandResponse { ok = true, message = "scene loaded", kind = "scene" });
                }
                catch (Exception ex)
                {
                    return JsonUtility.ToJson(new ProjectCommandResponse
                    {
                        ok = false,
                        message = $"scene load failed: {ex.Message}"
                    });
                }
            }

            if (extension.Equals(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                var requestedPrefabPath = request.assetPath.Replace('\\', '/');
                if (!File.Exists(Path.Combine(GetProjectRoot(), requestedPrefabPath.Replace('/', Path.DirectorySeparatorChar))))
                {
                    return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = $"prefab not found: {request.assetPath}" });
                }

                try
                {
                    if (DaemonSceneManager.TryGetActiveScene(out var activeScene))
                    {
                        DaemonScenePersistenceService.SaveScenesWithoutMarkDirty(
                            "project prefab-switch preflight",
                            activeScene);
                    }

                    if (!DaemonHierarchyService.TryLoadPrefabSnapshotRoot(requestedPrefabPath, out var loadError))
                    {
                        return JsonUtility.ToJson(new ProjectCommandResponse
                        {
                            ok = false,
                            message = loadError ?? $"prefab load failed: {requestedPrefabPath}"
                        });
                    }

                    return JsonUtility.ToJson(new ProjectCommandResponse { ok = true, message = "prefab loaded", kind = "prefab" });
                }
                catch (Exception ex)
                {
                    return JsonUtility.ToJson(new ProjectCommandResponse
                    {
                        ok = false,
                        message = $"prefab load failed: {ex.Message}"
                    });
                }
            }

            if (extension.Equals(".cs", StringComparison.OrdinalIgnoreCase))
            {
                var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(request.assetPath);
                if (asset is null)
                {
                    return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = $"script not found: {request.assetPath}" });
                }

                AssetDatabase.OpenAsset(asset);
                return JsonUtility.ToJson(new ProjectCommandResponse { ok = true, message = "script opened", kind = "script" });
            }

            return JsonUtility.ToJson(new ProjectCommandResponse
            {
                ok = false,
                message = $"unsupported asset type: {extension} (supported: .unity, .prefab, .cs)"
            });
        }

        private static string ExecuteHierarchyFind(ProjectCommandRequest request)
        {
            var payload = string.IsNullOrWhiteSpace(request.content)
                ? new HierarchyFindPayload()
                : JsonUtility.FromJson<HierarchyFindPayload>(request.content) ?? new HierarchyFindPayload();
            return DaemonHierarchyService.ExecuteSearch(JsonUtility.ToJson(new HierarchySearchRequest
            {
                query = payload.query,
                limit = payload.limit,
                parentId = payload.parentId,
                tag = payload.tag ?? string.Empty,
                layer = payload.layer ?? string.Empty,
                component = payload.component ?? string.Empty
            }));
        }

        private static string ExecuteHierarchyDuplicate(ProjectCommandRequest request)
        {
            HierarchyDuplicatePayload? payload;
            try
            {
                payload = JsonUtility.FromJson<HierarchyDuplicatePayload>(request.content);
            }
            catch
            {
                payload = null;
            }

            if (payload is null || payload.targetId == 0)
            {
                return JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = false,
                    message = "hierarchy-duplicate requires content.targetId"
                });
            }

            return DaemonHierarchyService.ExecuteCommand(JsonUtility.ToJson(new HierarchyCommandRequest
            {
                action = "duplicate",
                targetId = payload.targetId,
                parentId = payload.parentId,
                name = payload.name ?? string.Empty,
                intent = request.intent
            }));
        }

        private static string ExecuteSettingsInspect()
        {
            try
            {
                var playerDump = DumpStaticSettingsType(typeof(PlayerSettings));
                var editorDump = DumpStaticSettingsType(typeof(EditorSettings));
                var content = JsonUtility.ToJson(new SettingsInspectResponse
                {
                    playerSettings = playerDump,
                    editorSettings = editorDump
                });
                return JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = true,
                    message = "inspected editor settings",
                    kind = "settings",
                    content = content
                });
            }
            catch (Exception ex)
            {
                return JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = false,
                    message = $"settings inspection failed: {ex.Message}"
                });
            }
        }

        private static string ExecuteConsoleClear()
        {
            try
            {
                var logEntriesType = Type.GetType("UnityEditor.LogEntries, UnityEditor");
                var clearMethod = logEntriesType?.GetMethod("Clear", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (clearMethod is null)
                {
                    return JsonUtility.ToJson(new ProjectCommandResponse
                    {
                        ok = false,
                        message = "console clear is unavailable: UnityEditor.LogEntries.Clear() not found"
                    });
                }

                clearMethod.Invoke(null, null);
                return JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = true,
                    message = "console cleared",
                    kind = "console"
                });
            }
            catch (Exception ex)
            {
                return JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = false,
                    message = $"console clear failed: {ex.Message}"
                });
            }
        }

        private static string ExecuteSceneLoad(ProjectCommandRequest request, bool additive)
        {
            var scenePath = ResolveScenePathFromRequest(request);
            if (!IsValidAssetPath(scenePath))
            {
                return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = "scene command requires a valid scenePath (or assetPath)" });
            }

            var normalizedScenePath = scenePath.Replace('\\', '/');
            if (!File.Exists(Path.Combine(GetProjectRoot(), normalizedScenePath.Replace('/', Path.DirectorySeparatorChar))))
            {
                return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = $"scene not found: {normalizedScenePath}" });
            }

            try
            {
                if (!additive && DaemonSceneManager.TryGetActiveScene(out var activeScene))
                {
                    DaemonScenePersistenceService.SaveScenesWithoutMarkDirty("project scene-switch preflight", activeScene);
                }

                DaemonHierarchyService.ClearLoadedPrefabSnapshotRoot();

                var mode = additive ? OpenSceneMode.Additive : OpenSceneMode.Single;
                var openedScene = EditorSceneManager.OpenScene(normalizedScenePath, mode);
                if (!openedScene.IsValid() || !openedScene.isLoaded)
                {
                    return JsonUtility.ToJson(new ProjectCommandResponse
                    {
                        ok = false,
                        message = $"scene load failed: {normalizedScenePath}"
                    });
                }

                if (!SceneManager.SetActiveScene(openedScene))
                {
                    EditorSceneManager.SetActiveScene(openedScene);
                }

                TryPersistLastOpenedScenePath(normalizedScenePath);
                return JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = true,
                    message = additive ? "scene added" : "scene loaded",
                    kind = "scene",
                    content = normalizedScenePath
                });
            }
            catch (Exception ex)
            {
                return JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = false,
                    message = $"scene load failed: {ex.Message}"
                });
            }
        }

        private static string ExecuteSceneUnload(ProjectCommandRequest request)
        {
            var scenePath = ResolveScenePathFromRequest(request);
            if (string.IsNullOrWhiteSpace(scenePath))
            {
                return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = "scene-unload requires content.scenePath (or assetPath)" });
            }

            var normalizedScenePath = scenePath.Replace('\\', '/');
            var scene = SceneManager.GetSceneByPath(normalizedScenePath);
            if (!scene.IsValid() || !scene.isLoaded)
            {
                return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = $"scene is not loaded: {normalizedScenePath}" });
            }

            try
            {
                if (!EditorSceneManager.CloseScene(scene, true))
                {
                    return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = $"scene unload failed: {normalizedScenePath}" });
                }

                if (!DaemonSceneManager.TryGetActiveScene(out _))
                {
                    // If no valid loaded scene remains, clear prefab snapshot root to avoid stale mutation context.
                    DaemonHierarchyService.ClearLoadedPrefabSnapshotRoot();
                }

                return JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = true,
                    message = "scene unloaded",
                    kind = "scene",
                    content = normalizedScenePath
                });
            }
            catch (Exception ex)
            {
                return JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = false,
                    message = $"scene unload failed: {ex.Message}"
                });
            }
        }

        private static string ResolveScenePathFromRequest(ProjectCommandRequest request)
        {
            if (!string.IsNullOrWhiteSpace(request.assetPath))
            {
                return request.assetPath.Trim();
            }

            if (string.IsNullOrWhiteSpace(request.content))
            {
                return string.Empty;
            }

            SceneCommandPayload? payload;
            try
            {
                payload = JsonUtility.FromJson<SceneCommandPayload>(request.content);
            }
            catch
            {
                payload = null;
            }

            return payload?.scenePath?.Trim() ?? string.Empty;
        }

        [Serializable]
        private sealed class SettingsInspectResponse
        {
            public SettingsPropertyEntry[] playerSettings = Array.Empty<SettingsPropertyEntry>();
            public SettingsPropertyEntry[] editorSettings = Array.Empty<SettingsPropertyEntry>();
        }

        [Serializable]
        private sealed class SettingsPropertyEntry
        {
            public string name = string.Empty;
            public string type = string.Empty;
            public string value = string.Empty;
            public string error = string.Empty;
        }

        private static SettingsPropertyEntry[] DumpStaticSettingsType(Type type)
        {
            var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Static)
                .Where(property => property.CanRead
                                   && property.GetIndexParameters().Length == 0)
                .OrderBy(property => property.Name, StringComparer.Ordinal)
                .ToArray();
            var output = new List<SettingsPropertyEntry>(properties.Length);
            foreach (var property in properties)
            {
                var entry = new SettingsPropertyEntry
                {
                    name = property.Name,
                    type = property.PropertyType.Name
                };

                try
                {
                    var value = property.GetValue(null, null);
                    entry.value = SerializeSettingValue(value);
                }
                catch (Exception ex)
                {
                    entry.error = ex.GetType().Name + ": " + ex.Message;
                }

                output.Add(entry);
            }

            return output.ToArray();
        }

        private static string SerializeSettingValue(object? value)
        {
            if (value is null)
            {
                return "null";
            }

            if (value is string text)
            {
                return text;
            }

            if (value is bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal)
            {
                return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            }

            if (value is Enum enumValue)
            {
                return enumValue.ToString();
            }

            if (value is UnityEngine.Object unityObject)
            {
                return unityObject ? unityObject.name : "null";
            }

            if (value is System.Collections.IEnumerable enumerable && value is not IDictionary)
            {
                var parts = new List<string>();
                foreach (var item in enumerable)
                {
                    parts.Add(SerializeSettingValue(item));
                }

                return "[" + string.Join(", ", parts) + "]";
            }

            return value.ToString() ?? string.Empty;
        }

        private static string ExecuteUpmList(ProjectCommandRequest request)
        {
            var options = ParseUpmListOptions(request.content);
            var allPackages = UpmPackageInfo.GetAllRegisteredPackages() ?? Array.Empty<UpmPackageInfo>();
            var filtered = new List<UpmPackageEntry>(allPackages.Length);

            foreach (var package in allPackages)
            {
                if (package is null || string.IsNullOrWhiteSpace(package.name))
                {
                    continue;
                }

                var source = package.source.ToString();
                var isBuiltIn = package.source == PackageSource.BuiltIn;
                var isGit = package.source == PackageSource.Git;
                var latestCompatible = package.versions?.latestCompatible ?? string.Empty;
                var installedVersion = package.version ?? string.Empty;
                var isOutdated = !string.IsNullOrWhiteSpace(latestCompatible)
                                 && !string.IsNullOrWhiteSpace(installedVersion)
                                 && !installedVersion.Equals(latestCompatible, StringComparison.OrdinalIgnoreCase);
                var isDeprecated = package.isDeprecated;
                var isPreview = installedVersion.Contains("preview", StringComparison.OrdinalIgnoreCase)
                                || latestCompatible.Contains("preview", StringComparison.OrdinalIgnoreCase);

                if (!options.includeBuiltin && isBuiltIn)
                {
                    continue;
                }

                if (options.includeGit && !isGit)
                {
                    continue;
                }

                if (options.includeOutdated && !isOutdated)
                {
                    continue;
                }

                filtered.Add(new UpmPackageEntry
                {
                    packageId = package.name,
                    displayName = string.IsNullOrWhiteSpace(package.displayName) ? package.name : package.displayName,
                    version = installedVersion,
                    source = source,
                    latestCompatibleVersion = latestCompatible,
                    isOutdated = isOutdated,
                    isDeprecated = isDeprecated,
                    isPreview = isPreview
                });
            }

            var sorted = filtered
                .OrderBy(p => p.displayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.packageId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var payload = JsonUtility.ToJson(new UpmListResponse
            {
                packages = sorted
            });

            return JsonUtility.ToJson(new ProjectCommandResponse
            {
                ok = true,
                message = "upm list completed",
                kind = "upm-list",
                content = payload
            });
        }

        private static Task<string> ExecuteUpmRemove(ProjectCommandRequest request)
        {
            var options = ParseUpmRemoveOptions(request.content);
            var packageId = options.packageId?.Trim() ?? string.Empty;
            if (!IsRegistryPackageId(packageId))
            {
                return Task.FromResult(JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = false,
                    message = "upm remove requires a valid package id (e.g., com.unity.addressables)"
                }));
            }

            RemoveRequest removeRequest;
            try
            {
                removeRequest = Client.Remove(packageId);
            }
            catch (Exception ex)
            {
                return Task.FromResult(JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = false,
                    message = $"failed to start UPM remove: {ex.Message}"
                }));
            }

            return AwaitRequestWithResolveAsync(
                removeRequest,
                timeoutMessage: "upm remove timed out after 120 seconds",
                failurePrefix: "upm remove failed",
                onSuccess: () => JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = true,
                    message = "upm remove completed",
                    kind = "upm-remove"
                }),
                postVerify: () => !IsPackageInManifest(packageId),
                postVerifyMessage: $"upm remove completed but package is still present in manifest: {packageId}");
        }

        private static string ExecuteCompileRequest()
        {
            UnifoclCompilationService.RequestRecompile();
            return JsonUtility.ToJson(new ProjectCommandResponse
            {
                ok = true,
                message = "compile request submitted",
                kind = "compile-request"
            });
        }

        /// <summary>
        /// Returns a snapshot of errors collected during the last compilation pass.
        /// Thread-safe; reads from the event-based compilation state cache.
        /// </summary>
        internal static string[] GetLastCompileErrors()
        {
            lock (CompilationStateLock)
            {
                return _compilationState.errors ?? Array.Empty<string>();
            }
        }

        private static string ExecuteCompileStatus()
        {
            return JsonUtility.ToJson(new ProjectCommandResponse
            {
                ok = true,
                message = "compile status",
                kind = "compile-status",
                content = GetCompilationStatusPayload()
            });
        }

        [Serializable]
        private sealed class CompilationStatusResponse
        {
            public bool running;
            public bool succeeded;
            public string[] errors = Array.Empty<string>();
            public string startedAtUtc = string.Empty;
            public string finishedAtUtc = string.Empty;
        }

        private sealed class CompilationRuntimeState
        {
            public bool running;
            public bool succeeded;
            public string[]? errors;
            public string startedAtUtc = string.Empty;
            public string finishedAtUtc = string.Empty;
        }

        private static UpmListRequestOptions ParseUpmListOptions(string rawContent)
        {
            if (string.IsNullOrWhiteSpace(rawContent))
            {
                return new UpmListRequestOptions();
            }

            try
            {
                return JsonUtility.FromJson<UpmListRequestOptions>(rawContent) ?? new UpmListRequestOptions();
            }
            catch
            {
                return new UpmListRequestOptions();
            }
        }

        private static UpmRemoveRequestOptions ParseUpmRemoveOptions(string rawContent)
        {
            if (string.IsNullOrWhiteSpace(rawContent))
            {
                return new UpmRemoveRequestOptions();
            }

            try
            {
                return JsonUtility.FromJson<UpmRemoveRequestOptions>(rawContent) ?? new UpmRemoveRequestOptions();
            }
            catch
            {
                return new UpmRemoveRequestOptions();
            }
        }

        private static bool IsRegistryPackageId(string value)
        {
            var packageId = TryExtractRegistryPackageId(value);
            if (string.IsNullOrWhiteSpace(packageId))
            {
                return false;
            }

            var segments = packageId.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2)
            {
                return false;
            }

            foreach (var segment in segments)
            {
                if (segment.Length == 0 || !char.IsLetterOrDigit(segment[0]))
                {
                    return false;
                }

                for (var i = 0; i < segment.Length; i++)
                {
                    var ch = segment[i];
                    if (!char.IsLetterOrDigit(ch) && ch != '-' && ch != '_')
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static string? TryExtractRegistryPackageId(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var at = value.IndexOf('@');
            if (at < 0)
            {
                return value;
            }

            var packageId = value[..at];
            var version = at + 1 < value.Length ? value[(at + 1)..] : string.Empty;
            if (string.IsNullOrWhiteSpace(packageId) || string.IsNullOrWhiteSpace(version))
            {
                return null;
            }

            return packageId;
        }

        private static bool IsPackageInManifest(string packageId)
        {
            if (string.IsNullOrWhiteSpace(packageId))
            {
                return false;
            }

            try
            {
                var manifestPath = Path.Combine(GetProjectRoot(), "Packages", "manifest.json");
                if (!File.Exists(manifestPath))
                {
                    return false;
                }

                var content = File.ReadAllText(manifestPath);
                return content.Contains($"\"{packageId}\"", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static Request? StartUpmResolveRequest()
        {
            var resolveMethod = typeof(Client).GetMethod(
                "Resolve",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null);
            if (resolveMethod is null)
            {
                throw new MissingMethodException("UnityEditor.PackageManager.Client.Resolve()");
            }

            var result = resolveMethod.Invoke(null, null);
            if (resolveMethod.ReturnType == typeof(void) || result is null)
            {
                return null;
            }

            if (result is Request request)
            {
                return request;
            }

            throw new InvalidOperationException(
                $"Unexpected return type from Client.Resolve(): {resolveMethod.ReturnType.FullName}");
        }

        private static Task<string> AwaitRequestWithResolveAsync(
            Request request,
            string timeoutMessage,
            string failurePrefix,
            Func<string> onSuccess,
            Func<bool>? postVerify = null,
            string? postVerifyMessage = null)
        {
            if (!IsUnityMainThread())
            {
                var marshaled = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                CLIDaemon.DispatchOnMainThread(() =>
                {
                    _ = AwaitRequestWithResolveAsync(
                            request,
                            timeoutMessage,
                            failurePrefix,
                            onSuccess,
                            postVerify,
                            postVerifyMessage)
                        .ContinueWith(task =>
                        {
                            if (task.IsCanceled)
                            {
                                marshaled.TrySetCanceled();
                                return;
                            }

                            if (task.IsFaulted)
                            {
                                var exception = task.Exception;
                                if (exception is not null)
                                {
                                    IEnumerable<Exception> exceptions = exception.InnerExceptions.Count > 0
                                        ? exception.InnerExceptions
                                        : new[] { exception };
                                    marshaled.TrySetException(exceptions);
                                }
                                else
                                {
                                    marshaled.TrySetException(new InvalidOperationException("UPM request marshaling failed with unknown exception."));
                                }
                                return;
                            }

                            marshaled.TrySetResult(task.Result);
                        }, TaskScheduler.Default);
                });
                return marshaled.Task;
            }

            var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var timeout = TimeSpan.FromSeconds(120);
            var deadline = EditorApplication.timeSinceStartup + timeout.TotalSeconds;
            var startedAtUtc = DateTime.UtcNow;
            var finishedState = 0;
            var primaryHandled = false;
            var resolveHandled = false;
            var nextRefreshAt = 0d;
            var timeoutCts = new CancellationTokenSource();
            Request? resolveRequest = null;
            var stage = "waiting-primary";
            var checkpointsLock = new object();
            var checkpoints = new List<string>(12)
            {
                $"start thread={Environment.CurrentManagedThreadId}"
            };

            double ElapsedSeconds() => Math.Max(0d, (DateTime.UtcNow - startedAtUtc).TotalSeconds);

            void AddCheckpoint(string detail)
            {
                lock (checkpointsLock)
                {
                    if (checkpoints.Count >= 12)
                    {
                        return;
                    }

                    checkpoints.Add($"{ElapsedSeconds():0.0}s {detail}");
                }

                CLIDaemon.UpdateProjectCommandStage(stage, detail);
            }

            string FormatRequestSnapshot(Request? target, string label)
            {
                if (target is null)
                {
                    return $"{label}=<none>";
                }

                var completed = target.IsCompleted ? "true" : "false";
                var status = target.IsCompleted ? target.Status.ToString() : "InProgress";
                var error = target.Error is null
                    ? "-"
                    : $"{target.Error.errorCode}:{target.Error.message}";
                return $"{label}[completed={completed},status={status},error={error}]";
            }

            string BuildTimeoutDiagnostics()
            {
                var hint = string.IsNullOrWhiteSpace(postVerifyMessage)
                    ? "postVerifyHint=-"
                    : $"postVerifyHint={postVerifyMessage}";
                string trace;
                lock (checkpointsLock)
                {
                    trace = checkpoints.Count == 0 ? "-" : string.Join(" | ", checkpoints);
                }
                return $"stage={stage}; {FormatRequestSnapshot(request, "primary")}; {FormatRequestSnapshot(resolveRequest, "resolve")}; {hint}; trace={trace}";
            }

            string BuildWallClockTimeoutDiagnostics()
            {
                string trace;
                lock (checkpointsLock)
                {
                    trace = checkpoints.Count == 0 ? "-" : string.Join(" | ", checkpoints);
                }

                return $"stage={stage}; source=wall-clock; elapsed={ElapsedSeconds():0.0}s; trace={trace}";
            }

            void Finish(string payload)
            {
                if (Interlocked.CompareExchange(ref finishedState, 1, 0) != 0)
                {
                    return;
                }

                try
                {
                    timeoutCts.Cancel();
                }
                catch
                {
                }

                CLIDaemon.UpdateProjectCommandStage(stage, "finished");
                CLIDaemon.DispatchOnMainThread(() => EditorApplication.update -= Tick);
                completion.TrySetResult(payload);
            }

            void Tick()
            {
                if (Volatile.Read(ref finishedState) != 0)
                {
                    return;
                }

                if (EditorApplication.timeSinceStartup >= deadline)
                {
                    AddCheckpoint("timeout reached");
                    stage = "timeout";
                    Finish(JsonUtility.ToJson(new ProjectCommandResponse
                    {
                        ok = false,
                        message = $"{timeoutMessage}; diagnostics: {BuildTimeoutDiagnostics()}"
                    }));
                    return;
                }

                if (!primaryHandled)
                {
                    if (!request.IsCompleted)
                    {
                        return;
                    }

                    primaryHandled = true;
                    AddCheckpoint("primary request completed");
                    if (request.Status != StatusCode.Success)
                    {
                        var errorText = request.Error is null
                            ? "unknown package manager error"
                            : $"{request.Error.errorCode}: {request.Error.message}";
                        Finish(JsonUtility.ToJson(new ProjectCommandResponse
                        {
                            ok = false,
                            message = $"{failurePrefix}: {errorText}"
                        }));
                        return;
                    }

                    try
                    {
                        resolveRequest = StartUpmResolveRequest();
                        stage = "waiting-resolve";
                        AddCheckpoint("resolve request started");
                    }
                    catch (Exception ex)
                    {
                        AddCheckpoint($"resolve start failed: {ex.GetType().Name}");
                        Finish(JsonUtility.ToJson(new ProjectCommandResponse
                        {
                            ok = false,
                            message = $"{failurePrefix}: failed to start resolve ({ex.Message})"
                        }));
                        return;
                    }

                    if (postVerify is null)
                    {
                        if (resolveRequest is null)
                        {
                            Finish(onSuccess());
                            return;
                        }
                    }

                    if (resolveRequest is null)
                    {
                        stage = "waiting-post-verify";
                    }
                }

                if (!resolveHandled && resolveRequest is not null)
                {
                    if (!resolveRequest.IsCompleted)
                    {
                        return;
                    }

                    resolveHandled = true;
                    AddCheckpoint("resolve request completed");
                    if (resolveRequest.Status != StatusCode.Success)
                    {
                        var resolveError = resolveRequest.Error is null
                            ? "unknown package manager resolve error"
                            : $"{resolveRequest.Error.errorCode}: {resolveRequest.Error.message}";
                        Finish(JsonUtility.ToJson(new ProjectCommandResponse
                        {
                            ok = false,
                            message = $"{failurePrefix}: resolve failed ({resolveError})"
                        }));
                        return;
                    }

                    stage = "waiting-post-verify";
                    if (postVerify is null)
                    {
                        Finish(onSuccess());
                        return;
                    }
                }

                if (EditorApplication.timeSinceStartup >= nextRefreshAt
                    && stage.Equals("waiting-post-verify", StringComparison.Ordinal))
                {
                    nextRefreshAt = EditorApplication.timeSinceStartup + 0.5d;
                    try
                    {
                        AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                    }
                    catch
                    {
                    }
                }

                if (postVerify is not null && !postVerify())
                {
                    return;
                }

                AddCheckpoint("post-verify passed");
                stage = "success";
                Finish(onSuccess());
            }

            EditorApplication.update += Tick;
            CLIDaemon.UpdateProjectCommandStage(stage, "update loop registered");
            _ = completion.Task.ContinueWith(_ =>
            {
                try
                {
                    timeoutCts.Dispose();
                }
                catch
                {
                }
            }, TaskScheduler.Default);

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(timeout, timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                    return;
                }

                if (Volatile.Read(ref finishedState) != 0)
                {
                    return;
                }

                AddCheckpoint("wall-clock timeout reached");
                stage = "timeout";
                Finish(JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = false,
                    message = $"{timeoutMessage}; diagnostics: {BuildWallClockTimeoutDiagnostics()}"
                }));
            });

            return completion.Task;
        }

        private static bool IsGitPackageUrl(string value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            {
                return false;
            }

            var scheme = uri.Scheme;
            var isHttp = scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                         || scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
            if (!isHttp)
            {
                return false;
            }

            var withoutQuery = value.Split(new[] { '?', '#' })[0];
            return withoutQuery.EndsWith(".git", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsLocalFilePackagePath(string value)
        {
            if (!value.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var pathPart = value.Substring("file:".Length).Trim();
            return !string.IsNullOrWhiteSpace(pathPart);
        }

        private static Type? ResolveType(string typeName)
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType(typeName, false))
                .FirstOrDefault(type => type is not null);
        }

        private static string GenerateUniqueAssetPathWithReservations(
            string parentPath,
            string baseName,
            string extension,
            HashSet<string> reservedPaths)
        {
            var normalizedParent = parentPath.TrimEnd('/', '\\');
            var normalizedExtension = string.IsNullOrWhiteSpace(extension)
                ? string.Empty
                : extension.StartsWith(".", StringComparison.Ordinal) ? extension : $".{extension}";
            var candidateName = baseName;
            var suffix = 1;
            while (true)
            {
                var candidatePath = $"{normalizedParent}/{candidateName}{normalizedExtension}".Replace('\\', '/');
                if (!reservedPaths.Contains(candidatePath) && !DoesAssetPathExist(candidatePath))
                {
                    return candidatePath;
                }

                candidateName = $"{baseName}_{suffix++}";
            }
        }

        private static string GenerateUniqueFolderPathWithReservations(
            string parentPath,
            string folderName,
            HashSet<string> reservedPaths)
        {
            var resolvedName = string.IsNullOrWhiteSpace(folderName) ? "NewFolder" : folderName.Trim();
            var basePath = $"{parentPath.TrimEnd('/', '\\')}/{resolvedName}".Replace('\\', '/');
            var candidate = basePath;
            var suffix = 1;
            while (true)
            {
                if (!reservedPaths.Contains(candidate) && !AssetDatabase.IsValidFolder(candidate))
                {
                    return candidate;
                }

                candidate = $"{basePath}_{suffix++}".Replace('\\', '/');
            }
        }
    }
}
#nullable restore
#endif
