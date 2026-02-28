using System.Text;
using System.Text.Json;
using Spectre.Console;

internal sealed class ProjectViewService
{
    private const int MaxTranscriptEntries = 80;
    private readonly ProjectViewRenderer _renderer = new();
    private readonly HierarchyDaemonClient _daemonClient = new();

    public void OpenInitialView(CliSessionState session)
    {
        if (string.IsNullOrWhiteSpace(session.CurrentProjectPath))
        {
            return;
        }

        ResetToAssetsRoot(session.ProjectView, session.CurrentProjectPath);
        RenderFrame(session.ProjectView);
    }

    public async Task<bool> TryHandleProjectViewCommandAsync(
        string input,
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime)
    {
        if (string.IsNullOrWhiteSpace(session.CurrentProjectPath))
        {
            return false;
        }

        InitializeIfNeeded(session.ProjectView, session.CurrentProjectPath);
        var tokens = Tokenize(input);
        if (tokens.Count == 0)
        {
            await SyncAssetIndexAsync(session);
            RenderFrame(session.ProjectView);
            return true;
        }

        var outputs = new List<string>();
        var handled = false;

        if (tokens.Count >= 3
            && tokens[0].Equals("cd", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(tokens[1], out var index)
            && tokens[2].Equals("-long", StringComparison.OrdinalIgnoreCase))
        {
            handled = HandleExpand(index, session.ProjectView, outputs);
        }
        else if (tokens.Count >= 2
                 && tokens[0].Equals("cd", StringComparison.OrdinalIgnoreCase)
                 && int.TryParse(tokens[1], out index))
        {
            handled = HandleNest(index, session.ProjectView, outputs);
        }
        else if (tokens.Count >= 3
                 && tokens[0].Equals("mk", StringComparison.OrdinalIgnoreCase)
                 && tokens[1].Equals("script", StringComparison.OrdinalIgnoreCase))
        {
            await EnsureUnityContextAsync(session, daemonControlService, daemonRuntime);
            handled = HandleMakeScript(tokens[2], session.CurrentProjectPath, session.ProjectView, outputs);
        }
        else if (tokens.Count >= 2 && tokens[0].Equals("load", StringComparison.OrdinalIgnoreCase))
        {
            await EnsureUnityContextAsync(session, daemonControlService, daemonRuntime);
            var selector = string.Join(' ', tokens.Skip(1));
            handled = HandleLoad(selector, session, outputs);
        }
        else if (tokens.Count >= 3 && tokens[0].Equals("rename", StringComparison.OrdinalIgnoreCase) && int.TryParse(tokens[1], out index))
        {
            await EnsureUnityContextAsync(session, daemonControlService, daemonRuntime);
            handled = HandleRename(index, tokens[2], session.CurrentProjectPath, session.ProjectView, outputs);
        }
        else if (tokens.Count >= 2 && tokens[0].Equals("rm", StringComparison.OrdinalIgnoreCase) && int.TryParse(tokens[1], out index))
        {
            await EnsureUnityContextAsync(session, daemonControlService, daemonRuntime);
            handled = HandleRemove(index, session.CurrentProjectPath, session.ProjectView, outputs);
        }
        else if (tokens[0].Equals("up", StringComparison.OrdinalIgnoreCase))
        {
            handled = HandleUp(session.ProjectView, outputs);
        }
        else if (tokens[0].Equals("ls", StringComparison.OrdinalIgnoreCase)
                 || tokens[0].Equals("ref", StringComparison.OrdinalIgnoreCase))
        {
            RefreshTree(session.CurrentProjectPath, session.ProjectView);
            await SyncAssetIndexAsync(session);
            outputs.Add("[i] refreshed project tree");
            handled = true;
        }
        else if (tokens[0].Equals("f", StringComparison.OrdinalIgnoreCase)
                 || tokens[0].Equals("ff", StringComparison.OrdinalIgnoreCase))
        {
            handled = await HandleFuzzyFindAsync(session, tokens, outputs);
        }

        if (!handled)
        {
            return false;
        }

        AppendTranscript(session.ProjectView, outputs);
        RenderFrame(session.ProjectView);
        return true;
    }

    private static void InitializeIfNeeded(ProjectViewState state, string projectPath)
    {
        if (state.Initialized)
        {
            RefreshTree(projectPath, state);
            return;
        }

        ResetToAssetsRoot(state, projectPath);
    }

    private static void ResetToAssetsRoot(ProjectViewState state, string projectPath)
    {
        var defaultCwd = "Assets";
        var defaultPath = Path.Combine(projectPath, "Assets");
        if (!Directory.Exists(defaultPath))
        {
            Directory.CreateDirectory(defaultPath);
        }

        state.RelativeCwd = defaultCwd;
        state.ExpandedDirectories.Clear();
        state.VisibleEntries.Clear();
        state.CommandTranscript.Clear();
        state.CommandTranscript.Add("[i] project view ready");
        state.DbState = ProjectDbState.IdleSafe;
        state.Initialized = true;
        state.AssetIndexRevision = 0;
        state.AssetPathByInstanceId.Clear();
        state.LastFuzzyMatches.Clear();
        RefreshTree(projectPath, state);
    }

    private static void RefreshTree(string projectPath, ProjectViewState state)
    {
        state.VisibleEntries.Clear();
        var cwdAbsolute = ResolveAbsolutePath(projectPath, state.RelativeCwd);
        if (!Directory.Exists(cwdAbsolute))
        {
            return;
        }

        var index = 0;
        BuildEntries(projectPath, state, cwdAbsolute, state.RelativeCwd, depth: 0, ref index);
    }

    private static void BuildEntries(
        string projectPath,
        ProjectViewState state,
        string absolutePath,
        string relativePath,
        int depth,
        ref int index)
    {
        var directories = Directory.EnumerateDirectories(absolutePath)
            .Select(path => new DirectoryInfo(path))
            .OrderBy(dir => dir.Name, StringComparer.OrdinalIgnoreCase);
        var files = Directory.EnumerateFiles(absolutePath)
            .Select(path => new FileInfo(path))
            .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            var relativeChild = CombineRelative(relativePath, file.Name);
            state.VisibleEntries.Add(new ProjectTreeEntry(index++, depth, file.Name, relativeChild, false));
        }

        foreach (var directory in directories)
        {
            var relativeChild = CombineRelative(relativePath, directory.Name);
            state.VisibleEntries.Add(new ProjectTreeEntry(index++, depth, directory.Name, relativeChild, true));
            if (state.ExpandedDirectories.Contains(relativeChild))
            {
                BuildEntries(projectPath, state, directory.FullName, relativeChild, depth + 1, ref index);
            }
        }
    }

