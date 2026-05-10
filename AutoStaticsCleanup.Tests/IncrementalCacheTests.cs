using System;
using System.IO;
using System.Linq;
using AutoStaticsCleanup.Tests.Utils;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using Xunit.Abstractions;

namespace AutoStaticsCleanup.Tests;

public class IncrementalCacheTests
{
    private readonly ITestOutputHelper _out;
    public IncrementalCacheTests(ITestOutputHelper o) => _out = o;

    /// <summary>
    /// Editing a file that doesn't carry [AutoStaticsCleanup] should leave the
    /// generator's user-facing pipeline steps cached. If <c>ResetEntry.Equals</c>,
    /// <c>ExtractResult.Equals</c>, or any nested struct's equality breaks, the
    /// transform's output goes from <c>Unchanged</c> → <c>Modified</c>, and
    /// <c>SourceOutput</c> falls out of cache.
    /// </summary>
    [Fact]
    public void UnrelatedEditKeepsExtractAndSourceOutputCached()
    {
        var refs = LoadRefs();

        var stub = CSharpSyntaxTree.ParseText(GeneratorTestHelper.AttributeStub, path: "stub.cs");
        var attributed = CSharpSyntaxTree.ParseText(@"
using Unity.Scripting.LifecycleManagement;
public partial class Foo { [AutoStaticsCleanup] public static int A; }",
            path: "foo.cs");
        var bystander = CSharpSyntaxTree.ParseText("// bystander", path: "bystander.cs");

        var compilation = CSharpCompilation.Create("X",
            new[] { stub, attributed, bystander }, refs);

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new AutoStaticsCleanupGenerator().AsSourceGenerator() },
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));

        driver = driver.RunGenerators(compilation);

        var newBystander = CSharpSyntaxTree.ParseText("// bystander\n// edit", path: "bystander.cs");
        compilation = compilation.ReplaceSyntaxTree(bystander, newBystander);
        driver = driver.RunGenerators(compilation);

        var run = driver.GetRunResult().Results.Single();

        // Dump every tracked step for diagnostic visibility.
        foreach (var (stepName, runs) in run.TrackedSteps)
        foreach (var step in runs)
        foreach (var (value, reason) in step.Outputs)
            _out.WriteLine($"{stepName,-60} {reason} {value?.GetType().Name}");

        // Steps we own: the transform's output and both RegisterSourceOutput
        // actions. None of them should be Modified — that would mean an Equals
        // implementation regressed.
        AssertAllOutputsAreCacheHits(run, "result_ForAttributeWithMetadataName");
        AssertAllOutputsAreCacheHits(run, "SourceOutput");
    }

    [Fact]
    public void EditingAttributedFileOnlyInvalidatesThatFilesEntries()
    {
        var refs = LoadRefs();

        var stub = CSharpSyntaxTree.ParseText(GeneratorTestHelper.AttributeStub, path: "stub.cs");
        var fooTree = CSharpSyntaxTree.ParseText(@"
using Unity.Scripting.LifecycleManagement;
public partial class Foo { [AutoStaticsCleanup] public static int A; }",
            path: "foo.cs");
        var barTree = CSharpSyntaxTree.ParseText(@"
using Unity.Scripting.LifecycleManagement;
public partial class Bar { [AutoStaticsCleanup] public static int B; }",
            path: "bar.cs");

        var compilation = CSharpCompilation.Create("X",
            new[] { stub, fooTree, barTree }, refs);

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new AutoStaticsCleanupGenerator().AsSourceGenerator() },
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));

        driver = driver.RunGenerators(compilation);

        var newFoo = CSharpSyntaxTree.ParseText(@"
using Unity.Scripting.LifecycleManagement;
public partial class Foo { [AutoStaticsCleanup] public static int Renamed; }",
            path: "foo.cs");
        compilation = compilation.ReplaceSyntaxTree(fooTree, newFoo);
        driver = driver.RunGenerators(compilation);

        var run = driver.GetRunResult().Results.Single();
        var transformRuns = run.TrackedSteps["result_ForAttributeWithMetadataName"];

        var reasons = transformRuns
            .SelectMany(s => s.Outputs.Select(o => o.Reason))
            .ToList();

        Assert.Single(reasons, r => r == IncrementalStepRunReason.Modified);
        Assert.Contains(reasons, r => r == IncrementalStepRunReason.Unchanged || r == IncrementalStepRunReason.Cached);
    }

    /// <summary>
    /// With three attributed types fanned out as separate <c>TypeGroup</c>
    /// values, editing one of them should re-run only its <c>SourceOutput</c>;
    /// the other two groups must compare equal to the previous run via
    /// <c>TypeGroup.Equals</c> and stay <c>Cached</c>/<c>Unchanged</c>. If
    /// <c>TypeGroup</c>'s structural equality regresses, or the SelectMany
    /// fan-out loses its deterministic ordering, all three SourceOutput runs
    /// would go <c>Modified</c> and the per-group cache silently disappears.
    /// </summary>
    [Fact]
    public void PerGroupSourceOutputCachesUnaffectedTypes()
    {
        var refs = LoadRefs();

        var stub = CSharpSyntaxTree.ParseText(GeneratorTestHelper.AttributeStub, path: "stub.cs");
        var foo = CSharpSyntaxTree.ParseText(@"
using Unity.Scripting.LifecycleManagement;
public partial class Foo { [AutoStaticsCleanup] public static int A; }",
            path: "foo.cs");
        var bar = CSharpSyntaxTree.ParseText(@"
using Unity.Scripting.LifecycleManagement;
public partial class Bar { [AutoStaticsCleanup] public static int B; }",
            path: "bar.cs");
        var baz = CSharpSyntaxTree.ParseText(@"
using Unity.Scripting.LifecycleManagement;
public partial class Baz { [AutoStaticsCleanup] public static int C; }",
            path: "baz.cs");

        var compilation = CSharpCompilation.Create("X", new[] { stub, foo, bar, baz }, refs);

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new AutoStaticsCleanupGenerator().AsSourceGenerator() },
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));

        driver = driver.RunGenerators(compilation);

        // Edit only Bar's tree.
        var newBar = CSharpSyntaxTree.ParseText(@"
using Unity.Scripting.LifecycleManagement;
public partial class Bar { [AutoStaticsCleanup] public static int Renamed; }",
            path: "bar.cs");
        compilation = compilation.ReplaceSyntaxTree(bar, newBar);
        driver = driver.RunGenerators(compilation);

        var run = driver.GetRunResult().Results.Single();

        foreach (var (stepName, runs) in run.TrackedSteps)
        foreach (var step in runs)
        foreach (var (value, reason) in step.Outputs)
            _out.WriteLine($"{stepName,-60} {reason} {value?.GetType().Name}");

        // Three groups → three SourceOutput step runs. Two of them must be
        // Cached/Unchanged (Foo and Baz didn't change — that's the whole point
        // of the fan-out). The third is the edited Bar; Roslyn surfaces it as
        // New rather than Modified because SelectMany matches fanned-out
        // elements by structural equality and a non-matching new value lands
        // in a "new slot" semantically.
        var sourceOutput = run.TrackedSteps["SourceOutput"];
        var reasons = sourceOutput
            .SelectMany(s => s.Outputs.Select(o => o.Reason))
            .ToList();

        Assert.Equal(3, reasons.Count);
        Assert.Equal(2, reasons.Count(r =>
            r == IncrementalStepRunReason.Cached || r == IncrementalStepRunReason.Unchanged));
    }

    private static void AssertAllOutputsAreCacheHits(GeneratorRunResult run, string stepName)
    {
        if (!run.TrackedSteps.TryGetValue(stepName, out var steps)) return;
        foreach (var step in steps)
        foreach (var (_, reason) in step.Outputs)
            Assert.True(
                reason == IncrementalStepRunReason.Cached || reason == IncrementalStepRunReason.Unchanged,
                $"Step '{stepName}' produced output with reason {reason}; expected Cached or Unchanged");
    }

    private static MetadataReference[] LoadRefs()
    {
        var trustedAssemblies = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        return trustedAssemblies
            .Split(Path.PathSeparator)
            .Where(p => !string.IsNullOrEmpty(p) && File.Exists(p))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToArray();
    }
}
