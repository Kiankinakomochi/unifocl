using System.Net.Http;
using System.Text;
using System.Text.Json;
using Spectre.Console;

internal static class CliDumpService
{
    private static readonly TimeSpan InspectorDumpRequestTimeout = TimeSpan.FromSeconds(8);
    public static async Task HandleDumpCommandAsync(string input, CliSessionState session, List<string> streamLog)
    {
        var result = await ExecuteDumpCommandAsync(input, session);
        if (!result.Ok)
        {
            CliLogService.AppendLog(streamLog, $"[red]dump[/]: {Markup.Escape(result.Error?.Message ?? "dump failed")}");
            return;
        }

        CliLogService.AppendLog(streamLog, $"[grey]dump[/]: emitted {result.Category} ({result.Format.ToString().ToLowerInvariant()})");
        if (CliRuntimeState.SuppressConsoleOutput)
        {
            return;
        }

        Console.WriteLine(result.PayloadText);
    }

    public static async Task<(bool Ok, AgenticOutputFormat Format, string Category, object? PayloadData, string PayloadText, AgenticError? Error)> ExecuteDumpCommandAsync(string input, CliSessionState session)
    {
        var tokens = CliCommandParsingService.TokenizeComposerInput(input);
        if (tokens.Count < 2)
        {
            return (false, AgenticOutputFormat.Json, string.Empty, null, string.Empty, new AgenticError("E_PARSE", "usage: /dump <hierarchy|project|inspector> [--format json|yaml] [--compact] [--depth n] [--limit n]"));
        }

        var category = tokens[1].Trim().ToLowerInvariant();
        var format = AgenticOutputFormat.Json;
        var depth = 6;
        var limit = 1000;
        for (var i = 2; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Equals("--compact", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (token.Equals("--format", StringComparison.OrdinalIgnoreCase) && i + 1 < tokens.Count)
            {
                var raw = tokens[++i].Trim().ToLowerInvariant();
                if (raw == "json")
                {
                    format = AgenticOutputFormat.Json;
                }
                else if (raw == "yaml")
                {
                    format = AgenticOutputFormat.Yaml;
                }
                else
                {
                    return (false, AgenticOutputFormat.Json, string.Empty, null, string.Empty, new AgenticError("E_PARSE", "invalid --format value for /dump (use json|yaml)"));
                }
                continue;
            }

            if (token.Equals("--depth", StringComparison.OrdinalIgnoreCase) && i + 1 < tokens.Count)
            {
                _ = int.TryParse(tokens[++i], out depth);
                depth = Math.Clamp(depth, 1, 20);
                continue;
            }

            if (token.Equals("--limit", StringComparison.OrdinalIgnoreCase) && i + 1 < tokens.Count)
            {
                _ = int.TryParse(tokens[++i], out limit);
                limit = Math.Clamp(limit, 1, 20000);
                continue;
            }
        }

        var recognizedCategory = category is "hierarchy" or "project" or "inspector";
        if (!recognizedCategory)
        {
            return (false, format, category, null, string.Empty, new AgenticError("E_VALIDATION", $"unsupported dump category: {category}", "supported: hierarchy, project, inspector"));
        }

        object? data = category switch
        {
            "hierarchy" => await BuildHierarchyDumpAsync(session),
            "project" => BuildProjectDump(session, depth, limit),
            "inspector" => await BuildInspectorDumpAsync(session),
            _ => null
        };

        if (data is null)
        {
            var hint = category switch
            {
                "project" => "set --project or open a project first",
                "hierarchy" => "attach daemon with --attach-port to fetch hierarchy snapshot",
                "inspector" => "attach daemon and enter inspector context before dumping inspector",
                _ => string.Empty
            };
            return (false, format, category, null, string.Empty, new AgenticError("E_MODE_INVALID", $"dump {category} is unavailable in current session", hint));
        }

        var payload = format == AgenticOutputFormat.Yaml
            ? AgenticFormatter.SerializeYaml(data)
            : JsonSerializer.Serialize(data, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        return (true, format, category, data, payload, null);
    }

    private static async Task<object?> BuildHierarchyDumpAsync(CliSessionState session)
    {
        if (session.AttachedPort is null)
        {
            return null;
        }

        var client = new HierarchyDaemonClient();
        var snapshot = await client.GetSnapshotAsync(session.AttachedPort.Value);
        return snapshot;
    }

    private static object? BuildProjectDump(CliSessionState session, int depth, int limit)
    {
        if (string.IsNullOrWhiteSpace(session.CurrentProjectPath))
        {
            return null;
        }

        var root = Path.Combine(session.CurrentProjectPath!, "Assets");
        if (!Directory.Exists(root))
        {
            return new { root = "Assets", entries = Array.Empty<object>() };
        }

        var entries = new List<object>();
        var count = 0;
        var stack = new Stack<(string AbsolutePath, string RelativePath, int Depth)>();
        stack.Push((root, "Assets", 0));

        while (stack.Count > 0 && count < limit)
        {
            var current = stack.Pop();
            if (current.Depth > depth)
            {
                continue;
            }

            var directories = Directory.GetDirectories(current.AbsolutePath)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var files = Directory.GetFiles(current.AbsolutePath)
                .Where(path => !path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var file in files)
            {
                if (count >= limit)
                {
                    break;
                }

                var rel = CombineDumpRelative(current.RelativePath, Path.GetFileName(file));
                entries.Add(new { path = rel, kind = "file" });
                count++;
            }

            for (var i = directories.Count - 1; i >= 0; i--)
            {
                if (count >= limit)
                {
                    break;
                }

                var dir = directories[i];
                var rel = CombineDumpRelative(current.RelativePath, Path.GetFileName(dir));
                entries.Add(new { path = rel, kind = "directory" });
                count++;
                stack.Push((dir, rel, current.Depth + 1));
            }
        }

        return new { root = "Assets", entries };
    }

    private static string CombineDumpRelative(string basePath, string name)
    {
        var normalizedBase = basePath.Replace('\\', '/').TrimEnd('/');
        var normalizedName = name.Replace('\\', '/').Trim('/');
        if (string.IsNullOrWhiteSpace(normalizedBase))
        {
            return normalizedName;
        }

        return string.IsNullOrWhiteSpace(normalizedName)
            ? normalizedBase
            : $"{normalizedBase}/{normalizedName}";
    }

    private static async Task<object?> BuildInspectorDumpAsync(CliSessionState session)
    {
        if (session.AttachedPort is null)
        {
            return null;
        }

        try
        {
            var targetPath = session.Inspector?.PromptPath ?? "/";
            using var http = new HttpClient();
            using var cts = new CancellationTokenSource(InspectorDumpRequestTimeout);
            var listPayload = JsonSerializer.Serialize(new
            {
                action = "list-components",
                targetPath,
                componentIndex = -1,
                componentName = "",
                fieldName = "",
                value = "",
                query = ""
            });
            using var listResponse = await http.PostAsync(
                $"http://127.0.0.1:{session.AttachedPort.Value}/inspect",
                new StringContent(listPayload, Encoding.UTF8, "application/json"),
                cts.Token);
            if (!listResponse.IsSuccessStatusCode)
            {
                return null;
            }

            var listBody = await listResponse.Content.ReadAsStringAsync(cts.Token);
            using var listDoc = JsonDocument.Parse(listBody);
            if (!listDoc.RootElement.TryGetProperty("components", out var components))
            {
                return new { targetPath, components = Array.Empty<object>() };
            }

            var expanded = new List<object>();
            foreach (var component in components.EnumerateArray())
            {
                var index = component.TryGetProperty("index", out var indexProp) ? indexProp.GetInt32() : -1;
                var name = component.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? string.Empty : string.Empty;
                var fieldsPayload = JsonSerializer.Serialize(new
                {
                    action = "list-fields",
                    targetPath,
                    componentIndex = index,
                    componentName = name,
                    fieldName = "",
                    value = "",
                    query = ""
                });
                using var fieldResponse = await http.PostAsync(
                    $"http://127.0.0.1:{session.AttachedPort.Value}/inspect",
                    new StringContent(fieldsPayload, Encoding.UTF8, "application/json"),
                    cts.Token);
                var fields = Array.Empty<object>();
                if (fieldResponse.IsSuccessStatusCode)
                {
                    var fieldsBody = await fieldResponse.Content.ReadAsStringAsync(cts.Token);
                    using var fieldsDoc = JsonDocument.Parse(fieldsBody);
                    if (fieldsDoc.RootElement.TryGetProperty("fields", out var fieldsElement))
                    {
                        fields = fieldsElement.EnumerateArray().Select(field => new
                        {
                            name = field.TryGetProperty("name", out var n) ? n.GetString() : null,
                            value = field.TryGetProperty("value", out var v) ? v.GetString() : null,
                            type = field.TryGetProperty("type", out var t) ? t.GetString() : null,
                            isBoolean = field.TryGetProperty("isBoolean", out var b) && b.GetBoolean()
                        }).Cast<object>().ToArray();
                    }
                }

                expanded.Add(new
                {
                    index,
                    name,
                    enabled = component.TryGetProperty("enabled", out var enabledProp) && enabledProp.GetBoolean(),
                    fields
                });
            }

            return new
            {
                targetPath,
                components = expanded
            };
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }
}
