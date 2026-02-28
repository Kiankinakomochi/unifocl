using Spectre.Console;
using System.Reflection;
using System.Text;

internal sealed class EditorDependencyInitializerService
{
    private const string EmbeddedPackageName = "com.unifocl.cli";
    private const string EmbeddedPackageFolder = "daemon-package";
    private const string ExcludeEntry = "Packages/com.unifocl.cli/";
    private const string DaemonSourceResource = "Payload/EditorScripts/CLIDaemon.cs";
    private const string SharedModelsSourceResource = "Payload/SharedModels/BridgeModels.cs";

    public OperationResult InitializeProject(string projectPath, Action<string> log)
    {
        if (!Directory.Exists(Path.Combine(projectPath, "ProjectSettings")))
        {
            return OperationResult.Fail("target is not a Unity project (missing ProjectSettings/)");
        }

        if (!Directory.Exists(Path.Combine(projectPath, "Packages")))
        {
            return OperationResult.Fail("target is not a Unity project (missing Packages/)");
        }

        log("[grey]init[/]: step 1/3 ensure global editor payload");
        var globalPayloadResult = EnsureGlobalPayload();
        if (!globalPayloadResult.Ok)
        {
            return globalPayloadResult;
        }

        log("[grey]init[/]: step 2/3 install embedded editor package");
        var installResult = EnsureEmbeddedPackage(projectPath);
        if (!installResult.Ok)
        {
            return installResult;
        }

        log("[grey]init[/]: step 3/3 update local git exclude (if repository)");
        var excludeResult = EnsureGitExclude(projectPath);
        if (!excludeResult.Ok)
        {
            return excludeResult;
        }

        log("[green]init[/]: editor dependencies initialized");
        return OperationResult.Success();
    }

    public bool PromptForInitialization(Action<string> log)
    {
        log("[grey]init[/]: initialization installs a local Unity editor bridge package so CLI commands can talk to the editor without modifying manifest.json.");
        if (Console.IsInputRedirected)
        {
            log("[yellow]init[/]: prompt skipped in redirected input mode; run /init to install editor dependencies");
            return false;
        }

        return AnsiConsole.Confirm("Initialize editor dependencies now?", defaultValue: true);
    }

