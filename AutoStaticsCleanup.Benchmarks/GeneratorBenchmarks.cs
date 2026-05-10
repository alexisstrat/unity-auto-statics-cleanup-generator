using BenchmarkDotNet.Attributes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace AutoStaticsCleanup.Benchmarks;

/// <summary>
/// Measures generator performance under three conditions:
///   • <see cref="ColdRun"/> — fresh driver, no cache (worst case).
///   • <see cref="IncrementalRunUnrelatedEdit"/> — warm driver, edit to a file
///     that doesn't carry [AutoStaticsCleanup]. Should be near-zero work; if it
///     isn't, an Equals implementation is broken somewhere.
///   • <see cref="IncrementalRunSingleAttributedEdit"/> — warm driver, edit to
///     one attributed file. Only that file's pipeline branch should re-run.
/// </summary>
[MemoryDiagnoser]
public class GeneratorBenchmarks
{
    [Params(1, 10, 100)]
    public int AttributedTypes;

    private CSharpCompilation _coldCompilation = null!;
    private GeneratorDriver _warmDriver = null!;
    private CSharpCompilation _editedUnrelated = null!;
    private CSharpCompilation _editedAttributed = null!;

    [GlobalSetup]
    public void Setup()
    {
        var refs = ((string)System.AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => !string.IsNullOrEmpty(p) && File.Exists(p))
            .Select(p => MetadataReference.CreateFromFile(p))
            .Cast<MetadataReference>()
            .ToArray();

        var trees = new List<SyntaxTree>
        {
            CSharpSyntaxTree.ParseText(StubsSource, path: "Stubs.cs"),
            CSharpSyntaxTree.ParseText("// unrelated bystander", path: "Unrelated.cs"),
        };
        for (var i = 0; i < AttributedTypes; i++)
            trees.Add(CSharpSyntaxTree.ParseText(MakeUserSource(i), path: $"Type{i}.cs"));

        _coldCompilation = CSharpCompilation.Create("Bench", trees, refs);

        var driver = CSharpGeneratorDriver.Create(new AutoStaticsCleanupGenerator());
        _warmDriver = driver.RunGenerators(_coldCompilation);

        // Unrelated edit: append a comment to the bystander tree.
        var unrelatedTree = trees[1];
        var newUnrelated = CSharpSyntaxTree.ParseText(
            unrelatedTree.ToString() + "\n// edit", path: unrelatedTree.FilePath);
        _editedUnrelated = _coldCompilation.ReplaceSyntaxTree(unrelatedTree, newUnrelated);

        // Attributed edit: rename a member in the first attributed file.
        var firstAttributed = trees[2];
        var newAttributed = CSharpSyntaxTree.ParseText(
            firstAttributed.ToString().Replace("public static int A;", "public static int Renamed;"),
            path: firstAttributed.FilePath);
        _editedAttributed = _coldCompilation.ReplaceSyntaxTree(firstAttributed, newAttributed);
    }

    [Benchmark(Description = "Cold run (no cache)")]
    public object ColdRun()
    {
        var driver = CSharpGeneratorDriver.Create(new AutoStaticsCleanupGenerator());
        return driver.RunGenerators(_coldCompilation).GetRunResult();
    }

    [Benchmark(Description = "Incremental: edit unrelated file")]
    public object IncrementalRunUnrelatedEdit()
        => _warmDriver.RunGenerators(_editedUnrelated).GetRunResult();

    [Benchmark(Description = "Incremental: rename a member in one attributed file")]
    public object IncrementalRunSingleAttributedEdit()
        => _warmDriver.RunGenerators(_editedAttributed).GetRunResult();

    private static string MakeUserSource(int i) => $@"
using System;
using System.Collections.Generic;
using Unity.Scripting.LifecycleManagement;

namespace Bench.Ns{i}
{{
    public partial class Type{i}
    {{
        [AutoStaticsCleanup] public static int A;
        [AutoStaticsCleanup] public static string B = ""hello"";
        [AutoStaticsCleanup] public static List<int> C = new();
        [AutoStaticsCleanup] public static Dictionary<string,int> D = new();
        [AutoStaticsCleanup] public static event Action E;
    }}
}}";

    private const string StubsSource = @"
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
";
}
