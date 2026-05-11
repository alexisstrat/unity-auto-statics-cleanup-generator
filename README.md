# AutoStaticsCleanup Generator

A backport of Unity 6.5+'s built-in [AutoStaticsCleanup](https://docs.unity3d.com/6000.5/Documentation/ScriptReference/Unity.Scripting.LifecycleManagement.AutoStaticsCleanupAttribute.html) source generator to Unity 6.0–6.4.

Static fields, properties, and events marked with the `[AutoStaticsCleanup]` attribute are reset on every Editor play-mode transition.

## Contents

- [What it does](#what-it-does)
- [Installation](#installation)
- [Building from source](#building-from-source)
- [Setup](#setup)
  - [1. Define the trigger attributes and runtime scaffolding](#1-define-the-trigger-attributes-and-runtime-scaffolding)
  - [2. Drop the analyzer DLL into Unity](#2-drop-the-analyzer-dll-into-unity)
  - [3. Use it](#3-use-it)
- [Diagnostics](#diagnostics)
- [Migrating to Unity 6.5+](#migrating-to-unity-65)
- [How it works](#how-it-works)

## What it does

For every static member you opt in, on every Editor play-mode transition:

- **Settable fields and properties** are reassigned to their declared initializer (or `default` if none). Reference-typed targets get an `if (X is not null) X = …;` guard so the assignment is a no-op for never-touched fields.
- **Static events** are unsubscribed handler-by-handler via `GetInvocationList()`.
- **Generic types** like `class Singleton<T>` work out of the box: the cleanup class is emitted inside the open generic, and each closed instantiation registers itself when its static constructor first runs.
- Visibility doesn't matter — `private` and `internal` members work because the cleanup class is nested inside the target type and has direct access.

## Installation

You have two options. Pick whichever you trust more.

### Option A — Download the precompiled DLL (convenience)

Each [tagged release](https://github.com/alexisstrat/unity-auto-statics-cleanup-generator/releases) ships `AutoStaticsCleanup.dll` as an asset, alongside:

- `AutoStaticsCleanup.dll.sha256` — checksum for manual verification.
- A GitHub-issued [build-provenance attestation](https://docs.github.com/en/actions/security-guides/using-artifact-attestations-to-establish-provenance-for-builds) proving the DLL was built by this repo's CI from the tagged commit.

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

### 1. Define the trigger attributes and runtime scaffolding

Create these 4 files in your project. <br>**Namespaces for attributes and abstract class must be exactly defined as shown in order for the generator to work and not break in a Unity 6.5+ update.**

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

`PlayModeScopeAutoCleanup.cs` — abstract base class. Generated cleanup classes derive from it.
```csharp
#if !UNITY_6000_5_OR_NEWER
namespace UnityEngine
{
    public abstract class PlayModeScopeAutoCleanup
    {
        public abstract void Cleanup();
    }
}
#endif
```

`PlayModeScopeAutoCleanupRegistrar.cs` — editor-only central hook (must be inside an `Editor/` folder).
```csharp
#if UNITY_EDITOR && !UNITY_6000_5_OR_NEWER
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace MyCustomNameSpace
{
    [InitializeOnLoad]
    internal static class PlayModeScopeAutoCleanupRegistrar
    {
        private static readonly Dictionary<Type, PlayModeScopeAutoCleanup> SByType = new();
        
        static PlayModeScopeAutoCleanupRegistrar()
        {
            foreach (var t in TypeCache.GetTypesDerivedFrom<PlayModeScopeAutoCleanup>())
            {
                if (t.IsAbstract || t.ContainsGenericParameters) continue;
                var instance = (PlayModeScopeAutoCleanup) Activator.CreateInstance(t);
                var instanceType = instance.GetType();
                SByType[instanceType] = instance;
            }

            EditorApplication.playModeStateChanged -= OnChange;
            EditorApplication.playModeStateChanged += OnChange;
        }

        private static void OnChange(PlayModeStateChange change)
        {
            if (change != PlayModeStateChange.ExitingEditMode
                && change != PlayModeStateChange.ExitingPlayMode) return;
            foreach (var c in SByType.Values)
                c.Cleanup();
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

Generic base class — every closed instantiation that's been touched in play mode is reset automatically. The cleanup class is emitted inside the open generic; each closed type's static constructor runs the registration when it's first referenced.

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

> **Note on generics:** closed instantiations only register if their static constructor runs (i.e. the type was used at least once in play mode). Untouched types need no cleanup, so this is correct — but it does mean that abstract or unused closed types aren't enumerated through reflection. This matches Unity 6.5's built-in behavior. Types nested inside a generic outer (`Outer<T>.Inner`) compile and emit cleanup code, but only closed instantiations whose static constructor runs in play mode actually register — same caveat applies.

## Diagnostics

`AutoStaticsCleanupAnalyzer` (a `[DiagnosticAnalyzer]` shipped in the same DLL as the generator) reports the rules below. ASC002-007 fire only when the attribute is on the **member directly** — class-level `[AutoStaticsCleanup]` silently filters unfit members to match Unity 6.5's "reset everything resettable, leave the rest alone" semantic. ASC008 fires at the type level whenever the attributed type has an explicit static constructor.

All rules are Errors. Quick fixes are available for ASC001/002/003/006; ASC004/007/008 are diagnostic-only — the message tells you what to do, but the change requires a manual decision.

| ID | Severity | When | Quick fix |
|---|---|---|---|
| `ASC001` | Error | The attributed type (or any enclosing type) is not declared `partial`, and the generator would otherwise emit code for at least one member. | Add `partial` modifier to the offending type. |
| `ASC002` | Error | Member-level `[AutoStaticsCleanup]` on a `readonly` field that can't be reset — either the type has no `Clear()` method or the initializer is non-trivial (e.g., `new() { 1, 2, 3 }`). Readonly + `Clear()` + trivial init (`new()`, `new T()`) is supported and emits a `Clear()` call instead. | Remove the `readonly` modifier. |
| `ASC003` | Error | Member-level on a property without a settable setter (get-only, init-only) — the generator can't reset it. | Add a `set;` accessor (auto-properties only — manual / expression-bodied / init-only properties need a manual fix). |
| `ASC004` | Error | Member-level on a manual event (explicit `add`/`remove`) — the unsubscribe loop needs the compiler-generated backing field. | — |
| `ASC006` | Error | Member-level on an instance member — only static state is cleaned up. | Add the `static` modifier. |
| `ASC007` | Error | Member-level on a `const` field — constants can't be reset. | — |
| `ASC008` | Error | The attributed type has an explicit `static T() { … }` constructor — its run-order relative to the generated cleanup-class initialization is unspecified, which can leave state re-initialized after cleanup. | — |

### IDisposable fields and properties

When an attributed field or property's type implements `System.IDisposable` (directly, transitively, or via a generic constraint) and there's an initializer to reassign to, the generator emits `field?.Dispose()` before the reassignment. This handles `Stream`, `HttpClient`, and any user `IDisposable` implementation without leaking the previous instance on play-mode transitions.

### Readonly collections with `Clear()`

`static readonly` fields are normally unreachable for cleanup (the generator can't reassign them). When the field type exposes a public parameterless `Clear()` method **and** the initializer is trivial (`new()`, `new T()`, `null`, `default`), the generator emits `field.Clear();` instead — empties the container in place while keeping the reference stable. Covers `List<T>`, `Dictionary<K,V>`, `HashSet<T>`, `Queue<T>`, `Stack<T>`, `ConcurrentDictionary<K,V>`, and any user wrapper with a matching `Clear()`.

Non-trivial initializers like `new() { 1, 2, 3 }` are intentionally rejected (ASC002) because `Clear()` would empty the collection rather than restore the original elements.

## Migrating to Unity 6.5+

1. Upgrade the Unity Editor to 6.5+.
2. **Delete `AutoStaticsCleanup.dll` from the project.** Otherwise the backport's analyzer continues to run alongside Unity's built-in one, producing its own diagnostics. Removing the DLL also stops the redundant source-generator passes — the emitted code is already gated out at compile time, but the per-keystroke symbol walks aren't.
3. Optionally delete the user-side scaffolding files (`AutoStaticsCleanupAttribute.cs`, `NoAutoStaticsCleanupAttribute.cs`, `PlayModeScopeAutoCleanup.cs`, `PlayModeScopeAutoCleanupRegistrar.cs`). They compile to nothing under `#if !UNITY_6000_5_OR_NEWER`, so leaving them in is harmless.

## How it works

- **Detection:** `IIncrementalGenerator.ForAttributeWithMetadataName("Unity.Scripting.LifecycleManagement.AutoStaticsCleanupAttribute", …)` finds attribute targets across the compilation.
- **Per-type emission:** for each attributed type the generator emits a `partial class T { class UnityEngine_PlayModeScopeAutoCleanup_Both_AutoCleanupType : UnityEngine.PlayModeScopeAutoCleanup { … } static readonly … = new(); }` block. The nested class overrides `Cleanup()` with direct assignments / unsubscribe loops; the static readonly field instantiates it.
- **Registration:** the base class's constructor self-registers the instance with a registry. A central editor hook (`[InitializeOnLoad]`, one subscription only) walks `TypeCache.GetTypesDerivedFrom<PlayModeScopeAutoCleanup>()` on domain reload, force-instantiates non-generic derived types, and invokes `Cleanup()` on every registered instance on `ExitingEditMode` / `ExitingPlayMode`.
- **Initializer preservation:** the field/property initializer expression is captured verbatim and emitted unchanged (newlines, collection initializers, target-typed `new()` all carry over). No accessibility check is needed because the cleanup class is nested inside the target type.
- **Minimal usings:** every captured initializer is walked with the semantic model to collect the namespaces of types/methods it references. Only source-file `using` directives whose target namespace shows up in that set make it into the generated file (`using static …;` and `using Alias = …;` are kept unconditionally — too risky to trace). Generated files for simple primitive resets end up with just `using System;` and `using Unity.Scripting.LifecycleManagement;`.
- **Output:** one generated file per attributed type, named `{Namespace}.{ClassName}.autocleanup.generated.cs`. Generic types flatten the parameter list with underscore markers (`Singleton<T>` → `Singleton_T_.autocleanup.generated.cs`, `Pair<T1, T2>` → `Pair_T1_T2_.autocleanup.generated.cs`); nested types include the outer chain (`Outer.Inner.autocleanup.generated.cs`). Each file is wrapped in `#if !UNITY_6000_5_OR_NEWER`. Per-file emission means edits to one attributed type don't invalidate the cached parse trees of the others.

