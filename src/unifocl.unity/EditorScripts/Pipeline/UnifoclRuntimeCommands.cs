#if UNITY_EDITOR && UNIFOCL_PIPELINE
#nullable enable
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Unity.Pipeline.Commands;
using UnityEngine;

namespace UniFocl.EditorBridge.Pipeline
{
    /// <summary>
    /// The unifocl CLI's link to a running player (Editor Play Mode or a connected device build), carried
    /// by the official Unity CLI. The Editor relays each request to the player over
    /// <c>EditorConnection</c> (<see cref="DaemonRuntimeBridge"/>); each command takes and returns the JSON
    /// the CLI's runtime commands expect.
    /// </summary>
    /// <remarks>
    /// Like <see cref="UnifoclBridgeCommands"/>, a response with <c>ok: false</c> is still a successful
    /// call: the CLI reads the failure from the payload.
    /// </remarks>
    internal static class UnifoclRuntimeCommands
    {
        private const string Group = "unifocl.bridge";
        private const string PayloadHelp = "Request as JSON, in the shape the unifocl CLI sends";

        // ── Targets ─────────────────────────────────────────────────────────────────────────

        [CliCommand(Group, "runtime_targets", "unifocl CLI link: players the Editor can attach to",
            Effect = CommandEffect.ReadOnly)]
        internal static ExitCode Targets(CommandContext cmd)
        {
            DaemonRuntimeBridge.EnsureRegistered();
            return Reply(cmd, JsonUtility.ToJson(new RuntimeTargetListResponse
            {
                ok = true,
                targets = SerializeTargets(DaemonRuntimeBridge.ListTargets()),
            }));
        }

        [CliCommand(Group, "runtime_attach", "unifocl CLI link: attach to a player",
            Effect = CommandEffect.Idempotent)]
        internal static ExitCode Attach(CommandContext cmd, [CliArg("target", "Target address, such as editor:playmode")] string target = "editor:playmode")
        {
            DaemonRuntimeBridge.EnsureRegistered();
            var attached = DaemonRuntimeBridge.Attach(target);
            return Reply(cmd, JsonUtility.ToJson(new RuntimeSimpleResponse
            {
                ok = attached,
                message = attached ? $"attached to {target}" : $"no target found matching '{target}'",
            }));
        }

        [CliCommand(Group, "runtime_status", "unifocl CLI link: the attached player and connection state",
            Effect = CommandEffect.ReadOnly)]
        internal static ExitCode Status(CommandContext cmd) =>
            Reply(cmd, JsonUtility.ToJson(new RuntimeStatusResponse
            {
                ok = true,
                state = DaemonRuntimeBridge.ConnectionState.ToString(),
                targetAddress = DaemonRuntimeBridge.AttachedTargetAddress,
                playerId = DaemonRuntimeBridge.AttachedPlayerId,
            }));

        [CliCommand(Group, "runtime_detach", "unifocl CLI link: detach from the player",
            Effect = CommandEffect.Idempotent)]
        internal static ExitCode Detach(CommandContext cmd)
        {
            DaemonRuntimeBridge.Detach();
            return Reply(cmd, JsonUtility.ToJson(new RuntimeSimpleResponse { ok = true, message = "detached" }));
        }

        // ── Commands ────────────────────────────────────────────────────────────────────────

        [CliCommand(Group, "runtime_manifest", "unifocl CLI link: the commands the attached player offers",
            Effect = CommandEffect.ReadOnly)]
        internal static async Task<ExitCode> Manifest(CommandContext cmd)
        {
            try
            {
                var manifest = await OnMainThread(DaemonRuntimeBridge.RequestManifestAsync(10_000));
                return Reply(cmd, JsonUtility.ToJson(new RuntimeManifestResponse { ok = true, manifest = manifest }));
            }
            catch (Exception ex)
            {
                return Reply(cmd, JsonUtility.ToJson(new RuntimeManifestResponse { ok = false, error = ex.Message }));
            }
        }

        [CliCommand(Group, "runtime_exec", "unifocl CLI link: run a command on the attached player",
            Effect = CommandEffect.Destructive)]
        internal static async Task<ExitCode> Exec(CommandContext cmd, [CliArg("payload", PayloadHelp, Required = true)] string payload)
        {
            try
            {
                var request = JsonUtility.FromJson<RuntimeExecRequest>(payload);
                var resultJson = await OnMainThread(DaemonRuntimeBridge.ExecuteCommandAsync(request.command, request.argsJson, 30_000));
                var response = JsonUtility.FromJson<UniFocl.Runtime.RuntimeCommandResponse>(resultJson);
                return Reply(cmd, JsonUtility.ToJson(new RuntimeExecResponse
                {
                    ok = response?.success ?? false,
                    requestId = response?.requestId ?? string.Empty,
                    success = response?.success ?? false,
                    message = response?.message ?? "no response",
                    resultJson = response?.resultJson ?? "{}",
                }));
            }
            catch (Exception ex)
            {
                return Reply(cmd, JsonUtility.ToJson(new RuntimeExecResponse { ok = false, message = $"runtime exec failed: {ex.Message}" }));
            }
        }

