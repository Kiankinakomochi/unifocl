#if UNITY_EDITOR
using System;
using System.Threading.Tasks;
using UniFocl.SharedModels;
using UnityEditor;
using UnityEngine;

namespace UniFocl.EditorBridge
{
    /// <summary>
    /// Editor-side state for the unifocl CLI link: the activity clock, the Editor session id, the status of
    /// the current project command and main-thread dispatch. The CLI reaches the Editor only through the
    /// official Unity CLI; the commands it calls are in <c>Pipeline/UnifoclBridgeCommands.cs</c> and
    /// <c>Pipeline/UnifoclRuntimeCommands.cs</c>.
    /// </summary>
    [InitializeOnLoad]
    public static class CLIDaemon
    {
        private const string RuntimeInstanceIdKey = "unifocl.runtimeInstanceId";
        private static DateTime _lastActivityUtc = DateTime.UtcNow;
        private static readonly string _runtimeInstanceId;
        private static volatile bool _editorIsCompiling;
        private static volatile bool _editorIsUpdating;
        private static readonly object ProjectCommandStatusLock = new();
        private static ProjectCommandStatusResponse _projectCommandStatus = new();
        private static string _projectCommandStageLogSignature = string.Empty;

        static CLIDaemon()
        {
            // SessionState survives domain reloads and is cleared when the Editor quits, so the id tells
            // the CLI that the Editor restarted rather than recompiled.
            _runtimeInstanceId = SessionState.GetString(RuntimeInstanceIdKey, string.Empty);
            if (string.IsNullOrEmpty(_runtimeInstanceId))
            {
                _runtimeInstanceId = Guid.NewGuid().ToString("N");
                SessionState.SetString(RuntimeInstanceIdKey, _runtimeInstanceId);
            }

            RefreshEditorStateCache();
            EditorApplication.update -= RefreshEditorStateCache;
            EditorApplication.update += RefreshEditorStateCache;
        }

        internal static DateTime LastActivityUtc => _lastActivityUtc;

        internal static void MarkActivity()
        {
            _lastActivityUtc = DateTime.UtcNow;
        }

