using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
/// Parses and executes YAML-based runtime scenario files (.unifocl/scenarios/*.yaml).
/// Each scenario contains a sequence of steps that execute runtime commands
/// with optional assertions on results.
/// </summary>
internal sealed class RuntimeScenarioService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>Run a scenario file end-to-end. Returns aggregated results.</summary>
    public async Task<ScenarioRunResult> RunAsync(string scenarioPath, int daemonPort, CancellationToken ct)
    {
        var (scenario, parseErrors) = ParseScenarioFile(scenarioPath);
        if (parseErrors.Count > 0)
        {
            return new ScenarioRunResult
            {
                ScenarioPath = scenarioPath,
                AllPassed = false,
                Summary = $"parse errors: {string.Join("; ", parseErrors)}",
                Steps = []
            };
        }

        var stepResults = new List<ScenarioStepResult>();
        var allPassed = true;
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var step in scenario.Steps)
        {
            var resolvedArgs = ResolveVariables(step.ArgsJson, variables);
            var body = JsonSerializer.Serialize(new { command = step.Command, argsJson = resolvedArgs }, JsonOpts);

            string? responseJson;
            try
            {
                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                using var resp = await Http.PostAsync($"http://127.0.0.1:{daemonPort}/runtime/exec", content, ct);
                responseJson = await resp.Content.ReadAsStringAsync(ct);
            }
            catch (Exception ex)
            {
                stepResults.Add(new ScenarioStepResult
                {
                    StepName = step.Name,
                    Command = step.Command,
                    Passed = false,
                    Message = $"HTTP error: {ex.Message}"
                });
                allPassed = false;
                if (!step.ContinueOnFailure) break;
                continue;
            }

            // Capture output variable if requested
            if (!string.IsNullOrEmpty(step.CaptureAs) && responseJson != null)
            {
                variables[step.CaptureAs] = responseJson;
            }

            // Evaluate assertions
            var assertionErrors = EvaluateAssertions(step.Assertions, responseJson);
            var passed = assertionErrors.Count == 0;
            if (!passed) allPassed = false;

            stepResults.Add(new ScenarioStepResult
            {
                StepName = step.Name,
                Command = step.Command,
                Passed = passed,
                Message = passed ? "ok" : string.Join("; ", assertionErrors),
                ResponseJson = responseJson
            });

            if (!passed && !step.ContinueOnFailure) break;

            // Optional delay between steps
            if (step.DelayMs > 0)
            {
                await Task.Delay(step.DelayMs, ct);
            }
        }

        var passCount = stepResults.Count(s => s.Passed);
        return new ScenarioRunResult
        {
            ScenarioPath = scenarioPath,
            ScenarioName = scenario.Name,
            AllPassed = allPassed,
            Summary = $"{passCount}/{stepResults.Count} steps passed",
            Steps = stepResults
        };
    }

    /// <summary>Validate a scenario file without executing it.</summary>
    public (bool Valid, List<string> Errors) Validate(string scenarioPath)
    {
        var (_, errors) = ParseScenarioFile(scenarioPath);
        return (errors.Count == 0, errors);
    }

    // ── YAML-like parser (lightweight, no dependency) ───────────────────

    private static (ScenarioDefinition Scenario, List<string> Errors) ParseScenarioFile(string path)
    {
        var errors = new List<string>();
        var scenario = new ScenarioDefinition { Name = Path.GetFileNameWithoutExtension(path) };

        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (Exception ex)
        {
            errors.Add($"cannot read file: {ex.Message}");
            return (scenario, errors);
        }

        ScenarioStep? currentStep = null;
        var inAssertions = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
                continue;

            // Top-level keys
            if (!line.StartsWith(' ') && !line.StartsWith('\t'))
            {
                inAssertions = false;
                if (line.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
                {
                    scenario.Name = line["name:".Length..].Trim().Trim('"', '\'');
                }
                else if (line.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
                {
                    scenario.Description = line["description:".Length..].Trim().Trim('"', '\'');
                }
                else if (line.TrimStart().StartsWith("- name:", StringComparison.OrdinalIgnoreCase))
                {
                    currentStep = new ScenarioStep { Name = line.Split(':', 2)[1].Trim().Trim('"', '\'') };
                    scenario.Steps.Add(currentStep);
                }
                else if (line.StartsWith("steps:", StringComparison.OrdinalIgnoreCase))
                {
                    // steps: header, just continue
                }
                continue;
            }

            var trimmed = line.TrimStart();

            // Step-level entry (list item)
            if (trimmed.StartsWith("- name:", StringComparison.OrdinalIgnoreCase))
            {
                currentStep = new ScenarioStep { Name = trimmed["- name:".Length..].Trim().Trim('"', '\'') };
                scenario.Steps.Add(currentStep);
                inAssertions = false;
                continue;
            }

            if (currentStep == null) continue;

            if (trimmed.StartsWith("command:", StringComparison.OrdinalIgnoreCase))
            {
                currentStep.Command = trimmed["command:".Length..].Trim().Trim('"', '\'');
                inAssertions = false;
            }
            else if (trimmed.StartsWith("args:", StringComparison.OrdinalIgnoreCase))
            {
                currentStep.ArgsJson = trimmed["args:".Length..].Trim();
                inAssertions = false;
            }
            else if (trimmed.StartsWith("capture_as:", StringComparison.OrdinalIgnoreCase))
            {
                currentStep.CaptureAs = trimmed["capture_as:".Length..].Trim().Trim('"', '\'');
                inAssertions = false;
            }
            else if (trimmed.StartsWith("continue_on_failure:", StringComparison.OrdinalIgnoreCase))
            {
                currentStep.ContinueOnFailure = trimmed["continue_on_failure:".Length..].Trim()
                    .Equals("true", StringComparison.OrdinalIgnoreCase);
                inAssertions = false;
            }
            else if (trimmed.StartsWith("delay_ms:", StringComparison.OrdinalIgnoreCase))
            {
                _ = int.TryParse(trimmed["delay_ms:".Length..].Trim(), out var delay);
                currentStep.DelayMs = delay;
                inAssertions = false;
            }
            else if (trimmed.StartsWith("assert:", StringComparison.OrdinalIgnoreCase))
            {
                inAssertions = true;
            }
            else if (inAssertions && trimmed.StartsWith("- "))
            {
                currentStep.Assertions.Add(trimmed[2..].Trim().Trim('"', '\''));
            }
        }

        // Validate steps
        for (var i = 0; i < scenario.Steps.Count; i++)
        {
            var step = scenario.Steps[i];
            if (string.IsNullOrWhiteSpace(step.Command))
            {
                errors.Add($"step {i} ('{step.Name}'): command is required");
            }
        }

        return (scenario, errors);
    }

    private static string ResolveVariables(string input, Dictionary<string, string> variables)
    {
        if (string.IsNullOrEmpty(input) || variables.Count == 0) return input;
        return Regex.Replace(input, @"\$\{(\w+)\}", match =>
        {
            var key = match.Groups[1].Value;
            return variables.TryGetValue(key, out var val) ? val : match.Value;
        });
    }

    private static List<string> EvaluateAssertions(List<string> assertions, string? responseJson)
    {
        var errors = new List<string>();
        if (assertions.Count == 0) return errors;

        JsonElement? root = null;
        if (!string.IsNullOrEmpty(responseJson))
        {
            try { root = JsonSerializer.Deserialize<JsonElement>(responseJson); } catch { /* ignore */ }
        }

        foreach (var assertion in assertions)
        {
            // success == true
            if (assertion.Contains("=="))
            {
                var parts = assertion.Split("==", 2);
                var path = parts[0].Trim();
                var expected = parts[1].Trim().Trim('"', '\'');

                var actual = ResolveJsonPath(root, path);
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"assert failed: {path} == {expected} (got: {actual ?? "null"})");
                }
            }
            // success != false
            else if (assertion.Contains("!="))
            {
                var parts = assertion.Split("!=", 2);
                var path = parts[0].Trim();
                var notExpected = parts[1].Trim().Trim('"', '\'');

                var actual = ResolveJsonPath(root, path);
                if (string.Equals(actual, notExpected, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"assert failed: {path} != {notExpected} (got: {actual ?? "null"})");
                }
            }
            // exists(resultJson)
            else if (assertion.StartsWith("exists(") && assertion.EndsWith(')'))
            {
                var path = assertion["exists(".Length..^1];
                var actual = ResolveJsonPath(root, path);
                if (actual == null)
                {
                    errors.Add($"assert failed: exists({path}) — property not found");
                }
            }
            else
            {
                errors.Add($"unknown assertion syntax: {assertion}");
            }
        }

        return errors;
    }

    private static string? ResolveJsonPath(JsonElement? root, string path)
    {
        if (root == null) return null;
        var current = root.Value;
        foreach (var segment in path.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object) return null;
            if (!current.TryGetProperty(segment, out var next)) return null;
            current = next;
        }
        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => current.GetRawText(),
            JsonValueKind.Null => "null",
            _ => current.GetRawText()
        };
    }
}

// ── Scenario models ─────────────────────────────────────────────────────

internal sealed class ScenarioDefinition
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<ScenarioStep> Steps { get; set; } = [];
}

internal sealed class ScenarioStep
{
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public string ArgsJson { get; set; } = "{}";
    public string CaptureAs { get; set; } = "";
    public bool ContinueOnFailure { get; set; }
    public int DelayMs { get; set; }
    public List<string> Assertions { get; set; } = [];
}

internal sealed class ScenarioRunResult
{
    public string ScenarioPath { get; set; } = "";
    public string ScenarioName { get; set; } = "";
    public bool AllPassed { get; set; }
    public string Summary { get; set; } = "";
    public List<ScenarioStepResult> Steps { get; set; } = [];
}

internal sealed class ScenarioStepResult
{
    public string StepName { get; set; } = "";
    public string Command { get; set; } = "";
    public bool Passed { get; set; }
    public string Message { get; set; } = "";
    public string? ResponseJson { get; set; }
}
