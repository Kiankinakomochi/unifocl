using System.Text.Json.Serialization;

/// <summary>
/// A single test case entry returned by test.list.
/// </summary>
internal sealed record TestCaseEntry(string TestName, string Assembly);

/// <summary>
/// A single test failure captured from NUnit XML output.
/// </summary>
internal sealed record TestFailureEntry(
    string TestName,
    string Message,
    string StackTrace,
    double DurationMs);

/// <summary>
/// The platform targeted by a test run.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum TestPlatform
{
    EditMode,
    PlayMode
}

/// <summary>
/// Output contract for test.run (editmode and playmode).
/// </summary>
internal sealed record TestRunResult(
    int Total,
    int Passed,
    int Failed,
    int Skipped,
    double DurationMs,
    string ArtifactsPath,
    List<TestFailureEntry> Failures);
