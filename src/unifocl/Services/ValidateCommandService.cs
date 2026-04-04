using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Spectre.Console;

internal sealed class ValidateCommandService
{
    private static readonly JsonSerializerOptions ReadJsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HierarchyDaemonClient _daemonClient = new();

    private static readonly string[] AllValidators = ["scene-list", "missing-scripts", "packages", "build-settings", "asmdef", "asset-refs", "addressables", "scripts"];

    public async Task HandleValidateCommandAsync(
        string input,
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime,
        Action<string> log)
    {
        if (session.Mode != CliMode.Project || string.IsNullOrWhiteSpace(session.CurrentProjectPath))
        {
            log("[yellow]validate[/]: open a project first with /open");
            return;
        }

        var tokens = Tokenize(input);
        if (tokens.Count < 2)
        {
            log("[x] usage: /validate <scene-list|missing-scripts|packages|build-settings|asmdef|asset-refs|addressables|scripts|all>");
            return;
        }

        var subcommand = tokens[1].ToLowerInvariant();
        var validators = subcommand == "all" ? AllValidators : new[] { subcommand };

        var isAll = subcommand == "all";
        var first = true;
        foreach (var validator in validators)
        {
            if (isAll && !first)
            {
                var sep = "─── " + new string('─', 40);
                log($"[{CliTheme.TextMuted}]{sep}[/]");
            }

            first = false;

            switch (validator)
            {
                case "scene-list":
                case "missing-scripts":
                case "build-settings":
                case "asset-refs":
                case "addressables":
                    await RunDaemonValidatorAsync(validator, session, log);
                    break;
                case "packages":
                    // packages is CLI-only — run locally without daemon
                    RunLocalPackagesValidator(session, log);
                    break;
                case "asmdef":
                    RunLocalAsmdefValidator(session, log);
                    break;
                case "scripts":
                    RunLocalScriptsValidator(session, log);
                    break;
                default:
                    log($"[x] unknown validator: {Markup.Escape(validator)}");
                    log("supported: scene-list | missing-scripts | packages | build-settings | asmdef | asset-refs | addressables | scripts | all");
                    return;
            }
        }
    }

    private async Task RunDaemonValidatorAsync(string validator, CliSessionState session, Action<string> log)
    {
        if (DaemonControlService.GetPort(session) is not int port)
        {
            log($"[yellow]validate[/]: daemon not running — start with /open");
            return;
        }

        var action = $"validate-{validator}";
        var request = new ProjectCommandRequestDto(action, null, null, null, Guid.NewGuid().ToString("N"));

        log($"[grey]validate[/]: running {Markup.Escape(validator)}...");
        var response = await _daemonClient.ExecuteProjectCommandAsync(port, request,
            onStatus: status => log($"[grey]validate[/]: {Markup.Escape(status)}"));

        if (!response.Ok)
        {
            log($"[red]validate[/]: {Markup.Escape(validator)} failed — {Markup.Escape(response.Message)}");
            return;
        }

        if (string.IsNullOrWhiteSpace(response.Content))
        {
            log($"[green]validate[/]: {Markup.Escape(validator)} passed (no diagnostics)");
            return;
        }

        ValidateResult? result;
        try
        {
            result = JsonSerializer.Deserialize<ValidateResult>(response.Content, ReadJsonOptions);
        }
        catch
        {
            log($"[green]validate[/]: {Markup.Escape(validator)} — {Markup.Escape(response.Message)}");
            return;
        }

        if (result is null)
        {
            log($"[green]validate[/]: {Markup.Escape(validator)} — {Markup.Escape(response.Message)}");
            return;
        }

        RenderResult(result, log);
    }

