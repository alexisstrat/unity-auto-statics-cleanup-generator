using AutoStaticsCleanup.Tests.Utils;
using Xunit;

namespace AutoStaticsCleanup.Tests;

public class FieldTests
{
    private static string Run(string src) => GeneratorTestHelper.RunGenerator(src);

    [Fact]
    public void ValueTypeWithInitializerEmitsBareAssignment()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public partial class Foo
{
    [AutoStaticsCleanup] public static int Counter = 42;
}";
        var output = Run(src);
        Assert.Contains("Counter = 42;", output);
        Assert.DoesNotContain("if(Counter", output);
    }

    [Fact]
    public void ValueTypeNoInitializerEmitsDefault()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public partial class Foo
{
    [AutoStaticsCleanup] public static int Counter;
}";
        var output = Run(src);
        Assert.Contains("Counter = default;", output);
        Assert.DoesNotContain("if(Counter", output);
    }

    [Fact]
    public void ReferenceTypeNoInitializerGuardsWithDefault()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public partial class Foo
{
    [AutoStaticsCleanup] public static string Name;
}";
        var output = Run(src);
        Assert.Contains("if(Name is not null) Name = default;", output);
    }

    [Fact]
    public void ReferenceTypeWithInitializerGuardsAndPreservesInitializer()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public partial class Foo
{
    [AutoStaticsCleanup] public static string Name = ""hello"";
}";
        var output = Run(src);
        Assert.Contains("if(Name is not null) Name = \"hello\";", output);
    }

    [Fact]
    public void CollectionInitializerPreservedVerbatim()
    {
        const string src = @"
using System.Collections.Generic;
using Unity.Scripting.LifecycleManagement;
public partial class Foo
{
    [AutoStaticsCleanup] public static List<int> Items = new List<int>() { 1, 2, 3 };
}";
        var output = Run(src);
        Assert.Contains("if(Items is not null) Items = new List<int>() { 1, 2, 3 };", output);
    }

    [Fact]
    public void PrivateFieldEmitsDirectAccessFromNestedClass()
    {
        // Nested classes have access to private static members of their enclosing
        // type, so reflection is unnecessary regardless of visibility.
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public partial class Foo
{
    [AutoStaticsCleanup] private static int _counter = 7;
}";
        var output = Run(src);
        Assert.Contains("_counter = 7;", output);
        Assert.DoesNotContain("FieldInfo", output);
        Assert.DoesNotContain("BindingFlags", output);
    }

    [Fact]
    public void ConstFieldIsSkipped()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
[AutoStaticsCleanup]
public partial class Foo
{
    public const int Magic = 42;
    public static int Counter;
}";
        var output = Run(src);
        Assert.DoesNotContain("Magic", output);
        Assert.Contains("Counter = default;", output);
    }
}

public class PropertyTests
{
    private static string Run(string src) => GeneratorTestHelper.RunGenerator(src);

    [Fact]
    public void AutoPropertyWithSetterEmitsAssignment()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public partial class Foo
{
    [AutoStaticsCleanup] public static int Counter { get; set; } = 3;
}";
        var output = Run(src);
        Assert.Contains("Counter = 3;", output);
    }

    [Fact]
    public void PrivateAutoPropertyEmitsDirectAssignmentFromNestedClass()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public partial class Foo
{
    [AutoStaticsCleanup] private static int Counter { get; set; }
}";
        var output = Run(src);
        Assert.Contains("Counter = default;", output);
        Assert.DoesNotContain("BackingField", output);
    }

    [Fact]
    public void ManualPropertyWithSetterEmitsAssignment()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public partial class Foo
{
    private static int _x;
    [AutoStaticsCleanup]
    public static int Counter { get { return _x; } set { _x = value; } }
}";
        var output = Run(src);
        Assert.Contains("Counter = default;", output);
    }

    [Fact]
    public void ExpressionBodiedPropertyIsSilentlySkipped()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public partial class Foo
{
    private static int _x;
    [AutoStaticsCleanup] public static int Counter => _x;
}";
        var (output, diags) = GeneratorTestHelper.Run(src);
        Assert.DoesNotContain("Counter", output);
        Assert.DoesNotContain(diags, d => d.Id == "ASC003");
    }

    [Fact]
    public void GetOnlyAutoPropertyDiagnoses()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public partial class Foo
{
    [AutoStaticsCleanup] public static int Counter { get; } = 5;
}";
        var (_, diags) = GeneratorTestHelper.Run(src);
        Assert.Contains(diags, d => d.Id == "ASC003");
    }

    // Note: `init` accessors only apply to instance properties; the C# compiler
    // doesn't allow `static int X { get; init; }`, so the init-only path in the
    // generator is defensive-only and not directly testable here.
}

