using Spectre.Console;
using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text;

internal sealed partial class DaemonControlService
{
    /// <summary>
    /// Starts a unifocl CLI daemon service (<c>--daemon-service</c>). Unity Editors are started by
    /// <see cref="EnsureProjectDaemonAsync"/> through the Unity CLI instead.
    /// </summary>
    private async Task<bool> StartDaemonAsync(
        DaemonStartOptions startOptions,
        DaemonRuntime runtime,
        Action<string> log,
        TimeSpan? startupTimeout = null)
    {
        var existing = runtime.GetByPort(startOptions.Port);
        if (existing is not null && ProcessUtil.IsAlive(existing.Pid) && await TrySendControlAsync(existing.Port, "PING", "PONG"))
        {
            log($"[yellow]daemon[/]: port {startOptions.Port} already has a running daemon (pid {existing.Pid})");
            return true;
        }

        if (existing is not null)
        {
            runtime.Remove(startOptions.Port);
        }

        var process = Process.Start(ResolveDaemonLaunch(startOptions));
        if (process is null)
        {
            log("[red]daemon[/]: failed to start daemon process");
            return false;
        }

        var resolvedStartupTimeout = ResolveStartupTimeout(startupTimeout);
        if (!await WaitForDaemonReadyAsync(startOptions.Port, process, resolvedStartupTimeout))
        {
            _lastStartupFailure = new DaemonStartupFailure(
                IsCompileError: false,
                Summary: process.HasExited
                    ? $"process exited with code {process.ExitCode}"
                    : $"daemon did not respond on port {startOptions.Port} within {resolvedStartupTimeout.TotalSeconds:0}s",
                Lines: []);
            log($"[red]daemon[/]: {Markup.Escape(_lastStartupFailure.Summary)}");
            TryTerminateSpawnedProcess(process, log);
            runtime.Remove(startOptions.Port);
            return false;
        }

        _lastStartupFailure = null;
        log($"[green]daemon[/]: started [white]pid={process.Id}[/] [white]port={startOptions.Port}[/]");
        return true;
    }

    public bool TryGetLastStartupFailure(out DaemonStartupFailure? failure)
    {
        failure = _lastStartupFailure;
        return failure is not null;
    }

    private static ProcessStartInfo ResolveDaemonLaunch(DaemonStartOptions options)
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            throw new InvalidOperationException("Unable to resolve current process path for daemon launch.");
        }

        var daemonArgs = new List<string>
        {
            "--daemon-service",
            "--port", options.Port.ToString(),
            "--ttl-seconds", DefaultInactivityTimeoutSeconds.ToString()
        };

        if (options.UnityPath is not null)
        {
            daemonArgs.Add("--unity");
            daemonArgs.Add(options.UnityPath);
        }

        if (options.ProjectPath is not null)
        {
            daemonArgs.Add("--project");
            daemonArgs.Add(options.ProjectPath);
        }

        if (options.Headless)
        {
            daemonArgs.Add("--headless");
        }

        if (options.AllowUnsafe)
        {
            daemonArgs.Add("--allow-unsafe");
        }

        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            // Under the dotnet host the entry assembly sits in AppContext.BaseDirectory. Assembly.Location
            // is avoided because it is empty in single-file builds (IL3000), even though this branch
            // only runs when launched through dotnet.
            var entryAssemblyName = Assembly.GetEntryAssembly()?.GetName().Name
                                    ?? throw new InvalidOperationException("Unable to resolve entry assembly path.");
            var assemblyPath = Path.Combine(AppContext.BaseDirectory, entryAssemblyName + ".dll");
            var psi = new ProcessStartInfo(processPath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add(assemblyPath);
            foreach (var arg in daemonArgs)
            {
                psi.ArgumentList.Add(arg);
            }

            return psi;
        }

        var directPsi = new ProcessStartInfo(processPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            CreateNoWindow = true
        };
        foreach (var arg in daemonArgs)
        {
            directPsi.ArgumentList.Add(arg);
        }

        return directPsi;
    }

    private static void TryTerminateSpawnedProcess(Process process, Action<string> log)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }

            process.Kill(entireProcessTree: true);
            if (!process.WaitForExit(5000))
            {
                log($"[yellow]daemon[/]: failed to terminate stalled daemon pid={process.Id} within 5s");
            }
        }
        catch (Exception ex)
        {
            log($"[yellow]daemon[/]: unable to terminate stalled daemon pid={process.Id} ({Markup.Escape(ex.Message)})");
        }
    }

    private static async Task<bool> WaitForDaemonReadyAsync(int port, Process process, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline && !process.HasExited)
        {
            if (await TrySendControlAsync(port, "PING", "PONG"))
            {
                return true;
            }

            await Task.Delay(180);
        }

        return false;
    }

    /// <summary>
    /// Sends PING, TOUCH or STOP. For a port that belongs to a project these go to its Editor: PING and
    /// TOUCH are the bridge ping, and STOP shuts a batchmode Editor down. Any other port is a CLI daemon
    /// service, which answers over HTTP.
    /// </summary>
    private static async Task<bool> TrySendControlAsync(int port, string request, string expectedResponse)
    {
        if (EditorLinkHandler.TryGetProject(port) is not { } projectPath)
        {
            return await TrySendHttpControlAsync(port, request, expectedResponse);
        }

        return request switch
        {
            "PING" or "TOUCH" => await UnityEditorLifecycle.PingAsync(projectPath, EditorPingTimeout),
            "STOP" => await StopEditorAsync(projectPath, _ => { }),
            _ => false,
        };
    }

    private static async Task<bool> TrySendHttpControlAsync(int port, string request, string expectedResponse)
    {
        var endpoint = request switch
        {
            "PING" => ("GET", $"http://127.0.0.1:{port}/ping"),
            "TOUCH" => ("POST", $"http://127.0.0.1:{port}/touch"),
            "STOP" => ("POST", $"http://127.0.0.1:{port}/stop"),
            _ => default
        };
        if (string.IsNullOrWhiteSpace(endpoint.Item2))
        {
            return false;
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            HttpResponseMessage response;
            if (endpoint.Item1 == "GET")
            {
                response = await Http.GetAsync(endpoint.Item2, cts.Token);
            }
            else
            {
                response = await Http.PostAsync(endpoint.Item2, new StringContent(string.Empty, Encoding.UTF8, "text/plain"), cts.Token);
            }

            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var payload = (await response.Content.ReadAsStringAsync(cts.Token)).Trim();
            return string.Equals(payload, expectedResponse, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    public static async Task<bool> IsProjectCommandEndpointResponsiveAsync(int port, TimeSpan timeout)
    {
        return EditorLinkHandler.TryGetProject(port) is { } projectPath
            ? await UnityEditorLifecycle.PingAsync(projectPath, timeout)
            : await TrySendHttpControlAsync(port, "PING", "PONG");
    }

    private static TimeSpan ResolveStartupTimeout(TimeSpan? requestedTimeout)
    {
        var resolved = requestedTimeout ?? MinimumDaemonStartupTimeout;
        return resolved < MinimumDaemonStartupTimeout ? MinimumDaemonStartupTimeout : resolved;
    }

    private static async Task<int?> ResolveAttachedOnlyPortAsync(CliSessionState session)
    {
        if (GetPort(session) is not int attachedPort)
        {
            return null;
        }

        return await TrySendControlAsync(attachedPort, "PING", "PONG") ? attachedPort : null;
    }
}
