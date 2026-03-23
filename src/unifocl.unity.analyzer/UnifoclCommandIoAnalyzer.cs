using System;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace UniFocl.Unity.Analyzer
{
    /// <summary>
    /// Emits <c>UNIFOCL001</c> when a method decorated with <c>[UnifoclCommand]</c>
    /// contains a <c>System.IO</c> write operation that the unifocl Undo-based dry-run
    /// sandbox cannot automatically revert.
    ///
    /// Detected operations:
    ///   • Static method calls on File / Directory with write-indicating names
    ///   • Constructor invocations of StreamWriter, BinaryWriter, FileStream
    ///
    /// Not detected (documented limitation):
    ///   • Writes performed indirectly via helper methods in other classes
    ///   • P/Invoke or native I/O
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class UnifoclCommandIoAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "UNIFOCL001";

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            id: DiagnosticId,
            title: "System.IO write in [UnifoclCommand] method bypasses dry-run sandbox",
            messageFormat: "'{0}' calls '{1}' which writes to the file system and will NOT be reverted " +
                           "by the unifocl Undo-based dry-run. Prefer AssetDatabase APIs, or guard with " +
                           "DaemonDryRunContext.IsActive and skip the write manually.",
            category: "UnifoclDryRun",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "Methods annotated with [UnifoclCommand] are invoked by the unifocl MCP bridge. " +
                         "When the caller requests a dry-run, the bridge wraps the call in a Unity Undo group " +
                         "and reverts it afterwards. However, direct System.IO writes bypass the Undo system " +
                         "and are not reverted. Use AssetDatabase APIs where possible, or check " +
                         "DaemonDryRunContext.IsActive at runtime to suppress the write.");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
            ImmutableArray.Create(Rule);

        // Static methods on System.IO.File or System.IO.Directory whose names suggest writing.
        private static readonly ImmutableHashSet<string> WritingMethodNames =
            ImmutableHashSet.Create(
                StringComparer.Ordinal,
                // File writes
                "WriteAllText", "WriteAllBytes", "WriteAllLines",
                "AppendAllText", "AppendAllLines", "AppendAllBytes",
                "AppendText", "CreateText",
                // File mutations
                "Create", "Delete", "Move", "Copy", "Replace",
                "Encrypt", "Decrypt",
                // Attribute / timestamp mutations
                "SetAttributes", "SetCreationTime", "SetCreationTimeUtc",
                "SetLastWriteTime", "SetLastWriteTimeUtc",
                "SetLastAccessTime", "SetLastAccessTimeUtc",
                // Directory mutations
                "CreateDirectory", "CreateTempSubdirectory", "CreateSymbolicLink");

        // Writable stream/writer constructor types in System.IO.
        private static readonly ImmutableHashSet<string> WritingConstructorTypeNames =
            ImmutableHashSet.Create(
                StringComparer.Ordinal,
                "StreamWriter", "BinaryWriter", "FileStream");

        // Attribute short names that identify an [UnifoclCommand] method.
        private static readonly ImmutableHashSet<string> AttributeShortNames =
            ImmutableHashSet.Create(
                StringComparer.Ordinal,
                "UnifoclCommand", "UnifoclCommandAttribute");

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxNodeAction(AnalyzeMethodDeclaration, SyntaxKind.MethodDeclaration);
        }

        private static void AnalyzeMethodDeclaration(SyntaxNodeAnalysisContext ctx)
        {
            var method = (MethodDeclarationSyntax)ctx.Node;

            if (!HasUnifoclCommandAttribute(method))
            {
                return;
            }

            var methodName = method.Identifier.Text;

            // Walk the method body for System.IO write operations.
            foreach (var node in method.DescendantNodes())
            {
                switch (node)
                {
                    case InvocationExpressionSyntax invocation:
                        CheckInvocation(ctx, invocation, methodName);
                        break;

                    case ObjectCreationExpressionSyntax creation:
                        CheckObjectCreation(ctx, creation, methodName);
                        break;
                }
            }
        }

        private static void CheckInvocation(
            SyntaxNodeAnalysisContext ctx,
            InvocationExpressionSyntax invocation,
            string containingMethodName)
        {
            // Resolve the method symbol via the semantic model.
            if (ctx.SemanticModel.GetSymbolInfo(invocation, ctx.CancellationToken).Symbol
                    is not IMethodSymbol methodSymbol)
            {
                return;
            }

            var containingType = methodSymbol.ContainingType;
            if (containingType is null)
            {
                return;
            }

            // Must be in System.IO namespace.
            if (!IsSystemIoNamespace(containingType.ContainingNamespace))
            {
                return;
            }

            // Must be a write-indicating method name.
            if (!WritingMethodNames.Contains(methodSymbol.Name))
            {
                return;
            }

            var memberDisplay = $"{containingType.Name}.{methodSymbol.Name}";
            ctx.ReportDiagnostic(Diagnostic.Create(
                Rule,
                invocation.GetLocation(),
                containingMethodName,
                memberDisplay));
        }

        private static void CheckObjectCreation(
            SyntaxNodeAnalysisContext ctx,
            ObjectCreationExpressionSyntax creation,
            string containingMethodName)
        {
            if (ctx.SemanticModel.GetTypeInfo(creation, ctx.CancellationToken).Type
                    is not INamedTypeSymbol typeSymbol)
            {
                return;
            }

            // Must be in System.IO namespace.
            if (!IsSystemIoNamespace(typeSymbol.ContainingNamespace))
            {
                return;
            }

            // Must be a known write-capable constructor type.
            if (!WritingConstructorTypeNames.Contains(typeSymbol.Name))
            {
                return;
            }

            ctx.ReportDiagnostic(Diagnostic.Create(
                Rule,
                creation.GetLocation(),
                containingMethodName,
                $"new {typeSymbol.Name}(...)"));
        }

        private static bool IsSystemIoNamespace(INamespaceSymbol? ns)
        {
            // Walk up: must match System.IO (two levels).
            if (ns is null || ns.Name != "IO")
            {
                return false;
            }

            var parent = ns.ContainingNamespace;
            return parent is not null
                && parent.Name == "System"
                && (parent.ContainingNamespace?.IsGlobalNamespace ?? false);
        }

        private static bool HasUnifoclCommandAttribute(MethodDeclarationSyntax method)
        {
            foreach (var attrList in method.AttributeLists)
            {
                foreach (var attr in attrList.Attributes)
                {
                    // Attribute name can be qualified: UniFocl.EditorBridge.UnifoclCommand
                    var name = attr.Name switch
                    {
                        IdentifierNameSyntax id         => id.Identifier.Text,
                        QualifiedNameSyntax qual        => qual.Right.Identifier.Text,
                        AliasQualifiedNameSyntax alias  => alias.Name.Identifier.Text,
                        _                               => string.Empty
                    };

                    if (AttributeShortNames.Contains(name))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
