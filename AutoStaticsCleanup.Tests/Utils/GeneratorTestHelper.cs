using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using AutoStaticsCleanup;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace AutoStaticsCleanup.Tests.Utils;

internal static class GeneratorTestHelper
{
    public const string AttributeStub = @"
namespace Unity.Scripting.LifecycleManagement
{
    [System.AttributeUsage(System.AttributeTargets.All)]
    public class AutoStaticsCleanupAttribute : System.Attribute { }
    [System.AttributeUsage(System.AttributeTargets.All)]
    public class NoAutoStaticsCleanupAttribute : System.Attribute { }
}

namespace UnityEngine
{
    public abstract class PlayModeScopeAutoCleanup
    {
        protected PlayModeScopeAutoCleanup() { }
        public abstract void Cleanup();
    }
}

namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
";

    /// <summary>
    /// Returns the concatenation of every generated source tree, separated by
    /// blank lines. Tests that only need to assert on substrings can keep using
    /// this; tests that need per-file inspection should call <see cref="RunFiles"/>.
    /// </summary>
    public static string RunGenerator(string userSource) =>
        string.Join("\n\n", RunFiles(userSource).Files.Select(f => f.Source));

    public static (string Source, ImmutableArray<Diagnostic> Diagnostics) Run(string userSource)
    {
        var r = RunFiles(userSource);
        var combined = string.Join("\n\n", r.Files.Select(f => f.Source));
        return (combined, r.Diagnostics);
    }

    public static (ImmutableArray<(string FileName, string Source)> Files, ImmutableArray<Diagnostic> Diagnostics) RunFiles(string userSource)
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
            CSharpSyntaxTree.ParseText(AttributeStub),
            CSharpSyntaxTree.ParseText(userSource),
        };

        var compilation = CSharpCompilation.Create(
            "TestAssembly", sources, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var generator = new AutoStaticsCleanupGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        driver = driver.RunGenerators(compilation);

        var result = driver.GetRunResult();
        var files = result.Results
            .SelectMany(r => r.GeneratedSources)
            .Select(s => (s.HintName, s.SourceText.ToString()))
            .ToImmutableArray();
        return (files, result.Diagnostics);
    }
}
