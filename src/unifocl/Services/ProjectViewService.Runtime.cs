using System.Net.Http;
using System.Text;
using System.Text.Json;

internal sealed partial class ProjectViewService
{
    private static readonly HttpClient RuntimeHttp = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly JsonSerializerOptions RuntimeJsonOpts =
        new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    // ── runtime target list ─────────────────────────────────────────────

    private async Task<bool> HandleRuntimeCommandAsync(
        IReadOnlyList<string> tokens,
        CliSessionState session,
        List<string> outputs)
    {
        // tokens: runtime <sub> [args...]
        if (tokens.Count < 2)
        {
            outputs.Add("[i] usage: runtime <target list|attach|status|detach> [args...]");
            return true;
        }

        if (DaemonControlService.GetPort(session) is not int port)
        {
            outputs.Add("[x] daemon is not attached");
            return true;
        }

        var sub = tokens[1].ToLowerInvariant();
        switch (sub)
        {
            case "target" when tokens.Count >= 3 && tokens[2].Equals("list", StringComparison.OrdinalIgnoreCase):
                return await HandleRuntimeTargetListAsync(port, outputs);

            case "attach":
            {
                var target = tokens.Count >= 3 ? tokens[2] : "editor:playmode";
                return await HandleRuntimeAttachAsync(port, target, outputs);
            }

            case "status":
                return await HandleRuntimeStatusAsync(port, outputs);

            case "detach":
                return await HandleRuntimeDetachAsync(port, outputs);

            // S2: manifest
            case "manifest":
                return await HandleRuntimeManifestAsync(port, outputs);

            // S3: query + exec
            case "query" or "exec":
            {
                if (tokens.Count < 3)
                {
                    outputs.Add($"[i] usage: runtime {sub} <command> [argsJson]");
                    return true;
                }
                var command = tokens[2];
                var argsJson = tokens.Count >= 4 ? string.Join(" ", tokens.Skip(3)) : "{}";
                return await HandleRuntimeExecCliAsync(port, command, argsJson, outputs);
            }

            // S4: jobs
            case "job" when tokens.Count >= 3:
                return await HandleRuntimeJobCliAsync(port, tokens, outputs);

            // S5: stream
            case "stream" when tokens.Count >= 3:
                return await HandleRuntimeStreamCliAsync(port, tokens, outputs);

            // S5: watch
            case "watch" when tokens.Count >= 3:
                return await HandleRuntimeWatchCliAsync(port, tokens, outputs);

            // S6: scenario
            case "scenario" when tokens.Count >= 3:
                return await HandleRuntimeScenarioCliAsync(port, tokens, session, outputs);

            default:
                outputs.Add($"[x] unknown runtime subcommand: {sub}");
                return true;
        }
    }

    private async Task<bool> HandleRuntimeTargetListAsync(int port, List<string> outputs)
    {
        var json = await SendRuntimeGetAsync(port, "/runtime/targets");
        if (json is null)
        {
            outputs.Add("[x] failed to reach daemon");
            return true;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("targets", out var targetsRaw))
            {
                outputs.Add("[i] no targets reported");
                return true;
            }

            // targets is a JSON string containing an array
            var targetsJson = targetsRaw.GetString() ?? "[]";
            using var arrDoc = JsonDocument.Parse(targetsJson);
            var arr = arrDoc.RootElement;

            if (arr.GetArrayLength() == 0)
            {
                outputs.Add("[i] no runtime targets available (enter Play Mode or connect a device)");
                return true;
            }

            outputs.Add("[+] runtime targets:");
            foreach (var t in arr.EnumerateArray())
            {
                var name = t.GetProperty("name").GetString();
                var platform = t.GetProperty("platform").GetString();
                var playerId = t.GetProperty("playerId").GetInt32();
                var connected = t.GetProperty("isConnected").GetBoolean();
                var marker = connected ? "*" : " ";
                outputs.Add($"  {marker} {platform}:{name}  (id={playerId})");
            }
        }
        catch
        {
            outputs.Add("[x] failed to parse target list");
        }