public class EventTests
{
    private static string Run(string src) => GeneratorTestHelper.RunGenerator(src);

    [Fact]
    public void StaticEventEmitsUnsubscribeLoop()
    {
        const string src = @"
using System;
using Unity.Scripting.LifecycleManagement;
public partial class Bus
{
    [AutoStaticsCleanup] public static event Action OnSomething;
}";
        var output = Run(src);
        Assert.Contains("if(OnSomething != null)", output);
        Assert.Contains("foreach(global::System.Action handler in OnSomething.GetInvocationList())", output);
        Assert.Contains("OnSomething -= handler;", output);
    }

    [Fact]
    public void GenericDelegateEventUsesFullyQualifiedDelegateType()
    {
        const string src = @"
using System;
using Unity.Scripting.LifecycleManagement;
public partial class Bus
{
    [AutoStaticsCleanup] public static event Action<int, string> OnSomething;
}";
        var output = Run(src);
        Assert.Contains("foreach(global::System.Action<int, string> handler in OnSomething.GetInvocationList())", output);
    }

    [Fact]
    public void EventsAreEmittedAfterNonEventsRegardlessOfSourceOrder()
    {
        const string src = @"
using System;
using Unity.Scripting.LifecycleManagement;
public partial class Foo
{
    [AutoStaticsCleanup] public static event Action OnSomething;
    [AutoStaticsCleanup] public static int Counter;
}";
        var output = Run(src);
        var counterIdx = output.IndexOf("Counter = default;");
        var eventIdx = output.IndexOf("if(OnSomething != null)");
        Assert.True(counterIdx > 0 && eventIdx > 0);
        Assert.True(counterIdx < eventIdx, "Non-events must precede events in Cleanup body");
    }
}

public class ReadonlyDiagnosticTests
{
    [Fact]
    public void ReadonlyFieldEmitsAsc002()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public partial class Foo
{
    [AutoStaticsCleanup] public static readonly int Constant = 5;
}";
        var (output, diags) = GeneratorTestHelper.Run(src);
        Assert.Contains(diags, d => d.Id == "ASC002");
        Assert.DoesNotContain("Constant", output);
    }

}

public class PartialDiagnosticTests
{
    [Fact]
    public void NonPartialClassEmitsAsc001()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public class Foo
{
    [AutoStaticsCleanup] public static int Counter;
}";
        var (output, diags) = GeneratorTestHelper.Run(src);
        Assert.Contains(diags, d => d.Id == "ASC001");
        Assert.DoesNotContain("Counter", output);
    }

    [Fact]
    public void NonPartialOuterClassEmitsAsc001ForNestedTarget()
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
        var (_, diags) = GeneratorTestHelper.Run(src);
        Assert.Contains(diags, d => d.Id == "ASC001");
    }

    [Fact]
    public void TypeLevelAttributeOnNonPartialClassEmitsAsc001Once()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
[AutoStaticsCleanup]
public class Foo
{
    public static int A;
    public static int B;
}";
        var (_, diags) = GeneratorTestHelper.Run(src);
        // One diagnostic emitted at the type level rather than one per member.
        Assert.Single(diags, d => d.Id == "ASC001");
    }

    [Fact]
    public void FullyPartialNestedChainEmitsCleanup()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public partial class Outer
{
    public partial class Inner
    {
        [AutoStaticsCleanup] public static int Counter;
    }
}";
        var (output, diags) = GeneratorTestHelper.Run(src);
        Assert.DoesNotContain(diags, d => d.Id == "ASC001");
        Assert.Contains("partial class Outer", output);
        Assert.Contains("partial class Inner", output);
        Assert.Contains("Counter = default;", output);
    }
}

