using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// Keeps one <c>unity mcp</c> process per project and sends Pipeline commands to it as MCP
/// <c>tools/call</c> requests over stdio. The Unity CLI starts once per session, so a call costs
/// roughly what the command itself costs (milliseconds) instead of a process start.
/// </summary>
/// <remarks>
/// The server runs with <c>--tool-groups</c>: one tool per command group, named after the group with
/// its dots replaced by underscores (<c>unifocl.tool.spike</c> is <c>unifocl_tool_spike</c>), and
/// called with <c>{ command, args }</c>. If the process dies it is restarted on the next call.
/// </remarks>
internal sealed class UnityMcpTransport : IPipelineTransport
{
    private const string ProtocolVersion = "2025-06-18";
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);

    private readonly string _unityCli;
    private readonly string _projectPath;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonObject>> _pending = new();
    private Process? _process;
    private long _nextId;
    private bool _disposed;

    public UnityMcpTransport(string unityCli, string projectPath)
    {
        _unityCli = unityCli;
        _projectPath = projectPath;
    }

    public async Task<PipelineCallResult> ExecAsync(
        string group,
        string name,
        IReadOnlyDictionary<string, object?>? args = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            await EnsureStartedAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return PipelineCallResult.Failure($"could not start `unity mcp`: {ex.Message}");
        }

        var parameters = new JsonObject
        {
            ["name"] = ToolName(group),
            ["arguments"] = BuildToolArguments(name, args),
        };

        JsonObject response;
        try
        {
            response = await RequestAsync("tools/call", parameters, timeout ?? DefaultTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            return PipelineCallResult.Failure(
                $"`{group} {name}` did not answer within {(timeout ?? DefaultTimeout).TotalSeconds:0} s.");
        }
        catch (IOException ex)
        {
            return PipelineCallResult.Failure($"`unity mcp` stopped while running `{group} {name}`: {ex.Message}");
        }

        return ParseToolResponse(response);
    }

    /// <summary>The MCP tool that carries a command group under <c>--tool-groups</c>.</summary>
    internal static string ToolName(string group) => group.Replace('.', '_');

    internal static JsonObject BuildToolArguments(string name, IReadOnlyDictionary<string, object?>? args)
    {
        var argsObject = new JsonObject();
        if (args is not null)
        {
            foreach (var (key, value) in args)
            {
                if (value is not null)
                {
                    argsObject[key] = JsonSerializer.SerializeToNode(value);
                }
            }
        }

        return new JsonObject { ["command"] = name, ["args"] = argsObject };
    }

    /// <summary>
    /// A JSON-RPC response to <c>tools/call</c> as a call result. The command's output is the text of
    /// the first content item. <c>isError</c> marks a failure, and a JSON-RPC error means the request
    /// itself was refused.
    /// </summary>
    internal static PipelineCallResult ParseToolResponse(JsonObject response)
    {
        if (response["error"] is JsonObject rpcError)
        {
            return PipelineCallResult.Failure(rpcError["message"]?.GetValue<string>() ?? "MCP request failed");
        }

        if (response["result"] is not JsonObject result)
        {
            return PipelineCallResult.Failure("MCP response carried no result");
        }

        var text = string.Empty;
        if (result["content"] is JsonArray content)
        {
            foreach (var item in content)
            {
                if (item is JsonObject part && part["type"]?.GetValue<string>() == "text")
                {
                    text = part["text"]?.GetValue<string>() ?? string.Empty;
                    break;
                }
            }
        }

        var isError = result["isError"] is JsonValue flag && flag.TryGetValue<bool>(out var b) && b;
        return isError
            ? PipelineCallResult.Failure(PipelineOutput.ParseError(text, "command failed"), exitCode: null)
            : PipelineCallResult.Success(PipelineOutput.ParseResult(text), exitCode: null);
    }

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_process is { HasExited: false })
        {
            return;
        }

        await _startGate.WaitAsync(cancellationToken);
        try
        {
            if (_process is { HasExited: false })
            {
                return;
            }

            _process?.Dispose();
            FailPending(new IOException("the `unity mcp` process exited"));

            var startInfo = new ProcessStartInfo(_unityCli)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in new[] { "mcp", "--project-path", _projectPath, "--tool-groups", "--no-banner", "--non-interactive" })
            {
                startInfo.ArgumentList.Add(arg);
            }

            startInfo.Environment["UNITY_NO_BANNER"] = "1";
            startInfo.Environment["NO_COLOR"] = "1";

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.Start();
            _process = process;
            _ = Task.Run(() => ReadLoopAsync(process));
            _ = Task.Run(() => DrainAsync(process.StandardError));

            await RequestAsync(
                "initialize",
                new JsonObject
                {
                    ["protocolVersion"] = ProtocolVersion,
                    ["capabilities"] = new JsonObject(),
                    ["clientInfo"] = new JsonObject { ["name"] = "unifocl", ["version"] = CliVersion.SemVer },
                },
                StartTimeout,
                cancellationToken);
            await WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }, cancellationToken);
        }
        finally
        {
            _startGate.Release();
        }
    }

    private async Task<JsonObject> RequestAsync(
        string method,
        JsonObject parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            await WriteAsync(
                new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters },
                cancellationToken);
            return await completion.Task.WaitAsync(timeout, cancellationToken);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task WriteAsync(JsonObject message, CancellationToken cancellationToken)
    {
        var process = _process ?? throw new IOException("`unity mcp` is not running");
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await process.StandardInput.WriteLineAsync(message.ToJsonString().AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReadLoopAsync(Process process)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                JsonObject? message;
                try
                {
                    message = JsonNode.Parse(line) as JsonObject;
                }
                catch (JsonException)
                {
                    continue;
                }

                // Notifications (tools/list_changed and the like) carry no id and need no answer.
                if (message?["id"] is JsonValue idValue
                    && idValue.TryGetValue<long>(out var id)
                    && _pending.TryGetValue(id, out var completion))
                {
                    completion.TrySetResult(message);
                }
            }
        }
        catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException)
        {
            // The process went away; FailPending below reports it to every waiting call.
        }

        FailPending(new IOException("the `unity mcp` process exited"));
    }

    private static async Task DrainAsync(StreamReader stderr)
    {
        try
        {
            while (await stderr.ReadLineAsync() is not null)
            {
                // Discard: keeps the pipe from filling and blocking the process.
            }
        }
        catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException)
        {
        }
    }

    private void FailPending(Exception reason)
    {
        foreach (var completion in _pending.Values)
        {
            completion.TrySetException(reason);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var process = _process;
        _process = null;
        if (process is not null)
        {
            try
            {
                // Closing stdin is how an MCP stdio server is told to exit.
                process.StandardInput.Close();
                using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await process.WaitForExitAsync(wait.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException || ex is InvalidOperationException || ex is IOException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
            }

            process.Dispose();
        }

        FailPending(new ObjectDisposedException(nameof(UnityMcpTransport)));
        _startGate.Dispose();
        _writeGate.Dispose();
    }
}
