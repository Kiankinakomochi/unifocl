using System.Net.Http;
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
}