        // ── Jobs ────────────────────────────────────────────────────────────────────────────

        [CliCommand(Group, "runtime_job_submit", "unifocl CLI link: start a long-running job on the attached player",
            Effect = CommandEffect.Destructive)]
        internal static async Task<ExitCode> JobSubmit(CommandContext cmd, [CliArg("payload", PayloadHelp, Required = true)] string payload)
        {
            try
            {
                var request = JsonUtility.FromJson<RuntimeJobSubmitRequest>(payload);
                var jobId = await OnMainThread(DaemonRuntimeBridge.SubmitJobAsync(request.command, request.argsJson, request.timeoutMs));
                return Reply(cmd, JsonUtility.ToJson(new RuntimeSimpleResponse { ok = true, message = jobId }));
            }
            catch (Exception ex)
            {
                return Reply(cmd, JsonUtility.ToJson(new RuntimeSimpleResponse { ok = false, message = ex.Message }));
            }
        }

        [CliCommand(Group, "runtime_job_status", "unifocl CLI link: state of a player job",
            Effect = CommandEffect.ReadOnly)]
        internal static ExitCode JobStatus(CommandContext cmd, [CliArg("job-id", "Job id", Required = true)] string jobId)
        {
            var job = DaemonRuntimeBridge.GetJobStatus(jobId);
            return Reply(cmd, job is null
                ? JsonUtility.ToJson(new RuntimeSimpleResponse { ok = false, message = $"job not found: {jobId}" })
                : JsonUtility.ToJson(ToStatus(job)));
        }

        [CliCommand(Group, "runtime_job_cancel", "unifocl CLI link: cancel a player job",
            Effect = CommandEffect.Idempotent)]
        internal static ExitCode JobCancel(CommandContext cmd, [CliArg("job-id", "Job id", Required = true)] string jobId)
        {
            var cancelled = DaemonRuntimeBridge.CancelJob(jobId);
            return Reply(cmd, JsonUtility.ToJson(new RuntimeSimpleResponse
            {
                ok = cancelled,
                message = cancelled ? $"job {jobId} cancelled" : $"cannot cancel job {jobId}",
            }));
        }

        [CliCommand(Group, "runtime_job_list", "unifocl CLI link: jobs on the attached player",
            Effect = CommandEffect.ReadOnly)]
        internal static ExitCode JobList(CommandContext cmd)
        {
            var jobs = DaemonRuntimeBridge.ListJobs();
            var items = new List<string>(jobs.Count);
            foreach (var job in jobs)
            {
                items.Add(JsonUtility.ToJson(ToStatus(job)));
            }

            return Reply(cmd, JsonUtility.ToJson(new RuntimeJobListResponse { ok = true, jobs = JsonArray(items) }));
        }

        // ── Streams and watches ─────────────────────────────────────────────────────────────

        [CliCommand(Group, "runtime_stream_subscribe", "unifocl CLI link: subscribe to a player event stream",
            Effect = CommandEffect.Idempotent)]
        internal static async Task<ExitCode> StreamSubscribe(CommandContext cmd, [CliArg("payload", PayloadHelp, Required = true)] string payload)
        {
            try
            {
                var request = JsonUtility.FromJson<RuntimeStreamSubscribeRequest>(payload);
                var subscriptionId = await OnMainThread(DaemonRuntimeBridge.SubscribeStreamAsync(request.channel, request.filterJson, 10_000));
                return Reply(cmd, JsonUtility.ToJson(new RuntimeStreamResponse { ok = true, subscriptionId = subscriptionId, message = "subscribed" }));
            }
            catch (Exception ex)
            {
                return Reply(cmd, JsonUtility.ToJson(new RuntimeStreamResponse { ok = false, message = ex.Message }));
            }
        }

        [CliCommand(Group, "runtime_stream_unsubscribe", "unifocl CLI link: end a player event stream subscription",
            Effect = CommandEffect.Idempotent)]
        internal static async Task<ExitCode> StreamUnsubscribe(CommandContext cmd, [CliArg("subscription-id", "Subscription id", Required = true)] string subscriptionId)
        {
            var removed = await OnMainThread(DaemonRuntimeBridge.UnsubscribeStreamAsync(subscriptionId, 10_000));
            return Reply(cmd, JsonUtility.ToJson(new RuntimeStreamResponse
            {
                ok = removed,
                subscriptionId = subscriptionId,
                message = removed ? "unsubscribed" : "subscription not found",
            }));
        }

