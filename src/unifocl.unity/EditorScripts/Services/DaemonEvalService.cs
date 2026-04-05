#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace UniFocl.EditorBridge
{
    /// <summary>
    /// Compiles and executes arbitrary C# code in the Unity Editor context.
    /// Uses Unity's <see cref="AssemblyBuilder"/> for compilation so user code
    /// targets the same C# language version as the project's assembly definitions.
    /// Supports dry-run execution via the same Undo-group sandbox as
    /// <see cref="DaemonCustomToolService"/>.
    /// </summary>
    internal static class DaemonEvalService
    {
        private const string MethodName = "Run";
        private const int MaxSerializationDepth = 8;

        private static int _sequence;

        public static async Task<string> ExecuteAsync(ProjectCommandRequest request, bool isDryRun)
        {
            EvalRequestPayload payload;
            try
            {
                payload = JsonUtility.FromJson<EvalRequestPayload>(request.content);
                if (payload is null) payload = new EvalRequestPayload();
            }
            catch
            {
                payload = new EvalRequestPayload();
            }

            if (string.IsNullOrWhiteSpace(payload.code))
            {
                return ErrorResponse("eval requires a code expression");
            }

            var (method, compileError) = await CompileToMethodAsync(payload.code, payload.declarations);
            if (method is null)
            {
                return ErrorResponse("compilation failed:\n" + compileError);
            }

            using var cts = payload.timeoutMs > 0
                ? new CancellationTokenSource(payload.timeoutMs)
                : new CancellationTokenSource();

            return isDryRun
                ? await InvokeWithDryRunAsync(method, cts.Token)
                : await InvokeDirectAsync(method, cts.Token);
        }

        // ── Invocation ──────────────────────────────────────────────────────────

        private static async Task<string> InvokeDirectAsync(MethodInfo method, CancellationToken ct)
        {
            try
            {
                var result = await RunAndAwaitAsync(method, ct);
                return SuccessResponse("eval", Serialize(result));
            }
            catch (TargetInvocationException tie)
            {
                var inner = tie.InnerException ?? tie;
                return ErrorResponse($"eval execution failed: {inner.Message}\n{inner.StackTrace}");
            }
            catch (Exception ex)
            {
                return ErrorResponse($"eval execution failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Executes inside a Unity Undo-group sandbox, then reverts all
        /// Undo-tracked changes. Same pattern as
        /// <see cref="DaemonCustomToolService"/> InvokeWithDryRun.
        /// Raw <c>System.IO</c> writes are NOT reverted (documented limitation).
        /// </summary>
        private static async Task<string> InvokeWithDryRunAsync(MethodInfo method, CancellationToken ct)
        {
            var undoGroupBefore = Undo.GetCurrentGroup();
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("[unifocl dry-run] eval");

            IDisposable dryRunScope = DaemonDryRunContext.Enter();
            string serialized = "null";

            try
            {
                var result = await RunAndAwaitAsync(method, ct);
                serialized = Serialize(result);
            }
            catch (TargetInvocationException tie)
            {
                var inner = tie.InnerException ?? tie;
                Debug.LogWarning($"[unifocl] eval dry-run threw: {inner.Message}");

                Undo.RevertAllDownToGroup(undoGroupBefore);
                AssetDatabase.Refresh();
                dryRunScope.Dispose();
                return ErrorResponse($"eval execution failed during dry-run: {inner.Message}\n{inner.StackTrace}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[unifocl] eval dry-run threw: {ex.Message}");

                Undo.RevertAllDownToGroup(undoGroupBefore);
                AssetDatabase.Refresh();
                dryRunScope.Dispose();
                return ErrorResponse($"eval execution failed during dry-run: {ex.Message}\n{ex.StackTrace}");
            }
            finally
            {
                dryRunScope.Dispose();
            }

            Undo.RevertAllDownToGroup(undoGroupBefore);
            AssetDatabase.Refresh();

            return JsonUtility.ToJson(new ProjectCommandResponse
            {
                ok = true,
                kind = "eval-dry-run",
                message = "Dry-run completed. Unity Undo-tracked changes were reverted. " +
                          "Raw file I/O (System.IO) is not guaranteed to have been prevented.",
                content = serialized
            });
        }

        /// <summary>
        /// Invoke the compiled method and transparently await if it returns a Task.
        /// Uses <c>ConfigureAwait(false)</c> to avoid posting the continuation
        /// back to the <c>UnitySynchronizationContext</c>, which would deadlock
        /// if the main thread is occupied by the drain loop.
        /// </summary>
        private static async Task<object?> RunAndAwaitAsync(MethodInfo method, CancellationToken ct)
        {
            // Temporarily clear the SynchronizationContext so that any await
            // inside the user's async code does NOT capture the Unity main-thread
            // context. Without this, user awaits (e.g. Task.Delay) try to post
            // their continuation back to the main thread, which is occupied by
            // the durable mutation dispatcher — causing a deadlock.
            var prevCtx = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            try
            {
                var rv = method.Invoke(null, new object[] { ct });

                if (rv is Task<object> typed)
                    return await typed.ConfigureAwait(false);
                if (rv is Task bare)
                {
                    await bare.ConfigureAwait(false);
                    return null;
                }

                return rv;
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(prevCtx);
            }
        }

        // ── Compilation ─────────────────────────────────────────────────────────

        /// <summary>
        /// Writes a wrapper source file, compiles via <see cref="AssemblyBuilder"/>,
        /// loads the resulting assembly from bytes, and returns the entry-point
        /// <see cref="MethodInfo"/>. Temp artefacts are always cleaned up.
        /// </summary>
        private static async Task<(MethodInfo? method, string? error)> CompileToMethodAsync(string code, string declarations)
        {
            var seq = ++_sequence;
            var typeName = $"_UnifoclEval{seq}_";
            var dir = Path.Combine("Temp", "unifocl-eval");
            var srcPath = Path.Combine(dir, $"{typeName}.cs");
            var dllPath = Path.Combine(dir, $"{typeName}.dll");

            Directory.CreateDirectory(dir);

            try
            {
                File.WriteAllText(srcPath, BuildSource(typeName, code, declarations));

                var compileErr = await CompileSourceAsync(srcPath, dllPath);
                if (compileErr is not null)
                    return (null, compileErr);

                var asm = System.Reflection.Assembly.Load(File.ReadAllBytes(dllPath));
                var type = asm.GetType(typeName);
                var mi = type?.GetMethod(MethodName, BindingFlags.Public | BindingFlags.Static);
                return mi is not null
                    ? (mi, null)
                    : (null, $"entry point {typeName}.{MethodName} not found in compiled assembly");
            }
            catch (Exception ex)
            {
                return (null, ex.Message);
            }
            finally
            {
                TryDelete(srcPath);
                TryDelete(dllPath);
            }
        }

        /// <summary>
        /// Build the complete C# source that wraps user code in an async entry point.
        /// The method is always <c>async Task&lt;object&gt;</c> so user code can
        /// freely use <c>await</c> without detection heuristics. Unreachable-code
        /// and async-without-await warnings are suppressed via pragmas.
        /// </summary>
        private static string BuildSource(string typeName, string userCode, string declarations)
        {
            var sb = new StringBuilder(512);
            sb.AppendLine("#pragma warning disable CS0162");
            sb.AppendLine("#pragma warning disable CS1998");
            sb.AppendLine("using System;");
            sb.AppendLine("using System.IO;");
            sb.AppendLine("using System.Linq;");
            sb.AppendLine("using System.Collections;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using System.Text.RegularExpressions;");
            sb.AppendLine("using System.Threading;");
            sb.AppendLine("using System.Threading.Tasks;");
            sb.AppendLine("using UnityEngine;");
            sb.AppendLine("using UnityEditor;");
            sb.AppendLine();

            if (!string.IsNullOrWhiteSpace(declarations))
            {
                sb.AppendLine(declarations);
                sb.AppendLine();
            }

            sb.Append("public static class ").AppendLine(typeName);
            sb.AppendLine("{");
            sb.Append("    public static async Task<object> ").Append(MethodName)
              .AppendLine("(CancellationToken cancellationToken)");
            sb.AppendLine("    {");
            sb.Append("        ").AppendLine(userCode);
            sb.AppendLine("        return null;");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        /// <summary>
        /// Dispatch to the appropriate compiler backend.
        /// Bridge mode (GUI editor) uses <see cref="AssemblyBuilder"/> with an async
        /// await so the main thread yields and Unity can fire <c>buildFinished</c>
        /// on its next update tick.
        /// Host mode (batchmode) shells out to Unity's bundled Roslyn <c>csc</c>
        /// via <see cref="Process"/> because the batchmode main-thread loop does
        /// not pump Unity's internal update and <c>buildFinished</c> never fires.
        /// Returns null on success or a newline-joined error string.
        /// </summary>
        private static Task<string?> CompileSourceAsync(string srcPath, string dllPath)
        {
            return Application.isBatchMode
                ? CompileViaProcessAsync(srcPath, dllPath)
                : CompileViaAssemblyBuilderAsync(srcPath, dllPath);
        }

        // ── AssemblyBuilder path (bridge mode) ─────────────────────────────────

        private static async Task<string?> CompileViaAssemblyBuilderAsync(string srcPath, string dllPath)
        {
            try
            {
                var done = new TaskCompletionSource<CompilerMessage[]>();
#pragma warning disable CS0618 // AssemblyBuilder is obsolete but still functional
                var builder = new AssemblyBuilder(dllPath, srcPath)
                {
                    referencesOptions = ReferencesOptions.UseEngineModules,
                    additionalReferences = CollectReferences()
                };
                builder.buildFinished += (_, msgs) => done.TrySetResult(msgs);
#pragma warning restore CS0618

                if (!builder.Build())
                    return "AssemblyBuilder failed to start — is the editor compiling?";

                var timeout = Task.Delay(TimeSpan.FromSeconds(30));
                var winner = await Task.WhenAny(done.Task, timeout);
                if (winner == timeout)
                    return "compilation timed out (30 s) — AssemblyBuilder.buildFinished was never invoked";

                var messages = done.Task.Result;
                var errors = new StringBuilder();
                foreach (var m in messages)
                {
                    if (m.type != CompilerMessageType.Error) continue;
                    if (errors.Length > 0) errors.AppendLine();
                    errors.Append(m.message);
                }

                return errors.Length > 0 ? errors.ToString() : null;
            }
            catch (Exception ex)
            {
                return $"AssemblyBuilder error: {ex.Message}";
            }
        }

        // ── Process-based path (host / batchmode) ──────────────────────────────

        private static Task<string?> CompileViaProcessAsync(string srcPath, string dllPath)
        {
            try
            {
                var (dotnetPath, cscPath) = ResolveBundledCompiler();
                if (dotnetPath is null)
                    return Task.FromResult<string?>("cannot locate Unity's bundled dotnet/csc — eval is unavailable in batchmode");

                var refs = CollectReferences();
                var sb = new StringBuilder();
                sb.Append("exec ").Append(Quote(cscPath!));
                sb.Append(" -target:library");
                sb.Append(" -out:").Append(Quote(Path.GetFullPath(dllPath)));
                sb.Append(" -nologo -nowarn:CS0162,CS1998");
                foreach (var r in refs)
                    sb.Append(" -reference:").Append(Quote(r));
                sb.Append(' ').Append(Quote(Path.GetFullPath(srcPath)));

                var psi = new ProcessStartInfo
                {
                    FileName = dotnetPath,
                    Arguments = sb.ToString(),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi);
                if (proc is null)
                    return Task.FromResult<string?>("failed to start compiler process");

                var stdout = proc.StandardOutput.ReadToEnd();
                var stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit(30_000);

                if (!proc.HasExited)
                {
                    try { proc.Kill(); } catch { }
                    return Task.FromResult<string?>("compiler process timed out (30 s)");
                }

                if (proc.ExitCode != 0)
                {
                    var output = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                    return Task.FromResult<string?>($"compilation failed (exit {proc.ExitCode}):\n{output.Trim()}");
                }

                return Task.FromResult<string?>(null);
            }
            catch (Exception ex)
            {
                return Task.FromResult<string?>($"process compiler error: {ex.Message}");
            }
        }

        private static (string? dotnet, string? csc) ResolveBundledCompiler()
        {
            var contentsPath = EditorApplication.applicationContentsPath; // .../Unity.app/Contents
            var scriptingRoot = Path.Combine(contentsPath, "Resources", "Scripting");

            var dotnet = Path.Combine(scriptingRoot, "NetCoreRuntime", "dotnet");
            var csc = Path.Combine(scriptingRoot, "DotNetSdkRoslyn", "csc.dll");

            // Windows: dotnet.exe
            if (!File.Exists(dotnet) && File.Exists(dotnet + ".exe"))
                dotnet += ".exe";

            if (File.Exists(dotnet) && File.Exists(csc))
                return (dotnet, csc);

            return (null, null);
        }

        private static string Quote(string path)
        {
            return '"' + path.Replace("\"", "\\\"") + '"';
        }

        /// <summary>
        /// Gather all loadable assembly paths from the current AppDomain,
        /// de-duplicated by assembly name. Skips dynamic assemblies, assemblies
        /// without a location, and our own temporary eval DLLs.
        /// </summary>
        private static string[] CollectReferences()
        {
            var evalDir = Path.GetFullPath(Path.Combine("Temp", "unifocl-eval"));
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var paths = new List<string>();

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.IsDynamic) continue;
                try
                {
                    var loc = asm.Location;
                    if (string.IsNullOrEmpty(loc) || !File.Exists(loc)) continue;
                    // Skip our own eval temp DLLs to avoid stale references
                    if (Path.GetFullPath(loc).StartsWith(evalDir, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!names.Add(asm.GetName().Name)) continue;
                    paths.Add(loc);
                }
                catch { }
            }

            return paths.ToArray();
        }

        // ── Serialization ───────────────────────────────────────────────────────
        //
        // Multi-strategy serializer that picks the best representation:
        //   1. null / primitives / string  → literal values
        //   2. UnityEngine.Object          → EditorJsonUtility (full Unity serialization)
        //   3. [Serializable] types        → JsonUtility (Unity's fast path)
        //   4. Everything else             → depth-limited reflection walk
        //
        // Unlike a simple ToString() fallback, this produces machine-parseable
        // JSON for structs, arrays, dictionaries, and nested objects.

        private static string Serialize(object? obj)
        {
            if (obj is null) return "null";
            if (obj is string s) return s;

            // Primitives and simple value types — go straight to the reflection walker
            // which handles int, float, bool, enum, etc. correctly.
            // Must come before the [Serializable] check because System.Int32 et al.
            // are [Serializable] but JsonUtility.ToJson produces "{}" for them.
            var type = obj.GetType();
            if (type.IsPrimitive || type.IsEnum || obj is decimal)
            {
                var sb = new StringBuilder(32);
                WriteValue(sb, obj, 0);
                return sb.ToString();
            }

            // Collections and dictionaries — use reflection walker which handles
            // them as JSON arrays/objects. Must come before [Serializable] because
            // System.Int32[], List<T>, Dictionary<K,V> etc. are [Serializable] but
            // JsonUtility.ToJson produces "{}" for them.
            if (obj is IDictionary || obj is IEnumerable)
            {
                var sb = new StringBuilder(256);
                WriteValue(sb, obj, 0);
                return sb.ToString();
            }

            // Unity objects get full editor serialization (components, assets, etc.)
            if (obj is UnityEngine.Object uo)
                return EditorJsonUtility.ToJson(uo, prettyPrint: true);

            // Types marked [Serializable] — use Unity's fast JsonUtility path
            if (type.IsDefined(typeof(SerializableAttribute), false))
            {
                try { return JsonUtility.ToJson(obj, prettyPrint: true); }
                catch { /* fall through to reflection */ }
            }

            // Structured reflection walk for everything else
            var sb2 = new StringBuilder(256);
            WriteValue(sb2, obj, 0);
            return sb2.ToString();
        }

        private static void WriteValue(StringBuilder sb, object obj, int depth)
        {
            if (obj is null) { sb.Append("null"); return; }
            if (depth > MaxSerializationDepth) { sb.Append("\"<max depth>\""); return; }

            switch (obj)
            {
                case string str:
                    WriteQuoted(sb, str);
                    return;
                case bool b:
                    sb.Append(b ? "true" : "false");
                    return;
                case float f:
                    sb.Append(f.ToString("G9"));
                    return;
                case double d:
                    sb.Append(d.ToString("G17"));
                    return;
                case decimal m:
                    sb.Append(m.ToString("G"));
                    return;
                case char c:
                    WriteQuoted(sb, c.ToString());
                    return;
            }

            var type = obj.GetType();

            if (type.IsPrimitive) { sb.Append(obj); return; }            // int, long, byte, etc.
            if (type.IsEnum) { WriteQuoted(sb, obj.ToString()); return; }

            // IDictionary — serialize as JSON object with string keys
            if (obj is IDictionary dict)
            {
                sb.Append('{');
                var first = true;
                foreach (DictionaryEntry entry in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteQuoted(sb, entry.Key?.ToString() ?? "null");
                    sb.Append(':');
                    WriteValue(sb, entry.Value, depth + 1);
                }
                sb.Append('}');
                return;
            }

            // IEnumerable (arrays, lists, hashsets, etc.) — serialize as JSON array
            if (obj is IEnumerable enumerable)
            {
                sb.Append('[');
                var first = true;
                foreach (var item in enumerable)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteValue(sb, item, depth + 1);
                }
                sb.Append(']');
                return;
            }

            // Structured object — serialize public fields and readable properties
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
            var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);

            if (fields.Length == 0 && props.Length == 0)
            {
                WriteQuoted(sb, obj.ToString());
                return;
            }

            sb.Append('{');
            var isFirst = true;

            foreach (var field in fields)
            {
                if (!isFirst) sb.Append(',');
                isFirst = false;
                WriteQuoted(sb, field.Name);
                sb.Append(':');
                WriteValue(sb, field.GetValue(obj), depth + 1);
            }

            foreach (var prop in props)
            {
                if (!prop.CanRead || prop.GetIndexParameters().Length > 0) continue;
                try
                {
                    var val = prop.GetValue(obj);
                    if (!isFirst) sb.Append(',');
                    isFirst = false;
                    WriteQuoted(sb, prop.Name);
                    sb.Append(':');
                    WriteValue(sb, val, depth + 1);
                }
                catch
                {
                    // Skip properties that throw on access
                }
            }

            sb.Append('}');
        }

        /// <summary>Write a JSON-escaped quoted string.</summary>
        private static void WriteQuoted(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"':  sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append($"\\u{(int)c:x4}");
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ── Helpers ─────────────────────────────────────────────────────────────

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static string SuccessResponse(string kind, string content)
        {
            return JsonUtility.ToJson(new ProjectCommandResponse { ok = true, kind = kind, content = content });
        }

        private static string ErrorResponse(string message)
        {
            return JsonUtility.ToJson(new ProjectCommandResponse { ok = false, message = message, kind = "eval" });
        }
    }
}
#endif
