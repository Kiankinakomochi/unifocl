#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace UniFocl.EditorBridge
{
    internal static class DaemonMutationCommandDispatcher
    {
        private static readonly object DispatchSync = new();
        private static bool _initialized;
        private static readonly HashSet<string> ActiveRequests = new(StringComparer.Ordinal);

        public static void Initialize()
        {
            lock (DispatchSync)
            {
                if (_initialized)
                {
                    return;
                }

                _initialized = true;
            }

            EditorApplication.delayCall += RecoverPendingCommands;
        }

        public static ProjectCommandAcceptedResponse Submit(ProjectCommandRequest request)
        {
            Initialize();
            request.requestId = DaemonMutationCommandStore.NormalizeRequestId(request.requestId);
            var envelope = DaemonMutationCommandStore.UpsertQueued(request, out var duplicated);
            if (!duplicated)
            {
                AddCheckpoint(envelope.requestId, $"queued action={request.action}");
            }

            Dispatch(envelope.requestId);
            return new ProjectCommandAcceptedResponse
            {
                ok = true,
                requestId = envelope.requestId,
                action = envelope.action,
                duplicated = duplicated,
                stage = duplicated ? "duplicate" : "queued",
                message = duplicated
                    ? "mutation request already exists; returning tracked request id"
                    : "mutation request accepted"
            };
        }

        public static bool TryGetStatus(string requestId, out ProjectCommandStatusResponse status)
        {
            status = new ProjectCommandStatusResponse();
            if (!DaemonMutationCommandStore.TryReadEnvelope(requestId, out var envelope))
            {
                return false;
            }

            status = BuildStatus(envelope);
            return true;
        }

        public static ProjectCommandResultResponse GetResult(string requestId)
        {
            if (!DaemonMutationCommandStore.TryReadEnvelope(requestId, out var envelope))
            {
                return new ProjectCommandResultResponse
                {
                    found = false,
                    completed = false,
                    success = false,
                    requestId = requestId,
                    action = string.Empty,
                    state = "not-found",
                    message = "mutation request id was not found"
                };
            }

            return new ProjectCommandResultResponse
            {
                found = true,
                completed = DurableMutationState.IsTerminal(envelope.state),
                success = envelope.success,
                requestId = envelope.requestId,
                action = envelope.action,
                state = envelope.state,
                message = string.IsNullOrWhiteSpace(envelope.message) ? envelope.state : envelope.message,
                responsePayload = envelope.responsePayload ?? string.Empty
            };
        }

        public static ProjectCommandAcceptedResponse Cancel(string requestId)
        {
            if (!DaemonMutationCommandStore.TryUpdateEnvelope(
                    requestId,
                    current =>
                    {
                        if (DurableMutationState.IsTerminal(current.state))
                        {
                            return current;
                        }

                        current.cancelRequested = true;
                        if (current.state.Equals(DurableMutationState.Queued, StringComparison.OrdinalIgnoreCase))
                        {
                            current.state = DurableMutationState.Canceled;
                            current.success = false;
                            current.message = "mutation command canceled before execution";
                            current.finishedAtUtc = DateTime.UtcNow.ToString("O");
                        }
                        else
                        {
                            current.message = "cancel requested";
                        }

                        var checkpoints = current.checkpoints?.ToList() ?? new List<string>();
                        checkpoints.Add($"{DateTime.UtcNow:O} cancel-requested");
                        current.checkpoints = checkpoints.TakeLast(16).ToArray();
                        return current;
                    },
                    out var updated))
            {
                return new ProjectCommandAcceptedResponse
                {
                    ok = false,
                    requestId = requestId,
                    action = string.Empty,
                    duplicated = false,
                    stage = "not-found",
                    message = "mutation request id was not found"
                };
            }

            return new ProjectCommandAcceptedResponse
            {
                ok = true,
                requestId = updated.requestId,
                action = updated.action,
                duplicated = false,
                stage = updated.state,
                message = updated.message
            };
        }

        private static void RecoverPendingCommands()
        {
            Initialize();
            var pending = DaemonMutationCommandStore.ReadAll()
                .Where(entry => entry.state.Equals(DurableMutationState.Queued, StringComparison.OrdinalIgnoreCase)
                                || entry.state.Equals(DurableMutationState.Running, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var envelope in pending)
            {
                if (envelope.state.Equals(DurableMutationState.Running, StringComparison.OrdinalIgnoreCase))
                {
                    DaemonMutationCommandStore.TryUpdateEnvelope(
                        envelope.requestId,
                        current =>
                        {
                            current.state = DurableMutationState.Queued;
                            current.message = "re-queued after editor/domain reload";
                            return current;
                        },
                        out _);
                }

                Dispatch(envelope.requestId);
            }
        }

        private static void Dispatch(string requestId)
        {
            lock (DispatchSync)
            {
                if (!ActiveRequests.Add(requestId))
                {
                    return;
                }
            }

            CLIDaemon.DispatchOnMainThread(() =>
            {
                _ = ExecuteAsync(requestId).ContinueWith(_ =>
                {
                    lock (DispatchSync)
                    {
                        ActiveRequests.Remove(requestId);
                    }
                }, TaskScheduler.Default);
            });
        }

        private static async Task ExecuteAsync(string requestId)
        {
            if (!DaemonMutationCommandStore.TryReadEnvelope(requestId, out var envelope))
            {
                return;
            }

            if (DurableMutationState.IsTerminal(envelope.state))
            {
                return;
            }

            if (envelope.cancelRequested)
            {
                FinalizeCanceled(requestId, "mutation command canceled before execution");
                return;
            }

            if (!DaemonMutationCommandStore.TryUpdateEnvelope(
                    requestId,
                    current =>
                    {
                        current.state = DurableMutationState.Running;
                        current.startedAtUtc = DateTime.UtcNow.ToString("O");
                        current.message = "mutation command running";
                        return current;
                    },
                    out envelope))
            {
                return;
            }

            AddCheckpoint(requestId, "running");
            ProjectCommandRequest? request;
            try
            {
                request = JsonUtility.FromJson<ProjectCommandRequest>(envelope.requestPayload);
            }
            catch (Exception ex)
            {
                request = null;
                AddCheckpoint(requestId, $"deserialize failed: {ex.GetType().Name}");
            }

            if (request is null || string.IsNullOrWhiteSpace(request.action))
            {
                FinalizeFailed(requestId, "stored mutation request payload is invalid");
                return;
            }

            request.requestId = requestId;
            try
            {
                var payload = await DaemonProjectService.ExecuteMutationWorkerAsync(request);
                var ok = false;
                try
                {
                    var parsed = JsonUtility.FromJson<ProjectCommandResponse>(payload);
                    ok = parsed is not null && parsed.ok;
                }
                catch
                {
                }

                if (ok)
                {
                    FinalizeSucceeded(requestId, payload);
                }
                else
                {
                    var message = TryExtractResponseMessage(payload) ?? "mutation command failed";
                    FinalizeFailed(requestId, message, payload);
                }
            }
            catch (OperationCanceledException)
            {
                FinalizeCanceled(requestId, "mutation command canceled");
            }
            catch (Exception ex)
            {
                FinalizeFailed(requestId, $"mutation command exception: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static string? TryExtractResponseMessage(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
            {
                return null;
            }

            try
            {
                var parsed = JsonUtility.FromJson<ProjectCommandResponse>(payload);
                return parsed?.message;
            }
            catch
            {
                return null;
            }
        }

        private static ProjectCommandStatusResponse BuildStatus(DurableMutationCommandEnvelope envelope)
        {
            var isTerminal = DurableMutationState.IsTerminal(envelope.state);
            return new ProjectCommandStatusResponse
            {
                requestId = envelope.requestId,
                action = envelope.action,
                active = !isTerminal,
                success = envelope.success,
                stage = envelope.state,
                detail = string.IsNullOrWhiteSpace(envelope.message) ? envelope.state : envelope.message,
                startedAtUtc = envelope.startedAtUtc,
                lastUpdatedAtUtc = envelope.updatedAtUtc,
                finishedAtUtc = envelope.finishedAtUtc,
                isDurable = true,
                state = envelope.state,
                cancelRequested = envelope.cancelRequested
            };
        }

        private static void AddCheckpoint(string requestId, string detail)
        {
            DaemonMutationCommandStore.TryUpdateEnvelope(
                requestId,
                current =>
                {
                    var checkpoints = current.checkpoints?.ToList() ?? new List<string>();
                    checkpoints.Add($"{DateTime.UtcNow:O} {detail}");
                    current.checkpoints = checkpoints.TakeLast(16).ToArray();
                    return current;
                },
                out _);
        }

        private static void FinalizeSucceeded(string requestId, string responsePayload)
        {
            DaemonMutationCommandStore.TryUpdateEnvelope(
                requestId,
                current =>
                {
                    current.state = DurableMutationState.Succeeded;
                    current.success = true;
                    current.message = "mutation command succeeded";
                    current.responsePayload = responsePayload ?? string.Empty;
                    current.finishedAtUtc = DateTime.UtcNow.ToString("O");
                    return current;
                },
                out _);
        }

        private static void FinalizeFailed(string requestId, string message, string? responsePayload = null)
        {
            DaemonMutationCommandStore.TryUpdateEnvelope(
                requestId,
                current =>
                {
                    current.state = DurableMutationState.Failed;
                    current.success = false;
                    current.error = message ?? "mutation command failed";
                    current.message = message ?? "mutation command failed";
                    current.responsePayload = responsePayload ?? current.responsePayload;
                    current.finishedAtUtc = DateTime.UtcNow.ToString("O");
                    return current;
                },
                out _);
        }

        private static void FinalizeCanceled(string requestId, string message)
        {
            DaemonMutationCommandStore.TryUpdateEnvelope(
                requestId,
                current =>
                {
                    current.state = DurableMutationState.Canceled;
                    current.success = false;
                    current.cancelRequested = true;
                    current.message = message;
                    current.finishedAtUtc = DateTime.UtcNow.ToString("O");
                    return current;
                },
                out _);
        }
    }
}
#endif