        [CliCommand(Group, "runtime_watch_add", "unifocl CLI link: watch an expression on the attached player",
            Effect = CommandEffect.Idempotent)]
        internal static ExitCode WatchAdd(CommandContext cmd, [CliArg("payload", PayloadHelp, Required = true)] string payload)
        {
            var request = JsonUtility.FromJson<RuntimeWatchRequest>(payload);
            var watchId = DaemonRuntimeBridge.AddWatch(request.expression, request.target, request.intervalMs);
            return Reply(cmd, JsonUtility.ToJson(new RuntimeWatchResponse { ok = true, watchId = watchId, message = "watch added" }));
        }

        [CliCommand(Group, "runtime_watch_remove", "unifocl CLI link: stop watching an expression",
            Effect = CommandEffect.Idempotent)]
        internal static ExitCode WatchRemove(CommandContext cmd, [CliArg("watch-id", "Watch id", Required = true)] string watchId)
        {
            var removed = DaemonRuntimeBridge.RemoveWatch(watchId);
            return Reply(cmd, JsonUtility.ToJson(new RuntimeWatchResponse
            {
                ok = removed,
                watchId = watchId,
                message = removed ? "watch removed" : "watch not found",
            }));
        }

        [CliCommand(Group, "runtime_watch_list", "unifocl CLI link: watched expressions",
            Effect = CommandEffect.ReadOnly)]
        internal static ExitCode WatchList(CommandContext cmd)
        {
            var items = new List<string>();
            foreach (var watch in DaemonRuntimeBridge.ListWatches())
            {
                items.Add(
                    $"{{\"watchId\":\"{watch.watchId}\",\"expression\":\"{DaemonRuntimeBridge.EscapeJsonPublic(watch.expression)}\"," +
                    $"\"target\":\"{DaemonRuntimeBridge.EscapeJsonPublic(watch.target)}\",\"intervalMs\":{watch.intervalMs}," +
                    $"\"active\":{(watch.active ? "true" : "false")}}}");
            }

            return Reply(cmd, JsonUtility.ToJson(new RuntimeWatchListResponse { ok = true, watches = JsonArray(items) }));
        }

        [CliCommand(Group, "runtime_watch_poll", "unifocl CLI link: current values of the watched expressions",
            Effect = CommandEffect.ReadOnly)]
        internal static async Task<ExitCode> WatchPoll(CommandContext cmd)
        {
            try
            {
                var snapshots = await OnMainThread(DaemonRuntimeBridge.PollWatchesAsync(10_000));
                var items = new List<string>(snapshots.Count);
                foreach (var snapshot in snapshots)
                {
                    items.Add(JsonUtility.ToJson(new RuntimeWatchSnapshotEntry
                    {
                        watchId = snapshot.watchId,
                        expression = snapshot.expression,
                        valueJson = snapshot.valueJson,
                        timestampUtcMs = snapshot.timestampUtcMs,
                    }));
                }

                return Reply(cmd, $"{{\"ok\":true,\"snapshots\":{JsonArray(items)}}}");
            }
            catch (Exception ex)
            {
                return Reply(cmd, JsonUtility.ToJson(new RuntimeSimpleResponse { ok = false, message = ex.Message }));
            }
        }

        // ── Helpers ─────────────────────────────────────────────────────────────────────────

        private static RuntimeJobStatusResponse ToStatus(DaemonRuntimeBridge.RuntimeJobState job) => new()
        {
            ok = true,
            jobId = job.jobId,
            state = job.state,
            progress = job.progress,
            message = job.message,
            resultJson = job.resultJson,
        };

        private static string SerializeTargets(List<DaemonRuntimeBridge.RuntimeTargetInfo> targets)
        {
            var items = new List<string>(targets.Count);
            foreach (var t in targets)
            {
                items.Add(
                    $"{{\"playerId\":{t.playerId},\"name\":\"{DaemonRuntimeBridge.EscapeJsonPublic(t.name)}\"," +
                    $"\"platform\":\"{DaemonRuntimeBridge.EscapeJsonPublic(t.platform)}\",\"deviceId\":\"{DaemonRuntimeBridge.EscapeJsonPublic(t.deviceId)}\"," +
                    $"\"isConnected\":{(t.isConnected ? "true" : "false")}}}");
            }

            return JsonArray(items);
        }

        private static string JsonArray(List<string> items)
        {
            var sb = new StringBuilder("[");
            sb.Append(string.Join(",", items));
            return sb.Append(']').ToString();
        }

        private static ExitCode Reply(CommandContext cmd, string json)
        {
            CLIDaemon.MarkActivity();
            cmd.Result.Write(JToken.Parse(json));
            return ExitCode.Success;
        }

        /// <summary>
        /// Waits for a player round trip by giving frames back to the Editor, so the reply from the player
        /// (which arrives on the main thread) can complete the task and the command resumes there.
        /// </summary>
        private static async Task<T> OnMainThread<T>(Task<T> task)
        {
            while (!task.IsCompleted)
            {
                await Task.Yield();
            }

            return await task;
        }
    }
}
#endif
