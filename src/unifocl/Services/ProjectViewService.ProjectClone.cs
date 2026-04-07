internal sealed partial class ProjectViewService
{
    private static bool HandleProjectCloneCommand(IReadOnlyList<string> tokens, List<string> outputs)
    {
        // tokens: ["project", "clone", <source>, <dest>] [--no-library]
        if (tokens.Count < 4)
        {
            outputs.Add("[x] usage: project clone <source-path> <dest-path> [--no-library]");
            return true;
        }

        var sourcePath  = tokens[2];
        var destPath    = tokens[3];
        var seedLibrary = !tokens.Any(t => t.Equals("--no-library", StringComparison.OrdinalIgnoreCase));

        outputs.Add($"[i] cloning project: {sourcePath} -> {destPath}");

        var result = ProjectCloneService.Clone(
            sourcePath,
            destPath,
            seedLibrary,
            log: msg => outputs.Add(msg));

        if (result.Ok)
        {
            outputs.Add($"[+] {result.Message}");
            outputs.Add($"[i] open with: /open {result.ClonedPath}");
        }
        else
        {
            outputs.Add($"[x] clone failed: {result.Message}");
        }

        return true;
    }
}
