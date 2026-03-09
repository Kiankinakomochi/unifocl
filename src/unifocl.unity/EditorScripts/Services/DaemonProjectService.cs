#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UpmPackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace UniFocl.EditorBridge
{
    internal static class DaemonProjectService
    {
        private static readonly object BuildStateLock = new();
        private static readonly Regex PercentRegex = new(@"(?<!\d)(\d{1,3})\s*%", RegexOptions.Compiled);
        private static BuildRuntimeState _buildState = new();
        private static int _unityMainThreadId;
        private static bool _buildInProgress;
        private static bool _cancelRequested;
        private static string _activeBuildAction = string.Empty;

        public static string Execute(string payload)
        {
            return ExecuteAsync(payload).GetAwaiter().GetResult();
        }

        public static Task<string> ExecuteAsync(string payload)
        {
            if (_unityMainThreadId == 0)
            {
                _unityMainThreadId = Environment.CurrentManagedThreadId;
            }

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

            try
            {
                return request.action switch
                {
                    "healthcheck" => Task.FromResult(JsonUtility.ToJson(new ProjectCommandResponse { ok = true, message = "project command endpoint ready", kind = "healthcheck" })),
                    "mk-script" => Task.FromResult(ExecuteCreateScript(request)),
                    "rename-asset" => Task.FromResult(ExecuteRenameAsset(request)),
                    "remove-asset" => Task.FromResult(ExecuteRemoveAsset(request)),
                    "load-asset" => Task.FromResult(ExecuteLoadAsset(request)),
                    "upm-list" => Task.FromResult(ExecuteUpmList(request)),
                    "upm-install" => ExecuteUpmInstall(request),
                    "upm-remove" => ExecuteUpmRemove(request),
                    "build-run" => Task.FromResult(ExecuteBuildRun(request)),
                    "build-exec" => Task.FromResult(ExecuteBuildExec(request)),
                    "build-scenes-get" => Task.FromResult(ExecuteBuildScenesGet()),
                    "build-scenes-set" => Task.FromResult(ExecuteBuildScenesSet(request)),
                    "build-addressables" => Task.FromResult(ExecuteBuildAddressables(request)),
                    "build-cancel" => Task.FromResult(ExecuteBuildCancel()),
                    "build-targets" => Task.FromResult(ExecuteBuildTargets()),
                    _ => Task.FromResult(JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = $"unsupported action: {request.action}" }))
                };
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

        private static string ExecuteCreateScript(ProjectCommandRequest request)
        {
            if (!IsValidAssetPath(request.assetPath) || string.IsNullOrWhiteSpace(request.content))
            {
                return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = "mk-script requires assetPath and content" });
            }

            var assetPath = request.assetPath;
            if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath) is not null)
            {
                return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = $"asset already exists: {assetPath}" });
            }

            var absolutePath = Path.Combine(GetProjectRoot(), assetPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath) ?? GetProjectRoot());
            File.WriteAllText(absolutePath, request.content);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);

            return JsonUtility.ToJson(new ProjectCommandResponse
            {
                ok = true,
                message = "script created",
                kind = "script"
            });
        }

        private static string ExecuteRenameAsset(ProjectCommandRequest request)
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
        }

        private static string ExecuteRemoveAsset(ProjectCommandRequest request)
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
                    var loadedScene = EditorSceneManager.OpenScene(requestedScenePath, OpenSceneMode.Single);
                    if (!loadedScene.IsValid() || !loadedScene.isLoaded)
                    {
                        return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = $"scene load failed: {requestedScenePath}" });
                    }

                    var loadedScenePath = loadedScene.path.Replace('\\', '/');
                    if (!loadedScenePath.Equals(requestedScenePath, StringComparison.OrdinalIgnoreCase))
                    {
                        return JsonUtility.ToJson(new ProjectCommandResponse
                        {
                            ok = false,
                            message = $"scene load failed: requested '{requestedScenePath}', loaded '{loadedScenePath}'"
                        });
                    }

                    if (!TryEnsureActiveScene(loadedScene, requestedScenePath))
                    {
                        return JsonUtility.ToJson(new ProjectCommandResponse
                        {
                            ok = false,
                            message = $"scene load failed: unable to activate scene '{requestedScenePath}'"
                        });
                    }

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
                message = $"unsupported asset type: {extension} (supported: .unity, .cs)"
            });
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

        private static Task<string> ExecuteUpmInstall(ProjectCommandRequest request)
        {
            var options = ParseUpmInstallOptions(request.content);
            var target = options.target?.Trim() ?? string.Empty;
            if (!TryClassifyUpmInstallTarget(target, out var targetType))
            {
                return Task.FromResult(JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = false,
                    message = "upm install target must be registry ID, Git URL, or file: path"
                }));
            }

            var resolvedTarget = ResolveInstallTargetVersion(target, targetType);
            AddRequest addRequest;
            try
            {
                addRequest = Client.Add(resolvedTarget);
            }
            catch (Exception ex)
            {
                return Task.FromResult(JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = false,
                    message = $"failed to start UPM install: {ex.Message}"
                }));
            }

            var packageId = TryExtractRegistryPackageId(resolvedTarget) ?? TryExtractRegistryPackageId(target) ?? target;
            return AwaitRequestWithResolveAsync(
                addRequest,
                timeoutMessage: "upm install timed out after 120 seconds",
                failurePrefix: "upm install failed",
                onSuccess: () =>
                {
                    var installed = addRequest.Result;
                    var payload = JsonUtility.ToJson(new UpmInstallResponse
                    {
                        packageId = installed?.name ?? packageId,
                        version = installed?.version ?? string.Empty,
                        source = installed?.source.ToString() ?? string.Empty,
                        targetType = targetType
                    });

                    return JsonUtility.ToJson(new ProjectCommandResponse
                    {
                        ok = true,
                        message = "upm install completed",
                        kind = "upm-install",
                        content = payload
                    });
                },
                postVerify: () =>
                {
                    if (!IsRegistryPackageId(packageId))
                    {
                        return true;
                    }

                    var installed = FindRegisteredPackage(packageId);
                    return installed is not null;
                },
                postVerifyMessage: $"upm install completed but package is not loaded: {packageId}");
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

        private static string ExecuteBuildRun(ProjectCommandRequest request)
        {
            if (_buildInProgress)
            {
                return JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = false,
                    message = $"build is already running: {_activeBuildAction}"
                });
            }

            BuildRunRequestOptions? options;
            try
            {
                options = JsonUtility.FromJson<BuildRunRequestOptions>(request.content);
            }
            catch
            {
                options = null;
            }

            if (options is null || string.IsNullOrWhiteSpace(options.target))
            {
                return JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = false,
                    message = "build-run requires target"
                });
            }

            if (!TryParseBuildTarget(options.target, out var buildTarget))
            {
                return JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = false,
                    message = $"unsupported build target: {options.target}"
                });
            }

            StartBackgroundBuild("run", () => RunBuildRun(buildTarget, options));
            return JsonUtility.ToJson(new ProjectCommandResponse
            {
                ok = true,
                message = $"build queued for {buildTarget}",
                kind = "build-run",
                content = JsonUtility.ToJson(new BuildOperationContent
                {
                    summary = "background build started",
                    details = Array.Empty<string>()
                })
            });
        }

        private static string ExecuteBuildExec(ProjectCommandRequest request)
        {
            if (_buildInProgress)
            {
                return JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = false,
                    message = $"build is already running: {_activeBuildAction}"
                });
            }

            BuildExecRequestOptions? options;
            try
            {
                options = JsonUtility.FromJson<BuildExecRequestOptions>(request.content);
            }
            catch
            {
                options = null;
            }

            if (options is null || string.IsNullOrWhiteSpace(options.method))
            {
                return JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = false,
                    message = "build-exec requires method"
                });
            }

            StartBackgroundBuild("exec", () =>
            {
                var signature = options.method.Trim();
                var separator = signature.LastIndexOf('.');
                if (separator <= 0 || separator >= signature.Length - 1)
                {
                    return JsonUtility.ToJson(new ProjectCommandResponse
                    {
                        ok = false,
                        message = "method must be in form Namespace.Type.Method"
                    });
                }

                var typeName = signature[..separator];
                var methodName = signature[(separator + 1)..];
                var targetType = AppDomain.CurrentDomain
                    .GetAssemblies()
                    .Select(assembly => assembly.GetType(typeName, throwOnError: false, ignoreCase: false))
                    .FirstOrDefault(type => type is not null);
                if (targetType is null)
                {
                    return JsonUtility.ToJson(new ProjectCommandResponse
                    {
                        ok = false,
                        message = $"type not found: {typeName}"
                    });
                }

                var method = targetType.GetMethod(
                    methodName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                    binder: null,
                    types: Type.EmptyTypes,
                    modifiers: null);
                if (method is null)
                {
                    return JsonUtility.ToJson(new ProjectCommandResponse
                    {
                        ok = false,
                        message = $"static parameterless method not found: {signature}"
                    });
                }

                try
                {
                    method.Invoke(null, null);
                }
                catch (TargetInvocationException ex)
                {
                    var inner = ex.InnerException ?? ex;
                    return JsonUtility.ToJson(new ProjectCommandResponse
                    {
                        ok = false,
                        message = $"method threw: {inner.GetType().Name}: {inner.Message}"
                    });
                }

                return JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = true,
                    message = $"executed build method: {signature}",
                    kind = "build-exec",
                    content = JsonUtility.ToJson(new BuildOperationContent
                    {
                        summary = $"method={signature}",
                        details = Array.Empty<string>()
                    })
                });
            });
            return JsonUtility.ToJson(new ProjectCommandResponse
            {
                ok = true,
                message = $"build method queued: {options.method}",
                kind = "build-exec"
            });
        }

        private static string ExecuteBuildScenesGet()
        {
            var scenes = EditorBuildSettings.scenes
                .Select((scene, index) => new BuildSceneEntry
                {
                    path = scene.path ?? string.Empty,
                    enabled = scene.enabled,
                    order = index
                })
                .ToArray();
            return JsonUtility.ToJson(new ProjectCommandResponse
            {
                ok = true,
                message = "build scenes loaded",
                kind = "build-scenes",
                content = JsonUtility.ToJson(new BuildScenesResponse { scenes = scenes })
            });
        }

        private static string ExecuteBuildScenesSet(ProjectCommandRequest request)
        {
            BuildScenesUpdateRequest? payload;
            try
            {
                payload = JsonUtility.FromJson<BuildScenesUpdateRequest>(request.content);
            }
            catch
            {
                payload = null;
            }

            if (payload?.scenes is null || payload.scenes.Length == 0)
            {
                return JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = false,
                    message = "build-scenes-set requires scenes"
                });
            }

            var updated = new List<EditorBuildSettingsScene>(payload.scenes.Length);
            for (var i = 0; i < payload.scenes.Length; i++)
            {
                var scene = payload.scenes[i];
                var path = (scene.path ?? string.Empty).Replace('\\', '/');
                if (!IsValidAssetPath(path) || !path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonUtility.ToJson(new ProjectCommandResponse
                    {
                        ok = false,
                        message = $"invalid scene path: {path}"
                    });
                }

                if (!File.Exists(Path.Combine(GetProjectRoot(), path.Replace('/', Path.DirectorySeparatorChar))))
                {
                    return JsonUtility.ToJson(new ProjectCommandResponse
                    {
                        ok = false,
                        message = $"scene file not found: {path}"
                    });
                }

                updated.Add(new EditorBuildSettingsScene(path, scene.enabled));
            }

            EditorBuildSettings.scenes = updated.ToArray();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            return JsonUtility.ToJson(new ProjectCommandResponse
            {
                ok = true,
                message = "build scenes updated",
                kind = "build-scenes",
                content = JsonUtility.ToJson(new BuildOperationContent
                {
                    summary = $"{updated.Count} scene(s) saved",
                    details = Array.Empty<string>()
                })
            });
        }

        private static string ExecuteBuildAddressables(ProjectCommandRequest request)
        {
            if (_buildInProgress)
            {
                return JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = false,
                    message = $"build is already running: {_activeBuildAction}"
                });
            }

            BuildAddressablesRequestOptions? options;
            try
            {
                options = JsonUtility.FromJson<BuildAddressablesRequestOptions>(request.content);
            }
            catch
            {
                options = null;
            }

            options ??= new BuildAddressablesRequestOptions();
            StartBackgroundBuild("addressables", () =>
            {
                var details = new List<string>();
                var editorAssembly = AppDomain.CurrentDomain
                    .GetAssemblies()
                    .FirstOrDefault(assembly =>
                        string.Equals(assembly.GetName().Name, "Unity.Addressables.Editor", StringComparison.Ordinal));
                if (editorAssembly is null)
                {
                    return JsonUtility.ToJson(new ProjectCommandResponse
                    {
                        ok = false,
                        message = "Addressables editor assembly is not loaded (install com.unity.addressables)"
                    });
                }

                var settingsDefaultType = editorAssembly.GetType("UnityEditor.AddressableAssets.Settings.AddressableAssetSettingsDefaultObject");
                var settingsProperty = settingsDefaultType?.GetProperty("Settings", BindingFlags.Public | BindingFlags.Static);
                var settings = settingsProperty?.GetValue(null);
                if (settings is null)
                {
                    return JsonUtility.ToJson(new ProjectCommandResponse
                    {
                        ok = false,
                        message = "Addressables settings asset was not found"
                    });
                }

                if (options.clean)
                {
                    var cleanMethod = settings.GetType().GetMethod("CleanPlayerContent", BindingFlags.Public | BindingFlags.Instance)
                                      ?? settings.GetType().GetMethod("CleanPlayerContent", BindingFlags.Public | BindingFlags.Static);
                    if (cleanMethod is not null)
                    {
                        cleanMethod.Invoke(cleanMethod.IsStatic ? null : settings, null);
                        details.Add("cleaned Addressables player content");
                    }
                }

                if (_cancelRequested)
                {
                    return JsonUtility.ToJson(new ProjectCommandResponse
                    {
                        ok = false,
                        message = "build cancelled before Addressables build started"
                    });
                }

                if (options.update)
                {
                    var contentUpdateType = editorAssembly.GetType("UnityEditor.AddressableAssets.Build.ContentUpdateScript");
                    var buildUpdateMethod = contentUpdateType?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                        .FirstOrDefault(method => method.Name.Equals("BuildContentUpdate", StringComparison.Ordinal));
                    if (buildUpdateMethod is null)
                    {
                        return JsonUtility.ToJson(new ProjectCommandResponse
                        {
                            ok = false,
                            message = "Addressables content update API not found in this Unity version"
                        });
                    }

                    var parameters = buildUpdateMethod.GetParameters();
                    if (parameters.Length == 0)
                    {
                        buildUpdateMethod.Invoke(null, null);
                    }
                    else if (parameters.Length == 1 && parameters[0].ParameterType == typeof(string))
                    {
                        var contentStatePath = ResolveAddressablesContentStatePath(editorAssembly, settings);
                        if (string.IsNullOrWhiteSpace(contentStatePath))
                        {
                            return JsonUtility.ToJson(new ProjectCommandResponse
                            {
                                ok = false,
                                message = "Addressables content state file path was not found; run a full Addressables build first"
                            });
                        }

                        buildUpdateMethod.Invoke(null, new object[] { contentStatePath });
                    }
                    else
                    {
                        return JsonUtility.ToJson(new ProjectCommandResponse
                        {
                            ok = false,
                            message = "Addressables content update API signature is unsupported"
                        });
                    }

                    details.Add("executed Addressables content update build");
                }
                else
                {
                    var buildMethod = settings.GetType().GetMethod("BuildPlayerContent", BindingFlags.Public | BindingFlags.Instance)
                                      ?? settings.GetType().GetMethod("BuildPlayerContent", BindingFlags.Public | BindingFlags.Static);
                    if (buildMethod is null)
                    {
                        return JsonUtility.ToJson(new ProjectCommandResponse
                        {
                            ok = false,
                            message = "Addressables BuildPlayerContent API not found"
                        });
                    }

                    buildMethod.Invoke(buildMethod.IsStatic ? null : settings, null);
                    details.Add("executed Addressables full content build");
                }

                return JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = true,
                    message = "Addressables build completed",
                    kind = "build-addressables",
                    content = JsonUtility.ToJson(new BuildOperationContent
                    {
                        summary = options.update ? "mode=update" : "mode=full",
                        details = details.ToArray()
                    })
                });
            });
            return JsonUtility.ToJson(new ProjectCommandResponse
            {
                ok = true,
                message = "Addressables build queued",
                kind = "build-addressables"
            });
        }

        private static string? ResolveAddressablesContentStatePath(Assembly editorAssembly, object settings)
        {
            var settingsType = settings.GetType();
            var contentStateProperty = settingsType.GetProperty(
                "ContentStateBuildPath",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            if (contentStateProperty is not null)
            {
                var pathValue = contentStateProperty.GetValue(contentStateProperty.GetMethod?.IsStatic == true ? null : settings) as string;
                if (!string.IsNullOrWhiteSpace(pathValue))
                {
                    return pathValue;
                }
            }

            var contentUpdateType = editorAssembly.GetType("UnityEditor.AddressableAssets.Build.ContentUpdateScript");
            var getter = contentUpdateType?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(method =>
                    method.Name.Equals("GetContentStateDataPath", StringComparison.Ordinal)
                    && method.ReturnType == typeof(string));
            if (getter is null)
            {
                return null;
            }

            var parameters = getter.GetParameters();
            if (parameters.Length == 0)
            {
                return getter.Invoke(null, null) as string;
            }

            if (parameters.Length == 1 && parameters[0].ParameterType == typeof(bool))
            {
                return getter.Invoke(null, new object[] { true }) as string;
            }

            return null;
        }

        private static string ExecuteBuildCancel()
        {
            _cancelRequested = true;
            lock (BuildStateLock)
            {
                _buildState.cancelRequested = true;
                _buildState.message = "cancel requested";
            }
            var cancelApplied = TryInvokeBuildPipelineCancel();
            if (!_buildInProgress)
            {
                _cancelRequested = false;
                return JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = true,
                    message = "no build is currently running"
                });
            }

            return JsonUtility.ToJson(new ProjectCommandResponse
            {
                ok = true,
                message = cancelApplied
                    ? $"cancel signal sent to active build: {_activeBuildAction}"
                    : $"cancel requested for active build: {_activeBuildAction}"
            });
        }

        private static string ExecuteBuildTargets()
        {
            var targets = new[]
            {
                BuildTarget.StandaloneWindows64,
                BuildTarget.Android,
                BuildTarget.iOS,
                BuildTarget.WebGL,
                BuildTarget.StandaloneOSX,
                BuildTarget.StandaloneLinux64
            };

            var entries = targets
                .Select(target => new BuildTargetEntry
                {
                    name = GetBuildTargetDisplayName(target),
                    installed = BuildPipeline.IsBuildTargetSupported(BuildPipeline.GetBuildTargetGroup(target), target),
                    note = target.ToString()
                })
                .ToArray();

            return JsonUtility.ToJson(new ProjectCommandResponse
            {
                ok = true,
                message = "build targets loaded",
                kind = "build-targets",
                content = JsonUtility.ToJson(new BuildTargetsResponse { targets = entries })
            });
        }

        private static void StartBackgroundBuild(string action, Func<string> execute)
        {
            _buildInProgress = true;
            _activeBuildAction = action;
            _cancelRequested = false;
            var logPath = PrepareBuildLogFilePath();
            lock (BuildStateLock)
            {
                _buildState = new BuildRuntimeState
                {
                    running = true,
                    cancelRequested = false,
                    progress01 = 0.01f,
                    step = "queued",
                    kind = action,
                    logPath = logPath,
                    startedAtUtc = DateTime.UtcNow.ToString("O"),
                    lastHeartbeatUtc = DateTime.UtcNow.ToString("O"),
                    success = false,
                    message = "queued"
                };
            }

            Application.logMessageReceivedThreaded += OnUnityLog;
            AppendBuildDiagnostic($"build action scheduled on main thread dispatcher (batch={Application.isBatchMode})");
            CLIDaemon.DispatchOnMainThread(RunBackground);

            void RunBackground()
            {
                if (!IsUnityMainThread())
                {
                    AppendBuildDiagnostic(
                        $"background build callback arrived on non-main thread (current={Environment.CurrentManagedThreadId}, main={_unityMainThreadId}); rescheduling");
                    CLIDaemon.DispatchOnMainThread(RunBackground);
                    return;
                }

                string payload;
                try
                {
                    UpdateBuildStatus(step: "running", progress: 0.05f, message: $"starting {action}");
                    AppendBuildDiagnostic($"build action started: {action}");
                    payload = execute();
                }
                catch (Exception ex)
                {
                    AppendBuildDiagnostic($"unhandled build exception: {ex.GetType().Name}: {ex.Message}", isError: true);
                    if (!string.IsNullOrWhiteSpace(ex.StackTrace))
                    {
                        AppendBuildDiagnostic(ex.StackTrace, isError: true);
                    }
                    payload = JsonUtility.ToJson(new ProjectCommandResponse
                    {
                        ok = false,
                        message = $"{action} failed: {ex.GetType().Name}: {ex.Message}",
                        kind = $"build-{action}",
                        content = JsonUtility.ToJson(new BuildOperationContent
                        {
                            summary = $"{action} failed with unhandled exception",
                            details = new[]
                            {
                                $"{ex.GetType().FullName}: {ex.Message}",
                                ex.StackTrace ?? string.Empty
                            }
                        })
                    });
                }
                finally
                {
                    Application.logMessageReceivedThreaded -= OnUnityLog;
                    _buildInProgress = false;
                    _activeBuildAction = string.Empty;
                    _cancelRequested = false;
                }

                ProjectCommandResponse parsed;
                try
                {
                    parsed = JsonUtility.FromJson<ProjectCommandResponse>(payload) ?? new ProjectCommandResponse
                    {
                        ok = false,
                        message = "build returned invalid response",
                        kind = $"build-{action}"
                    };
                }
                catch
                {
                    parsed = new ProjectCommandResponse
                    {
                        ok = false,
                        message = "build returned invalid response",
                        kind = $"build-{action}"
                    };
                }

                var outputPath = TryExtractOutputPath(parsed.content);
                lock (BuildStateLock)
                {
                    _buildState.running = false;
                    _buildState.cancelRequested = false;
                    _buildState.progress01 = 1f;
                    _buildState.step = parsed.ok ? "completed" : "failed";
                    _buildState.finishedAtUtc = DateTime.UtcNow.ToString("O");
                    _buildState.success = parsed.ok;
                    _buildState.message = parsed.message ?? string.Empty;
                    _buildState.outputPath = outputPath;
                    _buildState.lastHeartbeatUtc = DateTime.UtcNow.ToString("O");
                }
            }
        }

        private static string RunBuildRun(BuildTarget buildTarget, BuildRunRequestOptions options)
        {
            if (!IsUnityMainThread())
            {
                AppendBuildDiagnostic(
                    $"build-run rejected: called off main thread (current={Environment.CurrentManagedThreadId}, main={_unityMainThreadId})",
                    isError: true);
                return JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = false,
                    message = "build-run must execute on Unity main thread"
                });
            }

            var buildStartedAt = DateTime.UtcNow;
            try
            {
                AppendBuildDiagnostic($"build-run executing on main thread id={Environment.CurrentManagedThreadId}");
                if (options.clean)
                {
                    UpdateBuildStatus(step: "cleaning caches", progress: 0.1f, message: "cleaning build caches");
                    AppendBuildDiagnostic("cleaning build caches");
                    CleanBuildCaches();
                }

                UpdateBuildStatus(step: "collecting scenes", progress: 0.15f, message: "collecting enabled scenes");
                var enabledScenes = EditorBuildSettings.scenes
                    .Where(scene => scene.enabled)
                    .Select(scene => scene.path)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .ToArray();
                AppendBuildDiagnostic($"enabled scene count: {enabledScenes.Length}");
                if (enabledScenes.Length == 0)
                {
                    return JsonUtility.ToJson(new ProjectCommandResponse
                    {
                        ok = false,
                        message = "no enabled scenes in EditorBuildSettings"
                    });
                }

                var buildTargetGroup = BuildPipeline.GetBuildTargetGroup(buildTarget);
                if (!BuildPipeline.IsBuildTargetSupported(buildTargetGroup, buildTarget))
                {
                    return JsonUtility.ToJson(new ProjectCommandResponse
                    {
                        ok = false,
                        message = $"build target module is not installed: {buildTarget}"
                    });
                }

                var outputPath = ResolveBuildOutputPath(buildTarget, options.outputPath);
                var buildOptions = BuildOptions.None;
                if (options.development)
                {
                    buildOptions |= BuildOptions.Development;
                }

                if (options.scriptDebugging)
                {
                    buildOptions |= BuildOptions.AllowDebugging;
                }

                UpdateBuildStatus(step: "building player", progress: 0.25f, message: $"BuildPipeline.BuildPlayer({buildTarget})");
                AppendBuildDiagnostic($"build target={buildTarget}, group={buildTargetGroup}, output={outputPath}, options={buildOptions}");

                var result = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = enabledScenes,
                    target = buildTarget,
                    locationPathName = outputPath,
                    options = buildOptions
                });
                if (result is null)
                {
                    return JsonUtility.ToJson(new ProjectCommandResponse
                    {
                        ok = false,
                        message = "Unity returned no build report"
                    });
                }

                var summary = result.summary;
                AppendBuildDiagnostic($"build summary result={summary.result}, warnings={summary.totalWarnings}, errors={summary.totalErrors}, output={summary.outputPath}");
                var detail = JsonUtility.ToJson(new BuildOperationContent
                {
                    summary = $"target={buildTarget}, result={summary.result}, output={summary.outputPath}",
                    details = new[]
                    {
                        $"duration={summary.totalTime.TotalSeconds:0.0}s",
                        $"elapsed={(DateTime.UtcNow - buildStartedAt).TotalSeconds:0.0}s",
                        $"size={summary.totalSize} bytes",
                        $"warnings={summary.totalWarnings}, errors={summary.totalErrors}"
                    }
                });

                return summary.result == BuildResult.Succeeded
                    ? JsonUtility.ToJson(new ProjectCommandResponse
                    {
                        ok = true,
                        message = $"build completed for {buildTarget}",
                        kind = "build-run",
                        content = detail
                    })
                    : JsonUtility.ToJson(new ProjectCommandResponse
                    {
                        ok = false,
                        message = $"build failed for {buildTarget} ({summary.result})",
                        kind = "build-run",
                        content = detail
                    });
            }
            catch (Exception ex)
            {
                AppendBuildDiagnostic($"build-run exception: {ex.GetType().Name}: {ex.Message}", isError: true);
                if (!string.IsNullOrWhiteSpace(ex.StackTrace))
                {
                    AppendBuildDiagnostic(ex.StackTrace, isError: true);
                }

                return JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = false,
                    message = $"build-run failed: {ex.GetType().Name}: {ex.Message}",
                    kind = "build-run",
                    content = JsonUtility.ToJson(new BuildOperationContent
                    {
                        summary = "build-run exception",
                        details = new[]
                        {
                            $"{ex.GetType().FullName}: {ex.Message}",
                            ex.StackTrace ?? string.Empty
                        }
                    })
                });
            }
        }

        private static bool TryInvokeBuildPipelineCancel()
        {
            try
            {
                var cancelMethod = typeof(BuildPipeline).GetMethod("CancelBuild", BindingFlags.Public | BindingFlags.Static);
                if (cancelMethod is null)
                {
                    return false;
                }

                var result = cancelMethod.Invoke(null, null);
                return result is bool isCanceled ? isCanceled : true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryParseBuildTarget(string rawTarget, out BuildTarget target)
        {
            var normalized = rawTarget.Replace("-", string.Empty, StringComparison.Ordinal)
                .Replace("_", string.Empty, StringComparison.Ordinal)
                .ToLowerInvariant();
            target = normalized switch
            {
                "win64" or "windows64" or "standalonewindows64" => BuildTarget.StandaloneWindows64,
                "android" => BuildTarget.Android,
                "ios" => BuildTarget.iOS,
                "webgl" => BuildTarget.WebGL,
                "mac" or "macos" or "osx" or "standaloneosx" => BuildTarget.StandaloneOSX,
                "linux" or "linux64" or "standalonelinux64" => BuildTarget.StandaloneLinux64,
                _ => BuildTarget.NoTarget
            };
            return target != BuildTarget.NoTarget;
        }

        private static string GetBuildTargetDisplayName(BuildTarget target)
        {
            return target switch
            {
                BuildTarget.StandaloneWindows64 => "Win64",
                BuildTarget.Android => "Android",
                BuildTarget.iOS => "iOS",
                BuildTarget.WebGL => "WebGL",
                BuildTarget.StandaloneOSX => "macOS",
                BuildTarget.StandaloneLinux64 => "Linux64",
                _ => target.ToString()
            };
        }

        private static void CleanBuildCaches()
        {
            DeleteDirectorySafe(Path.Combine(GetProjectRoot(), "Library", "Bee"));
            DeleteDirectorySafe(Path.Combine(GetProjectRoot(), "Library", "Il2cppBuildCache"));
            DeleteDirectorySafe(Path.Combine(GetProjectRoot(), "Build"));
        }

        private static void DeleteDirectorySafe(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[unifocl] Failed to delete build cache path '{path}': {ex.Message}");
            }
        }

        private static string ResolveBuildOutputPath(BuildTarget target, string? requestedOutputPath)
        {
            var projectRoot = GetProjectRoot();
            var buildRoot = string.IsNullOrWhiteSpace(requestedOutputPath)
                ? Path.Combine(projectRoot, "Build", GetBuildTargetDisplayName(target), DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"))
                : ResolveAbsolutePath(projectRoot, requestedOutputPath);
            var productName = string.IsNullOrWhiteSpace(PlayerSettings.productName)
                ? "UnityPlayer"
                : PlayerSettings.productName;
            var isDirectoryTarget = target is BuildTarget.iOS or BuildTarget.WebGL;

            if (isDirectoryTarget)
            {
                Directory.CreateDirectory(buildRoot);
                return buildRoot;
            }

            var extension = target switch
            {
                BuildTarget.StandaloneWindows64 => ".exe",
                BuildTarget.Android => ".apk",
                BuildTarget.StandaloneOSX => ".app",
                BuildTarget.StandaloneLinux64 => string.Empty,
                _ => string.Empty
            };
            var normalizedRoot = buildRoot.Replace('\\', '/');
            var treatAsDirectory = string.IsNullOrWhiteSpace(Path.GetExtension(buildRoot))
                                   || normalizedRoot.EndsWith("/", StringComparison.Ordinal)
                                   || Directory.Exists(buildRoot);
            if (treatAsDirectory)
            {
                Directory.CreateDirectory(buildRoot);
                return Path.Combine(buildRoot, $"{productName}{extension}");
            }

            var directory = Path.GetDirectoryName(buildRoot);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            return buildRoot;
        }

        private static string ResolveAbsolutePath(string projectRoot, string path)
        {
            if (Path.IsPathRooted(path))
            {
                return path;
            }

            return Path.GetFullPath(Path.Combine(projectRoot, path));
        }

        private static void OnUnityLog(string condition, string stackTrace, LogType type)
        {
            string? logPath;
            string currentStep;
            float currentProgress;
            lock (BuildStateLock)
            {
                if (!_buildState.running)
                {
                    return;
                }

                logPath = _buildState.logPath;
                currentStep = _buildState.step;
                currentProgress = _buildState.progress01;
            }

            if (string.IsNullOrWhiteSpace(logPath))
            {
                return;
            }

            var level = type switch
            {
                LogType.Error => "error",
                LogType.Assert => "error",
                LogType.Exception => "error",
                LogType.Warning => "warning",
                _ => "info"
            };
            var line = $"{DateTime.UtcNow:O}|{level}|{condition?.Replace(Environment.NewLine, " ").Replace('\n', ' ') ?? string.Empty}";
            try
            {
                File.AppendAllText(logPath, line + Environment.NewLine, Encoding.UTF8);
                if (!string.IsNullOrWhiteSpace(stackTrace) && level.Equals("error", StringComparison.OrdinalIgnoreCase))
                {
                    File.AppendAllText(logPath, $"{DateTime.UtcNow:O}|{level}|{stackTrace.Replace(Environment.NewLine, " ").Replace('\n', ' ')}{Environment.NewLine}", Encoding.UTF8);
                }
            }
            catch
            {
            }

            var lowered = condition?.ToLowerInvariant() ?? string.Empty;
            var step = lowered.Contains("il2cpp", StringComparison.Ordinal) ? "Compiling IL2CPP"
                : lowered.Contains("shader", StringComparison.Ordinal) ? "Compiling shaders"
                : lowered.Contains("addressable", StringComparison.Ordinal) ? "Building Addressables"
                : lowered.Contains("build", StringComparison.Ordinal) ? "Building player"
                : currentStep;

            var progress = currentProgress;
            var match = PercentRegex.Match(condition ?? string.Empty);
            if (match.Success
                && int.TryParse(match.Groups[1].Value, out var percent)
                && percent >= 0
                && percent <= 100)
            {
                progress = Math.Max(progress, percent / 100f);
            }
            else
            {
                progress = Math.Min(0.95f, progress + 0.002f);
            }

            UpdateBuildStatus(step, progress, condition ?? string.Empty);
        }

        private static void UpdateBuildStatus(string step, float progress, string message)
        {
            lock (BuildStateLock)
            {
                _buildState.step = string.IsNullOrWhiteSpace(step) ? _buildState.step : step;
                _buildState.progress01 = Math.Clamp(progress, 0f, 1f);
                _buildState.message = string.IsNullOrWhiteSpace(message) ? _buildState.message : message;
                _buildState.lastHeartbeatUtc = DateTime.UtcNow.ToString("O");
            }
        }

        private static bool IsUnityMainThread()
        {
            return _unityMainThreadId != 0
                   && Environment.CurrentManagedThreadId == _unityMainThreadId;
        }

        private static void AppendBuildDiagnostic(string message, bool isError = false)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            string? logPath;
            lock (BuildStateLock)
            {
                _buildState.lastDiagnostic = message.Length > 600 ? message[..600] : message;
                if (isError)
                {
                    _buildState.lastException = _buildState.lastDiagnostic;
                }

                _buildState.lastHeartbeatUtc = DateTime.UtcNow.ToString("O");
                logPath = _buildState.logPath;
            }

            if (string.IsNullOrWhiteSpace(logPath))
            {
                return;
            }

            var level = isError ? "error" : "info";
            var normalized = message.Replace(Environment.NewLine, " ").Replace('\n', ' ').Replace('\r', ' ');
            var line = $"{DateTime.UtcNow:O}|{level}|{normalized}";
            try
            {
                File.AppendAllText(logPath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
            }
        }

        private static string PrepareBuildLogFilePath()
        {
            var root = Path.Combine(GetProjectRoot(), ".unifocl", "logs");
            Directory.CreateDirectory(root);
            var filePath = Path.Combine(root, $"build-{DateTime.UtcNow:yyyyMMdd-HHmmss}.txt");
            try
            {
                File.WriteAllText(filePath, string.Empty, Encoding.UTF8);
            }
            catch
            {
            }

            return filePath;
        }

        private static string? TryExtractOutputPath(string? content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return null;
            }

            try
            {
                var parsed = JsonUtility.FromJson<BuildOperationContent>(content);
                if (parsed is null || string.IsNullOrWhiteSpace(parsed.summary))
                {
                    return null;
                }

                var marker = "output=";
                var index = parsed.summary.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                {
                    return null;
                }

                return parsed.summary[(index + marker.Length)..].Trim();
            }
            catch
            {
                return null;
            }
        }

        private static BuildLogLine ParseLogLine(string rawLine)
        {
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                return new BuildLogLine { level = "info", text = string.Empty };
            }

            var first = rawLine.IndexOf('|');
            if (first < 0)
            {
                return new BuildLogLine { level = "info", text = rawLine };
            }

            var second = rawLine.IndexOf('|', first + 1);
            if (second < 0)
            {
                return new BuildLogLine { level = "info", text = rawLine[(first + 1)..] };
            }

            var level = rawLine[(first + 1)..second];
            var text = rawLine[(second + 1)..];
            return new BuildLogLine
            {
                level = string.IsNullOrWhiteSpace(level) ? "info" : level,
                text = text
            };
        }

        [Serializable]
        private sealed class BuildRunRequestOptions
        {
            public string target = string.Empty;
            public bool development;
            public bool scriptDebugging;
            public bool clean;
            public string outputPath = string.Empty;
        }

        [Serializable]
        private sealed class BuildExecRequestOptions
        {
            public string method = string.Empty;
        }

        [Serializable]
        private sealed class BuildAddressablesRequestOptions
        {
            public bool clean;
            public bool update;
        }

        [Serializable]
        private sealed class BuildOperationContent
        {
            public string summary = string.Empty;
            public string[] details = Array.Empty<string>();
        }

        [Serializable]
        private sealed class BuildSceneEntry
        {
            public string path = string.Empty;
            public bool enabled;
            public int order;
        }

        [Serializable]
        private sealed class BuildScenesResponse
        {
            public BuildSceneEntry[] scenes = Array.Empty<BuildSceneEntry>();
        }

        [Serializable]
        private sealed class BuildScenesUpdateRequest
        {
            public BuildSceneEntry[] scenes = Array.Empty<BuildSceneEntry>();
        }

        [Serializable]
        private sealed class BuildTargetEntry
        {
            public string name = string.Empty;
            public bool installed;
            public string note = string.Empty;
        }

        [Serializable]
        private sealed class BuildTargetsResponse
        {
            public BuildTargetEntry[] targets = Array.Empty<BuildTargetEntry>();
        }

        [Serializable]
        private sealed class BuildStatusResponse
        {
            public bool running;
            public bool cancelRequested;
            public float progress01;
            public string step = string.Empty;
            public string kind = string.Empty;
            public string logPath = string.Empty;
            public string outputPath = string.Empty;
            public string startedAtUtc = string.Empty;
            public string finishedAtUtc = string.Empty;
            public bool success;
            public string message = string.Empty;
            public string lastHeartbeatUtc = string.Empty;
            public string lastDiagnostic = string.Empty;
            public string lastException = string.Empty;
        }

        [Serializable]
        private sealed class BuildLogChunkResponse
        {
            public long nextOffset;
            public BuildLogLine[] lines = Array.Empty<BuildLogLine>();
        }

        [Serializable]
        private sealed class BuildLogLine
        {
            public string level = string.Empty;
            public string text = string.Empty;
        }

        private sealed class BuildRuntimeState
        {
            public bool running;
            public bool cancelRequested;
            public float progress01;
            public string step = "idle";
            public string kind = string.Empty;
            public string logPath = string.Empty;
            public string outputPath = string.Empty;
            public string startedAtUtc = string.Empty;
            public string finishedAtUtc = string.Empty;
            public bool success;
            public string message = "idle";
            public string lastHeartbeatUtc = string.Empty;
            public string lastDiagnostic = string.Empty;
            public string lastException = string.Empty;
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

        private static UpmInstallRequestOptions ParseUpmInstallOptions(string rawContent)
        {
            if (string.IsNullOrWhiteSpace(rawContent))
            {
                return new UpmInstallRequestOptions();
            }

            try
            {
                return JsonUtility.FromJson<UpmInstallRequestOptions>(rawContent) ?? new UpmInstallRequestOptions();
            }
            catch
            {
                return new UpmInstallRequestOptions();
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

        private static bool TryClassifyUpmInstallTarget(string target, out string targetType)
        {
            targetType = string.Empty;
            if (string.IsNullOrWhiteSpace(target))
            {
                return false;
            }

            if (IsRegistryPackageId(target))
            {
                targetType = "registry";
                return true;
            }

            if (IsGitPackageUrl(target))
            {
                targetType = "git";
                return true;
            }

            if (IsLocalFilePackagePath(target))
            {
                targetType = "file";
                return true;
            }

            return false;
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

        private static string ResolveInstallTargetVersion(string target, string targetType)
        {
            if (!targetType.Equals("registry", StringComparison.OrdinalIgnoreCase))
            {
                return target;
            }

            if (!IsRegistryPackageId(target))
            {
                return target;
            }

            if (target.Contains('@'))
            {
                return target;
            }

            var packageId = TryExtractRegistryPackageId(target);
            if (string.IsNullOrWhiteSpace(packageId))
            {
                return target;
            }

            var installed = FindRegisteredPackage(packageId);
            var latestCompatible = installed?.versions?.latestCompatible;
            if (string.IsNullOrWhiteSpace(latestCompatible))
            {
                return target;
            }

            return $"{packageId}@{latestCompatible}";
        }

        private static UpmPackageInfo? FindRegisteredPackage(string packageId)
        {
            if (string.IsNullOrWhiteSpace(packageId))
            {
                return null;
            }

            var all = UpmPackageInfo.GetAllRegisteredPackages() ?? Array.Empty<UpmPackageInfo>();
            return all.FirstOrDefault(package =>
                package is not null
                && package.name.Equals(packageId, StringComparison.OrdinalIgnoreCase));
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
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(timeout).ConfigureAwait(false);
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

        private static bool IsValidAssetPath(string? assetPath)
        {
            return !string.IsNullOrWhiteSpace(assetPath)
                   && assetPath.StartsWith("Assets/", StringComparison.Ordinal)
                   && !assetPath.Contains("..", StringComparison.Ordinal);
        }

        private static bool TryEnsureActiveScene(Scene loadedScene, string requestedScenePath)
        {
            if (!loadedScene.IsValid() || !loadedScene.isLoaded)
            {
                return false;
            }

            if (IsRequestedSceneActive(requestedScenePath))
            {
                return true;
            }

            if (SceneManager.SetActiveScene(loadedScene) && IsRequestedSceneActive(requestedScenePath))
            {
                return true;
            }

            if (EditorSceneManager.SetActiveScene(loadedScene) && IsRequestedSceneActive(requestedScenePath))
            {
                return true;
            }

            // Opening with OpenSceneMode.Single usually makes the scene active already.
            return IsRequestedSceneActive(requestedScenePath);
        }

        private static bool IsRequestedSceneActive(string requestedScenePath)
        {
            var activeScene = SceneManager.GetActiveScene();
            if (!activeScene.IsValid() || !activeScene.isLoaded)
            {
                return false;
            }

            return activeScene.path.Replace('\\', '/')
                .Equals(requestedScenePath, StringComparison.OrdinalIgnoreCase);
        }

        private static string GetProjectRoot()
        {
            return Directory.GetParent(Application.dataPath)?.FullName
                   ?? throw new InvalidOperationException("failed to resolve Unity project root");
        }
    }
}
#endif
