using System.Linq;
using AutoStaticsCleanup.Tests.Utils;
using Microsoft.CodeAnalysis;
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
    public void IDisposableFieldEmitsDisposeBeforeReassignment()
    {
        const string src = @"
using System;
using Unity.Scripting.LifecycleManagement;
public class MyDisposable : IDisposable { public void Dispose() {} }
public partial class Foo
{
    [AutoStaticsCleanup] private static MyDisposable _d = new MyDisposable();
}";
        var output = Run(src);
        // `){` with no space is the pinned output format.
        Assert.Contains("if(_d is not null){ _d.Dispose(); _d = new MyDisposable(); }", output);
    }

    [Fact]
    public void TransitivelyIDisposableFieldEmitsDispose()
    {
        // System.IO.MemoryStream implements IDisposable via System.IO.Stream.
        const string src = @"
using System.IO;
using Unity.Scripting.LifecycleManagement;
public partial class Foo
{
    [AutoStaticsCleanup] private static MemoryStream _m = new MemoryStream();
}";
        var output = Run(src);
        Assert.Contains("_m.Dispose();", output);
    }

    [Fact]
    public void DisposableFieldWithoutInitializerIsSkippedAndEmitsAsc009()
    {
        // Nothing to construct a replacement from after disposing, and
        // resetting to default without disposing would leak the old instance
        // — the member is skipped and flagged instead.
        const string src = @"
using System;
using Unity.Scripting.LifecycleManagement;
public class MyDisposable : IDisposable { public void Dispose() {} }
public partial class Foo
{
    [AutoStaticsCleanup] private static MyDisposable _d;
}";
        var output = Run(src);
        var diags = AnalyzerTestHelper.Run(src);
        var asc009 = diags.Single(d => d.Id == "ASC009");
        Assert.Equal(DiagnosticSeverity.Error, asc009.Severity);
        Assert.DoesNotContain("_d", output);
    }

    [Fact]
    public void DisposablePropertyWithoutInitializerIsSkippedAndEmitsAsc009()
    {
        const string src = @"
using System;
using Unity.Scripting.LifecycleManagement;
public class MyDisposable : IDisposable { public void Dispose() {} }
public partial class Foo
{
    [AutoStaticsCleanup] public static MyDisposable D { get; set; }
}";
        var output = Run(src);
        var diags = AnalyzerTestHelper.Run(src);
        Assert.Contains(diags, d => d.Id == "ASC009");
        Assert.DoesNotContain("D =", output);
    }

    [Fact]
    public void DisposableFieldWithNullInitializerStillEmitsDispose()
    {
        // `= null` IS an initializer — it's captured verbatim and the
        // dispose-then-reassign shape is emitted.
        const string src = @"
using System;
using Unity.Scripting.LifecycleManagement;
public class MyDisposable : IDisposable { public void Dispose() {} }
public partial class Foo
{
    [AutoStaticsCleanup] private static MyDisposable _d = null;
}";
        var output = Run(src);
        Assert.Contains("if(_d is not null){ _d.Dispose(); _d = null; }", output);
    }

    [Fact]
    public void NonDisposableReferenceFieldDoesNotEmitDispose()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public partial class Foo
{
    [AutoStaticsCleanup] private static string _s = ""hello"";
}";
        var output = Run(src);
        Assert.DoesNotContain("Dispose", output);
        Assert.Contains("if(_s is not null) _s = \"hello\";", output);
    }

    [Fact]
    public void GenericIDisposableConstrainedFieldDoesNotEmitDispose()
    {
        // Dispose detection is duck-typed by method name: type parameters
        // have no members of their own, so a T constrained to IDisposable
        // does NOT get a Dispose() call.
        const string src = @"
using System;
using Unity.Scripting.LifecycleManagement;
public partial class Bag<T> where T : IDisposable, new()
{
    [AutoStaticsCleanup] private static T _x = new T();
}";
        var output = Run(src);
        Assert.DoesNotContain("Dispose", output);
        Assert.Contains("_x = new T();", output);
    }

    [Fact]
    public void DuckTypedDisposeWithoutIDisposableEmitsDispose()
    {
        // Conversely, a Dispose() method counts even when the type never
        // implements IDisposable — detection is by method name only.
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public class Handle { public void Dispose() {} }
public partial class Foo
{
    [AutoStaticsCleanup] private static Handle _h = new Handle();
}";
        var output = Run(src);
        Assert.Contains("if(_h is not null){ _h.Dispose(); _h = new Handle(); }", output);
    }

    [Fact]
    public void ReadonlyCollectionWithClearAndTrivialInitEmitsClear()
    {
        const string src = @"
using System.Collections.Generic;
using Unity.Scripting.LifecycleManagement;
public partial class Foo
{
    [AutoStaticsCleanup] public static readonly List<int> Items = new();
}";
        var output = Run(src);
        var diags = AnalyzerTestHelper.Run(src);
        Assert.Empty(diags);
        Assert.Contains("Items.Clear();", output);
        Assert.DoesNotContain("Items =", output);
    }

    [Fact]
    public void ReadonlyCollectionWithExplicitTypeArgsAndTrivialInitEmitsClear()
    {
        const string src = @"
using System.Collections.Generic;
using Unity.Scripting.LifecycleManagement;
public partial class Foo
{
    [AutoStaticsCleanup] public static readonly Dictionary<string, int> Map = new Dictionary<string, int>();
}";
        var output = Run(src);
        var diags = AnalyzerTestHelper.Run(src);
        Assert.Empty(diags);
        Assert.Contains("Map.Clear();", output);
    }

    [Fact]
    public void ReadonlyCollectionWithCollectionInitializerEmitsClearAndRestoresElements()
    {
        // Braced initializer elements are restored after Clear()
        // (PostClearStatements). No diagnostic; this shape is fully supported.
        const string src = @"
using System.Collections.Generic;
using Unity.Scripting.LifecycleManagement;
public partial class Foo
{
    [AutoStaticsCleanup] public static readonly List<int> Items = new() { 1, 2, 3 };
}";
        var output = Run(src);
        var diags = AnalyzerTestHelper.Run(src);
        Assert.Empty(diags);
        Assert.Contains("Items.Clear();", output);
        Assert.Contains("Items.Add(1);", output);
        Assert.Contains("Items.Add(2);", output);
        Assert.Contains("Items.Add(3);", output);
        Assert.DoesNotContain("Items =", output);
    }

    [Fact]
    public void ReadonlyDictionaryWithComplexInitializerRestoresViaIndexer()
    {
        // Two-expression initializer elements ({ "k", 1 }) become indexer
        // writes.
        const string src = @"
using System.Collections.Generic;
using Unity.Scripting.LifecycleManagement;
public partial class Foo
{
    [AutoStaticsCleanup] public static readonly Dictionary<string, int> Map = new() { { ""a"", 1 }, { ""b"", 2 } };
}";
        var output = Run(src);
        var diags = AnalyzerTestHelper.Run(src);
        Assert.Empty(diags);
        Assert.Contains("Map.Clear();", output);
        Assert.Contains("Map[\"a\"] = 1;", output);
        Assert.Contains("Map[\"b\"] = 2;", output);
    }

    [Fact]
    public void ReadonlyExemptTypesAreSilentlySkipped()
    {
        // Readonly unmanaged / string / whitelisted-immutable (System.Uri) /
        // arrays of unmanaged are exempt — neither reset nor flagged, even
        // with an explicit member-level attribute.
        const string src = @"
using System;
using Unity.Scripting.LifecycleManagement;
public partial class Foo
{
    [AutoStaticsCleanup] public static readonly int Number = 5;
    [AutoStaticsCleanup] public static readonly string Text = ""hi"";
    [AutoStaticsCleanup] public static readonly Uri Endpoint = new Uri(""https://x"");
    [AutoStaticsCleanup] public static readonly int[] Table = { 1, 2 };
}";
        var output = Run(src);
        var diags = AnalyzerTestHelper.Run(src);
        Assert.Empty(diags);
        Assert.DoesNotContain("Number", output);
        Assert.DoesNotContain("Text", output);
        Assert.DoesNotContain("Endpoint", output);
        Assert.DoesNotContain("Table", output);
    }

    [Fact]
    public void ReadonlyUserWrapperWithClearAndTrivialInitEmitsClear()
    {
        // Any user type exposing public parameterless void Clear() qualifies.
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public class Wrapper { public void Clear() {} }
public partial class Foo
{
    [AutoStaticsCleanup] public static readonly Wrapper W = new();
}";
        var output = Run(src);
        var diags = AnalyzerTestHelper.Run(src);
        Assert.Empty(diags);
        Assert.Contains("W.Clear();", output);
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
    public void ManualPropertyWithSetterIsSkippedAndEmitsAsc003()
    {
        // Only auto-properties are reset — a manual property is never
        // collected; it's skipped by the generator and flagged by the
        // analyzer instead.
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public partial class Foo
{
    private static int _x;
    [AutoStaticsCleanup]
    public static int Counter { get { return _x; } set { _x = value; } }
}";
        var output = Run(src);
        var diags = AnalyzerTestHelper.Run(src);
        Assert.Contains(diags, d => d.Id == "ASC003");
        Assert.DoesNotContain("Counter = ", output);
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
        var output = GeneratorTestHelper.RunGenerator(src);
        var diags = AnalyzerTestHelper.Run(src);
        Assert.DoesNotContain("Counter", output);
        Assert.Empty(diags);
    }

    [Fact]
    public void GetOnlyExemptAutoPropertyIsSilentlySkipped()
    {
        // Getter-only auto-properties follow the readonly-field rules: an
        // unmanaged type is exempt — silently skipped, no ASC003.
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public partial class Foo
{
    [AutoStaticsCleanup] public static int Counter { get; } = 5;
}";
        var output = GeneratorTestHelper.RunGenerator(src);
        var diags = AnalyzerTestHelper.Run(src);
        Assert.Empty(diags);
        Assert.DoesNotContain("Counter", output);
    }

    [Fact]
    public void GetOnlyAutoPropertyWithoutClearEmitsAsc003AndIsSkipped()
    {
        // Non-exempt type with no Clear() — can't be reset through a getter,
        // so ASC003 fires and the property is skipped.
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public partial class Foo
{
    [AutoStaticsCleanup] public static object Blob { get; } = new object();
}";
        var output = GeneratorTestHelper.RunGenerator(src);
        var diags = AnalyzerTestHelper.Run(src);
        var asc003 = diags.Single(d => d.Id == "ASC003");
        Assert.Equal(DiagnosticSeverity.Error, asc003.Severity);
        Assert.DoesNotContain("Blob", output);
    }

    [Fact]
    public void GetOnlyCollectionAutoPropertyEmitsClearAndRestoresElements()
    {
        // Getter-only auto-property of a Clear-able collection follows the
        // readonly Clear strategy, including element restoration.
        const string src = @"
using System.Collections.Generic;
using Unity.Scripting.LifecycleManagement;
public partial class Foo
{
    [AutoStaticsCleanup] public static List<string> Names { get; } = new() { ""a"" };
}";
        var output = GeneratorTestHelper.RunGenerator(src);
        var diags = AnalyzerTestHelper.Run(src);
        Assert.Empty(diags);
        Assert.Contains("Names.Clear();", output);
        Assert.Contains("Names.Add(\"a\");", output);
        Assert.DoesNotContain("Names =", output);
    }

    // Note: `init` accessors only apply to instance properties; the C# compiler
    // doesn't allow `static int X { get; init; }`, so the init-only path in the
    // generator is defensive-only and not directly testable here.

    [Fact]
    public void IDisposablePropertyEmitsDisposeBeforeReassignment()
    {
        const string src = @"
using System;
using Unity.Scripting.LifecycleManagement;
public class MyDisposable : IDisposable { public void Dispose() {} }
public partial class Foo
{
    [AutoStaticsCleanup] public static MyDisposable D { get; set; } = new MyDisposable();
}";
        var output = Run(src);
        Assert.Contains("if(D is not null){ D.Dispose(); D = new MyDisposable(); }", output);
    }
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
        Assert.Contains("foreach(System.Action handler in OnSomething.GetInvocationList())", output);
        Assert.Contains("OnSomething -= handler;", output);
    }

    [Fact]
    public void GenericDelegateEventUsesNamespaceQualifiedDelegateType()
    {
        // Default display format — namespace-qualified, no global::.
        const string src = @"
using System;
using Unity.Scripting.LifecycleManagement;
public partial class Bus
{
    [AutoStaticsCleanup] public static event Action<int, string> OnSomething;
}";
        var output = Run(src);
        Assert.Contains("foreach(System.Action<int, string> handler in OnSomething.GetInvocationList())", output);
    }

    [Fact]
    public void FieldsAreEmittedBeforePropertiesRegardlessOfSourceOrder()
    {
        // All fields, then all properties, then all events — not interleaved
        // source order.
        const string src = @"
using Unity.Scripting.LifecycleManagement;
[AutoStaticsCleanup]
public partial class Foo
{
    public static int Prop { get; set; } = 1;
    public static int Field = 2;
}";
        var output = GeneratorTestHelper.RunGenerator(src);
        var fieldIdx = output.IndexOf("Field = 2;");
        var propIdx = output.IndexOf("Prop = 1;");
        Assert.True(fieldIdx > 0 && propIdx > 0);
        Assert.True(fieldIdx < propIdx, "Fields must precede properties in the cleanup method");
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

public class ReadonlyFieldTests
{
    [Fact]
    public void MemberLevelReadonlyFieldWithoutClearEmitsAsc002AndIsSkipped()
    {
        // Non-exempt readonly type with no Clear() — nothing we can do with
        // it, so ASC002 fires and the field is skipped.
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public class Config { }
public partial class Foo
{
    [AutoStaticsCleanup] public static readonly Config C = new Config();
}";
        var output = GeneratorTestHelper.RunGenerator(src);
        var diags = AnalyzerTestHelper.Run(src);
        var asc002 = diags.Single(d => d.Id == "ASC002");
        Assert.Equal(DiagnosticSeverity.Error, asc002.Severity);
        Assert.DoesNotContain("Config C", output);
    }

    [Fact]
    public void MemberLevelReadonlyFieldWithNonCreationInitializerEmitsAsc002()
    {
        // Even with a Clear() available, a non-'new' initializer (factory
        // call) can't be restored after Clear() — ASC002.
        const string src = @"
using System.Collections.Generic;
using Unity.Scripting.LifecycleManagement;
public partial class Foo
{
    public static List<int> Make() => new List<int>();
    [AutoStaticsCleanup] public static readonly List<int> Items = Make();
}";
        var output = GeneratorTestHelper.RunGenerator(src);
        var diags = AnalyzerTestHelper.Run(src);
        Assert.Contains(diags, d => d.Id == "ASC002");
        Assert.DoesNotContain("Items.Clear();", output);
    }
}

public class PartialDiagnosticTests
{
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
        var diags = AnalyzerTestHelper.Run(src);
        Assert.Contains(diags, d => d.Id == "ASC001");
    }

    [Fact]
    public void TypeLevelAttributeOnNonPartialClassEmitsOneAsc001OnTheTypeIdentifier()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
[AutoStaticsCleanup]
public class Foo
{
    public static int A;
    public static int B;
}";
        var diags = AnalyzerTestHelper.Run(src);
        var asc001 = diags.Single(d => d.Id == "ASC001");

        var span = asc001.Location.SourceSpan;
        var text = asc001.Location.SourceTree!.GetText().ToString();
        Assert.Equal("Foo", text.Substring(span.Start, span.Length));
    }

    [Fact]
    public void MemberLevelAsc001IsAnchoredOnMemberAndCodegenSkips()
    {
        // Anchor on the member, not on the type identifier — Rider's
        // incremental analyzer hides diagnostics whose location is outside
        // the syntactic scope of the symbol being analyzed.
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public static class StaticClassTest
{
    [AutoStaticsCleanup] public static int MyInt = 20;
}";
        var output = GeneratorTestHelper.RunGenerator(src);
        var diags = AnalyzerTestHelper.Run(src);
        var asc001 = diags.Single(d => d.Id == "ASC001");

        var span = asc001.Location.SourceSpan;
        var text = asc001.Location.SourceTree!.GetText().ToString();
        var anchored = text.Substring(span.Start, span.Length);
        Assert.DoesNotContain("StaticClassTest", anchored);
        Assert.Contains("MyInt", anchored);

        Assert.DoesNotContain("MyInt", output);
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
        var output = GeneratorTestHelper.RunGenerator(src);
        var diags = AnalyzerTestHelper.Run(src);
        Assert.DoesNotContain(diags, d => d.Id == "ASC001");
        Assert.Contains("partial class Outer", output);
        Assert.Contains("partial class Inner", output);
        Assert.Contains("Counter = default;", output);
    }
}