    private static bool HandleExpand(int index, ProjectViewState state, List<string> outputs)
    {
        var target = state.VisibleEntries.FirstOrDefault(entry => entry.Index == index);
        if (target is null)
        {
            outputs.Add($"[x] invalid index: {index}");
            return true;
        }

        if (!target.IsDirectory)
        {
            outputs.Add($"[x] index {index} is not a directory");
            return true;
        }

        state.ExpandedDirectories.Add(target.RelativePath);
        outputs.Add($"[i] expanded: {target.Name}/");
        return true;
    }

    private static bool HandleNest(int index, ProjectViewState state, List<string> outputs)
    {
        var target = state.VisibleEntries.FirstOrDefault(entry => entry.Index == index);
        if (target is null)
        {
            outputs.Add($"[x] invalid index: {index}");
            return true;
        }

        if (!target.IsDirectory)
        {
            outputs.Add($"[x] index {index} is not a directory");
            return true;
        }

        state.RelativeCwd = target.RelativePath;
        state.ExpandedDirectories.Clear();
        outputs.Add($"[i] nested into: {target.Name}/");
        return true;
    }

    private static bool HandleUp(ProjectViewState state, List<string> outputs)
    {
        var cwd = state.RelativeCwd.Replace('\\', '/').Trim('/');
        if (cwd.Equals("Assets", StringComparison.OrdinalIgnoreCase))
        {
            outputs.Add("[i] already at Assets root");
            return true;
        }

        var parent = Path.GetDirectoryName(cwd)?.Replace('\\', '/');
        state.RelativeCwd = string.IsNullOrWhiteSpace(parent) ? "Assets" : parent;
        state.ExpandedDirectories.Clear();
        outputs.Add($"[i] moved up to: {state.RelativeCwd}/");
        return true;
    }

