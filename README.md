# AutoStaticsCleanup Generator

A backport of Unity 6.5+'s built-in [AutoStaticsCleanup](https://docs.unity3d.com/6000.5/Documentation/ScriptReference/Unity.Scripting.LifecycleManagement.AutoStaticsCleanupAttribute.html) source generator to Unity 6.0–6.4.

Static fields, properties, and events marked with the `[AutoStaticsCleanup]` attribute are reset on every Editor play-mode transition.

## Contents

- [What it does](#what-it-does)
- [Installation](#installation)
- [Building from source](#building-from-source)
- [Setup](#setup)
  - [1. Define the trigger attributes and helper classes](#1-define-the-trigger-attributes-and-helper-classes)
  - [2. Drop the analyzer DLL into Unity](#2-drop-the-analyzer-dll-into-unity)
  - [3. Use it](#3-use-it)
- [Diagnostics](#diagnostics)
- [Migrating to Unity 6.5+](#migrating-to-unity-65)
- [How it works](#how-it-works)

## What it does

For every static member you opt in, on every Editor play-mode transition:

- **Settable fields and properties** are reassigned to their declared initializer (or `default` if none). Reference-typed targets get an `if (X is not null) X = …;` guard so the assignment is a no-op for never-touched fields.
- **Readonly collections** (`static readonly List<int> Items = new() { 1, 2 };`) are emptied via `Clear()` and their initializer elements re-added, keeping the reference stable.
- **Static events** are unsubscribed handler-by-handler via `GetInvocationList()`.
- **Generic types** like `class Singleton<T>` work out of the box: the cleanup method is emitted inside the open generic, and each closed instantiation registers itself when its static constructor first runs.
- Visibility doesn't matter — `private` and `internal` members work because the cleanup method is emitted into the target type itself (as a partial-class member) and has direct access.

The generated code matches what Unity 6.5's built-in generator produces for the same input — byte-for-byte inside the `#if !UNITY_6000_5_OR_NEWER` guard — so behavior is identical before and after the upgrade.

## Installation

You have two options. Pick whichever you trust more.

### Option A — Download the precompiled DLL (convenience)

Each [tagged release](https://github.com/alexisstrat/unity-auto-statics-cleanup-generator/releases) ships `AutoStaticsCleanup.dll` as an asset, alongside:

- `AutoStaticsCleanup.dll.sha256` — checksum for manual verification.
- `AutoStaticsCleanup-Setup-x.y.z.unitypackage` (+ `.sha256`) — the required user-side files from [Setup step 1](#1-define-the-trigger-attributes-and-helper-classes) as an importable package, so you don't have to create them by hand. It contains only the four `.cs` files below — not the DLL.
- GitHub-issued [build-provenance attestations](https://docs.github.com/en/actions/security-guides/using-artifact-attestations-to-establish-provenance-for-builds) proving both assets were built by this repo's CI from the tagged commit.

**Verify the attestation** before dropping the DLL into your project:

```bash
gh attestation verify AutoStaticsCleanup.dll \
  --repo alexisstrat/unity-auto-statics-cleanup-generator
```

A passing verification means the DLL came from this repository's release workflow on the source commit at that tag — nothing else can produce a valid attestation under this repo's name.

Once verified, continue to [Setup](#setup).

### Option B — Build from source (recommended for full transparency)

Building yourself is the strongest trust signal — you read the source, you produce the binary, no third party is in the chain. See [Building from source](#building-from-source) below.

## Building from source

The shippable artifact is `AutoStaticsCleanup/bin/Release/netstandard2.0/AutoStaticsCleanup.dll`. Roslyn analyzers must target **netstandard2.0**; the project is already set up that way.

```bash
# Release build — produces the DLL you drop into Unity
dotnet build AutoStaticsCleanup/AutoStaticsCleanup.csproj -c Release

# Build the whole solution (generator + tests + benchmarks)
dotnet build AutoStaticsCleanup.sln

# Run the test suite
dotnet test AutoStaticsCleanup.Tests/AutoStaticsCleanup.Tests.csproj

# Run a single test
dotnet test AutoStaticsCleanup.Tests/AutoStaticsCleanup.Tests.csproj \
    --filter "FullyQualifiedName~TestMethodName"

# Generator benchmarks (cold + incremental cases, allocation breakdown)
dotnet run -c Release --project AutoStaticsCleanup.Benchmarks
# Filter to one case:
dotnet run -c Release --project AutoStaticsCleanup.Benchmarks -- --filter "*ColdRun*"
# Quick smoke job (fewer iterations):
dotnet run -c Release --project AutoStaticsCleanup.Benchmarks -- --job short
```

The test suite also includes `IncrementalCacheTests` which use Roslyn's `trackIncrementalGeneratorSteps` to verify that editing a file without attributes leaves every user-facing pipeline step `Cached`/`Unchanged`.

## Setup

### 1. Define the trigger attributes and helper classes

The easiest way is to import `AutoStaticsCleanup-Setup-x.y.z.unitypackage` from the [release assets](https://github.com/alexisstrat/unity-auto-statics-cleanup-generator/releases) (Assets → Import Package → Custom Package…), which places the 4 files below under `Assets/AutoStaticsCleanup/`. If you'd rather create them by hand, they are (the package source lives in [`UnityPackage/`](UnityPackage/)):

**Namespaces and type names for the attributes and the `DelegateAutoCleanup` class must be exactly as shown in order for the generator to work and not break in a Unity 6.5+ update.**

`AutoStaticsCleanupAttribute.cs`
```csharp
#if !UNITY_6000_5_OR_NEWER
using System;

namespace Unity.Scripting.LifecycleManagement
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Field
            | AttributeTargets.Property | AttributeTargets.Event, AllowMultiple = true)]
    public class AutoStaticsCleanupAttribute : Attribute { }
}
#endif
```

`NoAutoStaticsCleanupAttribute.cs`
```csharp
#if !UNITY_6000_5_OR_NEWER
using System;

namespace Unity.Scripting.LifecycleManagement
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Field
            | AttributeTargets.Property | AttributeTargets.Event, AllowMultiple = false)]
    public class NoAutoStaticsCleanupAttribute : Attribute { }
}
#endif
```

`DelegateAutoCleanup.cs` — the registration wrapper. Each generated cleanup method is registered through `CreateForPlayMode`; the constructor self-registers into a static list.
```csharp
#if !UNITY_6000_5_OR_NEWER
using System;
using System.Collections.Generic;

namespace UnityEngine
{
    public sealed class DelegateAutoCleanup
    {
        private static readonly List<DelegateAutoCleanup> Instances = new();

        private readonly Action _cleanup;
        private readonly string _ownerDescription;

        private DelegateAutoCleanup(Action cleanup, string ownerDescription)
        {
            _cleanup = cleanup ?? throw new ArgumentNullException(nameof(cleanup));
            _ownerDescription = ownerDescription;
            Instances.Add(this);
        }

        public static DelegateAutoCleanup CreateForPlayMode(Action cleanup, string ownerDescription = "")
            => new(cleanup, ownerDescription);

        public static IEnumerable<DelegateAutoCleanup> RegisteredInstances => Instances;

        public void Cleanup() => _cleanup();

        public override string ToString() => _ownerDescription;
    }
}
#endif
```

`AutoStaticsCleanupRegistrar.cs` — editor-only central hook (must be inside an `Editor/` folder).
```csharp
#if UNITY_EDITOR && !UNITY_6000_5_OR_NEWER
using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace AutoStaticsCleanup
{
    [InitializeOnLoad]
    internal static class AutoStaticsCleanupRegistrar
    {
        static AutoStaticsCleanupRegistrar()
        {
            EditorApplication.playModeStateChanged -= OnChange;
            EditorApplication.playModeStateChanged += OnChange;
        }

        private static void OnChange(PlayModeStateChange change)
        {
            if (change != PlayModeStateChange.ExitingEditMode
                && change != PlayModeStateChange.EnteredEditMode) return;
            
            var snapshot = DelegateAutoCleanup.RegisteredInstances.ToArray();
            foreach (var c in snapshot)
            {
                try
                {
                    c.Cleanup();
                }
                catch (Exception e)
                {
                    Debug.LogError("Failed to cleanup " + c + ": " + e.Message);
                }
            }
        }
    }
}
#endif
```

### 2. Drop the analyzer DLL into Unity

Take the `AutoStaticsCleanup.dll` you produced in [Building](#building) and copy it into your Unity project under `Assets/` (a folder like `Assets/Plugins/AutoStaticsCleanup/` is conventional).

In Unity's Project window, select the DLL to open the Plugin Inspector, then:

1. Under **Select platforms for plugin**, disable **Any Platform**.
2. Under **Include Platforms**, disable both **Editor** and **Standalone** (Roslyn analyzers must not be included in any build target).
3. Under **Asset Labels**, click the label icon (bottom-right of the Inspector) to open the Asset Labels sub-menu.
4. Type `RoslynAnalyzer` into the input field and press Enter to create and assign the label. **The label is case-sensitive and must be exact.** Once created, it stays in the Asset Labels list for reuse on other analyzers.
5. Click **Apply**.

Unity will reimport scripts and the generator will start producing cleanup code for every assembly that references it.

### 3. Use it

Member-level — opt in one piece of state at a time. The containing class must be `partial`.

```csharp
using Unity.Scripting.LifecycleManagement;
using System.Collections.Generic;

public static partial class GameCache
{
    [AutoStaticsCleanup] public static int FrameCount = 0;
    [AutoStaticsCleanup] public static List<string> Loaded = new();
    [AutoStaticsCleanup] public static event System.Action OnReset;
}
```

Type-level — opt in every static member of a type, with selective opt-out:

```csharp
[AutoStaticsCleanup]
public static partial class GameCache
{
    public static int FrameCount;
    public static Dictionary<string, int> Counters = new();

    [NoAutoStaticsCleanup] public static int PersistAcrossPlay;
}
```

Generic base class — every closed instantiation that's been touched in play mode is reset automatically. The cleanup method and registration field are emitted inside the open generic; each closed type registers when its statics first initialize.

```csharp
public partial class Singleton<T> : MonoBehaviour where T : MonoBehaviour
{
    [AutoStaticsCleanup] private static T _instance;

    public static T Instance =>
        _instance ??= FindFirstObjectByType<T>();
}

public class PlayerManager : Singleton<PlayerManager> { }
public class EnemyManager  : Singleton<EnemyManager>  { }
```

> **Note on registration:** classes (generic or not) only register when their statics initialize (i.e. the class was used at least once). Untouched classes need no cleanup, so this is correct — nothing is enumerated through reflection. This matches Unity 6.5's built-in behavior. Types nested inside a generic outer (`Outer<T>.Inner`) work the same way: only closed instantiations whose statics initialize in play mode actually register.

## Diagnostics

`AutoStaticsCleanupAnalyzer` (a `[DiagnosticAnalyzer]` shipped in the same DLL as the generator) reports the rules below. Most shape rules fire when the attribute is on the **member directly** — class-level `[AutoStaticsCleanup]` silently filters unfit members to match Unity 6.5's "reset everything resettable, leave the rest alone" semantic. The exceptions are the shapes that **break outright on Unity 6.5**: readonly non-cleanable members (ASC002/ASC003) and disposable members without an initializer (ASC009) crash Unity 6.5's generator run for the whole assembly, and readonly members that are null at cleanup time (ASC010) compile there but throw `NullReferenceException` on every play-mode transition. All four error under a class-level attribute too, so they get fixed before an upgrade. ASC008 fires at the type level whenever the attributed type has an explicit static constructor.

All rules are Errors. Quick fixes are available for ASC001/002/003/006/010; ASC004/007/008/009 are diagnostic-only — the message tells you what to do, but the change requires a manual decision.

| ID | Severity | When | Quick fix |
|---|---|---|---|
| `ASC001` | Error | The attributed type (or any enclosing type) is not declared `partial`, and the generator would otherwise emit code for at least one member. | Add `partial` modifier to the offending type. |
| `ASC002` | Error | Member-level `[AutoStaticsCleanup]` on a `readonly` field that can't be reset — the type has no `Clear()` method, or the initializer isn't a `new …` expression (e.g. a factory call). Readonly + `Clear()` + object-creation init is supported — including braced elements (`new() { 1, 2 }`), which are restored after `Clear()`. Readonly members of exempt types (unmanaged, `string`, known-immutable like `Uri`/`Regex`, arrays of unmanaged) are silently skipped, matching Unity 6.5. | Remove the `readonly` modifier. |
| `ASC003` | Error | Member-level on a property that can't be reset: manual accessors (Unity 6.5 only resets auto-properties), init-only setters, or getter-only auto-properties that don't qualify for the readonly `Clear()` strategy. Getter-only auto-properties of Clear-able collection types are supported; exempt types are silently skipped. | Add a `set;` accessor (auto-properties only — manual / expression-bodied / init-only properties need a manual fix). |
| `ASC004` | Error | Member-level on a manual event (explicit `add`/`remove`) — the unsubscribe loop needs the compiler-generated backing field. | — |
| `ASC006` | Error | Member-level on an instance member — only static state is cleaned up. | Add the `static` modifier. |
| `ASC007` | Error | Member-level on a `const` field — constants can't be reset. | — |
| `ASC008` | Error | The attributed type has an explicit `static T() { … }` constructor — its run-order relative to the generated cleanup-registration initialization is unspecified, which can leave state re-initialized after cleanup. | — |
| `ASC009` | Error | A static field or auto-property whose type has a `Dispose()` method but which has no initializer — after disposing there is nothing to construct a replacement from, so Unity 6.5's generator refuses the shape. Fires at member level and under class-level attributes. | — |
| `ASC010` | Error | A `readonly` static field or getter-only auto-property whose type has a `Clear()` but whose initializer is absent, `null`, or `default` — the member is null forever (readonly statics can't be assigned later, and ASC008 bans static constructors), so the `Clear()` cleanup would throw `NullReferenceException` on every play-mode transition. Unity 6.5 generates that failing cleanup; this rule forces the fix pre-upgrade. Fires at member level and under class-level attributes. Value types are unaffected (a default struct instance is real, cleanable state). | Initialize with `new()` (offered when the type is concrete with an accessible parameterless constructor). |

### Disposable fields and properties

When an attributed field or property's type has a parameterless instance `Dispose()` method (declared or inherited) and there's an initializer to reassign to, the generator emits `field.Dispose()` before the reassignment. This handles `Stream`, `HttpClient`, and any type with a `Dispose()` without leaking the previous instance on play-mode transitions.

A disposable static **without** an initializer is not reset at all — there is nothing to construct a replacement from after disposing, and Unity 6.5's generator refuses the shape. ASC009 reports it (member-level and class-level) so the code is fixed before an upgrade.

Detection is duck-typed by method name — exactly like Unity 6.5's — so a `Dispose()` counts even without `IDisposable`, and (conversely) a type parameter constrained to `IDisposable` does *not* trigger a dispose call.

### Readonly collections with `Clear()`

`static readonly` fields are normally unreachable for cleanup (the generator can't reassign them). When the field type exposes a parameterless `Clear()` method and the initializer is an object-creation expression, the generator emits `field.Clear();` instead — empties the container in place while keeping the reference stable — and re-adds any braced initializer elements afterwards:

```csharp
[AutoStaticsCleanup] public static readonly Dictionary<string, int> Weights = new() { { "a", 1 } };
// generated cleanup:
//     Weights.Clear();
//     Weights["a"] = 1;
```

Covers `List<T>`, `Dictionary<K,V>`, `HashSet<T>`, `Queue<T>`, `Stack<T>`, `ConcurrentDictionary<K,V>`, and any user wrapper with a matching `Clear()`. Getter-only auto-properties of such types follow the same rules.

The initializer must actually produce an instance: a readonly collection declared without one (or with `= null` / `= default`) is null forever, so `Clear()` would throw on every transition — that shape is rejected with ASC010 instead of being emitted (Unity 6.5 emits it and fails at runtime).

### Exempt readonly types

Readonly statics of types that can't meaningfully leak play-mode state are silently left alone (no reset, no diagnostic), matching Unity 6.5's built-in exemption list: unmanaged types (`int`, enums, user structs of unmanaged fields), `string`, arrays of unmanaged element types, and a whitelist of known-immutable types (`System.Uri`, `System.Text.RegularExpressions.Regex`, `System.Version`, `UnityEngine.GUIContent`, `Unity.Profiling.ProfilerMarker`, …).

## Migrating to Unity 6.5+

1. Upgrade the Unity Editor to 6.5+.
2. **Delete `AutoStaticsCleanup.dll` from the project.** Otherwise the backport's analyzer continues to run alongside Unity's built-in one, producing its own diagnostics. Removing the DLL also stops the redundant source-generator passes — the emitted code is already gated out at compile time, but the per-keystroke symbol walks aren't.
3. Optionally delete the user-side helper files (`AutoStaticsCleanupAttribute.cs`, `NoAutoStaticsCleanupAttribute.cs`, `DelegateAutoCleanup.cs`, `AutoStaticsCleanupRegistrar.cs`). They compile to nothing under `#if !UNITY_6000_5_OR_NEWER`, so leaving them in is harmless.

## How it works

- **Detection:** `IIncrementalGenerator.ForAttributeWithMetadataName("Unity.Scripting.LifecycleManagement.AutoStaticsCleanupAttribute", …)` finds attribute targets across the compilation.
- **Per-type emission:** for each attributed type the generator emits a partial-class block containing one static cleanup method (`__AutoStaticsCleanup_UnityEngine_PlayModeScope_Both`) with the assignments / `Clear()`-plus-restore statements / unsubscribe loops, and one registration field: `static readonly UnityEngine.DelegateAutoCleanup __autoCleanup_UnityEngine_PlayModeScope_Both = UnityEngine.DelegateAutoCleanup.CreateForPlayMode(__AutoStaticsCleanup_…, "Namespace.TypeName");`. The string is the owner description Unity 6.5 shows when a cleanup throws. This is the same Action-based shape Unity 6.5's generator produces — one shared wrapper type instead of a nested subclass per class.
- **Registration:** `DelegateAutoCleanup`'s constructor self-registers the instance with a registry. Registration is lazy and per-class: the field initializes together with the class's other statics, so a class that was never touched is never registered — and needs no cleanup. A central editor hook (`[InitializeOnLoad]`, one subscription only) invokes `Cleanup()` on every registered instance on play-mode transitions.
- **Initializer preservation:** the field/property initializer expression is captured verbatim and emitted unchanged (newlines, collection initializers, target-typed `new()` all carry over). No accessibility check is needed because the cleanup method is emitted into the target type itself.
- **Minimal usings:** every captured initializer is walked with the semantic model to collect the namespaces of types/methods it references. Only source-file `using` directives whose target namespace shows up in that set make it into the generated file (`using static …;` and `using Alias = …;` are kept unconditionally — too risky to trace). Generated files for simple primitive resets end up with just `using Unity.Scripting.LifecycleManagement;`.
- **Output:** one generated file per attributed type, named `{Namespace}.{ClassName}.autocleanup.generated.cs`. Generic types flatten the parameter list with underscore markers (`Singleton<T>` → `Singleton_T_.autocleanup.generated.cs`, `Pair<T1, T2>` → `Pair_T1_T2_.autocleanup.generated.cs`); nested types include the outer chain (`Outer.Inner.autocleanup.generated.cs`). Each file is wrapped in `#if !UNITY_6000_5_OR_NEWER`. Per-file emission means edits to one attributed type don't invalidate the cached parse trees of the others.

