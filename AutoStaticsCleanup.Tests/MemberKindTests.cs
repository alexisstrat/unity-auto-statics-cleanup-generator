using System.Collections.Generic;
using AutoStaticsCleanup.Tests.Utils;
using Xunit;

namespace AutoStaticsCleanup.Tests;

public class PrimitiveFieldTests
{
    private static string RunGenerator(string src) => GeneratorTestHelper.RunGenerator(src);

    [Fact]
    public void PublicIntFieldWithInitializerEmitsDirectAssign()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public class Foo
{
    [AutoStaticsCleanup] public static int Counter = 42;
}";
        var output = RunGenerator(src);
        Assert.Contains("global::Foo.Counter = 42;", output);
    }

    [Fact]
    public void PublicIntFieldWithoutInitializerEmitsValueDefault()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public class Foo
{
    [AutoStaticsCleanup] public static int Counter;
}";
        var output = RunGenerator(src);
        Assert.Contains("global::Foo.Counter = default;", output);
    }

    [Fact]
    public void PublicBoolFieldRoundTripsInitializer()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public class Foo
{
    [AutoStaticsCleanup] public static bool Flag = true;
}";
        var output = RunGenerator(src);
        Assert.Contains("global::Foo.Flag = true;", output);
    }

    [Fact]
    public void PublicStringFieldRoundTripsInitializer()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public class Foo
{
    [AutoStaticsCleanup] public static string Name = ""hello"";
}";
        var output = RunGenerator(src);
        Assert.Contains("global::Foo.Name = \"hello\";", output);
    }

    [Fact]
    public void PublicReferenceFieldWithoutInitializerEmitsNull()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public class Foo
{
    [AutoStaticsCleanup] public static string Name;
}";
        var output = RunGenerator(src);
        Assert.Contains("global::Foo.Name = null;", output);
    }

    [Fact]
    public void PrivateIntFieldUsesReflectionWithTypedLocal()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public class Foo
{
    [AutoStaticsCleanup] private static int _counter = 7;
}";
        var output = RunGenerator(src);
        Assert.Contains("typeof(global::Foo).GetField(\"_counter\", Flags)", output);
        Assert.Contains("int value = 7;", output);
        Assert.Contains("?.SetValue(null, value);", output);
    }

    [Fact]
    public void PrivateDoubleFieldNoInitializerEmitsTypedDefault()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public class Foo
{
    [AutoStaticsCleanup] private static double _ratio;
}";
        var output = RunGenerator(src);
        Assert.Contains("double value = default;", output);
    }

    [Fact]
    public void ReadonlyPrimitiveFieldIsSkipped()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public class Foo
{
    [AutoStaticsCleanup] public static readonly int Constant = 5;
}";
        var output = RunGenerator(src);
        Assert.DoesNotContain("Constant", output);
    }

    [Fact]
    public void ConstFieldIsSkipped()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
[AutoStaticsCleanup]
public class Foo
{
    public const int Magic = 42;
    public static int Counter;
}";
        var output = RunGenerator(src);
        Assert.DoesNotContain("Magic", output);
        Assert.Contains("Counter", output);
    }

    [Fact]
    public void NoAutoStaticsCleanupOptsOutAtTypeLevel()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
[AutoStaticsCleanup]
public class Foo
{
    public static int A;
    [NoAutoStaticsCleanup] public static int B;
}";
        var output = RunGenerator(src);
        Assert.Contains("global::Foo.A = default;", output);
        Assert.DoesNotContain(".B =", output);
    }
}

public class PropertyTests
{
    private static string RunGenerator(string src) => GeneratorTestHelper.RunGenerator(src);