        return true;
    }

    private async Task<bool> HandleRuntimeAttachAsync(int port, string target, List<string> outputs)
    {
        var json = await SendRuntimePostAsync(port, $"/runtime/attach?target={Uri.EscapeDataString(target)}");
        if (json is null)
        {
            outputs.Add("[x] failed to reach daemon");
            return true;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var ok = doc.RootElement.TryGetProperty("ok", out var okProp) && okProp.GetBoolean();
            var message = doc.RootElement.TryGetProperty("message", out var msgProp)
                ? msgProp.GetString() : null;
            outputs.Add(ok ? $"[+] {message}" : $"[x] {message}");
        }
        catch
        {
            outputs.Add("[x] failed to parse attach response");
        }

        return true;
    }

    private async Task<bool> HandleRuntimeStatusAsync(int port, List<string> outputs)
    {
        var json = await SendRuntimeGetAsync(port, "/runtime/status");
        if (json is null)
        {
            outputs.Add("[x] failed to reach daemon");
            return true;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var state = root.TryGetProperty("state", out var stateProp) ? stateProp.GetString() : "unknown";
            var addr = root.TryGetProperty("targetAddress", out var addrProp) ? addrProp.GetString() : "";
            var playerId = root.TryGetProperty("playerId", out var pidProp) ? pidProp.GetInt32() : -1;

            if (state == "Connected")
            {
                outputs.Add($"[+] connected to {addr} (playerId={playerId})");
            }
            else
            {
                outputs.Add($"[i] state: {state}");
            }
        }
        catch
        {
            outputs.Add("[x] failed to parse status response");
        }

        return true;
    }

    private async Task<bool> HandleRuntimeDetachAsync(int port, List<string> outputs)
    {
        var json = await SendRuntimePostAsync(port, "/runtime/detach");
        if (json is null)
        {
            outputs.Add("[x] failed to reach daemon");
            return true;
        }

        outputs.Add("[+] detached from runtime target");
        return true;
    }

    private static async Task<string?> SendRuntimeGetAsync(int port, string path)
    {
        try
        {
            return await RuntimeHttp.GetStringAsync($"http://127.0.0.1:{port}{path}");
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> SendRuntimePostAsync(int port, string path)
    {
        try
        {
            using var resp = await RuntimeHttp.PostAsync($"http://127.0.0.1:{port}{path}", null);
            return await resp.Content.ReadAsStringAsync();
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> SendRuntimePostWithBodyAsync(int port, string path, string body)
    {
        try
        {
            using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            using var resp = await RuntimeHttp.PostAsync($"http://127.0.0.1:{port}{path}", content);
            return await resp.Content.ReadAsStringAsync();
        }
        catch
        {
            return null;
        }
    }

    // ── S2: manifest ────────────────────────────────────────────────────

    private async Task<bool> HandleRuntimeManifestAsync(int port, List<string> outputs)
    {
        var json = await SendRuntimeGetAsync(port, "/runtime/manifest");
        if (json is null)
        {
            outputs.Add("[x] failed to reach daemon");
            return true;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var ok = root.TryGetProperty("ok", out var okProp) && okProp.GetBoolean();
            if (!ok)
            {
                var err = root.TryGetProperty("error", out var errProp) ? errProp.GetString() : "unknown error";
                outputs.Add($"[x] manifest request failed: {err}");
                return true;
            }

            var manifestStr = root.TryGetProperty("manifest", out var mProp) ? mProp.GetString() ?? "{}" : "{}";
            using var manifestDoc = JsonDocument.Parse(manifestStr);
            var manifest = manifestDoc.RootElement;

            if (!manifest.TryGetProperty("categories", out var cats))
            {
                outputs.Add("[i] empty manifest");
                return true;
            }

            outputs.Add("[+] runtime manifest:");
            foreach (var cat in cats.EnumerateArray())
            {
                var catName = cat.TryGetProperty("name", out var n) ? n.GetString() : "?";
                outputs.Add($"  category: {catName}");
                if (cat.TryGetProperty("tools", out var tools))
                {
                    foreach (var tool in tools.EnumerateArray())
                    {
                        var tName = tool.TryGetProperty("name", out var tn) ? tn.GetString() : "?";
                        var tDesc = tool.TryGetProperty("description", out var td) ? td.GetString() : "";
                        var tKind = tool.TryGetProperty("kind", out var tk) ? tk.GetString() : "";
                        outputs.Add($"    {tName} [{tKind}] — {tDesc}");
                    }
                }
            }
        }
        catch
        {
            outputs.Add("[x] failed to parse manifest response");
        }

        return true;
    }

    // ── S3: query/exec ──────────────────────────────────────────────────

    private async Task<bool> HandleRuntimeExecCliAsync(int port, string command, string argsJson, List<string> outputs)
    {
        var body = JsonSerializer.Serialize(new { command, argsJson }, RuntimeJsonOpts);
        var json = await SendRuntimePostWithBodyAsync(port, "/runtime/exec", body);
        if (json is null)
        {
            outputs.Add("[x] failed to reach daemon");
            return true;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var success = root.TryGetProperty("success", out var sp) && sp.GetBoolean();
            var message = root.TryGetProperty("message", out var mp) ? mp.GetString() : "";
            var resultJson = root.TryGetProperty("resultJson", out var rp) ? rp.GetString() : "{}";

            outputs.Add(success ? $"[+] {command}: {message}" : $"[x] {command}: {message}");
            if (!string.IsNullOrWhiteSpace(resultJson) && resultJson != "{}")
            {
                outputs.Add($"  result: {resultJson}");
            }
        }
        catch
        {
            outputs.Add("[x] failed to parse exec response");
        }

        return true;
    }

    // ── S4: jobs ────────────────────────────────────────────────────────

    private async Task<bool> HandleRuntimeJobCliAsync(int port, IReadOnlyList<string> tokens, List<string> outputs)
    {
        var sub = tokens[2].ToLowerInvariant();
        switch (sub)
        {
            case "submit":
            {
                if (tokens.Count < 4)
                {
                    outputs.Add("[i] usage: runtime job submit <command> [argsJson]");
                    return true;
                }
                var command = tokens[3];
                var argsJson = tokens.Count >= 5 ? string.Join(" ", tokens.Skip(4)) : "{}";
                var body = JsonSerializer.Serialize(new { command, argsJson, timeoutMs = 60000 }, RuntimeJsonOpts);
                var json = await SendRuntimePostWithBodyAsync(port, "/runtime/job/submit", body);
                if (json is null)
                {
                    outputs.Add("[x] failed to reach daemon");
                    return true;
                }
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var ok = doc.RootElement.TryGetProperty("ok", out var okP) && okP.GetBoolean();
                    var msg = doc.RootElement.TryGetProperty("message", out var msgP) ? msgP.GetString() : "";
                    outputs.Add(ok ? $"[+] job submitted: {msg}" : $"[x] {msg}");
                }
                catch { outputs.Add("[x] failed to parse response"); }
                return true;
            }

            case "status":
            {
                var jobId = tokens.Count >= 4 ? tokens[3] : "";
                var json = await SendRuntimeGetAsync(port, $"/runtime/job/status?jobId={Uri.EscapeDataString(jobId)}");
                if (json is null)
                {
                    outputs.Add("[x] failed to reach daemon");
                    return true;
                }
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    var state = root.TryGetProperty("state", out var sp) ? sp.GetString() : "unknown";
                    var progress = root.TryGetProperty("progress", out var pp) ? pp.GetSingle() : 0f;
                    var msg = root.TryGetProperty("message", out var mp) ? mp.GetString() : "";
                    outputs.Add($"[+] job {jobId}: {state} ({progress:P0}) — {msg}");
                }
                catch { outputs.Add("[x] failed to parse response"); }
                return true;
            }

            case "cancel":
            {
                var jobId = tokens.Count >= 4 ? tokens[3] : "";
                var json = await SendRuntimePostAsync(port, $"/runtime/job/cancel?jobId={Uri.EscapeDataString(jobId)}");
                if (json is null)
                {
                    outputs.Add("[x] failed to reach daemon");
                    return true;
                }
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var ok = doc.RootElement.TryGetProperty("ok", out var okP) && okP.GetBoolean();
                    var msg = doc.RootElement.TryGetProperty("message", out var msgP) ? msgP.GetString() : "";
                    outputs.Add(ok ? $"[+] {msg}" : $"[x] {msg}");
                }
                catch { outputs.Add("[x] failed to parse response"); }
                return true;
            }

            case "list":
            {
                var json = await SendRuntimeGetAsync(port, "/runtime/job/list");
                if (json is null)
                {
                    outputs.Add("[x] failed to reach daemon");
                    return true;
                }
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var jobsStr = doc.RootElement.TryGetProperty("jobs", out var jp) ? jp.GetString() ?? "[]" : "[]";
                    using var arrDoc = JsonDocument.Parse(jobsStr);
                    var arr = arrDoc.RootElement;
                    if (arr.GetArrayLength() == 0)
                    {
                        outputs.Add("[i] no active jobs");
                    }
                    else
                    {
                        outputs.Add("[+] runtime jobs:");
                        foreach (var j in arr.EnumerateArray())
                        {
                            var jid = j.TryGetProperty("jobId", out var jp2) ? jp2.GetString() : "?";
                            var st = j.TryGetProperty("state", out var sp) ? sp.GetString() : "?";
                            var cmd = j.TryGetProperty("message", out var mp) ? mp.GetString() : "";
                            outputs.Add($"  {jid}: {st} — {cmd}");
                        }
                    }
                }
                catch { outputs.Add("[x] failed to parse response"); }
                return true;
            }

            default:
                outputs.Add($"[x] unknown job subcommand: {sub}. Use submit|status|cancel|list.");
                return true;
        }
    }

    // ── S5: stream ──────────────────────────────────────────────────────

    private async Task<bool> HandleRuntimeStreamCliAsync(int port, IReadOnlyList<string> tokens, List<string> outputs)
    {
        var sub = tokens[2].ToLowerInvariant();
        switch (sub)
        {
            case "subscribe":
            {
                var channel = tokens.Count >= 4 ? tokens[3] : "";
                if (string.IsNullOrWhiteSpace(channel))
                {
                    outputs.Add("[i] usage: runtime stream subscribe <channel> [filterJson]");
                    return true;
                }
                var filterJson = tokens.Count >= 5 ? string.Join(" ", tokens.Skip(4)) : "{}";
                var body = JsonSerializer.Serialize(new { channel, filterJson }, RuntimeJsonOpts);
                var json = await SendRuntimePostWithBodyAsync(port, "/runtime/stream/subscribe", body);
                if (json is null)
                {
                    outputs.Add("[x] failed to reach daemon");
                    return true;
                }
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var ok = doc.RootElement.TryGetProperty("ok", out var okP) && okP.GetBoolean();
                    var subId = doc.RootElement.TryGetProperty("subscriptionId", out var sp) ? sp.GetString() : "";
                    outputs.Add(ok ? $"[+] subscribed to {channel}: {subId}" : "[x] subscribe failed");
                }
                catch { outputs.Add("[x] failed to parse response"); }
                return true;
            }

            case "unsubscribe":
            {
                var subId = tokens.Count >= 4 ? tokens[3] : "";
                var json = await SendRuntimePostAsync(port, $"/runtime/stream/unsubscribe?subscriptionId={Uri.EscapeDataString(subId)}");
                if (json is null)
                {
                    outputs.Add("[x] failed to reach daemon");
                    return true;
                }
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var ok = doc.RootElement.TryGetProperty("ok", out var okP) && okP.GetBoolean();
                    outputs.Add(ok ? $"[+] unsubscribed: {subId}" : $"[x] subscription not found: {subId}");
                }
                catch { outputs.Add("[x] failed to parse response"); }
                return true;
            }

            default:
                outputs.Add($"[x] unknown stream subcommand: {sub}. Use subscribe|unsubscribe.");
                return true;
        }
    }

    // ── S5: watch ───────────────────────────────────────────────────────

    private async Task<bool> HandleRuntimeWatchCliAsync(int port, IReadOnlyList<string> tokens, List<string> outputs)
    {
        var sub = tokens[2].ToLowerInvariant();
        switch (sub)
        {
            case "add":
            {
                var expr = tokens.Count >= 4 ? tokens[3] : "";
                if (string.IsNullOrWhiteSpace(expr))
                {
                    outputs.Add("[i] usage: runtime watch add <expression> [target] [intervalMs]");
                    return true;
                }
                var target = tokens.Count >= 5 ? tokens[4] : "";
                var interval = 1000;
                if (tokens.Count >= 6) _ = int.TryParse(tokens[5], out interval);
                var body = JsonSerializer.Serialize(new { expression = expr, target, intervalMs = interval }, RuntimeJsonOpts);
                var json = await SendRuntimePostWithBodyAsync(port, "/runtime/watch/add", body);
                if (json is null)
                {
                    outputs.Add("[x] failed to reach daemon");
                    return true;
                }
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var wid = doc.RootElement.TryGetProperty("watchId", out var wp) ? wp.GetString() : "?";
                    outputs.Add($"[+] watch added: {wid}");
                }
                catch { outputs.Add("[x] failed to parse response"); }
                return true;
            }

            case "remove":
            {
                var watchId = tokens.Count >= 4 ? tokens[3] : "";
                var json = await SendRuntimePostAsync(port, $"/runtime/watch/remove?watchId={Uri.EscapeDataString(watchId)}");
                if (json is null)
                {
                    outputs.Add("[x] failed to reach daemon");
                    return true;
                }
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var ok = doc.RootElement.TryGetProperty("ok", out var okP) && okP.GetBoolean();
                    outputs.Add(ok ? $"[+] watch removed: {watchId}" : $"[x] watch not found: {watchId}");
                }
                catch { outputs.Add("[x] failed to parse response"); }
                return true;
            }

            case "list":
            {
                var json = await SendRuntimeGetAsync(port, "/runtime/watch/list");
                if (json is null)
                {
                    outputs.Add("[x] failed to reach daemon");
                    return true;
                }
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var watchesStr = doc.RootElement.TryGetProperty("watches", out var wp) ? wp.GetString() ?? "[]" : "[]";
                    using var arrDoc = JsonDocument.Parse(watchesStr);
                    var arr = arrDoc.RootElement;
                    if (arr.GetArrayLength() == 0)
                    {
                        outputs.Add("[i] no active watches");
                    }
                    else
                    {
                        outputs.Add("[+] active watches:");
                        foreach (var w in arr.EnumerateArray())
                        {
                            var wid = w.TryGetProperty("watchId", out var wp2) ? wp2.GetString() : "?";
                            var expr = w.TryGetProperty("expression", out var ep) ? ep.GetString() : "?";
                            outputs.Add($"  {wid}: {expr}");
                        }
                    }
                }
                catch { outputs.Add("[x] failed to parse response"); }
                return true;
            }

            case "poll":
            {
                var json = await SendRuntimeGetAsync(port, "/runtime/watch/poll");
                if (json is null)
                {
                    outputs.Add("[x] failed to reach daemon");
                    return true;
                }
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("snapshots", out var snapArr))
                    {
                        if (snapArr.GetArrayLength() == 0)
                        {
                            outputs.Add("[i] no watch data");
                        }
                        else
                        {
                            outputs.Add("[+] watch snapshots:");
                            foreach (var s in snapArr.EnumerateArray())
                            {
                                var wid = s.TryGetProperty("watchId", out var wp2) ? wp2.GetString() : "?";
                                var expr = s.TryGetProperty("expression", out var ep) ? ep.GetString() : "?";
                                var val = s.TryGetProperty("valueJson", out var vp) ? vp.GetString() : "{}";
                                outputs.Add($"  {wid} ({expr}): {val}");
                            }
                        }
                    }
                    else
                    {
                        outputs.Add("[i] no snapshot data");
                    }
                }
                catch { outputs.Add("[x] failed to parse response"); }
                return true;
            }

            default:
                outputs.Add($"[x] unknown watch subcommand: {sub}. Use add|remove|list|poll.");
                return true;
        }
    }

    // ── S6: scenario ────────────────────────────────────────────────────

    private async Task<bool> HandleRuntimeScenarioCliAsync(
        int port, IReadOnlyList<string> tokens, CliSessionState session, List<string> outputs)
    {
        var sub = tokens[2].ToLowerInvariant();
        switch (sub)
        {
            case "run":
            {
                if (tokens.Count < 4)
                {
                    outputs.Add("[i] usage: runtime scenario run <path>");
                    return true;
                }
                var scenarioPath = tokens[3];
                var projectPath = session.CurrentProjectPath ?? "";
                var fullPath = Path.IsPathRooted(scenarioPath)
                    ? scenarioPath
                    : Path.Combine(projectPath, scenarioPath);

                if (!File.Exists(fullPath))
                {
                    outputs.Add($"[x] scenario file not found: {fullPath}");
                    return true;
                }

                var service = new RuntimeScenarioService();
                var result = await service.RunAsync(fullPath, port, CancellationToken.None);
                outputs.Add(result.AllPassed
                    ? $"[+] scenario '{result.ScenarioName}': {result.Summary}"
                    : $"[x] scenario '{result.ScenarioName}': {result.Summary}");
                foreach (var step in result.Steps)
                {
                    var marker = step.Passed ? "+" : "x";
                    outputs.Add($"  [{marker}] {step.StepName}: {step.Message}");
                }
                return true;
            }

            case "list":
            {
                var projectPath = session.CurrentProjectPath ?? "";
                var scenarioDir = Path.Combine(projectPath, ".unifocl", "scenarios");
                if (!Directory.Exists(scenarioDir))
                {
                    outputs.Add("[i] no scenario directory (.unifocl/scenarios/)");
                    return true;
                }

                var files = Directory.GetFiles(scenarioDir, "*.yaml", SearchOption.AllDirectories)
                    .Concat(Directory.GetFiles(scenarioDir, "*.yml", SearchOption.AllDirectories))
                    .Select(f => Path.GetRelativePath(projectPath, f).Replace('\\', '/'))
                    .OrderBy(f => f)
                    .ToArray();

                if (files.Length == 0)
                {
                    outputs.Add("[i] no scenario files found");
                }
                else
                {
                    outputs.Add($"[+] {files.Length} scenario(s):");
                    foreach (var f in files) outputs.Add($"  {f}");
                }
                return true;
            }

            case "validate":
            {
                if (tokens.Count < 4)
                {
                    outputs.Add("[i] usage: runtime scenario validate <path>");
                    return true;
                }
                var scenarioPath = tokens[3];
                var projectPath = session.CurrentProjectPath ?? "";
                var fullPath = Path.IsPathRooted(scenarioPath)
                    ? scenarioPath
                    : Path.Combine(projectPath, scenarioPath);

                if (!File.Exists(fullPath))
                {
                    outputs.Add($"[x] scenario file not found: {fullPath}");
                    return true;
                }

                var service = new RuntimeScenarioService();
                var (valid, errors) = service.Validate(fullPath);
                if (valid)
                {
                    outputs.Add("[+] scenario is valid");
                }
                else
                {
                    outputs.Add($"[x] {errors.Count} validation error(s):");
                    foreach (var e in errors) outputs.Add($"  - {e}");
                }
                return true;
            }

            default:
                outputs.Add($"[x] unknown scenario subcommand: {sub}. Use run|list|validate.");
                return true;
        }
    }
}
