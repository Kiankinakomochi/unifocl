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
/// The Editor has no HTTP listener. A request to a registered port whose path has no command gets a
/// 404, as the old bridge answered unknown paths. Requests to other ports go out as ordinary HTTP: that
/// is the CLI's own background service (<c>--daemon-service</c>), not an Editor.
/// </para>
/// </remarks>
internal sealed class EditorLinkHandler : DelegatingHandler
{
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

    /// <summary>
    /// Records which project <paramref name="port"/> stands for. The port no longer carries traffic: it is
    /// the id the CLI's session and daemon records use for a project's Editor link.
    /// </summary>
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

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is not { IsLoopback: true } uri
            || !ProjectsByPort.TryGetValue(uri.Port, out var projectPath))
        {
            return await base.SendAsync(request, cancellationToken);
        }

        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var route = EditorLinkRoutes.Resolve(request.Method.Method, uri.AbsolutePath, uri.Query.TrimStart('?'), body);
        return route is null
            ? Reply(HttpStatusCode.NotFound, "ERR")
            : await SendThroughPipelineAsync(projectPath, route, cancellationToken);
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