    [Fact]
    public void PublicAutoPropertyWithSetterUsesDirectAssign()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public class Foo
{
    [AutoStaticsCleanup] public static int Counter { get; set; } = 3;
}";
        var output = RunGenerator(src);
        Assert.Contains("global::Foo.Counter = 3;", output);
    }

    [Fact]
    public void PrivateAutoPropertyUsesBackingFieldReflection()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public class Foo
{
    [AutoStaticsCleanup] private static int Counter { get; set; }
}";
        var output = RunGenerator(src);
        Assert.Contains("\"<Counter>k__BackingField\"", output);
        Assert.Contains("int value = default;", output);
    }

    [Fact]
    public void GetOnlyAutoPropertyOfPrimitiveIsSkipped()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public class Foo
{
    [AutoStaticsCleanup] public static int Counter { get; } = 5;
}";
        var output = RunGenerator(src);
        Assert.DoesNotContain("Counter", output);
    }

    [Fact]
    public void GetOnlyAutoPropertyOfCollectionEmitsDirectClear()
    {
        const string src = @"
using System.Collections.Generic;
using Unity.Scripting.LifecycleManagement;
public class Foo
{
    [AutoStaticsCleanup] public static List<int> Items { get; } = new();
}";
        var output = RunGenerator(src);
        Assert.Contains("global::Foo.Items?.Clear();", output);
    }

    [Fact]
    public void ExpressionBodiedPropertyIsSkipped()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public class Foo
{
    private static int _x;
    [AutoStaticsCleanup] public static int Counter => _x;
}";
        var output = RunGenerator(src);
        Assert.DoesNotContain("Counter", output);
    }

    [Fact]
    public void ManualPropertyWithPublicSetterUsesDirectAssign()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public class Foo
{
    private static int _x;
    [AutoStaticsCleanup]
    public static int Counter { get { return _x; } set { _x = value; } }
}";
        var output = RunGenerator(src);
        Assert.Contains("global::Foo.Counter = default;", output);
    }

    [Fact]
    public void PrivateInternalAutoPropertyOfReferenceTypeNullsBackingField()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public class Foo
{
    [AutoStaticsCleanup] private static string Name { get; set; }
}";
        var output = RunGenerator(src);
        Assert.Contains("\"<Name>k__BackingField\"", output);
        Assert.Contains("string value = null;", output);
    }
}

public class EventTests
{
    private static string RunGenerator(string src) => GeneratorTestHelper.RunGenerator(src);

    [Fact]
    public void PublicStaticEventNullsBackingFieldViaReflection()
    {
        const string src = @"
using System;
using Unity.Scripting.LifecycleManagement;
public class Bus
{
    [AutoStaticsCleanup] public static event Action OnSomething;
}";
        var output = RunGenerator(src);
        Assert.Contains("typeof(global::Bus).GetField(\"OnSomething\", Flags)", output);
        Assert.Contains("System.Action value = null;", output);
    }

    [Fact]
    public void EventWithGenericDelegateTypeReflectsAndNulls()
    {
        const string src = @"
using System;
using Unity.Scripting.LifecycleManagement;
public class Bus
{
    [AutoStaticsCleanup] public static event Action<int, string> OnSomething;
}";
        var output = RunGenerator(src);
        Assert.Contains("typeof(global::Bus).GetField(\"OnSomething\", Flags)", output);
        Assert.Contains("System.Action<int, string> value = null;", output);
    }

    [Fact]
    public void EventWithDelegateBodyInitializerFallsBackToNull()
    {
        // Anonymous methods are treated as inaccessible by the initializer
        // accessibility check, so the generator drops the initializer and
        // falls back to setting the backing field to null.
        const string src = @"
using System;
using Unity.Scripting.LifecycleManagement;
public class Bus
{
    [AutoStaticsCleanup] public static event Action OnSomething = delegate { };
}";
        var output = RunGenerator(src);
        Assert.Contains("System.Action value = null;", output);
    }
}

public class ListTests
{
    private static string RunGenerator(string src) => GeneratorTestHelper.RunGenerator(src);

    [Fact]
    public void PublicReadonlyListEmitsDirectClear()
    {
        const string src = @"
using System.Collections.Generic;
using Unity.Scripting.LifecycleManagement;
public class Foo
{
    [AutoStaticsCleanup] public static readonly List<int> Items = new();
}";
        var output = RunGenerator(src);
        Assert.Contains("global::Foo.Items?.Clear();", output);
    }

    [Fact]
    public void PrivateReadonlyListEmitsReflectionClearWithCast()
    {
        const string src = @"
using System.Collections.Generic;
using Unity.Scripting.LifecycleManagement;
public class Foo
{
    [AutoStaticsCleanup] private static readonly List<string> _items = new();
}";
        var output = RunGenerator(src);
        Assert.Contains("typeof(global::Foo).GetField(\"_items\", Flags)", output);
        Assert.Contains("((global::System.Collections.Generic.List<string>)", output);
        Assert.Contains("?.Clear();", output);
    }

    [Fact]
    public void NonReadonlyListWithInitializerReassignsViaDirectAssign()
    {
        const string src = @"
using System.Collections.Generic;
using Unity.Scripting.LifecycleManagement;
public class Foo
{
    [AutoStaticsCleanup] public static List<int> Items = new();
}";
        var output = RunGenerator(src);
        Assert.Contains("global::Foo.Items = new();", output);
    }

    [Fact]
    public void PrivateNonReadonlyListReassignsViaReflection()
    {
        const string src = @"
using System.Collections.Generic;
using Unity.Scripting.LifecycleManagement;
public class Foo
{
    [AutoStaticsCleanup] private static List<int> _items = new();
}";
        var output = RunGenerator(src);
        Assert.Contains("global::System.Collections.Generic.List<int> value = new();", output);
        Assert.Contains("?.SetValue(null, value);", output);
    }
}

