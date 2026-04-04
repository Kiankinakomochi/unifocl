using Xunit;

/// <summary>
/// Tests for TagLayerCommandService — exercises routing, validation, and no-daemon guards
/// directly without MCP transport or a live Unity daemon.
/// </summary>
public class TagLayerCommandServiceTests
{
    // ── helpers ───────────────────────────────────────────────────────────────

    /// Session in Boot mode (no project open).
    private static CliSessionState BootSession() => new();

    /// Session in Project mode (project path set, no daemon attached).
    private static CliSessionState ProjectSession() => new()
    {
        Mode = CliMode.Project,
        CurrentProjectPath = Path.GetTempPath()
    };

    private static (TagLayerCommandService svc, DaemonControlService daemon, DaemonRuntime runtime)
        MakeServices()
    {
        var svc = new TagLayerCommandService();
        var daemon = new DaemonControlService();
        // DaemonRuntime needs a directory — use a throw-away temp subdir
        var runtime = new DaemonRuntime(Path.Combine(Path.GetTempPath(), "unifocl-tests-runtime"));
        return (svc, daemon, runtime);
    }

    private static async Task<List<string>> RunTagAsync(
        string input, CliSessionState session)
    {
        var (svc, daemon, runtime) = MakeServices();
        var logs = new List<string>();
        await svc.HandleTagCommandAsync(input, session, daemon, runtime, logs.Add);
        return logs;
    }

    private static async Task<List<string>> RunLayerAsync(
        string input, CliSessionState session)
    {
        var (svc, daemon, runtime) = MakeServices();
        var logs = new List<string>();
        await svc.HandleLayerCommandAsync(input, session, daemon, runtime, logs.Add);
        return logs;
    }

    // ── /tag — no project ─────────────────────────────────────────────────────

    [Fact]
    public async Task Tag_BootMode_PromptsToOpenProject()
    {
        var logs = await RunTagAsync("/tag list", BootSession());
        Assert.Single(logs);
        Assert.Contains("open a project first", logs[0]);
    }

    // ── /tag — usage validation ────────────────────────────────────────────────

    [Fact]
    public async Task Tag_NoSubcommand_ShowsUsage()
    {
        var logs = await RunTagAsync("/tag", ProjectSession());
        Assert.Single(logs);
        Assert.Contains("usage", logs[0]);
    }

    [Fact]
    public async Task Tag_UnknownSubcommand_ShowsError()
    {
        var logs = await RunTagAsync("/tag bogus", ProjectSession());
        Assert.Contains(logs, l => l.Contains("unknown tag subcommand"));
    }

    [Fact]
    public async Task TagAdd_MissingName_ShowsUsage()
    {
        var logs = await RunTagAsync("/tag add", ProjectSession());
        Assert.Single(logs);
        Assert.Contains("usage", logs[0]);
    }

    [Fact]
    public async Task TagRemove_MissingName_ShowsUsage()
    {
        // /tag rm is the alias for remove — test alias routing too
        var logs = await RunTagAsync("/tag rm", ProjectSession());
        Assert.Single(logs);
        Assert.Contains("usage", logs[0]);
    }

    // ── /tag — no-daemon gate ─────────────────────────────────────────────────

    [Theory]
    [InlineData("/tag list")]
    [InlineData("/tag ls")]
    public async Task TagList_NoDaemon_ReportsDaemonNotRunning(string input)
    {
        var logs = await RunTagAsync(input, ProjectSession());
        Assert.Single(logs);
        Assert.Contains("daemon not running", logs[0]);
    }

    [Theory]
    [InlineData("/tag add Enemy")]
    [InlineData("/tag a Enemy")]
    public async Task TagAdd_NoDaemon_ReportsDaemonNotRunning(string input)
    {
        var logs = await RunTagAsync(input, ProjectSession());
        Assert.Single(logs);
        Assert.Contains("daemon not running", logs[0]);
    }

    [Theory]
    [InlineData("/tag remove Enemy")]
    [InlineData("/tag rm Enemy")]
    public async Task TagRemove_NoDaemon_ReportsDaemonNotRunning(string input)
    {
        var logs = await RunTagAsync(input, ProjectSession());
        Assert.Single(logs);
        Assert.Contains("daemon not running", logs[0]);
    }

    // ── /layer — no project ────────────────────────────────────────────────────

    [Fact]
    public async Task Layer_BootMode_PromptsToOpenProject()
    {
        var logs = await RunLayerAsync("/layer list", BootSession());
        Assert.Single(logs);
        Assert.Contains("open a project first", logs[0]);
    }

    // ── /layer — usage validation ──────────────────────────────────────────────

    [Fact]
    public async Task Layer_NoSubcommand_ShowsUsage()
    {
        var logs = await RunLayerAsync("/layer", ProjectSession());
        Assert.Single(logs);
        Assert.Contains("usage", logs[0]);
    }

    [Fact]
    public async Task Layer_UnknownSubcommand_ShowsError()
    {
        var logs = await RunLayerAsync("/layer bogus", ProjectSession());
        Assert.Contains(logs, l => l.Contains("unknown layer subcommand"));
    }

    [Fact]
    public async Task LayerAdd_MissingName_ShowsUsage()
    {
        var logs = await RunLayerAsync("/layer add", ProjectSession());
        Assert.Single(logs);
        Assert.Contains("usage", logs[0]);
    }

    [Fact]
    public async Task LayerRename_MissingNewName_ShowsUsage()
    {
        var logs = await RunLayerAsync("/layer rename UI", ProjectSession());
        Assert.Single(logs);
        Assert.Contains("usage", logs[0]);
    }

    [Fact]
    public async Task LayerRemove_MissingName_ShowsUsage()
    {
        var logs = await RunLayerAsync("/layer rm", ProjectSession());
        Assert.Single(logs);
        Assert.Contains("usage", logs[0]);
    }

    // ── /layer — no-daemon gate ────────────────────────────────────────────────

    [Theory]
    [InlineData("/layer list")]
    [InlineData("/layer ls")]
    public async Task LayerList_NoDaemon_ReportsDaemonNotRunning(string input)
    {
        var logs = await RunLayerAsync(input, ProjectSession());
        Assert.Single(logs);
        Assert.Contains("daemon not running", logs[0]);
    }

    [Theory]
    [InlineData("/layer add UI")]
    [InlineData("/layer a UI")]
    [InlineData("/layer add UI --index 10")]
    public async Task LayerAdd_NoDaemon_ReportsDaemonNotRunning(string input)
    {
        var logs = await RunLayerAsync(input, ProjectSession());
        Assert.Single(logs);
        Assert.Contains("daemon not running", logs[0]);
    }

    [Theory]
    [InlineData("/layer rename UI FX")]
    [InlineData("/layer rn UI FX")]
    public async Task LayerRename_NoDaemon_ReportsDaemonNotRunning(string input)
    {
        var logs = await RunLayerAsync(input, ProjectSession());
        Assert.Single(logs);
        Assert.Contains("daemon not running", logs[0]);
    }

    [Theory]
    [InlineData("/layer remove UI")]
    [InlineData("/layer rm UI")]
    public async Task LayerRemove_NoDaemon_ReportsDaemonNotRunning(string input)
    {
        var logs = await RunLayerAsync(input, ProjectSession());
        Assert.Single(logs);
        Assert.Contains("daemon not running", logs[0]);
    }
}