public class ManualEventDiagnosticTests
{
    [Fact]
    public void ManualEventEmitsAsc004()
    {
        const string src = @"
using System;
using Unity.Scripting.LifecycleManagement;
public partial class Bus
{
    private static Action _backing;
    [AutoStaticsCleanup]
    public static event Action OnSomething { add { _backing += value; } remove { _backing -= value; } }
}";
        var (output, diags) = GeneratorTestHelper.Run(src);
        Assert.Contains(diags, d => d.Id == "ASC004");
        Assert.DoesNotContain("OnSomething", output);
    }

    [Fact]
    public void FieldLikeEventDoesNotEmitAsc004()
    {
        const string src = @"
using System;
using Unity.Scripting.LifecycleManagement;
public partial class Bus
{
    [AutoStaticsCleanup] public static event Action OnSomething;
}";
        var (_, diags) = GeneratorTestHelper.Run(src);
        Assert.DoesNotContain(diags, d => d.Id == "ASC004");
    }
}

public class NestedInGenericDiagnosticTests
{
    [Fact]
    public void TypeNestedInGenericOuterEmitsAsc005()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public partial class Outer<T>
{
    public partial class Inner
    {
        [AutoStaticsCleanup] public static int Counter;
    }
}";
        var (output, diags) = GeneratorTestHelper.Run(src);
        Assert.Contains(diags, d => d.Id == "ASC005");
        Assert.DoesNotContain("Counter", output);
    }

    [Fact]
    public void OpenGenericItselfDoesNotEmitAsc005()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public partial class Singleton<T> where T : class
{
    [AutoStaticsCleanup] private static T _instance;
}";
        var (_, diags) = GeneratorTestHelper.Run(src);
        Assert.DoesNotContain(diags, d => d.Id == "ASC005");
    }
}

public class StaticAndConstDiagnosticTests
{
    [Fact]
    public void InstanceFieldEmitsAsc006()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public partial class Foo
{
    [AutoStaticsCleanup] public int Counter;
}";
        var (_, diags) = GeneratorTestHelper.Run(src);
        Assert.Contains(diags, d => d.Id == "ASC006");
    }

    [Fact]
    public void InstancePropertyEmitsAsc006()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public partial class Foo
{
    [AutoStaticsCleanup] public int Counter { get; set; }
}";
        var (_, diags) = GeneratorTestHelper.Run(src);
        Assert.Contains(diags, d => d.Id == "ASC006");
    }

    [Fact]
    public void InstanceEventEmitsAsc006()
    {
        const string src = @"
using System;
using Unity.Scripting.LifecycleManagement;
public partial class Bus
{
    [AutoStaticsCleanup] public event Action OnSomething;
}";
        var (_, diags) = GeneratorTestHelper.Run(src);
        Assert.Contains(diags, d => d.Id == "ASC006");
    }

    [Fact]
    public void ConstFieldWithMemberLevelAttributeEmitsAsc007()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public partial class Foo
{
    [AutoStaticsCleanup] public const int Magic = 42;
}";
        var (_, diags) = GeneratorTestHelper.Run(src);
        Assert.Contains(diags, d => d.Id == "ASC007");
    }

    [Fact]
    public void ConstFieldUnderTypeLevelAttributeIsSilentlySkipped()
    {
        // Type-level scan filters out const/instance silently — the user didn't
        // explicitly attribute these members, so don't nag.
        const string src = @"
using Unity.Scripting.LifecycleManagement;
[AutoStaticsCleanup]
public partial class Foo
{
    public const int Magic = 42;
    public int Instance;
    public static int Counter;
}";
        var (output, diags) = GeneratorTestHelper.Run(src);
        Assert.DoesNotContain(diags, d => d.Id == "ASC006" || d.Id == "ASC007");
        Assert.Contains("Counter = default;", output);
    }
}

public class NoAutoStaticsCleanupTests
{
    private static string Run(string src) => GeneratorTestHelper.RunGenerator(src);

    [Fact]
    public void NoAttributeSkipsField()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
[AutoStaticsCleanup]
public partial class Foo
{
    public static int Kept;
    [NoAutoStaticsCleanup] public static int Skipped;
}";
        var output = Run(src);
        Assert.Contains("Kept = default;", output);
        Assert.DoesNotContain("Skipped", output);
    }

}

public class UsingTriviaTests
{
    private static string Run(string src) => GeneratorTestHelper.RunGenerator(src);