public class MemberLevelWarningTests
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
        var output = GeneratorTestHelper.RunGenerator(src);
        var diags = AnalyzerTestHelper.Run(src);
        var asc006 = diags.Single(d => d.Id == "ASC006");
        Assert.Equal(DiagnosticSeverity.Error, asc006.Severity);
        Assert.DoesNotContain("Counter", output);
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
        var output = GeneratorTestHelper.RunGenerator(src);
        var diags = AnalyzerTestHelper.Run(src);
        var asc006 = diags.Single(d => d.Id == "ASC006");
        Assert.Equal(DiagnosticSeverity.Error, asc006.Severity);
        Assert.DoesNotContain("Counter", output);
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
        var output = GeneratorTestHelper.RunGenerator(src);
        var diags = AnalyzerTestHelper.Run(src);
        var asc006 = diags.Single(d => d.Id == "ASC006");
        Assert.Equal(DiagnosticSeverity.Error, asc006.Severity);
        Assert.DoesNotContain("OnSomething", output);
    }

    [Fact]
    public void ConstFieldEmitsAsc007()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public partial class Foo
{
    [AutoStaticsCleanup] public const int Magic = 42;
}";
        var output = GeneratorTestHelper.RunGenerator(src);
        var diags = AnalyzerTestHelper.Run(src);
        var asc007 = diags.Single(d => d.Id == "ASC007");
        Assert.Equal(DiagnosticSeverity.Error, asc007.Severity);
        Assert.DoesNotContain("Magic", output);
    }

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
        var output = GeneratorTestHelper.RunGenerator(src);
        var diags = AnalyzerTestHelper.Run(src);
        var asc004 = diags.Single(d => d.Id == "ASC004");
        Assert.Equal(DiagnosticSeverity.Error, asc004.Severity);
        Assert.DoesNotContain("OnSomething", output);
    }

    [Fact]
    public void NoAutoStaticsCleanupSuppressesWarningOnDoubleMarkedMember()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public partial class Foo
{
    [AutoStaticsCleanup, NoAutoStaticsCleanup] public static readonly int Constant = 5;
}";
        var diags = AnalyzerTestHelper.Run(src);
        Assert.Empty(diags);
    }
}

