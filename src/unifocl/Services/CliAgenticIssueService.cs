internal static class CliAgenticIssueService
{
    public static (List<AgenticError> Errors, List<AgenticWarning> Warnings, bool RequiresEscalation, string? EscalationEvidence) ParseAgenticIssuesFromLogs(List<string> streamLog)
    {
        var errors = new List<AgenticError>();
        var warnings = new List<AgenticWarning>();
        string? escalationEvidence = null;
        foreach (var raw in streamLog)
        {
            var line = AgenticFormatter.StripMarkup(raw);
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var lower = line.ToLowerInvariant();
            if (IsBenignUnityLicensingLine(lower))
            {
                warnings.Add(new AgenticWarning("W_UNITY_LICENSING", line));
                continue;
            }

            if (IsRecoverableUnityCompileBootstrapLine(lower))
            {
                warnings.Add(new AgenticWarning("W_UNITY_COMPILE_RECOVERABLE", line));
                continue;
            }

            if (LooksLikeEscalationRequired(lower) && string.IsNullOrWhiteSpace(escalationEvidence))
            {
                escalationEvidence = line;
            }

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

        var requiresEscalation = !string.IsNullOrWhiteSpace(escalationEvidence) && errors.Count > 0;
        if (requiresEscalation && !errors.Any(error => error.Code.Equals("E_ESCALATION_REQUIRED", StringComparison.Ordinal)))
        {
            errors.Add(new AgenticError(
                "E_ESCALATION_REQUIRED",
                $"execution likely blocked by sandbox/network restrictions ({escalationEvidence})",
                "rerun the same command with elevated permissions (sandbox_permissions=require_escalated)"));
        }

        return (errors, warnings, requiresEscalation, escalationEvidence);
    }

    private static bool IsBenignUnityLicensingLine(string normalizedLine)
    {
        return normalizedLine.Contains("licensingclient has failed validation; ignoring")
               || normalizedLine.Contains("access token is unavailable; failed to update");
    }

    private static bool IsRecoverableUnityCompileBootstrapLine(string normalizedLine)
    {
        return normalizedLine.Contains("unity:")
               && normalizedLine.Contains("tundra build failed");
    }

    public static int ResolveExitCode(List<AgenticError> errors)
    {
        if (errors.Count == 0)
        {
            return 0;
        }

        if (errors.Any(error => error.Code is "E_ESCALATION_REQUIRED"))
        {
            return 6;
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

    private static bool LooksLikeEscalationRequired(string normalizedLine)
    {
        return normalizedLine.Contains("operation not permitted")
               || normalizedLine.Contains("permission denied")
               || normalizedLine.Contains("access to the path")
               || normalizedLine.Contains("listen eperm")
               || normalizedLine.Contains("sandboxdenied")
               || normalizedLine.Contains("could not lock config file")
               || normalizedLine.Contains("could not resolve host")
               || normalizedLine.Contains("nodename nor servname provided")
               || normalizedLine.Contains("name or service not known")
               || normalizedLine.Contains("temporary failure in name resolution")
               || normalizedLine.Contains("network is unreachable")
               || normalizedLine.Contains("failed to connect to")
               || normalizedLine.Contains("service index for source https://api.nuget.org");
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