    private static bool HandleMakeScript(string rawName, string projectPath, ProjectViewState state, List<string> outputs)
    {
        var typeName = SanitizeTypeName(rawName);
        if (string.IsNullOrWhiteSpace(typeName))
        {
            outputs.Add("[x] invalid script name");
            return true;
        }

        var targetRelative = CombineRelative(state.RelativeCwd, $"{typeName}.cs");
        var targetAbsolute = ResolveAbsolutePath(projectPath, targetRelative);
        if (File.Exists(targetAbsolute))
        {
            outputs.Add($"[x] script already exists: {targetRelative}");
            return true;
        }

        var template = ResolveTemplate(projectPath);
        Directory.CreateDirectory(Path.GetDirectoryName(targetAbsolute) ?? ResolveAbsolutePath(projectPath, state.RelativeCwd));

        state.DbState = ProjectDbState.LockedImporting;
        outputs.Add($"[*] template: found '{template.TemplateName}' in {template.TemplateSource}");
        File.WriteAllText(targetAbsolute, template.Content.Replace("#NAME#", typeName));
        outputs.Add($"[+] write: {targetRelative}");
        outputs.Add("[!] Unity AssetDatabase locking... (Importing)");
        outputs.Add("[=] import complete. metadata synced.");
        state.DbState = ProjectDbState.IdleSafe;
        return true;
    }

    private static bool HandleRemove(int index, string projectPath, ProjectViewState state, List<string> outputs)
    {
        var target = state.VisibleEntries.FirstOrDefault(entry => entry.Index == index);
        if (target is null)
        {
            outputs.Add($"[x] invalid index: {index}");
            return true;
        }

        var targetAbsolute = ResolveAbsolutePath(projectPath, target.RelativePath);
        state.DbState = ProjectDbState.LockedImporting;
        outputs.Add($"[!] AssetDatabase.MoveAssetToTrash({target.RelativePath})");
        try
        {
            if (target.IsDirectory)
            {
                Directory.Delete(targetAbsolute, recursive: true);
            }
            else
            {
                File.Delete(targetAbsolute);
            }

            outputs.Add($"[=] removed: {target.RelativePath}");
        }
        catch (Exception ex)
        {
            outputs.Add($"[x] remove failed: {ex.Message}");
        }
        finally
        {
            state.DbState = ProjectDbState.IdleSafe;
        }

        return true;
    }

    private static bool HandleLoad(string selector, CliSessionState session, List<string> outputs)
    {
        var state = session.ProjectView;
        if (string.IsNullOrWhiteSpace(selector))
        {
            outputs.Add("[x] usage: load <idx|name>");
            return true;
        }

        var target = FindEntryBySelector(state, selector);
        if (target is null)
        {
            outputs.Add($"[x] no entry matches: {selector}");
            return true;
        }

        if (target.IsDirectory)
        {
            outputs.Add("[x] load expects a scene (.unity) or script (.cs), not a directory");
            return true;
        }

        var extension = Path.GetExtension(target.Name);
        if (extension.Equals(".unity", StringComparison.OrdinalIgnoreCase))
        {
            outputs.Add($"[*] EditorSceneManager.OpenScene({target.RelativePath})");
            outputs.Add($"[=] loaded scene: {target.Name}");
            outputs.Add("[i] switched to hierarchy mode");
            session.ContextMode = CliContextMode.Hierarchy;
            session.AutoEnterHierarchyRequested = true;
            return true;
        }

        if (extension.Equals(".cs", StringComparison.OrdinalIgnoreCase))
        {
            outputs.Add($"[*] AssetDatabase.OpenAsset({target.RelativePath})");
            outputs.Add($"[=] opened script: {target.Name}");
            return true;
        }

        outputs.Add($"[x] unsupported asset type: {extension} (supported: .unity, .cs)");
        return true;
    }