public class TypeLevelSilentSkipTests
{
    [Fact]
    public void TypeLevelScanSilentlySkipsInstanceConstReadonlyAndManualEvent()
    {
        const string src = @"
using System;
using Unity.Scripting.LifecycleManagement;
[AutoStaticsCleanup]
public partial class Foo
{
    public const int Magic = 42;
    public int Instance;
    public static readonly int Constant = 5;
    private static Action _backing;
    public static event Action E { add { _backing += value; } remove { _backing -= value; } }
    public static int Manual { get { return 0; } set { } }
    public static int Counter;
}";
        var output = GeneratorTestHelper.RunGenerator(src);
        var diags = AnalyzerTestHelper.Run(src);
        Assert.Empty(diags);
        Assert.Contains("Counter = default;", output);
        Assert.DoesNotContain("Magic", output);
        Assert.DoesNotContain("Instance", output);
        Assert.DoesNotContain("Constant", output);
        Assert.DoesNotContain("E ", output);
        Assert.DoesNotContain("Manual", output);
    }
}

public class StructTests
{
    [Fact]
    public void PartialStructEmitsPartialStructDeclaration()
    {
        // A `partial class` declaration would not merge with the user's
        // struct (CS0261) — the declaration keyword must match per level.
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public partial struct Holder
{
    [AutoStaticsCleanup] public static int Counter;
}";
        var output = GeneratorTestHelper.RunGenerator(src);
        Assert.Contains("partial struct Holder", output);
        Assert.DoesNotContain("partial class Holder", output);
        Assert.Contains("Counter = default;", output);
    }

