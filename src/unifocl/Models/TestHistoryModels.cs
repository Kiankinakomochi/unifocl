using System.Text.Json.Serialization;

/// <summary>
/// A single test result entry within a history record (one test case).
/// </summary>
internal sealed record TestResultEntry(
    string TestName,
    TestOutcome Outcome,
    double DurationMs);

/// <summary>Outcome of a single test case within a history record.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum TestOutcome
{
    Passed,
    Failed,
    Skipped
}

/// <summary>
/// One complete test run appended to the persistent history store.
/// </summary>
internal sealed record TestHistoryRecord(
    string RunId,
    string TimestampUtc,
    string Platform,
    int Total,
    int Passed,
    int Failed,
    int Skipped,
    double DurationMs,
    List<TestResultEntry> Results);

/// <summary>
/// A test identified as flaky — mixed Pass/Fail outcomes across history.
/// </summary>
internal sealed record FlakyTestResult(
    string TestName,
    int PassCount,
    int FailCount,
    int TotalRuns,
    double FlakyScore);

/// <summary>
/// Output contract for test.flaky-report.
/// </summary>
internal sealed record FlakyReportResult(
    int RunsAnalyzed,
    int TotalTests,
    List<FlakyTestResult> FlakyTests);
