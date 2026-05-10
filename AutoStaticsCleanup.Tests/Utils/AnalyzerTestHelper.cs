using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using AutoStaticsCleanup;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace AutoStaticsCleanup.Tests.Utils;

internal static class AnalyzerTestHelper
{
    /// <summary>
    /// Runs <see cref="AutoStaticsCleanupAnalyzer"/> against
    /// <paramref name="userSource"/> + the attribute stub and returns its
    /// diagnostics (compilation diagnostics like CS-codes are filtered out).
    /// </summary>
    public static ImmutableArray<Diagnostic> Run(string userSource)
    {
        var trustedAssemblies = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        var refs = trustedAssemblies
            .Split(Path.PathSeparator)
            .Where(p => !string.IsNullOrEmpty(p) && File.Exists(p))
            .Select(p => MetadataReference.CreateFromFile(p))
            .Cast<MetadataReference>()
            .ToArray();

        var sources = new[]
        {
            CSharpSyntaxTree.ParseText(GeneratorTestHelper.AttributeStub),
            CSharpSyntaxTree.ParseText(userSource),
        };

        var compilation = CSharpCompilation.Create(
            "TestAssembly", sources, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var analyzers = ImmutableArray.Create<DiagnosticAnalyzer>(new AutoStaticsCleanupAnalyzer());
        var withAnalyzers = compilation.WithAnalyzers(analyzers);
        return withAnalyzers
            .GetAnalyzerDiagnosticsAsync(CancellationToken.None)
            .GetAwaiter().GetResult();
    }
}
