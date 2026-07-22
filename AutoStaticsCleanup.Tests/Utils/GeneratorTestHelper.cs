using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
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
    public sealed class DelegateAutoCleanup
    {
        private readonly System.Action _cleanup;
        private readonly string _ownerDescription;

        public DelegateAutoCleanup(System.Action cleanup, string ownerDescription = """")
        {
            _cleanup = cleanup;
            _ownerDescription = ownerDescription;
        }

        public void Cleanup() => _cleanup();
        public override string ToString() => _ownerDescription;

        public static DelegateAutoCleanup CreateForPlayMode(System.Action cleanup, string ownerDescription = """")
            => new DelegateAutoCleanup(cleanup, ownerDescription);
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
        string.Join("\n\n", RunFiles(userSource).Select(f => f.Source));

    public static ImmutableArray<(string FileName, string Source)> RunFiles(string userSource)
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
        return result.Results
            .SelectMany(r => r.GeneratedSources)
            .Select(s => (s.HintName, s.SourceText.ToString()))
            .ToImmutableArray();
    }
}
