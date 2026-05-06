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
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public partial class Foo
{
    [AutoStaticsCleanup] public static readonly int Constant = 5;
}";
        var fixed_ = CodeFixTestHelper.ApplyFirstFix(src, "ASC002");
        Assert.DoesNotContain("readonly", fixed_);
        Assert.Contains("public static int Constant = 5;", fixed_);
    }

    [Fact]
    public void Asc003AddsSetAccessorToGetOnlyAutoProperty()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public partial class Foo
{
    [AutoStaticsCleanup] public static int Counter { get; } = 5;
}";
        var fixed_ = CodeFixTestHelper.ApplyFirstFix(src, "ASC003");
        Assert.Contains("get;", fixed_);
        Assert.Contains("set;", fixed_);
    }
}
