using AutoStaticsCleanup.Tests.Utils;
using Xunit;

namespace AutoStaticsCleanup.Tests;

public class GenericTypeTests
{
    private static string Run(string src) => GeneratorTestHelper.RunGenerator(src);

    [Fact]
    public void UnconstrainedTypeParameterFieldDoesNotGuard()
    {
        // The null guard applies to reference types only; an unconstrained T
        // is not known to be a reference type, so the bare assignment is
        // emitted.
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public partial class Bus<T>
{
    [AutoStaticsCleanup] private static T _x;
}";
        var output = Run(src);
        Assert.Contains("_x = default;", output);
        Assert.DoesNotContain("if(_x", output);
    }

    [Fact]
    public void ClassConstrainedTypeParameterFieldGuards()
    {
        // `where T : class` makes T a reference type — the guard comes back.
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public partial class Bus<T> where T : class
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
        Assert.Contains("_a = default;", output);
        Assert.Contains("_b = default;", output);
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
        Assert.Contains("foreach(System.Action<T> handler in OnSomething.GetInvocationList())", output);
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
