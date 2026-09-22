using System.Text.Json;
using Spectre.Console;

/// <summary>
/// /probuilder — CLI front end for the optional "probuilder" tool category. Each subcommand maps onto
/// one probuilder.* custom tool: positional arguments and <c>--kebab-case</c> options become the tool's
/// named parameters, so the Unity side stays the single source of truth for validation.
/// </summary>
internal sealed partial class RuntimeCommandService
{
    private const string ProBuilderUnavailableHint =
        "ProBuilder tools are not loaded in this editor. They need com.unity.probuilder 5.0+ " +
        "(/upm install com.unity.probuilder), a re-run of /init after updating unifocl, and a finished recompile.";

    /// <param name="Tool">Custom tool name registered on the Unity side.</param>
    /// <param name="Usage">Usage line shown on argument errors.</param>
    /// <param name="Positionals">Parameter names bound to positional arguments, in order. A trailing
    /// "..." consumes the remaining positionals, joined with ';'.</param>
    /// <param name="Options">Parameter names accepted as <c>--option value</c>.</param>
    /// <param name="Switches">Boolean parameter names accepted as <c>--switch</c> / <c>--no-switch</c>.</param>
    /// <param name="ShowPayload">Render the full result in the interactive TUI, not just its message;
    /// for read operations whose value is the payload itself.</param>
    internal sealed record ProBuilderCliOperation(
        string Tool,
        string Usage,
        string[] Positionals,
        string[] Options,
        string[] Switches,
        bool ShowPayload = false);

