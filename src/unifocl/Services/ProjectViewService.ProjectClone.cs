using Spectre.Console;

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

        outputs.Add($"[i] cloning project: {Markup.Escape(sourcePath)} -> {Markup.Escape(destPath)}");

        var result = ProjectCloneService.Clone(
            sourcePath,
            destPath,
            seedLibrary,
            log: msg => outputs.Add(msg));

        if (result.Ok)
        {
            outputs.Add($"[+] {Markup.Escape(result.Message)}");
            outputs.Add($"[i] open with: /open {Markup.Escape(result.ClonedPath!)}");
        }
        else
        {
            outputs.Add($"[x] clone failed: {Markup.Escape(result.Message)}");
        }

        return true;
    }
}