public class DictionaryTests
{
    private static string RunGenerator(string src) => GeneratorTestHelper.RunGenerator(src);

    [Fact]
    public void PublicReadonlyDictionaryEmitsDirectClear()
    {
        const string src = @"
using System.Collections.Generic;
using Unity.Scripting.LifecycleManagement;
public class Foo
{
    [AutoStaticsCleanup] public static readonly Dictionary<string, int> Map = new();
}";
        var output = RunGenerator(src);
        Assert.Contains("global::Foo.Map?.Clear();", output);
    }

    [Fact]
    public void PrivateReadonlyDictionaryEmitsReflectionClearWithCast()
    {
        const string src = @"
using System.Collections.Generic;
using Unity.Scripting.LifecycleManagement;
public class Foo
{
    [AutoStaticsCleanup] private static readonly Dictionary<string, int> _map = new();
}";
        var output = RunGenerator(src);
        Assert.Contains("typeof(global::Foo).GetField(\"_map\", Flags)", output);
        Assert.Contains("((global::System.Collections.Generic.Dictionary<string, int>)", output);
        Assert.Contains("?.Clear();", output);
    }

    [Fact]
    public void GetOnlyAutoPropertyDictionaryEmitsDirectClear()
    {
        const string src = @"
using System.Collections.Generic;
using Unity.Scripting.LifecycleManagement;
public class Foo
{
    [AutoStaticsCleanup] public static Dictionary<int, string> Map { get; } = new();
}";
        var output = RunGenerator(src);
        Assert.Contains("global::Foo.Map?.Clear();", output);
    }

    [Fact]
    public void NonReadonlyDictionaryReassignsViaDirectAssign()
    {
        const string src = @"
using System.Collections.Generic;
using Unity.Scripting.LifecycleManagement;
public class Foo
{
    [AutoStaticsCleanup] public static Dictionary<string, int> Map = new();
}";
        var output = RunGenerator(src);
        Assert.Contains("global::Foo.Map = new();", output);
    }
    
    [Fact]
    public void NonReadonlyDictionaryReassignsValuesViaDirectAssign()
    {
        const string src = @"
using System.Collections.Generic;
using Unity.Scripting.LifecycleManagement;
public class Foo
{
    [AutoStaticsCleanup] public static Dictionary<string, int> Map = new() { {""str1"", 1}, {""str2"", 2} };
}";
        var output = RunGenerator(src);
        Assert.Contains("global::Foo.Map = new() { {\"str1\", 1}, {\"str2\", 2} }", output);
    }
}

public class NoAutoStaticsCleanupTests
{
    private static string RunGenerator(string src) => GeneratorTestHelper.RunGenerator(src);

    [Fact]
    public void NoAttributeSkipsField()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
[AutoStaticsCleanup]
public class Foo
{
    public static int Kept;
    [NoAutoStaticsCleanup] public static int Skipped;
}";
        var output = RunGenerator(src);
        Assert.Contains("global::Foo.Kept = default;", output);
        Assert.DoesNotContain("Skipped", output);
    }

    [Fact]
    public void NoAttributeSkipsAutoProperty()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
[AutoStaticsCleanup]
public class Foo
{
    public static int Kept { get; set; }
    [NoAutoStaticsCleanup] public static int Skipped { get; set; }
}";
        var output = RunGenerator(src);
        Assert.Contains("global::Foo.Kept = ", output);
        Assert.DoesNotContain("Skipped", output);
    }

    [Fact]
    public void NoAttributeSkipsEvent()
    {
        const string src = @"
using System;
using Unity.Scripting.LifecycleManagement;
[AutoStaticsCleanup]
public class Bus
{
    public static event Action Kept;
    [NoAutoStaticsCleanup] public static event Action Skipped;
}";
        var output = RunGenerator(src);
        Assert.Contains("\"Kept\"", output);
        Assert.DoesNotContain("Skipped", output);
    }

    [Fact]
    public void NoAttributeSkipsReadonlyCollection()
    {
        const string src = @"
using System.Collections.Generic;
using Unity.Scripting.LifecycleManagement;
[AutoStaticsCleanup]
public class Foo
{
    public static readonly List<int> Kept = new();
    [NoAutoStaticsCleanup] public static readonly List<int> Skipped = new();
}";
        var output = RunGenerator(src);
        Assert.Contains("global::Foo.Kept?.Clear();", output);
        Assert.DoesNotContain("Skipped", output);
    }

    [Fact]
    public void NoAttributeSkipsMemberOnGenericClass()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