        /// <summary>
        /// Runs <paramref name="action"/> on the main thread on a later Editor update, from any thread.
        /// </summary>
        internal static void DispatchOnMainThread(Action action)
        {
            if (action is null)
            {
                return;
            }

            EditorApplication.delayCall += () =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[unifocl] delayed main-thread action failed: {ex.GetType().Name}: {ex.Message}");
                }
            };
        }

        internal static void MarkAssetIndexDirty() => DaemonAssetIndexService.MarkDirty();

        private static void RefreshEditorStateCache()
        {
            _editorIsCompiling = EditorApplication.isCompiling;
            _editorIsUpdating = EditorApplication.isUpdating;
        }

        internal static string TryExtractProjectAction(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
            {
                return "<empty>";
            }

            const string marker = "\"action\"";
            var actionIndex = payload.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (actionIndex < 0)
            {
                return "<missing>";
            }

            var colonIndex = payload.IndexOf(':', actionIndex + marker.Length);
            if (colonIndex < 0)
            {
                return "<invalid>";
            }

            var valueStart = payload.IndexOf('"', colonIndex + 1);
            if (valueStart < 0)
            {
                return "<invalid>";
            }

            var valueEnd = payload.IndexOf('"', valueStart + 1);
            if (valueEnd <= valueStart)
            {
                return "<invalid>";
            }

            return payload[(valueStart + 1)..valueEnd];
        }

        internal static string HandleUnifoclMcpToolPayload(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
            {
                return JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = false,
                    message = "mcp tool payload is required",
                    kind = "mcp"
                });
            }

            McpProjectCommandToolRequest? request;
            try
            {
                request = JsonUtility.FromJson<McpProjectCommandToolRequest>(payload);
            }
            catch
            {
                request = null;
            }

            if (request is null || string.IsNullOrWhiteSpace(request.operation))
            {
                return JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = false,
                    message = "mcp tool operation is required",
                    kind = "mcp"
                });
            }

            var operation = request.operation.Trim().ToLowerInvariant();
            return operation switch
            {
                "submit" => string.IsNullOrWhiteSpace(request.commandPayload)
                    ? JsonUtility.ToJson(new ProjectCommandAcceptedResponse
                    {
                        ok = false,
                        stage = "error",
                        message = "submit requires commandPayload"
                    })
                    : DaemonProjectService.SubmitMutationPayload(request.commandPayload),
                "get_status" => string.IsNullOrWhiteSpace(request.requestId)
                    ? JsonUtility.ToJson(new ProjectCommandStatusResponse
                    {
                        requestId = string.Empty,
                        active = false,
                        stage = "error",
                        detail = "get_status requires requestId",
                        state = "error",
                        isDurable = true
                    })
                    : DaemonProjectService.GetMutationStatusPayload(request.requestId),
                "get_result" => string.IsNullOrWhiteSpace(request.requestId)
                    ? JsonUtility.ToJson(new ProjectCommandResultResponse
                    {
                        found = false,
                        completed = false,
                        success = false,
                        requestId = string.Empty,
                        action = string.Empty,
                        state = "error",
                        message = "get_result requires requestId"
                    })
                    : DaemonProjectService.GetMutationResultPayload(request.requestId),
                "cancel" => string.IsNullOrWhiteSpace(request.requestId)
                    ? JsonUtility.ToJson(new ProjectCommandAcceptedResponse
                    {
                        ok = false,
                        stage = "error",
                        message = "cancel requires requestId"
                    })
                    : DaemonProjectService.CancelMutationPayload(request.requestId),
                "execute_custom_tool" => string.IsNullOrWhiteSpace(request.tool)
                    ? JsonUtility.ToJson(new ProjectCommandResponse
                    {
                        ok = false,
                        message = "execute_custom_tool requires tool name",
                        kind = "mcp"
                    })
                    : DaemonCustomToolService.ExecuteCustomTool(request.tool, request.args, request.dryRun),
                _ => JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = false,
                    message = $"unsupported mcp tool operation: {request.operation}",
                    kind = "mcp"
                })
            };
        }

        /// <summary>The id of this Editor session; it changes when the Editor restarts, not on domain reload.</summary>
        internal static string RuntimeInstanceId => _runtimeInstanceId;

        /// <summary>
        /// The status of a project command: the durable record when <paramref name="requestedId"/> names a
        /// durable mutation, otherwise the in-memory status of the current or last command.
        /// </summary>
        internal static string ReadProjectCommandStatus(string? requestedId)
        {
            if (!string.IsNullOrWhiteSpace(requestedId)
                && DaemonProjectService.TryGetDurableMutationStatus(requestedId, out var durableStatus))
            {
                durableStatus.isCompiling = _editorIsCompiling;
                durableStatus.isUpdating = _editorIsUpdating;
                return JsonUtility.ToJson(durableStatus);
            }

            return GetProjectCommandStatusPayload(requestedId);
        }

        /// <summary>
        /// Runs a project command with the status tracking the HTTP endpoint does. Call it on the main thread.
        /// </summary>
        internal static async Task<string> RunProjectCommandAsync(string payload)
        {
            MarkActivity();
            var action = TryExtractProjectAction(payload);
            BeginProjectCommand(action, TryExtractProjectRequestId(payload));
            var response = await DaemonProjectService.ExecuteAsync(payload);
            CompleteProjectCommandFromResponse(response);
            return response;
        }

        internal static string? TryExtractProjectRequestId(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
            {
                return null;
            }

            const string marker = "\"requestId\"";
            var actionIndex = payload.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (actionIndex < 0)
            {
                return null;
            }

            var colonIndex = payload.IndexOf(':', actionIndex + marker.Length);
            if (colonIndex < 0)
            {
                return null;
            }

            var valueStart = payload.IndexOf('"', colonIndex + 1);
            if (valueStart < 0)
            {
                return null;
            }

            var valueEnd = payload.IndexOf('"', valueStart + 1);
            if (valueEnd <= valueStart)
            {
                return null;
            }

            var requestId = payload[(valueStart + 1)..valueEnd];
            return string.IsNullOrWhiteSpace(requestId) ? null : requestId;
        }

        internal static void UpdateProjectCommandStage(string stage, string detail = "")
        {
            lock (ProjectCommandStatusLock)
            {
                _projectCommandStatus.stage = string.IsNullOrWhiteSpace(stage) ? _projectCommandStatus.stage : stage;
                _projectCommandStatus.detail = detail ?? string.Empty;
                _projectCommandStatus.lastUpdatedAtUtc = DateTime.UtcNow.ToString("O");
                _projectCommandStatus.isCompiling = _editorIsCompiling;
                _projectCommandStatus.isUpdating = _editorIsUpdating;
                var signature =
                    $"{_projectCommandStatus.requestId}|{_projectCommandStatus.stage}|{_projectCommandStatus.detail}|compiling={_projectCommandStatus.isCompiling}|updating={_projectCommandStatus.isUpdating}";
                if (string.IsNullOrWhiteSpace(_projectCommandStatus.requestId)
                    || _projectCommandStatus.requestId.Equals("not-found", StringComparison.Ordinal)
                    || signature.Equals(_projectCommandStageLogSignature, StringComparison.Ordinal))
                {
                    return;
                }

                _projectCommandStageLogSignature = signature;
                Debug.Log(
                    $"[unifocl] project command stage: requestId={_projectCommandStatus.requestId}, action={_projectCommandStatus.action}, stage={_projectCommandStatus.stage}, detail={_projectCommandStatus.detail}, compiling={_projectCommandStatus.isCompiling}, updating={_projectCommandStatus.isUpdating}");
            }
        }

        internal static void BeginProjectCommand(string action, string? requestId)
        {
            lock (ProjectCommandStatusLock)
            {
                var now = DateTime.UtcNow.ToString("O");
                _projectCommandStatus = new ProjectCommandStatusResponse
                {
                    requestId = string.IsNullOrWhiteSpace(requestId) ? Guid.NewGuid().ToString("N") : requestId!,
                    action = action,
                    active = true,
                    success = false,
                    stage = "received",
                    detail = string.Empty,
                    startedAtUtc = now,
                    lastUpdatedAtUtc = now,
                    finishedAtUtc = string.Empty,
                    isCompiling = _editorIsCompiling,
                    isUpdating = _editorIsUpdating
                };
                _projectCommandStageLogSignature = string.Empty;
            }
        }

        internal static void CompleteProjectCommandFromResponse(string responsePayload)
        {
            var ok = false;
            var detail = "project command completed";
            try
            {
                var parsed = JsonUtility.FromJson<ProjectCommandResponse>(responsePayload);
                if (parsed is not null)
                {
                    ok = parsed.ok;
                    if (!string.IsNullOrWhiteSpace(parsed.message))
                    {
                        detail = parsed.message;
                    }
                }
            }
            catch
            {
            }

            CompleteProjectCommand(ok, detail);
        }

        private static void CompleteProjectCommand(bool success, string detail)
        {
            lock (ProjectCommandStatusLock)
            {
                _projectCommandStatus.active = false;
                _projectCommandStatus.success = success;
                _projectCommandStatus.stage = success ? "completed" : "failed";
                _projectCommandStatus.detail = detail ?? string.Empty;
                _projectCommandStatus.lastUpdatedAtUtc = DateTime.UtcNow.ToString("O");
                _projectCommandStatus.finishedAtUtc = _projectCommandStatus.lastUpdatedAtUtc;
                _projectCommandStatus.isCompiling = _editorIsCompiling;
                _projectCommandStatus.isUpdating = _editorIsUpdating;
            }
        }

        private static string GetProjectCommandStatusPayload(string? requestedId)
        {
            lock (ProjectCommandStatusLock)
            {
                if (!string.IsNullOrWhiteSpace(requestedId)
                    && !_projectCommandStatus.requestId.Equals(requestedId, StringComparison.Ordinal))
                {
                    return JsonUtility.ToJson(new ProjectCommandStatusResponse
                    {
                        requestId = requestedId,
                        action = string.Empty,
                        active = false,
                        success = false,
                        stage = "not-found",
                        detail = "request id not found in current daemon runtime",
                        startedAtUtc = string.Empty,
                        lastUpdatedAtUtc = DateTime.UtcNow.ToString("O"),
                        finishedAtUtc = string.Empty,
                        isCompiling = _editorIsCompiling,
                        isUpdating = _editorIsUpdating
                    });
                }

                return JsonUtility.ToJson(_projectCommandStatus);
            }
        }

        [Serializable]
        private sealed class McpProjectCommandToolRequest
        {
            public string operation = string.Empty;
            public string requestId = string.Empty;
            public string commandPayload = string.Empty;
            public string tool = string.Empty;
            public string args = string.Empty;
            public bool dryRun;
        }

    }

    internal sealed class CLIDaemonAssetPostprocessor : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            _ = importedAssets;
            _ = deletedAssets;
            _ = movedAssets;
            _ = movedFromAssetPaths;
            CLIDaemon.MarkAssetIndexDirty();
        }
    }
}
#endif