    [Fact]
    public void UsingWrappedInIfDirectiveDoesNotLeakDirectiveIntoOutput()
    {
        const string src = @"
#if SOMEFLAG
using System.Linq;
#endif
using Unity.Scripting.LifecycleManagement;
public partial class Foo
{
    [AutoStaticsCleanup] public static int Counter;
}";
        var output = Run(src);
        Assert.DoesNotContain("#if SOMEFLAG", output);
        Assert.DoesNotContain("SOMEFLAG", output);
        Assert.Contains("using Unity.Scripting.LifecycleManagement;", output);
    }

    [Fact]
    public void UsingWrappedInRegionDoesNotLeakRegionIntoOutput()
    {
        // Initializer references List<int> so the using survives the
        // initializer-namespace filter; otherwise the test would only verify
        // trivia handling, not that the using made it through at all.
        const string src = @"
#region Imports
using System.Collections.Generic;
using Unity.Scripting.LifecycleManagement;
#endregion
public partial class Foo
{
    [AutoStaticsCleanup] public static List<int> Items = new();
}";
        var output = Run(src);
        Assert.DoesNotContain("#region", output);
        Assert.DoesNotContain("#endregion", output);
        Assert.Contains("using System.Collections.Generic;", output);
    }

    [Fact]
    public void MultiLineUsingAliasIsCollapsedToSingleLine()
    {
        const string src = @"
using System;
using System.Collections.Generic;
using AliasDictionary =
    System.Collections.Generic.Dictionary<System.Type, System.Object>;
using Unity.Scripting.LifecycleManagement;
namespace MyNs
{
    internal static partial class Bar
    {
        [AutoStaticsCleanup] private static int _x;
    }
}";
        var output = Run(src);
        Assert.DoesNotContain("\nSystem.Collections.Generic.Dictionary", output);
        Assert.Contains(
            "using AliasDictionary =     System.Collections.Generic.Dictionary<System.Type, System.Object>;",
            output);
    }
}

public class UsingFilteringTests
{
    [Fact]
    public void UnreferencedUsingIsDroppedFromGeneratedFile()
    {
        const string src = @"
using System.Collections.Generic;
using System.Linq;
using Unity.Scripting.LifecycleManagement;
public partial class Foo
{
    [AutoStaticsCleanup] public static int Counter;
}";
        var output = GeneratorTestHelper.RunGenerator(src);
        Assert.DoesNotContain("using System.Collections.Generic;", output);
        Assert.DoesNotContain("using System.Linq;", output);
    }

    [Fact]
    public void ReferencedUsingIsKept()
    {
        const string src = @"
using System.Collections.Generic;
using System.Linq;
using Unity.Scripting.LifecycleManagement;
public partial class Foo
{
    [AutoStaticsCleanup] public static List<int> Items = new();
}";
        var output = GeneratorTestHelper.RunGenerator(src);
        Assert.Contains("using System.Collections.Generic;", output);
        Assert.DoesNotContain("using System.Linq;", output);
    }

    [Fact]
    public void StaticUsingIsAlwaysKept()
    {
        // We can't reliably trace what a `using static` introduces without a
        // much heavier syntactic analysis, so we keep them all.
        const string src = @"
using static System.Math;
using Unity.Scripting.LifecycleManagement;
public partial class Foo
{
    [AutoStaticsCleanup] public static int Counter;
}";
        var output = GeneratorTestHelper.RunGenerator(src);
        Assert.Contains("using static System.Math;", output);
    }

    [Fact]
    public void AliasUsingIsAlwaysKept()
    {
        const string src = @"
using IntList = System.Collections.Generic.List<int>;
using Unity.Scripting.LifecycleManagement;
public partial class Foo
{
    [AutoStaticsCleanup] public static int Counter;
}";
        var output = GeneratorTestHelper.RunGenerator(src);
        Assert.Contains("using IntList =", output);
    }