    private static OperationResult EnsureEmbeddedPackage(string projectPath)
    {
        try
        {
            var targetPath = Path.Combine(projectPath, "Packages", EmbeddedPackageName);
            var sourcePath = GetGlobalPayloadPath();
            if (!Directory.Exists(sourcePath))
            {
                return OperationResult.Fail($"global payload not found at {sourcePath}");
            }

            CopyDirectory(sourcePath, targetPath);
            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"failed to install embedded package ({ex.Message})");
        }
    }

    private static OperationResult EnsureGlobalPayload()
    {
        try
        {
            var payloadPath = GetGlobalPayloadPath();
            Directory.CreateDirectory(payloadPath);
            Directory.CreateDirectory(Path.Combine(payloadPath, "Editor"));

            var packageJson =
                """
                {
                  "name": "com.unifocl.cli",
                  "displayName": "UniFocl CLI Bridge",
                  "version": "0.1.0",
                  "unity": "2021.3",
                  "description": "Local embedded editor bridge for UniFocl CLI",
                  "author": {
                    "name": "UniFocl"
                  }
                }
                """;

            var asmdef =
                """
                {
                  "name": "UniFocl.EditorBridge",
                  "rootNamespace": "UniFocl",
                  "includePlatforms": [
                    "Editor"
                  ],
                  "excludePlatforms": [],
                  "allowUnsafeCode": false,
                  "overrideReferences": false,
                  "precompiledReferences": [],
                  "autoReferenced": true,
                  "defineConstraints": [],
                  "versionDefines": [],
                  "noEngineReferences": false
                }
                """;

            File.WriteAllText(Path.Combine(payloadPath, "package.json"), packageJson + Environment.NewLine, Encoding.UTF8);
            File.WriteAllText(Path.Combine(payloadPath, "Editor", "UniFocl.EditorBridge.asmdef"), asmdef + Environment.NewLine, Encoding.UTF8);

            var daemonSource = ReadEmbeddedResource(DaemonSourceResource);
            if (daemonSource is null)
            {
                return OperationResult.Fail($"missing embedded resource {DaemonSourceResource}");
            }

            var modelsSource = ReadEmbeddedResource(SharedModelsSourceResource);
            if (modelsSource is null)
            {
                return OperationResult.Fail($"missing embedded resource {SharedModelsSourceResource}");
            }

            File.WriteAllText(Path.Combine(payloadPath, "Editor", "CLIDaemon.cs"), daemonSource, Encoding.UTF8);
            File.WriteAllText(Path.Combine(payloadPath, "Editor", "BridgeModels.cs"), modelsSource, Encoding.UTF8);
            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"failed to prepare global payload ({ex.Message})");
        }
    }

    private static string? ReadEmbeddedResource(string resourceName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static OperationResult EnsureGitExclude(string projectPath)
    {
        try
        {
            var gitInfoPath = ResolveGitInfoPath(projectPath);
            if (gitInfoPath is null)
            {
                return OperationResult.Success();
            }

            Directory.CreateDirectory(gitInfoPath);
            var excludePath = Path.Combine(gitInfoPath, "exclude");
            if (!File.Exists(excludePath))
            {
                File.WriteAllText(excludePath, ExcludeEntry + Environment.NewLine, Encoding.UTF8);
                return OperationResult.Success();
            }

            var existingEntries = File.ReadAllLines(excludePath)
                .Select(line => NormalizeExcludePattern(line.Trim()))
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToHashSet(StringComparer.Ordinal);
            if (!existingEntries.Contains(NormalizeExcludePattern(ExcludeEntry)))
            {
                File.AppendAllText(excludePath, ExcludeEntry + Environment.NewLine, Encoding.UTF8);
            }

            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"failed to update .git/info/exclude ({ex.Message})");
        }
    }

    private static string? ResolveGitInfoPath(string projectPath)
    {
        var gitDirPath = Path.Combine(projectPath, ".git");
        if (Directory.Exists(gitDirPath))
        {
            return Path.Combine(gitDirPath, "info");
        }

        if (!File.Exists(gitDirPath))
        {
            return null;
        }

        var content = File.ReadAllText(gitDirPath).Trim();
        const string gitDirPrefix = "gitdir:";
        if (!content.StartsWith(gitDirPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rawPath = content[gitDirPrefix.Length..].Trim();
        var resolvedGitDir = Path.IsPathRooted(rawPath)
            ? rawPath
            : Path.GetFullPath(Path.Combine(projectPath, rawPath));
        return Path.Combine(resolvedGitDir, "info");
    }

    private static string GetGlobalPayloadPath()
    {
        var overrideRoot = Environment.GetEnvironmentVariable("UNIFOCL_GLOBAL_PAYLOAD_ROOT");
        if (!string.IsNullOrWhiteSpace(overrideRoot))
        {
            return Path.Combine(Path.GetFullPath(overrideRoot), EmbeddedPackageFolder);
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".unifocl", EmbeddedPackageFolder);
    }

    private static string NormalizeExcludePattern(string value)
    {
        var normalized = value.Replace('\\', '/').Trim();
        return normalized.EndsWith('/') ? normalized : normalized + "/";
    }

    private static void CopyDirectory(string sourcePath, string targetPath)
    {
        Directory.CreateDirectory(targetPath);
        foreach (var dir in Directory.GetDirectories(sourcePath, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourcePath, dir);
            Directory.CreateDirectory(Path.Combine(targetPath, relative));
        }

        foreach (var file in Directory.GetFiles(sourcePath, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourcePath, file);
            var destination = Path.Combine(targetPath, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }
}
