using Spectre.Console;
using System.Reflection;
using System.Text;
using System.Text.Json;

internal sealed class EditorDependencyInitializerService
{
    private const string EmbeddedPackageName = "com.unifocl.cli";
    private const string EmbeddedPackageFolder = "daemon-package";
    private const string ExcludeEntry = "Packages/com.unifocl.cli/";
    private const string DaemonSourceResource = "Payload/EditorScripts/CLIDaemon.cs";
    private const string DaemonRuntimeModelsResource = "Payload/EditorScripts/Models/DaemonBridgeModels.cs";
    private const string DaemonAssetIndexServiceResource = "Payload/EditorScripts/Services/DaemonAssetIndexService.cs";
    private const string DaemonDryRunServicesResource = "Payload/EditorScripts/Services/DaemonDryRunServices.cs";
    private const string DaemonHierarchyServiceResource = "Payload/EditorScripts/Services/DaemonHierarchyService.cs";
    private const string DaemonInspectorServiceResource = "Payload/EditorScripts/Services/DaemonInspectorService.cs";
    private const string DaemonMutationCommandDispatcherResource = "Payload/EditorScripts/Services/DaemonMutationCommandDispatcher.cs";
    private const string DaemonMutationCommandStoreResource = "Payload/EditorScripts/Services/DaemonMutationCommandStore.cs";
    private const string DaemonMutationTransactionCoordinatorResource = "Payload/EditorScripts/Services/DaemonMutationTransactionCoordinator.cs";
    private const string DaemonProjectServiceResource = "Payload/EditorScripts/Services/DaemonProjectService.cs";
    private const string DaemonSceneManagerResource = "Payload/EditorScripts/Services/DaemonSceneManager.cs";
    private const string DaemonScenePersistenceServiceResource = "Payload/EditorScripts/Services/DaemonScenePersistenceService.cs";
    private const string UnifoclCommandAttributeResource = "Payload/EditorScripts/UnifoclCommandAttribute.cs";
    private const string UnifoclManifestGeneratorResource = "Payload/EditorScripts/UnifoclManifestGenerator.cs";
    private const string UnifoclEditorConfigResource = "Payload/EditorScripts/Models/UnifoclEditorConfig.cs";
    private const string DaemonCustomToolServiceResource = "Payload/EditorScripts/Services/DaemonCustomToolService.cs";
    private const string UnifoclCompilationServiceResource = "Payload/EditorScripts/Services/UnifoclCompilationService.cs";
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
        log("[grey]init[/]: initialization installs a local Bridge mode package so CLI commands can talk to the editor without modifying manifest.json.");
        if (Console.IsInputRedirected)
        {
            log("[yellow]init[/]: prompt skipped in redirected input mode; run /init to install editor dependencies");
            return false;
        }

