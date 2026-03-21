internal static class CliAgenticIssueService
{
    public static (List<AgenticError> Errors, List<AgenticWarning> Warnings) ParseAgenticIssuesFromLogs(List<string> streamLog)
    {
        var errors = new List<AgenticError>();
        var warnings = new List<AgenticWarning>();
        foreach (var raw in streamLog)
        {
            var line = AgenticFormatter.StripMarkup(raw);
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var lower = line.ToLowerInvariant();
            if (lower.StartsWith("error") || lower.Contains("failed") || lower.StartsWith("x "))
            {
                errors.Add(new AgenticError(GuessErrorCode(lower), line));
                continue;
            }

            if (lower.StartsWith("warning") || lower.StartsWith("note") || lower.Contains("yellow"))
            {
                warnings.Add(new AgenticWarning("W_GENERIC", line));
            }
        }

        return (errors, warnings);
    }

    public static int ResolveExitCode(List<AgenticError> errors)
    {
        if (errors.Count == 0)
        {
            return 0;
        }

        if (errors.Any(error => error.Code is "E_PARSE" or "E_VALIDATION" or "E_MODE_INVALID" or "E_NOT_FOUND" or "E_VCS_SETUP_REQUIRED"))
        {
            return 2;
        }

        if (errors.Any(error => error.Code is "E_TIMEOUT" or "E_UNITY_API"))
        {
            return 3;
        }

        return 4;
    }

    private static string GuessErrorCode(string normalizedLine)
    {
        if (normalizedLine.Contains("usage") || normalizedLine.Contains("invalid"))
        {
            return "E_PARSE";
        }

        if (normalizedLine.Contains("vcs setup", StringComparison.OrdinalIgnoreCase))
        {
            return "E_VCS_SETUP_REQUIRED";
        }

        if (normalizedLine.Contains("open a project first") || normalizedLine.Contains("mode"))
        {
            return "E_MODE_INVALID";
        }

        if (normalizedLine.Contains("not found"))
        {
            return "E_NOT_FOUND";
        }

        if (normalizedLine.Contains("timeout") || normalizedLine.Contains("unreachable"))
        {
            return "E_TIMEOUT";
        }

        if (normalizedLine.Contains("daemon"))
        {
            return "E_UNITY_API";
        }

        return "E_VALIDATION";
    }
}