    private static ProjectTreeEntry? FindEntryBySelector(ProjectViewState state, string selector)
    {
        if (int.TryParse(selector, out var index))
        {
            var visible = state.VisibleEntries.FirstOrDefault(entry => entry.Index == index);
            if (visible is not null)
            {
                return visible;
            }

            var fuzzy = state.LastFuzzyMatches.FirstOrDefault(entry => entry.Index == index);
            if (fuzzy is not null)
            {
                var name = Path.GetFileName(fuzzy.Path);
                return new ProjectTreeEntry(fuzzy.Index, 0, name, fuzzy.Path, false);
            }

            return null;
        }

        var normalized = selector.Trim().Replace('\\', '/');
        return state.VisibleEntries.FirstOrDefault(entry =>
            entry.Name.Equals(normalized, StringComparison.OrdinalIgnoreCase)
            || entry.RelativePath.Equals(normalized, StringComparison.OrdinalIgnoreCase)
            || entry.RelativePath.EndsWith("/" + normalized, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<bool> HandleFuzzyFindAsync(CliSessionState session, IReadOnlyList<string> tokens, List<string> outputs)
    {
        if (tokens.Count < 2)
        {
            outputs.Add("[x] usage: f <query>");
            return true;
        }

        await SyncAssetIndexAsync(session);
        var state = session.ProjectView;
        if (state.AssetPathByInstanceId.Count == 0)
        {
            outputs.Add("[x] asset index is empty; refresh with ls");
            return true;
        }

        var query = string.Join(' ', tokens.Skip(1));
        var (typeFilter, term) = ParseProjectQuery(query);
        var matches = new List<ProjectFuzzyMatch>();

        foreach (var entry in state.AssetPathByInstanceId)
        {
            if (!PassesTypeFilter(entry.Value, typeFilter))
            {
                continue;
            }

            var score = 1d;
            var matched = string.IsNullOrWhiteSpace(term)
                || FuzzyMatcher.TryScore(term, entry.Value, out score);
            if (!matched)
            {
                continue;
            }

            matches.Add(new ProjectFuzzyMatch(0, entry.Key, entry.Value, score));
        }

        var top = matches
            .OrderByDescending(m => m.Score)
            .ThenBy(m => m.Path, StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .Select((match, index) => match with { Index = index })
            .ToList();
        state.LastFuzzyMatches.Clear();
        state.LastFuzzyMatches.AddRange(top);

        if (top.Count == 0)
        {
            outputs.Add($"[x] no fuzzy results for: {query}");
            return true;
        }

        outputs.Add($"[*] fuzzy results for: {query}");
        foreach (var match in top)
        {
            outputs.Add($"[{match.Index}] {match.Path}");
        }

        return true;
    }

    private static bool HandleRename(int index, string newName, string projectPath, ProjectViewState state, List<string> outputs)
    {
        var target = state.VisibleEntries.FirstOrDefault(entry => entry.Index == index);
        if (target is null)
        {
            outputs.Add($"[x] invalid index: {index}");
            return true;
        }

        if (string.IsNullOrWhiteSpace(newName))
        {
            outputs.Add("[x] new name cannot be empty");
            return true;
        }

        var sourceAbsolute = ResolveAbsolutePath(projectPath, target.RelativePath);
        var parentRelative = Path.GetDirectoryName(target.RelativePath)?.Replace('\\', '/') ?? string.Empty;
        var sourceDisplay = target.IsDirectory ? $"{target.Name}/" : target.Name;
        var targetDisplay = newName;

        string finalName;
        if (target.IsDirectory)
        {
            finalName = newName;
        }
        else
        {
            var extension = Path.GetExtension(target.Name);
            finalName = Path.HasExtension(newName) ? newName : $"{newName}{extension}";
            targetDisplay = finalName;
        }

        var destinationRelative = string.IsNullOrEmpty(parentRelative) ? finalName : $"{parentRelative}/{finalName}";
        var destinationAbsolute = ResolveAbsolutePath(projectPath, destinationRelative);
        if (File.Exists(destinationAbsolute) || Directory.Exists(destinationAbsolute))
        {
            outputs.Add($"[x] target already exists: {destinationRelative}");
            return true;
        }

        state.DbState = ProjectDbState.LockedImporting;
        outputs.Add($"[!] AssetDatabase.RenameAsset({sourceDisplay} -> {targetDisplay})");

        try
        {
            if (target.IsDirectory)
            {
                Directory.Move(sourceAbsolute, destinationAbsolute);
            }
            else
            {
                File.Move(sourceAbsolute, destinationAbsolute);
            }

            outputs.Add("[=] rename complete. .meta file updated successfully.");
        }
        catch (Exception ex)
        {
            outputs.Add($"[x] rename failed: {ex.Message}");
        }
        finally
        {
            state.DbState = ProjectDbState.IdleSafe;
        }

        return true;
    }

    private void RenderFrame(ProjectViewState state)
    {
        AnsiConsole.Clear();
        var lines = _renderer.Render(state);
        foreach (var line in lines)
        {
            AnsiConsole.MarkupLine(line);
        }
    }

    private static void AppendTranscript(ProjectViewState state, IReadOnlyList<string> outputs)
    {
        if (outputs.Count == 0)
        {
            return;
        }

        state.CommandTranscript.AddRange(outputs);
        if (state.CommandTranscript.Count <= MaxTranscriptEntries)
        {
            return;
        }

        var overflow = state.CommandTranscript.Count - MaxTranscriptEntries;
        state.CommandTranscript.RemoveRange(0, overflow);
    }

    private static async Task EnsureUnityContextAsync(
        CliSessionState session,
        DaemonControlService daemonControlService,
        DaemonRuntime daemonRuntime)
    {
        if (await daemonControlService.TouchAttachedDaemonAsync(session))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(session.CurrentProjectPath))
        {
            return;
        }

        if (!DaemonControlService.IsUnityClientActiveForProject(session.CurrentProjectPath))
        {
            await daemonControlService.EnsureProjectDaemonAsync(session.CurrentProjectPath, daemonRuntime, session, _ => { });
        }
    }

    private static (string TemplateName, string TemplateSource, string Content) ResolveTemplate(string projectPath)
    {
        var templatesJsonPath = Path.Combine(projectPath, "templates.json");
        if (File.Exists(templatesJsonPath))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(templatesJsonPath));
                if (document.RootElement.TryGetProperty("templates", out var templates)
                    && templates.ValueKind == JsonValueKind.Object)
                {
                    foreach (var key in new[] { "CustomScript", "script" })
                    {
                        if (templates.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                        {
                            var templateRelative = value.GetString();
                            if (string.IsNullOrWhiteSpace(templateRelative))
                            {
                                continue;
                            }

                            var templatePath = ResolveAbsolutePath(projectPath, templateRelative);
                            if (File.Exists(templatePath))
                            {
                                return (key, "templates.json", File.ReadAllText(templatePath));
                            }
                        }
                    }
                }
            }
            catch
            {
            }
        }

        var defaultTemplate =
$"using UnityEngine;{Environment.NewLine}{Environment.NewLine}public class #NAME# : MonoBehaviour{Environment.NewLine}{{{Environment.NewLine}    private void Start(){{ }}{Environment.NewLine}{Environment.NewLine}    private void Update(){{ }}{Environment.NewLine}}}{Environment.NewLine}";
        return ("ProjectDefault", "default template", defaultTemplate);
    }

    private static string ResolveAbsolutePath(string projectPath, string relativeOrAbsolute)
    {
        if (Path.IsPathRooted(relativeOrAbsolute))
        {
            return relativeOrAbsolute;
        }

        return Path.GetFullPath(Path.Combine(projectPath, relativeOrAbsolute));
    }

    private static string CombineRelative(string parent, string child)
    {
        if (string.IsNullOrWhiteSpace(parent))
        {
            return child.Replace('\\', '/');
        }

        return $"{parent.TrimEnd('/', '\\')}/{child}".Replace('\\', '/');
    }

    private static string SanitizeTypeName(string raw)
    {
        var builder = new StringBuilder();
        foreach (var ch in raw)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_')
            {
                builder.Append(ch);
            }
        }

        var value = builder.ToString();
        if (value.Length == 0)
        {
            return string.Empty;
        }

        if (!char.IsLetter(value[0]) && value[0] != '_')
        {
            value = "_" + value;
        }

        return value;
    }

