using System;
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
";

    public static string RunGenerator(string userSource)
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
        var generated = result.GeneratedTrees.FirstOrDefault();
        return generated?.ToString() ?? string.Empty;
    }
}