    [Fact]
    public void ClassNestedInStructEmitsMixedChain()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public partial struct Outer
{
    public partial class Inner
    {
        [AutoStaticsCleanup] public static int Counter;
    }
}";
        var output = GeneratorTestHelper.RunGenerator(src);
        Assert.Contains("partial struct Outer", output);
        Assert.Contains("partial class Inner", output);
        Assert.Contains("\"Outer.Inner\"", output);
    }
}

public class TypeLevelRefusedShapeTests
{
    // These shapes can't be cleaned at all, even when only the class carries
    // the attribute — they must error rather than be skipped silently.

    [Fact]
    public void TypeLevelReadonlyWithoutClearEmitsAsc002()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public class Config { }
[AutoStaticsCleanup]
public partial class Foo
{
    public static readonly Config C = new Config();
    public static int Fine;
}";
        var output = GeneratorTestHelper.RunGenerator(src);
        var diags = AnalyzerTestHelper.Run(src);
        Assert.Contains(diags, d => d.Id == "ASC002");
        // The generator still emits the healthy members.
        Assert.Contains("Fine = default;", output);
        Assert.DoesNotContain("Config C", output);
    }

    [Fact]
    public void TypeLevelDisposableWithoutInitializerEmitsAsc009()
    {
        const string src = @"
using System;
using Unity.Scripting.LifecycleManagement;
public class MyDisposable : IDisposable { public void Dispose() {} }
[AutoStaticsCleanup]
public partial class Foo
{
    public static MyDisposable D;
}";
        var diags = AnalyzerTestHelper.Run(src);
        Assert.Contains(diags, d => d.Id == "ASC009");
    }

    [Fact]
    public void TypeLevelGetOnlyAutoPropertyWithoutClearEmitsAsc003()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
