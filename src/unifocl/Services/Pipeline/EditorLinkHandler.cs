using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

/// <summary>
/// Sends the CLI's Editor requests through the official Unity CLI instead of unifocl's own HTTP bridge.
/// </summary>
/// <remarks>
/// <para>
/// Every <see cref="HttpClient"/> that talks to an Editor is built with <see cref="CreateClient"/>. The
/// handler catches requests to <c>http://127.0.0.1:{port}/...</c> where the port belongs to a known
/// project (<see cref="RegisterProject"/>) and the path has a <c>unifocl.bridge</c> command
/// (<see cref="EditorLinkRoutes"/>). It runs that command over the project's <c>unity mcp</c> session
/// and returns the command's JSON as the response body, so callers see the reply they always got.
/// </para>
/// <para>
/// Everything else goes out as ordinary HTTP: paths with no command yet (<c>/runtime/*</c>, <c>/stop</c>),
/// and every request when <c>UNIFOCL_EDITOR_LINK=http</c>. This goes away with the Editor HTTP bridge.
/// </para>
/// </remarks>
internal sealed class EditorLinkHandler : DelegatingHandler
{
    public const string ModeEnvironmentVariable = "UNIFOCL_EDITOR_LINK";

    private static readonly ConcurrentDictionary<int, string> ProjectsByPort = new();

    private static readonly EditorLinkHandler Shared = new();

    private EditorLinkHandler()
        : base(new SocketsHttpHandler())
    {
    }

    /// <summary>An <see cref="HttpClient"/> whose Editor requests go through the Unity CLI.</summary>
    public static HttpClient CreateClient(TimeSpan? timeout = null)
    {
        var client = new HttpClient(Shared, disposeHandler: false);
        if (timeout is not null)
        {
            client.Timeout = timeout.Value;
        }

        return client;
    }

    /// <summary>Records which project the Editor bridge on <paramref name="port"/> belongs to.</summary>
    public static void RegisterProject(int port, string? projectPath)
    {
        if (port is > 0 and <= 65535 && !string.IsNullOrWhiteSpace(projectPath))
        {
            ProjectsByPort[port] = Path.GetFullPath(projectPath);
        }
    }

    /// <summary>The project registered for <paramref name="port"/>, if any.</summary>
    public static string? TryGetProject(int port) =>
        ProjectsByPort.TryGetValue(port, out var projectPath) ? projectPath : null;

    /// <summary>Whether requests should try the Unity CLI at all (<c>UNIFOCL_EDITOR_LINK</c> is not <c>http</c>).</summary>
    public static bool PipelineEnabled =>
        !string.Equals(Environment.GetEnvironmentVariable(ModeEnvironmentVariable), "http", StringComparison.OrdinalIgnoreCase);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (PipelineEnabled
            && request.RequestUri is { } uri
            && uri.IsLoopback
            && ProjectsByPort.TryGetValue(uri.Port, out var projectPath))
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var route = EditorLinkRoutes.Resolve(request.Method.Method, uri.AbsolutePath, uri.Query.TrimStart('?'), body);
            if (route is not null)
            {
                return await SendThroughPipelineAsync(projectPath, route, cancellationToken);
            }

            if (body is not null)
            {
                // The content stream was consumed above; give the HTTP fallback a fresh copy.
                var mediaType = request.Content!.Headers.ContentType?.MediaType ?? "application/json";
                request.Content = new StringContent(body, Encoding.UTF8, mediaType);
            }
        }

        return await base.SendAsync(request, cancellationToken);
    }

    private static async Task<HttpResponseMessage> SendThroughPipelineAsync(
        string projectPath,
        EditorLinkRoute route,
        CancellationToken cancellationToken)
    {
        var transport = PipelineTransports.For(projectPath, PipelineTransportKind.McpSession, out var error);
        if (transport is null)
        {
            return Reply(HttpStatusCode.BadGateway, error ?? "Unity CLI unavailable");
        }

        var result = await transport.ExecAsync(EditorLinkRoutes.Group, route.Command, route.Args, route.Timeout, cancellationToken);
        if (!result.Ok)
        {
            // The transport or the command failed. The HTTP bridge answered such cases with a 5xx, and
            // callers treat any non-success status as "no response".
            return Reply(HttpStatusCode.BadGateway, result.Error ?? "Pipeline call failed");
        }

        return Reply(HttpStatusCode.OK, FormatReply(route.Reply, result.Result));
    }

    /// <summary>The response body for a command result, in the form the HTTP endpoint used.</summary>
    internal static string FormatReply(EditorLinkReply reply, JsonElement? result)
    {
        return reply switch
        {
            EditorLinkReply.Pong => "PONG",
            EditorLinkReply.Ok => "OK",
            EditorLinkReply.RuntimeId => result is { ValueKind: JsonValueKind.Object } r
                                         && r.TryGetProperty("runtimeId", out var id)
                                         && id.ValueKind == JsonValueKind.String
                ? id.GetString() ?? string.Empty
                : string.Empty,
            _ => result switch
            {
                null => string.Empty,
                { ValueKind: JsonValueKind.String } s => s.GetString() ?? string.Empty,
                { } value => value.GetRawText(),
            },
        };
    }

    private static HttpResponseMessage Reply(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
