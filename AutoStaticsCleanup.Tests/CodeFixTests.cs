using AutoStaticsCleanup.Tests.Utils;
using Xunit;

namespace AutoStaticsCleanup.Tests;

public class CodeFixTests
{
    [Fact]
    public void Asc001AddsPartialModifierToType()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public class Foo
{
    [AutoStaticsCleanup] public static int Counter;
}";
        var fixed_ = CodeFixTestHelper.ApplyFirstFix(src, "ASC001");
        Assert.Contains("public partial class Foo", fixed_);
    }

    [Fact]
    public void Asc001AddsPartialToOuterTypeWhenItIsTheOffender()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public class Outer
{
    public partial class Inner
    {
        [AutoStaticsCleanup] public static int Counter;
    }
}";
        var fixed_ = CodeFixTestHelper.ApplyFirstFix(src, "ASC001");
        Assert.Contains("public partial class Outer", fixed_);
    }

    [Fact]
    public void Asc002RemovesReadonlyModifier()
    {
        // Non-exempt type without Clear() — readonly int/string no longer
        // trigger ASC002 (they're exempt, silently skipped).
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public class Config { }
public partial class Foo
{
    [AutoStaticsCleanup] public static readonly Config C = new Config();
}";
        var fixed_ = CodeFixTestHelper.ApplyFirstFix(src, "ASC002");
        Assert.DoesNotContain("readonly", fixed_);
        Assert.Contains("public static Config C = new Config();", fixed_);
    }

    [Fact]
    public void Asc003AddsSetAccessorToGetOnlyAutoProperty()
    {
        // Non-exempt, non-Clear-able type — getter-only int properties no
        // longer trigger ASC003 (exempt), and Clear-able collections are
        // supported without a setter.
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public partial class Foo
{
    [AutoStaticsCleanup] public static object Blob { get; } = new object();
}";
        var fixed_ = CodeFixTestHelper.ApplyFirstFix(src, "ASC003");
        Assert.Contains("get;", fixed_);
        Assert.Contains("set;", fixed_);
    }

    [Fact]
    public void Asc006AddsStaticModifierToInstanceField()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public partial class Foo
{
    [AutoStaticsCleanup] public int Counter;
}";
        var fixed_ = CodeFixTestHelper.ApplyFirstFix(src, "ASC006");
        Assert.Contains("public static int Counter;", fixed_);
    }

    [Fact]
    public void Asc006AddsStaticModifierToInstanceProperty()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public partial class Foo
{
    [AutoStaticsCleanup] public int Counter { get; set; }
}";
        var fixed_ = CodeFixTestHelper.ApplyFirstFix(src, "ASC006");
        Assert.Contains("public static int Counter { get; set; }", fixed_);
    }

    [Fact]
    public void Asc006AddsStaticModifierToInstanceEvent()
    {
        const string src = @"
using System;
using Unity.Scripting.LifecycleManagement;
public partial class Bus
{
    [AutoStaticsCleanup] public event Action OnSomething;
}";
        var fixed_ = CodeFixTestHelper.ApplyFirstFix(src, "ASC006");
        Assert.Contains("public static event Action OnSomething;", fixed_);
    }
}
