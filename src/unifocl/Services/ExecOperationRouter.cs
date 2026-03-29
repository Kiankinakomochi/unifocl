using System.Text.Json;

/// <summary>
/// Routes structured ExecV2 requests through validation → approval → dispatch.
/// Delegates actual execution to ProjectDaemonBridge (existing infrastructure).
/// Test operations are dispatched directly as subprocesses (not via daemon).
/// </summary>
internal sealed class ExecOperationRouter
{
    private readonly ExecCommandRegistry _registry;
    private readonly ExecApprovalService _approval;
    private readonly ExecSessionService _sessions;
    private readonly TestCommandService _testService;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public ExecOperationRouter(ExecCommandRegistry registry, ExecApprovalService approval, ExecSessionService sessions)
    {
        _registry = registry;
        _approval = approval;
        _sessions = sessions;
        _testService = new TestCommandService();
    }

    /// <summary>Synchronous routing path used by the daemon HTTP endpoint.</summary>
    public ExecV2Response Route(ExecV2Request request, ProjectDaemonBridge projectBridge)
        => RouteAsync(request, projectBridge, CancellationToken.None).GetAwaiter().GetResult();

    public async Task<ExecV2Response> RouteAsync(ExecV2Request request, ProjectDaemonBridge projectBridge,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.RequestId))
        {
            return Rejected("", "requestId is required");
        }

        // session.* operations are handled inline — no approval required
        if (request.Operation.StartsWith("session.", StringComparison.OrdinalIgnoreCase))
        {
            return HandleSessionOperation(request);
        }

        // approval.confirm is a special meta-operation that consumes a pending ticket
        if (request.Operation.Equals("approval.confirm", StringComparison.OrdinalIgnoreCase))
        {
            return HandleConfirm(request, projectBridge);
        }

        if (!_registry.IsKnown(request.Operation))
        {
            return Rejected(request.RequestId, $"unknown operation: {request.Operation}");
        }

        _registry.TryGetRisk(request.Operation, out var risk);
        var intent = request.Intent ?? new ExecV2Intent();

        // Touch session activity if provided
        if (!string.IsNullOrWhiteSpace(request.SessionId))
        {
            _sessions.Touch(request.SessionId);
        }

        // If an approval token is already supplied, validate and execute
        if (!string.IsNullOrWhiteSpace(intent.ApprovalToken))
        {
            return ExecuteWithApprovalToken(request, intent, projectBridge);
        }

        // Gate destructive/privileged operations behind approval.
        // safe_write is allowed without approval when the session is trusted.
        var sessionTrusted = _sessions.IsTrusted(request.SessionId);
        if (_approval.IsApprovalRequired(risk, sessionTrusted))
        {
            var argsJson = request.Args.HasValue ? request.Args.Value.GetRawText() : null;
            var token = _approval.CreatePendingApproval(request.RequestId, request.Operation, argsJson);
            return new ExecV2Response(
                ExecV2Status.ApprovalRequired,
                request.RequestId,
                ApprovalToken: token,
                Message: $"operation '{request.Operation}' requires approval (risk: {risk}). " +
                         $"Re-submit with intent.approvalToken set to authorize execution.");
        }

        // test.* operations launch Unity as a subprocess — not dispatched through the daemon bridge.
        if (request.Operation.StartsWith("test.", StringComparison.OrdinalIgnoreCase))
        {
            return await DispatchTestOperationAsync(request, cancellationToken).ConfigureAwait(false);
        }

        return Dispatch(request, intent.DryRun, projectBridge);
    }

    private ExecV2Response HandleSessionOperation(ExecV2Request request)
    {
        switch (request.Operation.ToLowerInvariant())
        {
            case "session.open":
            {
                var projectPath = GetString(request.Args, "projectPath") ?? string.Empty;
                var runtimeType = GetString(request.Args, "runtimeType");
                var session = _sessions.Open(projectPath, runtimeType);
                return new ExecV2Response(
                    ExecV2Status.Completed,
                    request.RequestId,
                    Message: $"session opened",
                    Result: new { session.SessionId, session.ProjectPath, session.RuntimeType, session.Trusted });
            }

            case "session.close":
            {
                var targetId = GetString(request.Args, "sessionId") ?? request.SessionId ?? string.Empty;
                var closed = _sessions.Close(targetId);
                return new ExecV2Response(
                    ExecV2Status.Completed,
                    request.RequestId,
                    Message: closed ? "session closed" : "session not found");
            }

            case "session.status":
            {
                var targetId = GetString(request.Args, "sessionId") ?? request.SessionId ?? string.Empty;
                var session = _sessions.Get(targetId);
                if (session is null)
                {
                    return new ExecV2Response(ExecV2Status.Completed, request.RequestId, Message: "session not found");
                }

                return new ExecV2Response(
                    ExecV2Status.Completed,
                    request.RequestId,
                    Message: "session active",
                    Result: new
                    {
                        session.SessionId,
                        session.ProjectPath,
                        session.RuntimeType,
                        session.Trusted,
                        OpenedAtUtc = session.OpenedAtUtc.ToString("O"),
                        LastActivityUtc = session.LastActivityUtc.ToString("O")
                    });
            }

            default:
                return Rejected(request.RequestId, $"unknown session operation: {request.Operation}");
        }
    }

    private ExecV2Response ExecuteWithApprovalToken(
        ExecV2Request request,
        ExecV2Intent intent,
        ProjectDaemonBridge projectBridge)
    {
        if (!_approval.TryConsume(intent.ApprovalToken!, out var pending))
        {
            return Rejected(request.RequestId, "approval token is invalid or has already been consumed");
        }

        if (!pending!.Operation.Equals(request.Operation, StringComparison.OrdinalIgnoreCase))
        {
            return Rejected(
                request.RequestId,
                $"approval token was issued for operation '{pending.Operation}', not '{request.Operation}'");
        }

        return Dispatch(request, intent.DryRun, projectBridge);
    }

    private ExecV2Response HandleConfirm(ExecV2Request request, ProjectDaemonBridge projectBridge)
    {
        var token = GetString(request.Args, "approvalToken");
        if (string.IsNullOrWhiteSpace(token))
        {
            return Rejected(request.RequestId, "approval.confirm requires args.approvalToken");
        }

        if (!_approval.TryConsume(token, out var pending))
        {
            return Rejected(request.RequestId, "approval token is invalid or expired");
        }

        // Reconstruct the original request from the stored pending approval
        JsonElement? originalArgs = null;
        if (pending!.ArgsJson is not null)
        {
            try
            {
                originalArgs = JsonSerializer.Deserialize<JsonElement>(pending.ArgsJson);
            }
            catch
            {
                originalArgs = null;
            }
        }

        var confirmedRequest = new ExecV2Request(
            pending.RequestId,
            request.SessionId,
            pending.Operation,
            originalArgs,
            new ExecV2Intent(DryRun: false, ApprovalToken: null));

        return Dispatch(confirmedRequest, dryRun: false, projectBridge);
    }

    private ExecV2Response Dispatch(ExecV2Request request, bool dryRun, ProjectDaemonBridge projectBridge)
    {
        if (!_registry.TryBuildProjectRequest(request, dryRun, out var dto, out var validationError))
        {
            return Rejected(request.RequestId, validationError ?? "failed to build project request");
        }

        var serialized = JsonSerializer.Serialize(dto, JsonOptions);
        if (!projectBridge.TryHandle($"PROJECT_CMD {serialized}", out var response))
        {
            return new ExecV2Response(
                ExecV2Status.Failed,
                request.RequestId,
                Message: "project bridge did not handle the operation");
        }

        ProjectCommandResponseDto? result = null;
        try
        {
            result = JsonSerializer.Deserialize<ProjectCommandResponseDto>(response, JsonOptions);
        }
        catch
        {
            // leave result null; treat as failure
        }

        var status = result?.Ok == true ? ExecV2Status.Completed : ExecV2Status.Failed;
        return new ExecV2Response(status, request.RequestId, Message: result?.Message, Result: result);
    }

    /// <summary>
    /// Dispatches test.* operations directly as Unity subprocesses.
    /// Requires a session with a valid projectPath.
    /// </summary>
    private async Task<ExecV2Response> DispatchTestOperationAsync(
        ExecV2Request request,
        CancellationToken cancellationToken)
    {
        var session = _sessions.Get(request.SessionId);
        if (session is null || string.IsNullOrWhiteSpace(session.ProjectPath))
        {
            return Rejected(request.RequestId,
                "test operations require an active session with a projectPath. " +
                "Open a session first with operation 'session.open'.");
        }

        var projectPath = session.ProjectPath;

        switch (request.Operation.ToLowerInvariant())
        {
            case "test.list":
            {
                var (ok, result, error) = await _testService.ExecListAsync(projectPath, cancellationToken)
                    .ConfigureAwait(false);
                return ok
                    ? new ExecV2Response(ExecV2Status.Completed, request.RequestId, Result: result)
                    : new ExecV2Response(ExecV2Status.Failed, request.RequestId, Message: error);
            }

            case "test.run":
            {
                var platformRaw = GetString(request.Args, "platform") ?? "editmode";
                var timeoutSecs = request.Args.HasValue
                    && request.Args.Value.TryGetProperty("timeoutSeconds", out var tsProp)
                    && tsProp.ValueKind == JsonValueKind.Number
                    ? tsProp.GetInt32()
                    : 0;

                var platform = platformRaw.Equals("playmode", StringComparison.OrdinalIgnoreCase)
                    ? TestPlatform.PlayMode
                    : TestPlatform.EditMode;

                var timeout = timeoutSecs > 0
                    ? TimeSpan.FromSeconds(timeoutSecs)
                    : platform == TestPlatform.PlayMode
                        ? TimeSpan.FromMinutes(30)
                        : TimeSpan.FromMinutes(10);

                var (ok, result, error) = await _testService.ExecRunAsync(
                    projectPath, platform, timeout, cancellationToken).ConfigureAwait(false);
                return ok
                    ? new ExecV2Response(ExecV2Status.Completed, request.RequestId, Result: result)
                    : new ExecV2Response(ExecV2Status.Failed, request.RequestId, Message: error);
            }

            default:
                return Rejected(request.RequestId, $"unknown test operation: {request.Operation}");
        }
    }

    private static ExecV2Response Rejected(string requestId, string message)
        => new(ExecV2Status.Rejected, requestId, Message: message);

    private static string? GetString(JsonElement? element, string key)
    {
        if (element is null || element.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return element.Value.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }
}