    private static void RunLocalPackagesValidator(CliSessionState session, Action<string> log)
    {
        var projectPath = session.CurrentProjectPath!;
        var bridge = new ProjectDaemonBridge(projectPath);
        var request = new ProjectCommandRequestDto("validate-packages", null, null, null, Guid.NewGuid().ToString("N"));
        bridge.TryHandle($"PROJECT_CMD {JsonSerializer.Serialize(request, ReadJsonOptions)}", out var raw);

        ProjectCommandResponseDto? response;
        try
        {
            response = JsonSerializer.Deserialize<ProjectCommandResponseDto>(raw, ReadJsonOptions);
        }
        catch
        {
            log("[red]validate[/]: packages — failed to parse bridge response");
            return;
        }

        if (response is null || !response.Ok)
        {
            log($"[red]validate[/]: packages — {Markup.Escape(response?.Message ?? "unknown error")}");
            return;
        }

        if (string.IsNullOrWhiteSpace(response.Content))
        {
            log("[green]validate[/]: packages passed (no diagnostics)");
            return;
        }

        ValidateResult? result;
        try
        {
            result = JsonSerializer.Deserialize<ValidateResult>(response.Content, ReadJsonOptions);
        }
        catch
        {
            log($"[green]validate[/]: packages — {Markup.Escape(response.Message)}");
            return;
        }

        if (result is null)
        {
            log($"[green]validate[/]: packages — {Markup.Escape(response.Message)}");
            return;
        }

        RenderResult(result, log);
    }

    private static void RunLocalAsmdefValidator(CliSessionState session, Action<string> log)
    {
        var projectPath = session.CurrentProjectPath!;
        var bridge = new ProjectDaemonBridge(projectPath);
        var request = new ProjectCommandRequestDto("validate-asmdef", null, null, null, Guid.NewGuid().ToString("N"));
        bridge.TryHandle($"PROJECT_CMD {JsonSerializer.Serialize(request, ReadJsonOptions)}", out var raw);

        ProjectCommandResponseDto? response;
        try
        {
            response = JsonSerializer.Deserialize<ProjectCommandResponseDto>(raw, ReadJsonOptions);
        }
        catch
        {
            log("[red]validate[/]: asmdef — failed to parse bridge response");
            return;
        }

        if (response is null || !response.Ok)
        {
            log($"[red]validate[/]: asmdef — {Markup.Escape(response?.Message ?? "unknown error")}");
            return;
        }

        if (string.IsNullOrWhiteSpace(response.Content))
        {
            log("[green]validate[/]: asmdef passed (no diagnostics)");
            return;
        }

        ValidateResult? result;
        try
        {
            result = JsonSerializer.Deserialize<ValidateResult>(response.Content, ReadJsonOptions);
        }
        catch
        {
            log($"[green]validate[/]: asmdef — {Markup.Escape(response.Message)}");
            return;
        }

        if (result is null)
        {
            log($"[green]validate[/]: asmdef — {Markup.Escape(response.Message)}");
            return;
        }

        RenderResult(result, log);
    }

