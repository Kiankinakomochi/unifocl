using System.Text.Json;

/// <summary>
/// Routes structured ExecV2 requests through validation → approval → dispatch.
/// Delegates actual execution to ProjectDaemonBridge (existing infrastructure).
/// </summary>
internal sealed class ExecOperationRouter
{
    private readonly ExecCommandRegistry _registry;
    private readonly ExecApprovalService _approval;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public ExecOperationRouter(ExecCommandRegistry registry, ExecApprovalService approval)
    {
        _registry = registry;
        _approval = approval;
    }

    public ExecV2Response Route(ExecV2Request request, ProjectDaemonBridge projectBridge)
    {
        if (string.IsNullOrWhiteSpace(request.RequestId))
        {
            return Rejected("", "requestId is required");
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

        // If an approval token is already supplied, validate and execute
        if (!string.IsNullOrWhiteSpace(intent.ApprovalToken))
        {
            return ExecuteWithApprovalToken(request, intent, projectBridge);
        }

        // Gate destructive/privileged operations behind approval
        if (_approval.IsApprovalRequired(risk))
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

        return Dispatch(request, intent.DryRun, projectBridge);
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