[AutoStaticsCleanup]
public class Holder<T> where T : class
{
    private static T _kept;
    [NoAutoStaticsCleanup] private static T _skipped;
}";
        var output = RunGenerator(src);
        Assert.Contains("\"_kept\"", output);
        Assert.DoesNotContain("_skipped", output);
    }

    [Fact]
    public void NoAttributeIgnoredWithoutTypeLevelAttribute()
    {
        // [NoAutoStaticsCleanup] only takes effect when the type-level
        // [AutoStaticsCleanup] sweeps members. A member-level [AutoStaticsCleanup]
        // wins even if [NoAutoStaticsCleanup] is also on the same member, because
        // the No check only runs in the type-level traversal.
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public class Foo
{
    [AutoStaticsCleanup, NoAutoStaticsCleanup] public static int Counter;
}";
        var output = RunGenerator(src);
        Assert.Contains("global::Foo.Counter = default;", output);
    }
}

public class UsingTriviaTests
{
    private static string RunGenerator(string src) => GeneratorTestHelper.RunGenerator(src);

    [Fact]
    public void UsingWrappedInIfDirectiveDoesNotLeakDirectiveIntoOutput()
    {
        // Regression: ToFullString() on a using surrounded by #if/#endif used
        // to drag the directives along, producing CS1529 in the consuming
        // assembly when subsequent usings landed after a non-using element.
        const string src = @"
#if SOMEFLAG
using System.Linq;
#endif
using Unity.Scripting.LifecycleManagement;
public class Foo
{
    [AutoStaticsCleanup] public static int Counter;
}";
        var output = RunGenerator(src);
        Assert.DoesNotContain("#if SOMEFLAG", output);
        Assert.DoesNotContain("SOMEFLAG", output);
        Assert.Contains("using Unity.Scripting.LifecycleManagement;", output);
    }

    [Fact]
    public void UsingWrappedInRegionDoesNotLeakRegionIntoOutput()
    {
        const string src = @"
#region Imports
using System.Collections.Generic;
using Unity.Scripting.LifecycleManagement;
#endregion
public class Foo
{
    [AutoStaticsCleanup] public static int Counter;
}";
        var output = RunGenerator(src);
        Assert.DoesNotContain("#region", output);
        Assert.DoesNotContain("#endregion", output);
        Assert.Contains("using System.Collections.Generic;", output);
    }

    [Fact]
    public void CommentAboveUsingIsNotRetained()
    {
        const string src = @"
// Imports below
using System.Collections.Generic;
using Unity.Scripting.LifecycleManagement;
public class Foo
{
    [AutoStaticsCleanup] public static int Counter;
}";
        var output = RunGenerator(src);
        Assert.DoesNotContain("Imports below", output);
    }

    [Fact]
    public void InitializerReferencingOwnNamespaceTypeResolvesViaSynthesizedUsing()
    {
        // Regression: an initializer like `new PopupManager()` written inside
        // its own namespace was captured verbatim and emitted at global scope
        // in the generated file — but the generated file had no `using` for
        // that namespace, so CS0246 fired in the consumer.
        const string src = @"
using Unity.Scripting.LifecycleManagement;
namespace MyNamespace.MyBar
{
    public interface IMyBar { }
    public class MyBar : IMyBar
    {
        internal MyBar() { }
        [AutoStaticsCleanup] public static IMyBar Instance { get; private set; } = new MyBar();
    }
}";
        var output = RunGenerator(src);
        Assert.Contains("using MyNamespace.MyBar;", output);
        Assert.Contains("new MyBar()", output);
    }

    [Fact]
    public void MultiLineUsingAliasIsCollapsedToSingleLine()
    {
        // Regression: a `using Alias = Type;` directive split across multiple
        // lines used to be torn in half by EmitUsings' \n-based split,
        // producing an orphaned type-reference-plus-semicolon at the top of
        // the generated file. That orphan parses as a global statement, and
        // every subsequent using then trips CS1529.
        const string src = @"
using System;
using System.Collections.Generic;
using AliasDictionary =
    System.Collections.Generic.Dictionary<System.Type, System.Object>;
using Unity.Scripting.LifecycleManagement;
namespace Foo
{
    internal static partial class Bar
    {
        [AutoStaticsCleanup] private static int _x;
    }
}";
        var output = RunGenerator(src);

        // The orphaned continuation must NOT appear as a standalone line.
        Assert.DoesNotContain("\nSystem.Collections.Generic.Dictionary", output);
        // The alias should appear collapsed onto one line.
        Assert.Contains(
            "using AliasDictionary =     System.Collections.Generic.Dictionary<System.Type, System.Object>;",
            output);
    }
}
