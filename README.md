# AutoStaticsCleanup Generator

A Roslyn incremental source generator for Unity that automatically resets static fields, properties, and events when entering or exiting Play Mode in the Editor.

> **Compatibility:** works on **Unity 6.0 to Unity 6.4**. Unity 6.5 ships this functionality natively, with one caveat — its built-in implementation requires every class carrying `[AutoStaticsCleanup]` to be declared `partial`. This generator doesn't require `partial`, but **marking your attribute-bearing classes `partial` now** makes the eventual upgrade a true no-code-change drop-in: the analyzer DLL and the attribute stubs both auto-exclude via `#if !UNITY_6000_5_OR_NEWER`, and your existing source compiles unchanged against Unity's built-in implementation.

## What it does

For every static member you opt in, on every Editor play-mode transition:

- **Settable fields and properties** are reassigned to their declared initializer (or `default`/`null` if none).
- **Readonly collections** with a public parameterless `Clear()` (`List<T>`, `Dictionary<TKey,TValue>`, `HashSet<T>`, custom types) are cleared in place.
- **Static events** are reset to `null`.
- **Generic types** like `class Singleton<T>` are expanded to every closed instantiation (`Singleton<Player>`, `Singleton<Enemy>`, …) and each one is reset.
- Visibility doesn't matter — `private` and `internal` members work without exposing anything.

## How it works

- **Detection:** `IIncrementalGenerator.ForAttributeWithMetadataName("Unity.Scripting.LifecycleManagement.AutoStaticsCleanupAttribute", …)` discovers attribute targets across the compilation.
- **Strategy selection per member:** `DirectAssign` / `ReflectionAssign` for settable members (reflection when private or in an inaccessible type), `DirectClear` / `ReflectionClear` for readonly collections detected by walking the type for a public parameterless `Clear()`.
- **Initializer preservation:** the right-hand side of the field/property declaration is captured verbatim and emitted into the cleanup code, after a check that every symbol it references is externally accessible. If anything's private, falls back to `default`/`null`.
- **Open generics:** detected via `INamedTypeSymbol.IsGenericType`, mapped to a `typeof(Foo<>)` form, then resolved at static-init time via `UnityEditor.TypeCache.GetTypesDerivedFrom` — which only indexes *inheritance*, so the mechanism handles any closed instantiation that something inherits from (`class X : Foo<X>`, `class IntBus : Bus<int>`, etc.) but doesn't find isolated uses like `static Foo<int> _x;` that aren't backed by a derived class. The resulting `FieldInfo[]` is cached for the lifetime of the domain — play-mode transitions just iterate the array.
- **Output:** the generator runs once per consuming compilation, so each assembly that uses `[AutoStaticsCleanup]` gets its own `AutoStaticsCleanup.generated.cs`. Each generated class is registered with `[UnityEditor.InitializeOnLoad]`, subscribes to `EditorApplication.playModeStateChanged`, and runs its assembly's resets on `ExitingEditMode` / `ExitingPlayMode`. The whole file is wrapped in `#if UNITY_EDITOR && !UNITY_6000_5_OR_NEWER`.

## Setup

### 1. Define the trigger attributes

The generator looks for `[AutoStaticsCleanup]` and `[NoAutoStaticsCleanup]` by their fully qualified name `Unity.Scripting.LifecycleManagement.*`. From Unity 6.5 these are part of Unity itself; on earlier versions you must define them yourself. Drop these two files anywhere in `Assets/`:

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

### 2. Drop the analyzer DLL into Unity

Build a Release binary (see [Building](#building)) and copy `AutoStaticsCleanup.dll` into your Unity project under `Assets/` (a folder like `Assets/Plugins/AutoStaticsCleanup/` is conventional).

In Unity's Project window, select the DLL to open the Plugin Inspector, then:

1. Under **Select platforms for plugin**, disable **Any Platform**.
2. Under **Include Platforms**, disable both **Editor** and **Standalone** (Roslyn analyzers must not be included in any build target).
3. Under **Asset Labels**, click the label icon (bottom-right of the Inspector) to open the Asset Labels sub-menu.
4. Type `RoslynAnalyzer` into the input field and press Enter to create and assign the label. **The label is case-sensitive and must be exact.** Once created, it stays in the Asset Labels list for reuse on other analyzers.
5. Click **Apply**.

Unity will reimport scripts and the generator will start producing cleanup code for every assembly that references it.

### 3. Use it

Member-level — opt in one piece of state at a time:

```csharp
using Unity.Scripting.LifecycleManagement;
using System.Collections.Generic;

public static class GameCache
{
    [AutoStaticsCleanup] public static int FrameCount = 0;
    [AutoStaticsCleanup] public static readonly List<string> Loaded = new();
    [AutoStaticsCleanup] public static event System.Action OnReset;
}
```

Type-level — opt in every static member of a type, with selective opt-out:

```csharp
[AutoStaticsCleanup]
public static class GameCache
{
    public static int FrameCount;
    public static readonly Dictionary<string, int> Counters = new();

    [NoAutoStaticsCleanup] public static int PersistAcrossPlay;
}
```

Generic base class — every closed instantiation found via `TypeCache` is reset. The `Singleton<T>` pattern is the most common case, but the same mechanism works for any generic base with concrete derived types (`Repository<T>`, `Cache<TKey, TValue>`, `EventBus<T>`, …):

```csharp
public class Singleton<T> : MonoBehaviour where T : MonoBehaviour
{
    [AutoStaticsCleanup] private static T _instance;

    public static T Instance =>
        _instance ??= FindFirstObjectByType<T>();
}

public class PlayerManager : Singleton<PlayerManager> { }
public class EnemyManager  : Singleton<EnemyManager>  { }
// On play-mode change, both PlayerManager._instance and
// EnemyManager._instance are nulled out automatically.
```

> **Note on generics:** the generator finds closed instantiations by walking `UnityEditor.TypeCache.GetTypesDerivedFrom(typeof(Foo<>))`, which only indexes inheritance. Anything with a concrete derived type (`class X : Singleton<X>`, `class IntBus : Bus<int>`) works out of the box. Standalone uses like `static Foo<int> _x;` without an accompanying derived class won't be detected — that's the limitation of an inheritance-indexed approach. Types nested inside generic types are also not supported.

## Building

```bash
# Build everything
dotnet build AutoStaticsCleanup.sln

# Run the test suite
dotnet test AutoStaticsCleanup.Tests/AutoStaticsCleanup.Tests.csproj

# Run a single test
dotnet test AutoStaticsCleanup.Tests/AutoStaticsCleanup.Tests.csproj \
    --filter "FullyQualifiedName~TestMethodName"

# Release build (drop into Unity)
dotnet build AutoStaticsCleanup/AutoStaticsCleanup.csproj -c Release
```

The shippable artifact is `AutoStaticsCleanup/bin/Release/netstandard2.0/AutoStaticsCleanup.dll`. Roslyn analyzers must target **netstandard2.0** — the project is already set up that way.