    [Fact]
    public void RelativelyReferencedTypeKeepsParentUsing()
    {
        // The user wrote `Inner.Tool` rather than `Tool`, so the source needed
        // `using Outer;` not `using Outer.Inner;`. The filter must keep
        // `using Outer;` because the initializer's referenced namespace
        // (`Outer.Inner`) is a child of it.
        const string src = @"
using Outer;
using Unity.Scripting.LifecycleManagement;

namespace Outer.Inner
{
    public class Tool { }
}

namespace Outer
{
    public partial class Foo
    {
        [AutoStaticsCleanup] public static Inner.Tool T = new Inner.Tool();
    }
}";
        var output = GeneratorTestHelper.RunGenerator(src);
        Assert.Contains("using Outer;", output);
    }
}

public class FileShapeTests
{
    [Fact]
    public void FileWrapsInVersionGuardAndPragma()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public partial class Foo
{
    [AutoStaticsCleanup] public static int Counter;
}";
        var output = GeneratorTestHelper.RunGenerator(src);
        Assert.Contains("#if !UNITY_6000_5_OR_NEWER", output);
        Assert.Contains("#endif", output);
        Assert.Contains("#pragma warning disable CS0618", output);
        Assert.Contains("#pragma warning restore CS0618", output);
    }

    [Fact]
    public void GeneratedNestedClassExtendsPlayModeScopeAutoCleanup()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public partial class Foo
{
    [AutoStaticsCleanup] public static int Counter;
}";
        var output = GeneratorTestHelper.RunGenerator(src);
        Assert.Contains(
            "class UnityEngine_PlayModeScopeAutoCleanup_Both_AutoCleanupType : UnityEngine.PlayModeScopeAutoCleanup",
            output);
        Assert.Contains(
            "static readonly UnityEngine_PlayModeScopeAutoCleanup_Both_AutoCleanupType _UnityEngine_PlayModeScopeAutoCleanup_Both_AutoCleanupType = new();",
            output);
    }

    [Fact]
    public void CompilerGeneratedAttributeOnNestedClassAndField()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public partial class Foo
{
    [AutoStaticsCleanup] public static int Counter;
}";
        var output = GeneratorTestHelper.RunGenerator(src);
        Assert.Contains("[System.Runtime.CompilerServices.CompilerGenerated]", output);
    }

    [Fact]
    public void NamespaceIsEmittedAroundPartialBlock()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
namespace MyNs.Inner
{
    public partial class Foo
    {
        [AutoStaticsCleanup] public static int Counter;
    }
}";
        var output = GeneratorTestHelper.RunGenerator(src);
        Assert.Contains("namespace MyNs.Inner", output);
        Assert.Contains("partial class Foo", output);
    }

    [Fact]
    public void MultipleAttributedTypesEmitSeparateFiles()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public partial class Foo
{
    [AutoStaticsCleanup] public static int A;
}
public partial class Bar
{
    [AutoStaticsCleanup] public static int B;
}";
        var (files, _) = GeneratorTestHelper.RunFiles(src);
        Assert.Equal(2, files.Length);
        Assert.Contains(files, f => f.FileName == "Foo.autocleanup.generated.cs");
        Assert.Contains(files, f => f.FileName == "Bar.autocleanup.generated.cs");
    }

    [Fact]
    public void FileNameIncludesNamespace()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
namespace MyNs.Inner
{
    public partial class Foo
    {
        [AutoStaticsCleanup] public static int A;
    }
}";
        var (files, _) = GeneratorTestHelper.RunFiles(src);
        Assert.Single(files, f => f.FileName == "MyNs.Inner.Foo.autocleanup.generated.cs");
    }

    [Fact]
    public void GenericTypeFileNameUsesUnderscoreParameterMarkers()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public partial class Singleton<T> where T : class
{
    [AutoStaticsCleanup] private static T _instance;
}
public partial class Pair<T1, T2>
{
    [AutoStaticsCleanup] private static int _x;
}";
        var (files, _) = GeneratorTestHelper.RunFiles(src);
        Assert.Contains(files, f => f.FileName == "Singleton_T_.autocleanup.generated.cs");
        Assert.Contains(files, f => f.FileName == "Pair_T1_T2_.autocleanup.generated.cs");
    }

    [Fact]
    public void NestedTypeFileNameIncludesOuterChain()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public partial class Outer
{
    public partial class Inner
    {
        [AutoStaticsCleanup] public static int A;
    }
}";
        var (files, _) = GeneratorTestHelper.RunFiles(src);
        Assert.Single(files, f => f.FileName == "Outer.Inner.autocleanup.generated.cs");
    }
}