    internal static readonly Dictionary<string, ProBuilderCliOperation> ProBuilderOperations =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["shape create"] = new(
                "probuilder.shape.create",
                "/probuilder shape create <cube|stair|curved_stair|prism|cylinder|plane|door|pipe|cone|arch|sphere|torus> [--name <n>] [--parent <path>] [--position x,y,z] [--rotation x,y,z] [--size x,y,z] [--pivot center|bottom] [--material <path>] [--segments <n>] [--steps <n>] [--no-collider]",
                ["shape"],
                ["name", "parent", "position", "rotation", "size", "pivot", "material", "segments", "steps"],
                ["collider"]),
            ["mesh info"] = new(
                "probuilder.mesh.info",
                "/probuilder mesh info <target> [--max-faces <n>] [--no-include-faces]",
                ["target"],
                ["maxFaces"],
                ["includeFaces"],
                ShowPayload: true),
            ["mesh merge"] = new(
                "probuilder.mesh.merge",
                "/probuilder mesh merge <target> <other>... [--name <n>]",
                ["targets..."],
                ["name"],
                []),
            ["mesh probuilderize"] = new(
                "probuilder.mesh.probuilderize",
                "/probuilder mesh probuilderize <target> [--no-quads] [--no-smoothing] [--smoothing-angle <deg>]",
                ["target"],
                ["smoothingAngle"],
                ["quads", "smoothing"]),
            ["mesh export"] = new(
                "probuilder.mesh.export",
                "/probuilder mesh export <target> <Assets/.../Name.asset> [--overwrite]",
                ["target", "assetPath"],
                [],
                ["overwrite"]),
            ["face extrude"] = new(
                "probuilder.face.extrude",
                "/probuilder face extrude <target> --faces <selector> [--distance <m>] [--method face_normal|vertex_normal|individual]",
                ["target"],
                ["faces", "distance", "method"],
                []),
            ["face move"] = new(
                "probuilder.face.move",
                "/probuilder face move <target> --faces <selector> --offset x,y,z [--space world|local]",
                ["target"],
                ["faces", "offset", "space"],
                []),
            ["face material"] = new(
                "probuilder.face.set_material",
                "/probuilder face material <target> <material-path> [--faces <selector>]",
                ["target", "material"],
                ["faces"],
                []),
            ["face delete"] = new(
                "probuilder.face.delete",
                "/probuilder face delete <target> --faces <selector>",
                ["target"],
                ["faces"],
                []),
            ["face flip"] = new(
                "probuilder.face.flip_normals",
                "/probuilder face flip <target> [--faces <selector>]",
                ["target"],
                ["faces"],
                []),
            ["face subdivide"] = new(
                "probuilder.face.subdivide",
                "/probuilder face subdivide <target> [--faces <selector>]",
                ["target"],
                ["faces"],
                []),
            ["edge bevel"] = new(
                "probuilder.edge.bevel",
                "/probuilder edge bevel <target> [--faces <selector>] [--amount <0-1>]",
                ["target"],
                ["faces", "amount"],
                []),
        };

    public async Task HandleProBuilderCommandAsync(
        string input, CliSessionState session, Action<string> log)
    {
        if (!RequireProject(session, log, "probuilder")) return;

        var tokens = Tokenize(input);
        var key = tokens.Count >= 3 ? $"{tokens[1]} {tokens[2]}" : string.Empty;
        if (!ProBuilderOperations.TryGetValue(key, out var operation))
        {
            // A leading "error" is what marks the line as a failure for agentic exec callers.
            log(tokens.Count >= 3
                ? $"[red]error[/]: probuilder: unknown subcommand {Markup.Escape(key)}"
                : "[red]error[/]: probuilder: usage: /probuilder <shape|mesh|face|edge> <action> ...");
            foreach (var known in ProBuilderOperations.Values)
            {
                log($"  {Markup.Escape(known.Usage)}");
            }

            return;
        }

        if (!TryBuildProBuilderArgs(operation, tokens.Skip(3).ToList(), out var args, out var dryRun, out var error))
        {
            log($"[red]error[/]: probuilder: {Markup.Escape(error)}");
            log($"usage: {Markup.Escape(operation.Usage)}");
            return;
        }

        await DispatchCustomToolAsync(
            session,
            operation.Tool,
            JsonSerializer.Serialize(args),
            "probuilder",
            log,
            dryRun,
            ProBuilderUnavailableHint,
            operation.ShowPayload);
    }

    /// <summary>
    /// Binds tokens to tool parameters. Values are forwarded as strings; the Unity dispatcher coerces
    /// them to each parameter's declared type.
    /// </summary>
    internal static bool TryBuildProBuilderArgs(
        ProBuilderCliOperation operation,
        List<string> tokens,
        out Dictionary<string, string> args,
        out bool dryRun,
        out string error)
    {
        args = new Dictionary<string, string>(StringComparer.Ordinal);
        dryRun = false;
        error = string.Empty;
        var positionals = new List<string>();

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                positionals.Add(token);
                continue;
            }

            var flag = token[2..];
            if (flag.Equals("dry-run", StringComparison.OrdinalIgnoreCase))
            {
                dryRun = true;
                continue;
            }

            var negated = flag.StartsWith("no-", StringComparison.OrdinalIgnoreCase);
            var switchName = ResolveProBuilderParameter(negated ? flag[3..] : flag, operation.Switches);
            if (switchName is not null)
            {
                args[switchName] = negated ? "false" : "true";
                continue;
            }

            var optionName = ResolveProBuilderParameter(flag, operation.Options);
            if (optionName is null)
            {
                error = $"unknown option --{flag}";
                return false;
            }

            // Anything but another --option is a value, so negative vectors like -1,0,0 pass through.
            if (i + 1 >= tokens.Count || tokens[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                error = $"--{flag} requires a value";
                return false;
            }

            args[optionName] = tokens[++i];
        }

        for (var i = 0; i < operation.Positionals.Length; i++)
        {
            var name = operation.Positionals[i];
            if (name.EndsWith("...", StringComparison.Ordinal))
            {
                if (positionals.Count <= i)
                {
                    error = $"missing <{name[..^3]}>";
                    return false;
                }

                args[name[..^3]] = string.Join(";", positionals.Skip(i));
                positionals.Clear();
                break;
            }

            if (positionals.Count <= i)
            {
                error = $"missing <{name}>";
                return false;
            }

            args[name] = positionals[i];
        }

        if (positionals.Count > operation.Positionals.Length)
        {
            error = $"unexpected argument '{positionals[operation.Positionals.Length]}'";
            return false;
        }

        return true;
    }

    /// <summary>Matches a flag (kebab-case or camelCase) to one of the allowed parameter names.</summary>
    private static string? ResolveProBuilderParameter(string flag, string[] allowed)
    {
        var compact = flag.Replace("-", string.Empty);
        return allowed.FirstOrDefault(name => name.Equals(compact, StringComparison.OrdinalIgnoreCase));
    }
}