    /// <summary>
    /// ExecV2 entry point for agentic validate.scripts dispatch.
    /// Returns (ok, resultJson, error).
    /// </summary>
    public static (bool Ok, JsonElement? Result, string? Error) ExecScriptsValidation(string projectPath)
    {
        var logs = new List<string>();
        var session = new CliSessionState { Mode = CliMode.Project, CurrentProjectPath = projectPath };
        RunLocalScriptsValidator(session, line => logs.Add(line));

        var result = JsonSerializer.SerializeToElement(new { logs }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return (true, result, null);
    }

    private static void RunLocalScriptsValidator(CliSessionState session, Action<string> log)
    {
        var projectPath = session.CurrentProjectPath!;
        var diagnostics = new List<ValidateDiagnostic>();

        // 1. Resolve Unity Managed directory (single-shot: env → exact version → any installed editor)
        if (!TryResolveManagedDir(projectPath, out var managedDir, out var resolveError))
        {
            log($"[red]validate[/]: scripts — {Markup.Escape(resolveError ?? "could not locate Unity Managed directory")}");
            return;
        }

        // 2. Collect .cs files under Assets/
        var assetsPath = Path.Combine(projectPath, "Assets");
        if (!Directory.Exists(assetsPath))
        {
            diagnostics.Add(new ValidateDiagnostic(ValidateSeverity.Error, "VSCS001", "Assets/ directory not found"));
            RenderResult(new ValidateResult("scripts", false, 1, 0, diagnostics), log);
            return;
        }

        var csFiles = Directory.GetFiles(assetsPath, "*.cs", SearchOption.AllDirectories);
        if (csFiles.Length == 0)
        {
            RenderResult(new ValidateResult("scripts", true, 0, 0, []), log);
            return;
        }

        log($"[grey]validate[/]: scripts — compiling {csFiles.Length} file(s) against Unity stubs ({Markup.Escape(managedDir)})...");

        // 3. Generate temp csproj and run dotnet build
        var tempDir = Path.Combine(Path.GetTempPath(), $"unifocl-validate-scripts-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var scriptAssembliesDir = Path.Combine(projectPath, "Library", "ScriptAssemblies");
            var csprojContent = GenerateScriptsValidationCsproj(managedDir, scriptAssembliesDir, csFiles);
            var csprojPath = Path.Combine(tempDir, "validate-scripts.csproj");
            File.WriteAllText(csprojPath, csprojContent);

            var psi = new ProcessStartInfo("dotnet", $"build \"{csprojPath}\" --disable-build-servers -v quiet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = tempDir,
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                log("[red]validate[/]: scripts — failed to start dotnet build");
                return;
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            diagnostics = ParseCompilerDiagnostics(stdout + "\n" + stderr, projectPath);
            var errorCount = diagnostics.Count(d => d.Severity == ValidateSeverity.Error);
            var warningCount = diagnostics.Count(d => d.Severity == ValidateSeverity.Warning);
            RenderResult(new ValidateResult("scripts", errorCount == 0, errorCount, warningCount, diagnostics), log);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best effort cleanup */ }
        }
    }

    /// <summary>
    /// Single-shot resolution of the Unity Managed directory containing UnityEditor.dll.
    /// Fallback chain: env var → exact project version → any installed editor.
    /// </summary>
    private static bool TryResolveManagedDir(string projectPath, out string managedDir, out string? error)
    {
        managedDir = string.Empty;
        error = null;

        // 1. Explicit env var (same as compatcheck) — highest priority
        var envManaged = Environment.GetEnvironmentVariable("UNIFOCL_UNITY_EDITOR_MANAGED_DIR");
        if (!string.IsNullOrWhiteSpace(envManaged) && IsManagedDir(envManaged))
        {
            managedDir = envManaged;
            return true;
        }

        // 2. Exact project-version match via UnityEditorPathService
        if (UnityEditorPathService.TryResolveEditorForProject(projectPath, out var editorPath, out _, out _)
            && TryDeriveManagedFromEditor(editorPath, out managedDir))
        {
            return true;
        }

        // 3. Fallback: any installed editor (for offline compile, exact version rarely matters)
        var installed = UnityEditorPathService.DetectInstalledEditors(out _);
        foreach (var editor in installed)
        {
            if (TryDeriveManagedFromEditor(editor.EditorPath, out managedDir))
                return true;
        }

        error = installed.Count == 0
            ? "no Unity editors found — install via Unity Hub or set UNIFOCL_UNITY_EDITOR_MANAGED_DIR"
            : "could not derive Managed directory from any installed Unity editor";
        return false;
    }

    private static bool TryDeriveManagedFromEditor(string editorPath, out string managedDir)
    {
        managedDir = string.Empty;

        // macOS: .../Unity.app/Contents/MacOS/Unity → .../Unity.app/Contents/Managed
        if (OperatingSystem.IsMacOS())
        {
            var contentsDir = Path.GetDirectoryName(Path.GetDirectoryName(editorPath));
            if (!string.IsNullOrEmpty(contentsDir))
            {
                var candidate = Path.Combine(contentsDir, "Managed");
                if (IsManagedDir(candidate))
                {
                    managedDir = candidate;
                    return true;
                }
            }
        }

        // Windows/Linux: .../Editor/Unity[.exe] → .../Editor/Data/Managed
        var editorDir = Path.GetDirectoryName(editorPath);
        if (!string.IsNullOrEmpty(editorDir))
        {
            var candidate = Path.Combine(editorDir, "Data", "Managed");
            if (IsManagedDir(candidate))
            {
                managedDir = candidate;
                return true;
            }
        }

        return false;
    }

    private static bool IsManagedDir(string path) =>
        Directory.Exists(path) && File.Exists(Path.Combine(path, "UnityEditor.dll"));

    private static string GenerateScriptsValidationCsproj(
        string managedDir,
        string scriptAssembliesDir,
        string[] csFiles)
    {
        // Derive template libcache dir for UnityEngine.UI / TextMeshPro
        var templateLibcacheDir = managedDir
            .Replace("/Contents/Resources/Scripting/Managed", "/Contents/Resources/PackageManager/ProjectTemplates/libcache")
            .Replace("/Contents/Managed", "/Contents/Resources/PackageManager/ProjectTemplates/libcache")
            .Replace("\\Contents\\Managed", "\\Contents\\Resources\\PackageManager\\ProjectTemplates\\libcache");

        var compileItems = string.Join(Environment.NewLine,
            csFiles.Select(f => $"    <Compile Include=\"{EscapeXml(f)}\" />"));

        var scriptAssembliesRef = Directory.Exists(scriptAssembliesDir)
            ? $"""
                  <ItemGroup>
                    <UnityProjectAssembly Include="{EscapeXml(scriptAssembliesDir)}/*.dll" />
                    <Reference Include="@(UnityProjectAssembly)">
                      <Private>false</Private>
                    </Reference>
                  </ItemGroup>
              """
            : string.Empty;

        var templateRef = Directory.Exists(templateLibcacheDir)
            ? $"""
                  <ItemGroup>
                    <UnityTemplateAssembly Include="{EscapeXml(templateLibcacheDir)}/**/ScriptAssemblies/UnityEngine.UI.dll" />
                    <UnityTemplateAssembly Include="{EscapeXml(templateLibcacheDir)}/**/ScriptAssemblies/Unity.TextMeshPro.dll" />
                    <Reference Include="@(UnityTemplateAssembly)">
                      <Private>false</Private>
                    </Reference>
                  </ItemGroup>
              """
            : string.Empty;

        return $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>netstandard2.1</TargetFramework>
                <LangVersion>9.0</LangVersion>
                <Nullable>disable</Nullable>
                <ImplicitUsings>disable</ImplicitUsings>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                <DefineConstants>$(DefineConstants);UNITY_EDITOR</DefineConstants>
                <NoWarn>$(NoWarn);CS0649;CS1701;CS1702;CS0108;CS0114;CS0162;CS0414;CS0219</NoWarn>
                <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
              </PropertyGroup>

              <ItemGroup>
            {compileItems}
              </ItemGroup>

              <ItemGroup>
                <UnityManagedAssembly Include="{EscapeXml(managedDir)}/**/*.dll" />
                <Reference Include="@(UnityManagedAssembly)">
                  <Private>false</Private>
                </Reference>
              </ItemGroup>

            {scriptAssembliesRef}
            {templateRef}
            </Project>
            """;
    }

    private static readonly Regex DiagnosticPattern = new(
        @"^(.+?)\((\d+),(\d+)\):\s+(error|warning)\s+(CS\d{4}):\s+(.+)$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static List<ValidateDiagnostic> ParseCompilerDiagnostics(string buildOutput, string projectPath)
    {
        var diagnostics = new List<ValidateDiagnostic>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match match in DiagnosticPattern.Matches(buildOutput))
        {
            var filePath = match.Groups[1].Value.Trim();
            var line = match.Groups[2].Value;
            var severity = match.Groups[4].Value;
            var code = match.Groups[5].Value;
            var message = match.Groups[6].Value.Trim();

            // De-duplicate identical diagnostics
            var key = $"{filePath}:{line}:{code}";
            if (!seen.Add(key))
                continue;

            // Make path relative to project
            var relativePath = filePath;
            if (filePath.StartsWith(projectPath, StringComparison.OrdinalIgnoreCase))
            {
                relativePath = filePath[(projectPath.Length + 1)..];
            }

            var diagSeverity = severity == "error" ? ValidateSeverity.Error : ValidateSeverity.Warning;
            diagnostics.Add(new ValidateDiagnostic(
                diagSeverity,
                code,
                message,
                AssetPath: $"{relativePath}({line})"));
        }

        return diagnostics;
    }

    private static string EscapeXml(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    public async Task<BuildPreflightResult> BuildPreflightAsync(
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime,
        Action<string> log)
    {
        ValidateResult? sceneListResult = null;
        ValidateResult? buildSettingsResult = null;
        ValidateResult? packagesResult = null;

        // Run daemon validators
        foreach (var v in new[] { "scene-list", "build-settings" })
        {
            if (DaemonControlService.GetPort(session) is not int port)
            {
                log($"[yellow]preflight[/]: daemon not running — {v} skipped");
                continue;
            }

            var action = $"validate-{v}";
            var req = new ProjectCommandRequestDto(action, null, null, null, Guid.NewGuid().ToString("N"));
            var response = await _daemonClient.ExecuteProjectCommandAsync(port, req, onStatus: null);

            if (response.Ok && !string.IsNullOrWhiteSpace(response.Content))
            {
                try
                {
                    var r = JsonSerializer.Deserialize<ValidateResult>(response.Content, ReadJsonOptions);
                    if (v == "scene-list") sceneListResult = r;
                    else buildSettingsResult = r;
                }
                catch { /* leave null */ }
            }
        }

        // Run packages validator (CLI-only)
        var projectPath = session.CurrentProjectPath!;
        var bridge = new ProjectDaemonBridge(projectPath);
        var pkgRequest = new ProjectCommandRequestDto("validate-packages", null, null, null, Guid.NewGuid().ToString("N"));
        bridge.TryHandle($"PROJECT_CMD {JsonSerializer.Serialize(pkgRequest, ReadJsonOptions)}", out var pkgRaw);
        try
        {
            var pkgResponse = JsonSerializer.Deserialize<ProjectCommandResponseDto>(pkgRaw, ReadJsonOptions);
            if (pkgResponse?.Ok == true && !string.IsNullOrWhiteSpace(pkgResponse.Content))
            {
                packagesResult = JsonSerializer.Deserialize<ValidateResult>(pkgResponse.Content, ReadJsonOptions);
            }
        }
        catch { /* leave null */ }

        var totalErrors = (sceneListResult?.ErrorCount ?? 0) + (buildSettingsResult?.ErrorCount ?? 0) + (packagesResult?.ErrorCount ?? 0);
        var totalWarnings = (sceneListResult?.WarningCount ?? 0) + (buildSettingsResult?.WarningCount ?? 0) + (packagesResult?.WarningCount ?? 0);
        var passed = totalErrors == 0;

        return new BuildPreflightResult(passed, totalErrors, totalWarnings, sceneListResult, buildSettingsResult, packagesResult);
    }

    private static void RenderResult(ValidateResult result, Action<string> log)
    {
        var statusColor = result.Passed ? CliTheme.Success : CliTheme.Error;
        var statusIcon = result.Passed ? "✓" : "✗";
        log($"[bold {statusColor}]{statusIcon}[/] [{CliTheme.TextPrimary}]{Markup.Escape(result.Validator)}[/]  " +
            $"[{CliTheme.Error}]{result.ErrorCount} error(s)[/]  " +
            $"[{CliTheme.Warning}]{result.WarningCount} warning(s)[/]");

        if (result.Diagnostics.Count == 0)
            return;

        // Errors first, then warnings, then info
        var grouped = result.Diagnostics
            .OrderBy(d => d.Severity switch
            {
                ValidateSeverity.Error => 0,
                ValidateSeverity.Warning => 1,
                _ => 2
            })
            .ToList();

        foreach (var diag in grouped)
        {
            var (sevColor, sevIcon) = diag.Severity switch
            {
                ValidateSeverity.Error => (CliTheme.Error, "✗"),
                ValidateSeverity.Warning => (CliTheme.Warning, "⚠"),
                _ => (CliTheme.Info, "i")
            };
            var location = diag.AssetPath ?? diag.ObjectPath ?? string.Empty;
            var locationPart = string.IsNullOrEmpty(location)
                ? string.Empty
                : $" [{CliTheme.TextMuted}]@ {Markup.Escape(location)}[/]";
            var fixPart = diag.Fixable ? $" [{CliTheme.TextMuted}](fixable)[/]" : string.Empty;
            log($"  [{sevColor}]{sevIcon}[/] [{CliTheme.TextMuted}]{Markup.Escape(diag.ErrorCode)}[/] {Markup.Escape(diag.Message)}{locationPart}{fixPart}");
        }
    }

    private static List<string> Tokenize(string input)
    {
        var tokens = new List<string>();
        var span = input.AsSpan().Trim();
        while (!span.IsEmpty)
        {
            if (span[0] == '"' || span[0] == '\'')
            {
                var quote = span[0];
                span = span[1..];
                var end = span.IndexOf(quote);
                if (end < 0) end = span.Length;
                tokens.Add(span[..end].ToString());
                span = end < span.Length ? span[(end + 1)..].TrimStart() : ReadOnlySpan<char>.Empty;
            }
            else
            {
                var end = span.IndexOf(' ');
                if (end < 0) end = span.Length;
                tokens.Add(span[..end].ToString());
                span = end < span.Length ? span[(end + 1)..].TrimStart() : ReadOnlySpan<char>.Empty;
            }
        }

        return tokens;
    }
}