[AutoStaticsCleanup]
public partial class Foo
{
    public static object Blob { get; } = new object();
}";
        var diags = AnalyzerTestHelper.Run(src);
        Assert.Contains(diags, d => d.Id == "ASC003");
    }

    [Fact]
    public void TypeLevelRefusedShapeWithMemberAttributeReportsOnce()
    {
        // The member carries its own attribute, so the member-level Validate*
        // path reports it; the class-level walk must skip it to avoid a
        // duplicate diagnostic.
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public class Config { }
[AutoStaticsCleanup]
public partial class Foo
{
    [AutoStaticsCleanup] public static readonly Config C = new Config();
}";
        var diags = AnalyzerTestHelper.Run(src);
        Assert.Single(diags, d => d.Id == "ASC002");
    }

    [Fact]
    public void TypeLevelRefusedShapeWithOptOutIsSilent()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public class Config { }
[AutoStaticsCleanup]
public partial class Foo
{
    [NoAutoStaticsCleanup] public static readonly Config C = new Config();
}";
        var diags = AnalyzerTestHelper.Run(src);
        Assert.Empty(diags);
    }
}

public class StaticConstructorTests
{
    [Fact]
    public void TypeLevelAttributeWithExplicitStaticConstructorEmitsAsc008()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
[AutoStaticsCleanup]
public partial class Foo
{
    static Foo() { Counter = 1; }
    public static int Counter;
}";
        var diags = AnalyzerTestHelper.Run(src);
        var asc008 = diags.Single(d => d.Id == "ASC008");
        Assert.Equal(DiagnosticSeverity.Error, asc008.Severity);
    }

    [Fact]
    public void MemberLevelAttributeInClassWithExplicitStaticConstructorEmitsAsc008()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public partial class Foo
{
    static Foo() { Counter = 1; }
    [AutoStaticsCleanup] public static int Counter;
}";
        var diags = AnalyzerTestHelper.Run(src);
        Assert.Contains(diags, d => d.Id == "ASC008");
    }

    [Fact]
    public void ImplicitStaticConstructorFromFieldInitializersDoesNotEmitAsc008()
    {
        // The compiler synthesizes a static ctor for any class with static
        // field initializers; that's marked IsImplicitlyDeclared and must not
        // trip ASC008 (otherwise every attributed class would warn).
        const string src = @"
using Unity.Scripting.LifecycleManagement;
[AutoStaticsCleanup]
public partial class Foo
{
    public static int Counter = 42;
}";
        var diags = AnalyzerTestHelper.Run(src);
        Assert.DoesNotContain(diags, d => d.Id == "ASC008");
    }
}