        return CliTheme.ConfirmWithDividers("Initialize editor dependencies now?", defaultValue: true);
    }

    public bool NeedsInitialization(string projectPath, out string reason)
    {
        var packagePath = Path.Combine(projectPath, "Packages", EmbeddedPackageName);
        if (!Directory.Exists(packagePath))
        {
            reason = "embedded package folder is missing";
            return true;
        }

        var packageJsonPath = Path.Combine(packagePath, "package.json");
        if (!File.Exists(packageJsonPath))
        {
            reason = "package.json is missing";
            return true;
        }

        try
        {
            using var packageJson = JsonDocument.Parse(File.ReadAllText(packageJsonPath));
            if (!packageJson.RootElement.TryGetProperty("name", out var packageNameElement))
            {
                reason = "package.json is missing required field 'name'";
                return true;
            }

            var packageName = packageNameElement.GetString();
            if (!string.Equals(packageName, EmbeddedPackageName, StringComparison.Ordinal))
            {
                reason = "package.json has an unexpected package name";
                return true;
            }
        }
        catch (Exception)
        {
            reason = "package.json is invalid";
            return true;
        }

        var requiredFiles = new[]
        {
            Path.Combine(packagePath, "Editor", "UniFocl.EditorBridge.asmdef"),
            Path.Combine(packagePath, "Editor", "CLIDaemon.cs"),
            Path.Combine(packagePath, "Editor", "BridgeModels.cs"),
            Path.Combine(packagePath, "Editor", "Models", "DaemonBridgeModels.cs"),
            Path.Combine(packagePath, "Editor", "Services", "DaemonAssetIndexService.cs"),
            Path.Combine(packagePath, "Editor", "Services", "DaemonDryRunServices.cs"),
            Path.Combine(packagePath, "Editor", "Services", "DaemonHierarchyService.cs"),
            Path.Combine(packagePath, "Editor", "Services", "DaemonInspectorService.cs"),
            Path.Combine(packagePath, "Editor", "Services", "DaemonMutationCommandDispatcher.cs"),
            Path.Combine(packagePath, "Editor", "Services", "DaemonMutationCommandStore.cs"),
            Path.Combine(packagePath, "Editor", "Services", "DaemonMutationTransactionCoordinator.cs"),
            Path.Combine(packagePath, "Editor", "Services", "DaemonProjectService.cs"),
            Path.Combine(packagePath, "Editor", "Services", "DaemonSceneManager.cs"),
            Path.Combine(packagePath, "Editor", "Services", "DaemonScenePersistenceService.cs"),
            Path.Combine(packagePath, "Editor", "UnifoclCommandAttribute.cs"),
            Path.Combine(packagePath, "Editor", "UnifoclManifestGenerator.cs"),
            Path.Combine(packagePath, "Editor", "Models", "UnifoclEditorConfig.cs"),
            Path.Combine(packagePath, "Editor", "Services", "DaemonCustomToolService.cs"),
            Path.Combine(packagePath, "Editor", "Services", "UnifoclCompilationService.cs")
        };
        foreach (var requiredFile in requiredFiles)
        {
            if (!File.Exists(requiredFile))
            {
                reason = $"required file missing: {Path.GetRelativePath(packagePath, requiredFile)}";
                return true;
            }
        }

        var expectedDaemonSource = ReadEmbeddedResource(DaemonSourceResource);
        if (string.IsNullOrWhiteSpace(expectedDaemonSource))
        {
            reason = "embedded daemon payload is unavailable";
            return true;
        }

        var installedDaemonSource = File.ReadAllText(Path.Combine(packagePath, "Editor", "CLIDaemon.cs"));
        if (!NormalizeText(installedDaemonSource).Equals(NormalizeText(expectedDaemonSource), StringComparison.Ordinal))
        {
            reason = "embedded daemon package is outdated; CLIDaemon.cs does not match current CLI payload";
            return true;
        }

        reason = string.Empty;
        return false;
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
            Directory.CreateDirectory(Path.Combine(payloadPath, "Editor", "Models"));
            Directory.CreateDirectory(Path.Combine(payloadPath, "Editor", "Services"));

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

            var resourceToTarget = new (string Resource, string RelativePath)[]
            {
                (DaemonSourceResource, Path.Combine("Editor", "CLIDaemon.cs")),
                (SharedModelsSourceResource, Path.Combine("Editor", "BridgeModels.cs")),
                (DaemonRuntimeModelsResource, Path.Combine("Editor", "Models", "DaemonBridgeModels.cs")),
                (DaemonAssetIndexServiceResource, Path.Combine("Editor", "Services", "DaemonAssetIndexService.cs")),
                (DaemonDryRunServicesResource, Path.Combine("Editor", "Services", "DaemonDryRunServices.cs")),
                (DaemonHierarchyServiceResource, Path.Combine("Editor", "Services", "DaemonHierarchyService.cs")),
                (DaemonInspectorServiceResource, Path.Combine("Editor", "Services", "DaemonInspectorService.cs")),
                (DaemonMutationCommandDispatcherResource, Path.Combine("Editor", "Services", "DaemonMutationCommandDispatcher.cs")),
                (DaemonMutationCommandStoreResource, Path.Combine("Editor", "Services", "DaemonMutationCommandStore.cs")),
                (DaemonMutationTransactionCoordinatorResource, Path.Combine("Editor", "Services", "DaemonMutationTransactionCoordinator.cs")),
                (DaemonProjectServiceResource, Path.Combine("Editor", "Services", "DaemonProjectService.cs")),
                (DaemonSceneManagerResource, Path.Combine("Editor", "Services", "DaemonSceneManager.cs")),
                (DaemonScenePersistenceServiceResource, Path.Combine("Editor", "Services", "DaemonScenePersistenceService.cs")),
                (UnifoclCommandAttributeResource,        Path.Combine("Editor", "UnifoclCommandAttribute.cs")),
                (UnifoclManifestGeneratorResource,      Path.Combine("Editor", "UnifoclManifestGenerator.cs")),
                (UnifoclEditorConfigResource,           Path.Combine("Editor", "Models", "UnifoclEditorConfig.cs")),
                (DaemonCustomToolServiceResource,       Path.Combine("Editor", "Services", "DaemonCustomToolService.cs")),
                (UnifoclCompilationServiceResource,     Path.Combine("Editor", "Services", "UnifoclCompilationService.cs"))
            };

            foreach (var item in resourceToTarget)
            {
                var source = ReadEmbeddedResource(item.Resource);
                if (source is null)
                {
                    return OperationResult.Fail($"missing embedded resource {item.Resource}");
                }

                var targetPath = Path.Combine(payloadPath, item.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                File.WriteAllText(targetPath, source, Encoding.UTF8);
            }

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
        var homePayload = Path.Combine(home, ".unifocl", EmbeddedPackageFolder);
        if (CanUsePayloadPath(homePayload))
        {
            return homePayload;
        }

        var cwdPayload = Path.Combine(
            Path.GetFullPath(Environment.CurrentDirectory),
            ".unifocl-runtime",
            "global-payload",
            EmbeddedPackageFolder);
        if (CanUsePayloadPath(cwdPayload))
        {
            return cwdPayload;
        }

        var tempPayload = Path.Combine(Path.GetTempPath(), "unifocl", "global-payload", EmbeddedPackageFolder);
        if (CanUsePayloadPath(tempPayload))
        {
            return tempPayload;
        }

        // Final fallback keeps deterministic behavior even if all probes fail.
        return homePayload;
    }

    private static bool CanUsePayloadPath(string payloadPath)
    {
        try
        {
            Directory.CreateDirectory(payloadPath);
            var probePath = Path.Combine(payloadPath, ".write-probe");
            File.WriteAllText(probePath, "ok", Encoding.UTF8);
            File.Delete(probePath);
            return true;
        }
        catch
        {
            return false;
        }
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

    private static string NormalizeText(string value)
    {
        return value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Trim();
    }
}
