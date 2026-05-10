using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace AutoStaticsCleanup.Tests.Utils;

internal static class CodeFixTestHelper
{
    /// <summary>
    /// Runs the analyzer on <paramref name="userSource"/>, finds the first
    /// diagnostic matching <paramref name="diagnosticId"/>, asks the code fix
    /// provider to register a fix, applies it, and returns the resulting
    /// document text.
    /// </summary>
    public static string ApplyFirstFix(string userSource, string diagnosticId)
    {
        var trustedAssemblies = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        var refs = trustedAssemblies
            .Split(Path.PathSeparator)
            .Where(p => !string.IsNullOrEmpty(p) && File.Exists(p))
            .Select(p => MetadataReference.CreateFromFile(p))
            .Cast<MetadataReference>()
            .ToArray();

        var stubTree = CSharpSyntaxTree.ParseText(GeneratorTestHelper.AttributeStub);
        var userTree = CSharpSyntaxTree.ParseText(userSource);

        var compilation = CSharpCompilation.Create(
            "TestAssembly",
            new[] { stubTree, userTree },
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var analyzers = ImmutableArray.Create<DiagnosticAnalyzer>(new AutoStaticsCleanupAnalyzer());
        var withAnalyzers = compilation.WithAnalyzers(analyzers);
        var diagnostics = withAnalyzers.GetAnalyzerDiagnosticsAsync(CancellationToken.None).GetAwaiter().GetResult();
        var diagnostic = diagnostics.FirstOrDefault(d => d.Id == diagnosticId);
        if (diagnostic == null)
            throw new InvalidOperationException($"No diagnostic with id {diagnosticId} produced by analyzer");

        // Diagnostics may target either tree; locate the tree the diagnostic
        // actually points at and feed that to the workspace.
        var targetTree = diagnostic.Location.SourceTree ?? userTree;

        using var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var documentId = DocumentId.CreateNewId(projectId);

        var solution = workspace.CurrentSolution
            .AddProject(projectId, "TestAssembly", "TestAssembly", LanguageNames.CSharp)
            .WithProjectMetadataReferences(projectId, refs)
            .AddDocument(documentId, "User.cs", targetTree.GetText());

        var document = solution.GetDocument(documentId)!;

        var fixer = new AutoStaticsCleanupCodeFixProvider();
        var actions = new List<CodeAction>();
        var ctx = new CodeFixContext(document, diagnostic, (action, _) => actions.Add(action), CancellationToken.None);
        fixer.RegisterCodeFixesAsync(ctx).GetAwaiter().GetResult();

        if (actions.Count == 0)
            throw new InvalidOperationException($"Code fix provider registered no actions for {diagnosticId}");

        var operations = actions[0].GetOperationsAsync(CancellationToken.None).GetAwaiter().GetResult();
        var apply = operations.OfType<ApplyChangesOperation>().First();
        var newDoc = apply.ChangedSolution.GetDocument(documentId)!;
        return newDoc.GetTextAsync().GetAwaiter().GetResult().ToString();
    }
}