public class NestedInGenericTests
{
    [Fact]
    public void TypeNestedInGenericOuterEmitsCleanup()
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
        var output = GeneratorTestHelper.RunGenerator(src);
        var diags = AnalyzerTestHelper.Run(src);
        Assert.Empty(diags);
        Assert.Contains("partial class Outer<T>", output);
        Assert.Contains("partial class Inner", output);
        Assert.Contains("Counter = default;", output);
    }

    [Fact]
    public void OpenGenericTypeItselfEmitsCleanup()
    {
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public partial class Singleton<T> where T : class
{
    [AutoStaticsCleanup] private static T _instance;
}";
        var output = GeneratorTestHelper.RunGenerator(src);
        var diags = AnalyzerTestHelper.Run(src);
        Assert.Empty(diags);
        Assert.Contains("if(_instance is not null) _instance = default;", output);
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
    public void GeneratedShapeIsStaticMethodPlusDelegateAutoCleanupRegistration()
    {
        // The Action-based shape: one static cleanup method plus one
        // DelegateAutoCleanup registration field — no nested type.
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public partial class Foo
{
    [AutoStaticsCleanup] public static int Counter;
}";
        var output = GeneratorTestHelper.RunGenerator(src);
        Assert.Contains(
            "static void __AutoStaticsCleanup_UnityEngine_PlayModeScope_Both()",
            output);
        Assert.Contains(
            "static readonly UnityEngine.DelegateAutoCleanup __autoCleanup_UnityEngine_PlayModeScope_Both = "
            + "UnityEngine.DelegateAutoCleanup.CreateForPlayMode(__AutoStaticsCleanup_UnityEngine_PlayModeScope_Both, \"Foo\");",
            output);
        Assert.DoesNotContain("AutoCleanupType", output);
        Assert.DoesNotContain("override", output);
    }

    [Fact]
    public void OwnerDescriptionUsesDottedPathWithFlattenedGenerics()
    {
        // ownerDescription is the dotted type path — Namespace.Outer.Inner —
        // with < and > flattened to underscores.
        const string src = @"
using Unity.Scripting.LifecycleManagement;
namespace MyNs
{
    public partial class Outer<T>
    {
        public partial class Inner
        {
            [AutoStaticsCleanup] public static object State;
        }
    }
}";
        var output = GeneratorTestHelper.RunGenerator(src);
        Assert.Contains("\"MyNs.Outer_T_.Inner\"", output);
    }

    [Fact]
    public void GeneratedFileMatchesPinnedOutputByteForByte()
    {
        // The definitive output regression net: the whole generated file is
        // pinned byte-for-byte — any drift in layout, naming, or statement
        // shapes fails here first.
        const string src = @"
using Unity.Scripting.LifecycleManagement;
public static partial class StaticClass
{
    [AutoStaticsCleanup] public static int MyInt = 0;
}";
        var files = GeneratorTestHelper.RunFiles(src);
        var generated = Assert.Single(files).Source.Replace("\r\n", "\n");

        const string expected =
            "// <auto-generated/>\n"
            + "#if !UNITY_6000_5_OR_NEWER\n"
            + "#pragma warning disable CS0618\n"
            + "using Unity.Scripting.LifecycleManagement;\n"
            + "\n"
            + "partial class StaticClass\n"
            + "{\n"
            + "    [System.Runtime.CompilerServices.CompilerGenerated]\n"
            + "    static void __AutoStaticsCleanup_UnityEngine_PlayModeScope_Both()\n"
            + "    {\n"
            + "        MyInt = 0;\n"
            + "    }\n"
            + "    [System.Runtime.CompilerServices.CompilerGenerated]\n"
            + "    static readonly UnityEngine.DelegateAutoCleanup __autoCleanup_UnityEngine_PlayModeScope_Both = UnityEngine.DelegateAutoCleanup.CreateForPlayMode(__AutoStaticsCleanup_UnityEngine_PlayModeScope_Both, \"StaticClass\");\n"
            + "}\n"
            + "#pragma warning restore CS0618\n"
            + "#endif\n";
        Assert.Equal(expected, generated);
    }

    [Fact]
    public void CompilerGeneratedAttributeOnCleanupMethodAndRegistrationField()
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
        var files = GeneratorTestHelper.RunFiles(src);
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
        var files = GeneratorTestHelper.RunFiles(src);
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
        var files = GeneratorTestHelper.RunFiles(src);
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
        var files = GeneratorTestHelper.RunFiles(src);
        Assert.Single(files, f => f.FileName == "Outer.Inner.autocleanup.generated.cs");
    }
}
