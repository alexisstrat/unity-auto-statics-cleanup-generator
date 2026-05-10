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

}
