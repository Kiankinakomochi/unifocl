using System.Text.Json.Serialization;

/// <summary>
/// Script define symbols per build target group.
/// </summary>
internal sealed record DiagScriptDefinesResult(
    int TargetCount,
    List<DiagBuildTargetEntry> Targets);

internal sealed record DiagBuildTargetEntry(string BuildTarget, string Group, string Defines);

/// <summary>
/// Compiler messages from the last compilation pass.
/// </summary>
internal sealed record DiagCompileErrorsResult(
    int AssemblyCount,
    int ErrorCount,
    int WarningCount,
    List<DiagCompilerMessage> Messages);

/// Message text from Unity includes file/line context inline
/// (e.g. "Assets/Foo.cs(42,5): error CS0246: ...").
internal sealed record DiagCompilerMessage(string Message, string Type);

/// <summary>
/// Assembly dependency graph (asmdef references).
/// </summary>
internal sealed record DiagAssemblyGraphResult(
    int AssemblyCount,
    List<DiagAssemblyEntry> Assemblies);

internal sealed record DiagAssemblyEntry(string Name, string Refs);

/// <summary>
/// Asset dependencies per scene (build-settings enabled scenes only).
/// </summary>
internal sealed record DiagSceneDepsResult(
    int SceneCount,
    List<DiagDepEntry> Scenes);

/// <summary>
/// Asset dependencies per prefab (capped at 100 prefabs).
/// </summary>
internal sealed record DiagPrefabDepsResult(
    int PrefabCount,
    List<DiagDepEntry> Prefabs);

/// <summary>
/// Dependency entry shared by scene-deps and prefab-deps.
/// TopDeps is a semicolon-separated list of up to 20 dependency paths.
/// </summary>
internal sealed record DiagDepEntry(
    [property: JsonPropertyName("path")] string Path,
    int DepCount,
    string TopDeps);
