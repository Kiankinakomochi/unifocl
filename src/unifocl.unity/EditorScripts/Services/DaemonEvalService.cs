#if UNITY_EDITOR
using System;
using System.CodeDom.Compiler;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CSharp;
using UnityEditor;
using UnityEngine;

namespace UniFocl.EditorBridge
{
    /// <summary>
    /// Compiles and executes arbitrary C# code in the Unity Editor context.
    /// Supports dry-run execution via the same Undo-group sandbox as <see cref="DaemonCustomToolService"/>.
    /// </summary>
    internal static class DaemonEvalService
    {
        private const string EntryType = "__EvalEntry__";
        private const string EntryMethod = "Run";

        public static string Execute(ProjectCommandRequest request, bool isDryRun)
        {
            EvalRequestPayload payload;
            try
            {
                payload = JsonUtility.FromJson<EvalRequestPayload>(request.content);
                if (payload is null)
                {
                    payload = new EvalRequestPayload();
                }
            }
            catch
            {
                payload = new EvalRequestPayload();
            }

            if (string.IsNullOrWhiteSpace(payload.code))
            {
                return ErrorResponse("eval requires a code expression");
            }

            var (method, compileError) = Compile(payload.code, payload.declarations);
            if (method is null)
            {
                return ErrorResponse("compilation failed:\n" + compileError);
            }

            using var cts = payload.timeoutMs > 0
                ? new CancellationTokenSource(payload.timeoutMs)
                : new CancellationTokenSource();

            return isDryRun
                ? InvokeWithDryRun(method, cts.Token)
                : InvokeDirect(method, cts.Token);
        }

        private static string InvokeDirect(MethodInfo method, CancellationToken cancellationToken)
        {
            try
            {
                var raw = method.Invoke(null, new object[] { cancellationToken });
                var result = UnwrapTask(raw);
                return JsonUtility.ToJson(new ProjectCommandResponse
                {
                    ok = true,
                    kind = "eval",
                    content = SerializeResult(result)
                });
            }
            catch (TargetInvocationException tie)
            {
                var msg = tie.InnerException?.Message ?? tie.Message;
                return ErrorResponse($"eval execution failed: {msg}");
            }
            catch (Exception ex)
            {
                return ErrorResponse($"eval execution failed: {ex.Message}");
            }
        }

        private static string InvokeWithDryRun(MethodInfo method, CancellationToken cancellationToken)
        {
            var undoGroupBefore = Undo.GetCurrentGroup();
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("[unifocl dry-run] eval");

            IDisposable dryRunScope = DaemonDryRunContext.Enter();
            string serializedResult = "null";

            try
            {
                var raw = method.Invoke(null, new object[] { cancellationToken });
                var result = UnwrapTask(raw);
                serializedResult = SerializeResult(result);
            }
            catch (TargetInvocationException tie)
            {
                var msg = tie.InnerException?.Message ?? tie.Message;
                Debug.LogWarning($"[unifocl] eval dry-run threw: {msg}");

                Undo.RevertAllDownToGroup(undoGroupBefore);
                AssetDatabase.Refresh();
                dryRunScope.Dispose();
                return ErrorResponse($"eval execution failed during dry-run: {msg}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[unifocl] eval dry-run threw: {ex.Message}");

                Undo.RevertAllDownToGroup(undoGroupBefore);
                AssetDatabase.Refresh();
                dryRunScope.Dispose();
                return ErrorResponse($"eval execution failed during dry-run: {ex.Message}");
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
                content = serializedResult
            });
        }

        private static (MethodInfo method, string error) Compile(string code, string declarations)
        {
            var isAsync = code.Contains("await ");
            var returnType = isAsync ? "async System.Threading.Tasks.Task<object>" : "object";
            var source = $@"
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEditor;
{declarations ?? string.Empty}
public static class {EntryType} {{
    public static {returnType} {EntryMethod}(CancellationToken cancellationToken) {{
        {code}
    }}
}}";

            var provider = new CSharpCodeProvider();
            var parameters = new CompilerParameters
            {
                GenerateInMemory = true,
                GenerateExecutable = false
            };

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (asm.IsDynamic || string.IsNullOrEmpty(asm.Location))
                    {
                        continue;
                    }

                    parameters.ReferencedAssemblies.Add(asm.Location);
                }
                catch
                {
                    // Skip assemblies that can't provide their location
                }
            }

            var results = provider.CompileAssemblyFromSource(parameters, source);
            if (results.Errors.HasErrors)
            {
                var errors = string.Join("\n", results.Errors.Cast<CompilerError>()
                    .Where(e => !e.IsWarning)
                    .Select(e => $"  line {e.Line}: {e.ErrorText}"));
                return (null, errors);
            }

            var type = results.CompiledAssembly.GetType(EntryType);
            var method = type?.GetMethod(EntryMethod, BindingFlags.Public | BindingFlags.Static);
            if (method is null)
            {
                return (null, $"could not find {EntryType}.{EntryMethod} in compiled assembly");
            }

            return (method, null);
        }

        private static object UnwrapTask(object raw)
        {
            if (raw is Task task)
            {
                // Block on the task (we're on the main thread anyway)
                task.GetAwaiter().GetResult();

                // If it's Task<T>, extract .Result
                var taskType = raw.GetType();
                if (taskType.IsGenericType)
                {
                    var resultProp = taskType.GetProperty("Result");
                    return resultProp?.GetValue(raw);
                }

                return null;
            }

            return raw;
        }

        private static string SerializeResult(object obj)
        {
            if (obj is null)
            {
                return "null";
            }

            if (obj is string s)
            {
                return s;
            }

            if (obj is UnityEngine.Object uo)
            {
                return EditorJsonUtility.ToJson(uo, prettyPrint: true);
            }

            if (obj.GetType().GetCustomAttribute<SerializableAttribute>() is not null)
            {
                try
                {
                    return JsonUtility.ToJson(obj, prettyPrint: true);
                }
                catch
                {
                    // Fall through to ToString
                }
            }

            if (obj is IEnumerable enumerable and not string)
            {
                var items = enumerable.Cast<object>().Select(SerializeResult);
                return "[" + string.Join(",", items) + "]";
            }

            return obj.ToString();
        }

        private static string ErrorResponse(string message)
        {
            return JsonUtility.ToJson(new ProjectCommandResponse
            {
                ok = false,
                message = message,
                kind = "eval"
            });
        }
    }
}
#endif
