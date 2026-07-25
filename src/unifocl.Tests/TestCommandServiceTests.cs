using Xunit;

/// <summary>
/// Tests for the pure and filesystem-only helpers behind the test commands. None of these need a
/// live Unity, and two of them (<see cref="TestCommandService.ExtractFailureDetail"/> and
/// <see cref="CliAgenticIssueService.ContainsFailureWord"/>) previously shipped defects that these
/// cases pin down.
/// </summary>
public class TestCommandServiceTests
{
    // ── ExtractFailureDetail ─────────────────────────────────────────────────

    private const string Banner = "Aborting batchmode due to fatal error";

    [Fact]
    public void ExtractFailureDetail_FindsBannerOnStdout_EvenWhenStderrHasNoise()
    {
        // Unity writes the fatal banner to stdout and licensing noise to stderr. Returning the
        // first non-empty stream's tail would surface the noise and drop the cause.
        var stdout = $"Unity Editor version: 6000.4.0f1\n{Banner}:\nMultiple Unity instances cannot open the same project.";
        var stderr = "[Licensing::Client] Successfully resolved entitlement details";

        var detail = TestCommandService.ExtractFailureDetail(stdout, stderr);

        Assert.Contains("Multiple Unity instances cannot open the same project.", detail);
        Assert.DoesNotContain("entitlement", detail);
    }

    [Fact]
    public void ExtractFailureDetail_FindsBannerOnStderr()
    {
        var detail = TestCommandService.ExtractFailureDetail(
            stdout: "some ordinary progress output",
            stderr: $"{Banner}:\nsomething went wrong");

        Assert.Contains("something went wrong", detail);
    }

    [Fact]
    public void ExtractFailureDetail_FallsBackToTail_WhenNoBannerPresent()
    {
        var detail = TestCommandService.ExtractFailureDetail(
            stdout: string.Empty,
            stderr: "first line\nsecond line\nlast line");

        Assert.Contains("last line", detail);
    }

    [Fact]
    public void ExtractFailureDetail_ReturnsEmpty_WhenBothStreamsBlank()
    {
        Assert.Equal(string.Empty, TestCommandService.ExtractFailureDetail("   ", "\n\n"));
    }

    [Fact]
    public void ExtractFailureDetail_TruncatesVeryLongOutput()
    {
        var noisy = string.Join("\n", Enumerable.Range(0, 200).Select(i => $"line {i} " + new string('x', 80)));

        var detail = TestCommandService.ExtractFailureDetail(string.Empty, noisy);

        Assert.True(detail.Length <= 640, $"expected a truncated summary, got {detail.Length} chars");
    }

    // ── ContainsFailureWord ──────────────────────────────────────────────────

    [Theory]
    // Real failure messages must still be classified as errors.
    [InlineData("failed to write results", true)]
    [InlineData("compile: failed", true)]
    [InlineData("x failed:", true)]
    [InlineData("unity exited with code 1; the run failed", true)]
    // Test names are not failures. The listing prints one line per case, fully qualified.
    [InlineData("  purchase_grantfailure_returnsgrantfailed (blyume.tests.editmode)", false)]
    [InlineData("  foo.failedstatetests.handles_retry (some.assembly)", false)]
    [InlineData("  applymutations_failed_create_drops_row_with_warning (x)", false)]
    [InlineData("  namespace.failedpurchasetests.pays_nothing (x)", false)]
    [InlineData("test: found 829 test(s)", false)]
    public void ContainsFailureWord_MatchesWholeWordOnly(string normalizedLine, bool expected)
    {
        Assert.Equal(expected, CliAgenticIssueService.ContainsFailureWord(normalizedLine));
    }

    [Fact]
    public void ParseAgenticIssues_DoesNotFlagTestListing()
    {
        // Regression: a successful `test list` reported status=error with exit code 2 because
        // several of the 829 names contain "Failed" inside an identifier.
        var logs = new List<string>
        {
            "test: found 829 test(s)",
            "  Blyume.Tests.ShopPurchaseServiceTests.Purchase_GrantFailure_ReturnsGrantFailed (Blyume.Tests.EditMode)",
            "  Blyume.Tests.FailedStateTests.Handles_Retry (Blyume.Tests.EditMode)",
        };

        var (errors, _, _, _) = CliAgenticIssueService.ParseAgenticIssuesFromLogs(logs);

        Assert.Empty(errors);
    }

    [Fact]
    public void ParseAgenticIssues_StillFlagsRealFailures()
    {
        var logs = new List<string> { "test: Unity exited with code 1: failed to launch" };

        var (errors, _, _, _) = CliAgenticIssueService.ParseAgenticIssuesFromLogs(logs);

        Assert.NotEmpty(errors);
    }

    // ── IsProjectLockHeld ────────────────────────────────────────────────────

    [Fact]
    public void IsProjectLockHeld_FalseWhenNoLockFile()
    {
        using var project = new TempProject();

        Assert.False(TestCommandService.IsProjectLockHeld(project.Path));
    }

    [Fact]
    public void IsProjectLockHeld_FalseForStaleLockFile()
    {
        // A crashed editor leaves the file behind. Treating its mere existence as "locked" would
        // block a run that would otherwise succeed.
        using var project = new TempProject();
        project.WriteLockFile();

        Assert.False(TestCommandService.IsProjectLockHeld(project.Path));
    }

    [Fact]
    public void IsProjectLockHeld_TrueWhileLockFileIsHeldExclusively()
    {
        using var project = new TempProject();
        project.WriteLockFile();

        using var held = File.Open(project.LockFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.True(TestCommandService.IsProjectLockHeld(project.Path));
    }

    // ── ProducedResults ──────────────────────────────────────────────────────

    [Fact]
    public void ProducedResults_FalseWhenFileMissing()
    {
        using var project = new TempProject();
        var missing = Path.Combine(project.Path, "results.xml");

        Assert.False(TestCommandService.ProducedResults(missing, DateTime.UtcNow));
    }

    [Fact]
    public void ProducedResults_FalseForFileFromAnEarlierRun()
    {
        using var project = new TempProject();
        var stale = Path.Combine(project.Path, "results.xml");
        File.WriteAllText(stale, "<test-run />");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-1));

        Assert.False(TestCommandService.ProducedResults(stale, DateTime.UtcNow));
    }

    [Fact]
    public void ProducedResults_TrueForFileWrittenByThisRun()
    {
        using var project = new TempProject();
        var fresh = Path.Combine(project.Path, "results.xml");
        var runStarted = DateTime.UtcNow.AddSeconds(-2);
        File.WriteAllText(fresh, "<test-run />");

        Assert.True(TestCommandService.ProducedResults(fresh, runStarted));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private sealed class TempProject : IDisposable
    {
        public TempProject()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "unifocl-tests-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(System.IO.Path.Combine(Path, "Temp"));
        }

        public string Path { get; }

        public string LockFilePath => System.IO.Path.Combine(Path, "Temp", "UnityLockfile");

        public void WriteLockFile() => File.WriteAllText(LockFilePath, string.Empty);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Best-effort cleanup of a temp directory.
            }
        }
    }
}