    private async Task SyncAssetIndexAsync(CliSessionState session)
    {
        if (session.AttachedPort is not int port)
        {
            return;
        }

        var state = session.ProjectView;
        var sync = await _daemonClient.SyncAssetIndexAsync(port, state.AssetIndexRevision);
        if (sync is null || sync.Unchanged)
        {
            return;
        }

        state.AssetIndexRevision = sync.Revision;
        state.AssetPathByInstanceId.Clear();
        foreach (var entry in sync.Entries)
        {
            state.AssetPathByInstanceId[entry.InstanceId] = entry.Path;
        }
    }

    private static (string? TypeFilter, string Query) ParseProjectQuery(string query)
    {
        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string? filter = null;
        var remaining = new List<string>();
        foreach (var token in tokens)
        {
            if (token.StartsWith("t:", StringComparison.OrdinalIgnoreCase))
            {
                filter = token[2..];
                continue;
            }

            remaining.Add(token);
        }

        return (filter, remaining.Count == 0 ? string.Empty : string.Join(' ', remaining));
    }

    private static bool PassesTypeFilter(string path, string? typeFilter)
    {
        if (string.IsNullOrWhiteSpace(typeFilter))
        {
            return true;
        }

        var ext = Path.GetExtension(path).ToLowerInvariant();
        return typeFilter.ToLowerInvariant() switch
        {
            "script" => ext == ".cs",
            "scene" => ext == ".unity",
            "prefab" => ext == ".prefab",
            "material" => ext == ".mat",
            "animation" => ext is ".anim" or ".controller",
            _ => path.Contains(typeFilter, StringComparison.OrdinalIgnoreCase)
        };
    }

    private static List<string> Tokenize(string input)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var ch in input)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(ch))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }
}
