using BenchmarkDotNet.Attributes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace AutoStaticsCleanup.Benchmarks;

/// <summary>
/// Measures generator performance under three conditions:
///   • <see cref="ColdRun"/> — fresh driver, no cache (worst case).
///   • <see cref="IncrementalRunUnrelatedEdit"/> — warm driver, edit to a file
///     that doesn't carry [AutoStaticsCleanup].
///   • <see cref="IncrementalRunSingleAttributedEdit"/> — warm driver, edit to
///     one attributed file.
///
/// Expect the incremental runs to cost ~80% of a cold run, not near-zero:
/// ForAttributeWithMetadataName re-runs the extract transform (including
/// CaptureInitializer's semantic-model walks) for every attributed member on
/// every compilation change — that's inherent to Roslyn's incremental model
/// and is the majority of the cost. What the structural equality on
/// ResetEntry/ExtractResult/TypeGroup buys is the *output* side: grouping and
/// GenerateSource stay cached (IncrementalCacheTests proves the stages report
/// Cached/Unchanged). A regression to watch for is the incremental runs
/// approaching 100% of cold, or allocations jumping on the unrelated edit —
/// that's an Equals implementation gone reference-based.
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
        var refs = MetadataReferences();

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

    /// <summary>
    /// Smoke check for the benchmark corpus (run via `-- --verify`): every
    /// source tree must parse clean, the generator must emit one file per
    /// attributed type (it skips unfit shapes silently, so a corpus typo
    /// would otherwise shrink the workload without failing anything), and
    /// the combined user + stub + generated compilation must have no errors.
    /// </summary>
    public static int VerifyCorpus()
    {
        const int fileCount = 3;
        const int typesPerFile = 2; // Type{i} (member-level) + Scanned{i} (class-level)

        var trees = new List<SyntaxTree> { CSharpSyntaxTree.ParseText(StubsSource, path: "Stubs.cs") };
        for (var i = 0; i < fileCount; i++)
            trees.Add(CSharpSyntaxTree.ParseText(MakeUserSource(i), path: $"Type{i}.cs"));

        var parseErrors = trees
            .SelectMany(t => t.GetDiagnostics())
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        if (parseErrors.Count > 0)
        {
            Console.Error.WriteLine($"corpus has {parseErrors.Count} parse error(s):");
            foreach (var e in parseErrors) Console.Error.WriteLine($"  {e}");
            return 1;
        }

        var compilation = CSharpCompilation.Create(
            "BenchVerify", trees, MetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var driver = CSharpGeneratorDriver.Create(new AutoStaticsCleanupGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out var withGenerated, out _);
        var generated = driver.GetRunResult().GeneratedTrees;
        if (generated.Length != fileCount * typesPerFile)
        {
            Console.Error.WriteLine(
                $"expected {fileCount * typesPerFile} generated files, got {generated.Length} — " +
                "some corpus members are being silently skipped.");
            return 1;
        }

        var compileErrors = withGenerated.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        if (compileErrors.Count > 0)
        {
            Console.Error.WriteLine($"generated compilation has {compileErrors.Count} error(s):");
            foreach (var e in compileErrors) Console.Error.WriteLine($"  {e}");
            return 1;
        }

        Console.WriteLine($"corpus OK: {generated.Length} generated files, compilation clean.");
        return 0;
    }

    private static MetadataReference[] MetadataReferences() =>
        ((string)System.AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => !string.IsNullOrEmpty(p) && File.Exists(p))
            .Select(p => MetadataReference.CreateFromFile(p))
            .Cast<MetadataReference>()
            .ToArray();

    // Member mix mirrors the strategies the generator actually selects:
    // guarded/bare reassignment, initializer capture (semantic-model walk for
    // using-filtering), readonly Clear() + PostClearStatements restoration,
    // getter-only auto-property, event unsubscription — plus a second,
    // class-level-attributed type per file so the CollectTypeMembers full
    // member walk (with an opt-out to filter) is part of every measurement.
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
        [AutoStaticsCleanup] public static readonly List<int> R = new() {{ 1, 2, 3 }};
        [AutoStaticsCleanup] public static readonly Dictionary<string,int> W = new() {{ {{ ""a"", 1 }}, {{ ""b"", 2 }} }};
        [AutoStaticsCleanup] public static List<string> P {{ get; }} = new();
    }}

    [AutoStaticsCleanup]
    public partial class Scanned{i}
    {{
        public static int X;
        public static List<int> Y = new();
        public static readonly string Exempt = ""skipped"";
        [NoAutoStaticsCleanup] public static int OptedOut;
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
";
}
