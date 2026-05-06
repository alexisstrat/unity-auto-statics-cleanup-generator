using AutoStaticsCleanup.Tests.Utils;
using Xunit;

namespace AutoStaticsCleanup.Tests;

public class GenericTypeTests
{
    private static string Run(string src) => GeneratorTestHelper.RunGenerator(src);

    [Fact]
    public void UnconstrainedTypeParameterFieldGuards()
    {
        // Even with no class constraint Unity emits the guard, because `default`
        // for an unconstrained T is "null or default-value" and the pattern is
        // valid in both shapes.
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public partial class Bus<T>
{
    [AutoStaticsCleanup] private static T _x;
}";
        var output = Run(src);
        Assert.Contains("if(_x is not null) _x = default;", output);
    }

    [Fact]
    public void GenericTypeWithMultipleTypeParametersRenderedWithNames()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public partial class Pair<T1, T2>
{
    [AutoStaticsCleanup] private static T1 _a;
    [AutoStaticsCleanup] private static T2 _b;
}";
        var output = Run(src);
        Assert.Contains("partial class Pair<T1, T2>", output);
        Assert.Contains("if(_a is not null) _a = default;", output);
        Assert.Contains("if(_b is not null) _b = default;", output);
    }

    [Fact]
    public void GenericFieldWithNonTypeParameterTypeDoesNotGuard()
    {
        // `int` doesn't depend on T — emit a bare assignment.
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public partial class Bus<T>
{
    [AutoStaticsCleanup] private static int _counter;
}";
        var output = Run(src);
        Assert.Contains("_counter = default;", output);
        Assert.DoesNotContain("if(_counter", output);
    }

    [Fact]
    public void GenericEventEmitsUnsubscribeLoop()
    {
        const string src = @"
using System;
using Unity.Scripting.LifecycleManagement;
public partial class Bus<T>
{
    [AutoStaticsCleanup] public static event Action<T> OnSomething;
}";
        var output = Run(src);
        Assert.Contains("foreach(global::System.Action<T> handler in OnSomething.GetInvocationList())", output);
    }

    [Fact]
    public void TypeLevelAttributeOnGenericClassScansAllStatics()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
[AutoStaticsCleanup]
public partial class Holder<T> where T : class
{
    private static T _x;
    private static int _y;
    [NoAutoStaticsCleanup] private static string _skipped;
}";
        var output = Run(src);
        Assert.Contains("if(_x is not null) _x = default;", output);
        Assert.Contains("_y = default;", output);
        Assert.DoesNotContain("_skipped", output);
    }

    [Fact]
    public void NoTypeCacheReferencesInGeneratedOutput()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public partial class Singleton<T> where T : class
{
    [AutoStaticsCleanup] private static T _instance;
}";
        var output = Run(src);
        Assert.DoesNotContain("TypeCache", output);
        Assert.DoesNotContain("InitializeOnLoad", output);
        Assert.DoesNotContain("playModeStateChanged", output);
    }
}
