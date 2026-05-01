using AutoStaticsCleanup.Tests.Utils;
using Xunit;

namespace AutoStaticsCleanup.Tests;

public class GenericTypeTests
{
    private static string RunGenerator(string src) => GeneratorTestHelper.RunGenerator(src);

    [Fact]
    public void OpenGenericFieldEmitsTypeCacheResolverCall()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public class Singleton<T> where T : class
{
    [AutoStaticsCleanup]
    private static T _instance;
}
";
        var output = RunGenerator(src);
        Assert.Contains("ResolveOpenGenericFields(typeof(global::Singleton<>), \"_instance\")", output);
        Assert.Contains("UnityEditor.TypeCache.GetTypesDerivedFrom(openDef)", output);
        Assert.Contains("foreach (var field in", output);
        Assert.Contains(
            "field.FieldType.IsValueType ? global::System.Activator.CreateInstance(field.FieldType) : null",
            output);
    }

    [Fact]
    public void OpenGenericReadonlyCollectionEmitsClearLoop()
    {
        const string src = @"
using System.Collections.Generic;
using Unity.Scripting.LifecycleManagement;
public class Container<T>
{
    [AutoStaticsCleanup]
    private static readonly List<T> _items = new();
}
";
        var output = RunGenerator(src);
        Assert.Contains("ResolveOpenGenericFields(typeof(global::Container<>), \"_items\")", output);
        Assert.Contains("GetMethod(\"Clear\", global::System.Type.EmptyTypes)", output);
    }

    [Fact]
    public void OpenGenericEventIsHandled()
    {
        const string src = @"
using System;
using Unity.Scripting.LifecycleManagement;
public class Bus<T>
{
    [AutoStaticsCleanup]
    public static event Action<T> OnSomething;
}
";
        var output = RunGenerator(src);
        Assert.Contains("ResolveOpenGenericFields(typeof(global::Bus<>), \"OnSomething\")", output);
    }

    [Fact]
    public void OpenGenericMultipleTypeParamsUsesArityCommas()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public class Pair<T1, T2> where T1 : class where T2 : class
{
    [AutoStaticsCleanup]
    private static T1 _a;
    [AutoStaticsCleanup]
    private static T2 _b;
}
";
        var output = RunGenerator(src);
        Assert.Contains("typeof(global::Pair<,>)", output);
    }

    [Fact]
    public void TypeLevelAttributeOnGenericClassScansAllStatics()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
[AutoStaticsCleanup]
public class Holder<T> where T : class
{
    private static T _x;
    private static int _y;
    [NoAutoStaticsCleanup]
    private static string _skipped;
}
";
        var output = RunGenerator(src);
        Assert.Contains("ResolveOpenGenericFields(typeof(global::Holder<>), \"_x\")", output);
        Assert.Contains("ResolveOpenGenericFields(typeof(global::Holder<>), \"_y\")", output);
        Assert.DoesNotContain("\"_skipped\"", output);
    }

    [Fact]
    public void NonGenericFieldStillUsesDirectFieldInfoLookup()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public class Foo
{
    [AutoStaticsCleanup]
    private static int _counter;
}
";
        var output = RunGenerator(src);
        Assert.Contains("typeof(global::Foo).GetField(\"_counter\", Flags)", output);
        Assert.DoesNotContain("ResolveOpenGenericFields", output);
    }

    [Fact]
    public void GenericResolverHelperEmittedOnlyWhenNeeded()
    {
        const string nonGenericSrc = @"
using Unity.Scripting.LifecycleManagement;
public class Foo
{
    [AutoStaticsCleanup]
    public static int Counter;
}
";
        var output = RunGenerator(nonGenericSrc);
        Assert.DoesNotContain("ResolveOpenGenericFields", output);
        Assert.DoesNotContain("UnityEditor.TypeCache", output);
    }
}
