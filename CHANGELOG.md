# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

The release workflow extracts the section matching the version in
`AutoStaticsCleanup.csproj` and publishes it as the GitHub Release notes.

## v1.1.0

Matches the output shape and reset semantics of Unity 6.5's built-in
generator. Code that builds clean on 1.1.0
behaves identically after an upgrade to Unity 6.5.

### Upgrading from 1.0.0

1. Delete `PlayModeScopeAutoCleanup.cs` and the old `PlayModeScopeAutoCleanupRegistrar.cs` TypeCache-based
   registrar from your project.
2. Import `AutoStaticsCleanup-Setup-1.1.0.unitypackage` from the release
   assets (or copy the four helper files from the README).
3. Replace `AutoStaticsCleanup.dll` with the 1.1.0 build.

### Breaking changes

- Generated code now registers cleanup through a static
  `UnityEngine.DelegateAutoCleanup` field instead of a nested
  `PlayModeScopeAutoCleanup` subclass, so the user-side helper files must be
  replaced — see Upgrading above.
- Manual (non-auto) properties are no longer reset.
  - A member-level attribute on one now errors (ASC003); under a class-level
    attribute it is silently skipped and keeps its value across play sessions.
    Convert to an auto-property to keep the reset.
- Disposable statics without an initializer are no longer reset.
  - Now an error (ASC009): add an initializer to keep the reset, or remove
    the attribute / opt out with `[NoAutoStaticsCleanup]`.
- Class-level `[AutoStaticsCleanup]` now errors on members Unity 6.5's
  generator refuses: readonly without a usable `Clear()` (ASC002/ASC003) and
  disposable without an initializer (ASC009).
  - Previously silent code may now fail to build; each message says how to
    fix the member or opt it out.
- `Dispose()` detection is duck-typed by method name.
  - Fields typed as an `IDisposable`-constrained type parameter are now
    reassigned without being disposed; types with a `Dispose()` method but no
    `IDisposable` interface are now disposed.
- Readonly members that are null at cleanup time (no initializer, `= null`,
  or `= default` on a reference type with a `Clear()`) are rejected with the
  new ASC010 error instead of being reset.
  - Unity 6.5 generates a `Clear()` call for these that throws
    `NullReferenceException` on every play-mode transition; ASC010 forces the
    fix before the upgrade. A quick fix adds `= new()`.

### Added

- Readonly collections with braced initializers (`new() { 1, 2 }`) are reset
  via `Clear()` plus re-adding the elements. Previously ASC002.
- Getter-only auto-properties of `Clear()`-able types are reset the same way.
  Previously ASC003.
- Readonly exemption list: unmanaged types, `string`, arrays of unmanaged
  elements, and known-immutable types (`System.Uri`, `Regex`, `GUIContent`, …)
  are silently left alone.
- `partial struct` support (including types nested in structs).
- ASC009 (Error): disposable static without an initializer cannot be reset.
- ASC010 (Error): readonly member is null at cleanup time, so the `Clear()`
  cleanup would throw. Quick fix: initialize with `new()`.
- Setup `.unitypackage` release asset with the four required helper files.
  The analyzer DLL stays a separate, attestation-verifiable asset.
- Each registration carries an owner-description string (`MyNs.Outer.Inner`),
  surfaced in cleanup-failure logs.

### Changed

- Cleanup statements are grouped as fields, then properties, then events.
- Null guards apply to reference types only; unconstrained generic
  type-parameter fields are assigned bare.
- ASC002, ASC003, and ASC008 messages reworded.

## v1.0.0

Initial release: incremental source generator, analyzer (ASC001–ASC008, minus
ASC005), and code-fix provider backporting `[AutoStaticsCleanup]` static-state
reset to Unity 6.0–6.4.
