using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AutoStaticsCleanup;

/// <summary>
/// Diagnostic descriptors and validation helpers shared by
/// <see cref="AutoStaticsCleanupAnalyzer"/> (which reports them) and
/// <see cref="AutoStaticsCleanupGenerator"/> (which uses the shape predicates
/// to decide whether to emit code for a member).
///
/// Diagnostics fire only when the attribute is on a <i>member</i> directly.
/// Type-level <c>[AutoStaticsCleanup]</c> silently skips unfit members
/// (const, readonly, instance, manual events, etc.) — putting the attribute
/// on the class is "reset everything resettable, leave the rest alone."
/// Member-level attribution is an explicit signal that something is intended;
/// if the shape blocks it, that's a footgun worth flagging. All rules are
/// Errors.
/// </summary>
internal static class Validation
{
    public const string AttributeFullName = "Unity.Scripting.LifecycleManagement.AutoStaticsCleanupAttribute";
    public const string NoAttributeFullName = "Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanupAttribute";

    // -----------------------------------------------------------------
    //  Descriptors
    // -----------------------------------------------------------------

    public static readonly DiagnosticDescriptor MustBePartial = new(
        "ASC001",
        "Type must be 'partial'",
        "Type '{0}' must be declared 'partial' (and so must every enclosing type) to use [AutoStaticsCleanup]",
        "AutoStaticsCleanup",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ReadonlyNotSupported = new(
        "ASC002",
        "[AutoStaticsCleanup] readonly field cannot be reset",
        "Field '{0}' is readonly. Remove the 'readonly' modifier, or use a type with a parameterless Clear() method and a 'new …' initializer (collection initializers like 'new() { 1, 2 }' are restored element-by-element after Clear()).",
        "AutoStaticsCleanup",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor PropertyNeedsSetter = new(
        "ASC003",
        "[AutoStaticsCleanup] requires a settable auto-property",
        "Property '{0}' cannot be reset by the generated cleanup. Use a static auto-property with a 'set;' accessor (manual accessors and init-only setters are not supported; getter-only auto-properties work only for Clear-able collection types).",
        "AutoStaticsCleanup",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ManualEventNotSupported = new(
        "ASC004",
        "[AutoStaticsCleanup] does not support manual events",
        "Event '{0}' has explicit 'add'/'remove' accessors. Convert it to a field-like event, or remove [AutoStaticsCleanup].",
        "AutoStaticsCleanup",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor MemberMustBeStatic = new(
        "ASC006",
        "[AutoStaticsCleanup] requires a static member",
        "Member '{0}' is not static. Add the 'static' modifier, or remove [AutoStaticsCleanup].",
        "AutoStaticsCleanup",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ConstFieldNotSupported = new(
        "ASC007",
        "[AutoStaticsCleanup] cannot be applied to const fields",
        "Field '{0}' is const and cannot be reset. Remove [AutoStaticsCleanup] from the const field.",
        "AutoStaticsCleanup",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor DisposableNeedsInitializer = new(
        "ASC009",
        "[AutoStaticsCleanup] disposable static requires an initializer",
        "Member '{0}' has a Dispose() method but no initializer — after disposing there is nothing to construct a replacement from, so it cannot be reset. Add an initializer, or remove [AutoStaticsCleanup].",
        "AutoStaticsCleanup",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ReadonlyNullAtCleanup = new(
        "ASC010",
        "[AutoStaticsCleanup] readonly member is null at cleanup time",
        "Member '{0}' is readonly with no object-creation initializer, so it is null when cleanup runs and the generated Clear() call throws NullReferenceException on every play-mode transition — Unity 6000.5.4f1+ generates the same failing cleanup. Initialize it with 'new …', remove 'readonly', or exclude it with [NoAutoStaticsCleanup].",
        "AutoStaticsCleanup",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor StaticConstructorNotSupported = new(
        "ASC008",
        "[AutoStaticsCleanup] is incompatible with explicit static constructors",
        "Type '{0}' has an explicit static constructor; [AutoStaticsCleanup]'s cleanup-registration field initializes in unspecified order relative to it, which can leave the class re-initialized after cleanup. Remove the static constructor or [AutoStaticsCleanup].",
        "AutoStaticsCleanup",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    // -----------------------------------------------------------------
    //  Shape predicates (used by both analyzer and generator)
    // -----------------------------------------------------------------

    public static bool IsPartial(INamedTypeSymbol type)
    {
        foreach (var sr in type.DeclaringSyntaxReferences)
            if (sr.GetSyntax() is TypeDeclarationSyntax tds)
                foreach (var mod in tds.Modifiers)
                    if (mod.IsKind(SyntaxKind.PartialKeyword))
                        return true;
        return false;
    }

    public static bool IsPartialChain(INamedTypeSymbol type)
    {
        for (var t = type; t != null; t = t.ContainingType)
            if (!IsPartial(t))
                return false;
        return true;
    }

    public static bool IsFieldLikeEvent(IEventSymbol evt) =>
        evt.AddMethod?.IsImplicitlyDeclared ?? true;

    /// <summary>
    /// True if <paramref name="type"/> declares (or inherits) a parameterless
    /// instance method named <c>Dispose</c>. Deliberately duck-typed — no
    /// accessibility, return-type, or IDisposable-interface requirement.
    /// Notably this means explicit interface implementations do NOT count
    /// (their metadata name is "System.IDisposable.Dispose", and
    /// `field.Dispose()` wouldn't compile against them anyway), and type
    /// parameters never match (they have no members of their own).
    /// </summary>
    public static bool HasDisposeMethod(ITypeSymbol type) =>
        HasParameterlessInstanceMethod(type, "Dispose");

    /// <summary>
    /// True if <paramref name="type"/> declares an explicit (non-compiler-
    /// synthesized) static constructor. The compiler emits an implicit static
    /// ctor for any class with static field initializers — we have to filter
    /// those out via <c>IsImplicitlyDeclared</c>, otherwise every attributed
    /// class would trip ASC008.
    /// </summary>
    public static bool HasExplicitStaticConstructor(INamedTypeSymbol type)
    {
        foreach (var ctor in type.StaticConstructors)
            if (!ctor.IsImplicitlyDeclared)
                return true;
        return false;
    }

    /// <summary>
    /// True if <paramref name="type"/> declares (or inherits) a parameterless
    /// instance method named <c>Clear</c>. Duck-typed — no accessibility or
    /// return-type requirement, walks the base-type chain. Covers the standard
    /// collection types (<c>List&lt;T&gt;</c>, <c>Dictionary&lt;K,V&gt;</c>,
    /// <c>HashSet&lt;T&gt;</c>, …) and any user wrapper that exposes one.
    /// </summary>
    public static bool HasClearMethod(ITypeSymbol type) =>
        HasParameterlessInstanceMethod(type, "Clear");

    private static bool HasParameterlessInstanceMethod(ITypeSymbol type, string name)
    {
        for (var t = type; t != null; t = t.BaseType)
            foreach (var m in t.GetMembers(name))
                if (m is IMethodSymbol { IsStatic: false, Parameters.Length: 0 })
                    return true;
        return false;
    }

    /// <summary>
    /// Readonly members of these types are silently left alone — resetting
    /// them is either impossible or pointless (immutable state): unmanaged
    /// types, <c>string</c>, a whitelist of known-immutable types, and arrays
    /// whose elements would themselves be skipped. Only consulted for
    /// readonly members; mutable statics are always resettable.
    /// </summary>
    public static bool ReadonlyTypeIsExempt(ITypeSymbol type)
    {
        if (type == null) return false;
        if (type.IsUnmanagedType) return true;
        if (type.SpecialType == SpecialType.System_String) return true;
        if (s_exemptReadonlyTypes.Contains(type.ToDisplayString())) return true;
        if (type.TypeKind == TypeKind.Array)
            return ReadonlyTypeIsExempt(((IArrayTypeSymbol)type).ElementType);
        return false;
    }

    // Known-immutable engine/editor and BCL types whose readonly statics
    // never need cleanup. Don't trim entries that look editor-internal —
    // whether a readonly static errors or stays silent hinges on this list.
    private static readonly ImmutableHashSet<string> s_exemptReadonlyTypes = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "UnityEngine.GUIContent",
        "UnityEngine.GUIStyle",
        "UnityEngine.TextCore.Text.FontAsset",
        "UnityEngine.Texture2D",
        "System.Uri",
        "System.Text.RegularExpressions.Regex",
        "UnityEditor.ScalableGUIContent",
        "UnityEditor.PackageManager.UI.Internal.UIError",
        "UnityEditor.PrefColor",
        "Unity.Profiling.ProfilerMarker",
        "UnityEngine.UIElements.BindingId",
        "UnityEditor.ShapeModuleUI.MultiModeTexts",
        "UnityEditor.SavedBool",
        "UnityEditor.SavedInt",
        "UnityEditor.SavedFloat",
        "UnityEditor.Build.NamedBuildTarget",
        "NiceIO.NPath",
        "UnityEditor.Profiling.ProfilerCounterData",
        "System.Version");

    /// <summary>
    /// True if a readonly member of <paramref name="type"/> with the given
    /// initializer qualifies for the Clear cleanup strategy: the type needs a
    /// <c>Clear()</c>, and the member must hold a real instance when cleanup
    /// runs — any object-creation expression, or an absent/<c>default</c>
    /// initializer on a value type (a default struct instance is real state).
    /// Braced collection initializers (<c>new() { 1, 2 }</c>) are allowed
    /// because the generator restores their elements after <c>Clear()</c>
    /// (see PostClearStatements). A reference type without an object-creation
    /// initializer does NOT qualify — the member would be null forever and
    /// <c>Clear()</c> a guaranteed NullReferenceException (see
    /// <see cref="ReadonlyIsNullAtCleanup"/>).
    /// </summary>
    public static bool CanCleanReadonly(ITypeSymbol type, ExpressionSyntax initializer)
    {
        if (!HasClearMethod(type)) return false;
        return initializer switch
        {
            BaseObjectCreationExpressionSyntax => true,
            null => type.IsValueType,
            DefaultExpressionSyntax => type.IsValueType,
            LiteralExpressionSyntax lit when lit.IsKind(SyntaxKind.DefaultLiteralExpression) => type.IsValueType,
            _ => false,
        };
    }

    /// <summary>
    /// True if a readonly member of <paramref name="type"/> would be null
    /// when cleanup runs: the type has a usable <c>Clear()</c>, but the
    /// member is a reference type whose initializer is absent, <c>null</c>,
    /// or <c>default</c> — and a readonly static can never be assigned later
    /// (ASC008 already bans static constructors). The Clear strategy would
    /// throw NullReferenceException on every play-mode transition, so these
    /// shapes report ASC010 instead of emitting.
    /// </summary>
    public static bool ReadonlyIsNullAtCleanup(ITypeSymbol type, ExpressionSyntax initializer)
    {
        if (!type.IsReferenceType || !HasClearMethod(type)) return false;
        return initializer switch
        {
            null => true,
            DefaultExpressionSyntax => true,
            LiteralExpressionSyntax lit when lit.IsKind(SyntaxKind.DefaultLiteralExpression)
                || lit.IsKind(SyntaxKind.NullLiteralExpression) => true,
            _ => false,
        };
    }

    public static bool CanCleanReadonlyField(IFieldSymbol field) =>
        field.IsReadOnly
        && CanCleanReadonly(field.Type, GetFieldInitializerSyntax(field));

    public static ExpressionSyntax GetFieldInitializerSyntax(IFieldSymbol field)
    {
        if (field.DeclaringSyntaxReferences.Length == 0) return null;
        return field.DeclaringSyntaxReferences[0].GetSyntax() is VariableDeclaratorSyntax vds
            ? vds.Initializer?.Value
            : null;
    }

    public static ExpressionSyntax GetPropertyInitializerSyntax(IPropertySymbol prop)
    {
        if (prop.DeclaringSyntaxReferences.Length == 0) return null;
        return prop.DeclaringSyntaxReferences[0].GetSyntax() is PropertyDeclarationSyntax pds
            ? pds.Initializer?.Value
            : null;
    }

    /// <summary>
    /// True if <paramref name="prop"/> is an auto-property (has a
    /// compiler-generated backing field). Getter-only auto-properties are
    /// eligible for the Clear strategy; manual getter-only properties are not
    /// (there is no state behind them we can reason about).
    /// </summary>
    public static bool IsAutoProperty(IPropertySymbol prop)
    {
        foreach (var member in prop.ContainingType.GetMembers())
            if (member is IFieldSymbol { IsImplicitlyDeclared: true } f
                && SymbolEqualityComparer.Default.Equals(f.AssociatedSymbol, prop))
                return true;
        return false;
    }

    public static bool HasAttribute(ISymbol symbol, string fullName)
    {
        foreach (var attr in symbol.GetAttributes())
            if (attr.AttributeClass?.ToDisplayString() == fullName)
                return true;
        return false;
    }

    public static bool HasAttribute(ISymbol symbol, INamedTypeSymbol attribute)
    {
        foreach (var attr in symbol.GetAttributes())
            if (SymbolEqualityComparer.Default.Equals(attr.AttributeClass, attribute))
                return true;
        return false;
    }

    // -----------------------------------------------------------------
    //  Validation (analyzer-only, member-level dispatch)
    // -----------------------------------------------------------------
    //
    // Shape warnings (ASC002-007) fire BEFORE the partial-chain check
    // because a shape warning already signals "this member won't be
    // emitted." With no codegen there's no duplicate-definition risk,
    // so ASC001 would be redundant noise on top.

    public static Diagnostic ValidateField(IFieldSymbol field)
    {
        if (!field.IsStatic)
            return Diagnostic.Create(MemberMustBeStatic, FirstLocationOf(field), field.Name);
        if (field.IsConst)
            return Diagnostic.Create(ConstFieldNotSupported, FirstLocationOf(field), field.Name);

        if (field.IsReadOnly)
        {
            // Exempt readonly types (unmanaged, string, known-immutable, …)
            // are silently left alone — neither reset nor flagged. No codegen
            // ⇒ skip the chain checks too.
            if (ReadonlyTypeIsExempt(field.Type)) return null;

            // Clear()-able type but no instance to call Clear() on — the
            // member is null forever, so the cleanup is a guaranteed
            // NullReferenceException (ASC010, distinct from ASC002 because
            // the fix is different: add an initializer, not change the type).
            if (ReadonlyIsNullAtCleanup(field.Type, GetFieldInitializerSyntax(field)))
                return Diagnostic.Create(ReadonlyNullAtCleanup, FirstLocationOf(field), field.Name);

            // Otherwise readonly is supported only via the Clear strategy:
            // the type has Clear() and the initializer is a 'new …' expression
            // (braced elements are restored after Clear()) or default/absent.
            if (!CanCleanReadonlyField(field))
                return Diagnostic.Create(ReadonlyNotSupported, FirstLocationOf(field), field.Name);
        }
        else if (GetFieldInitializerSyntax(field) == null && HasDisposeMethod(field.Type))
        {
            // Disposable without an initializer: nothing to construct a
            // replacement from after disposing, so the member can't be reset.
            return Diagnostic.Create(DisposableNeedsInitializer, FirstLocationOf(field), field.Name);
        }

        var owner = field.ContainingType;
        if (!IsPartialChain(owner)) return CreatePartialDiagnostic(owner, field);
        if (HasExplicitStaticConstructor(owner)) return CreateStaticCtorDiagnostic(owner, field);

        return null;
    }

    public static Diagnostic ValidateProperty(IPropertySymbol prop)
    {
        if (!prop.IsStatic)
            return Diagnostic.Create(MemberMustBeStatic, FirstLocationOf(prop), prop.Name);

        // Expression-bodied — silently skipped (carries no state, warning would
        // be confusing).
        if (prop.DeclaringSyntaxReferences.Length > 0
            && prop.DeclaringSyntaxReferences[0].GetSyntax() is PropertyDeclarationSyntax { ExpressionBody: not null })
            return null;

        // Manual accessors — only auto-properties are reset, so the generator
        // skips these; an explicit member-level attribute on one is a footgun
        // worth an error.
        if (!IsAutoProperty(prop))
            return Diagnostic.Create(PropertyNeedsSetter, FirstLocationOf(prop), prop.Name);

        if (prop.SetMethod == null)
        {
            // Getter-only auto-properties follow the readonly-field rules:
            // exempt types are silently skipped, null-at-cleanup shapes get
            // ASC010, Clear-strategy shapes are supported, everything else
            // gets ASC003.
            if (ReadonlyTypeIsExempt(prop.Type)) return null;
            var init = GetPropertyInitializerSyntax(prop);
            if (ReadonlyIsNullAtCleanup(prop.Type, init))
                return Diagnostic.Create(ReadonlyNullAtCleanup, FirstLocationOf(prop), prop.Name);
            if (!CanCleanReadonly(prop.Type, init))
                return Diagnostic.Create(PropertyNeedsSetter, FirstLocationOf(prop), prop.Name);
        }
        else if (prop.SetMethod.IsInitOnly)
        {
            return Diagnostic.Create(PropertyNeedsSetter, FirstLocationOf(prop), prop.Name);
        }
        else if (GetPropertyInitializerSyntax(prop) == null && HasDisposeMethod(prop.Type))
        {
            // Same rule as fields: disposable with nothing to reassign after
            // disposing can't be reset.
            return Diagnostic.Create(DisposableNeedsInitializer, FirstLocationOf(prop), prop.Name);
        }

        var owner = prop.ContainingType;
        if (!IsPartialChain(owner)) return CreatePartialDiagnostic(owner, prop);
        if (HasExplicitStaticConstructor(owner)) return CreateStaticCtorDiagnostic(owner, prop);

        return null;
    }

    public static Diagnostic ValidateEvent(IEventSymbol evt)
    {
        if (!evt.IsStatic)
            return Diagnostic.Create(MemberMustBeStatic, FirstLocationOf(evt), evt.Name);
        if (!IsFieldLikeEvent(evt))
            return Diagnostic.Create(ManualEventNotSupported, FirstLocationOf(evt), evt.Name);

        var owner = evt.ContainingType;
        if (!IsPartialChain(owner)) return CreatePartialDiagnostic(owner, evt);
        if (HasExplicitStaticConstructor(owner)) return CreateStaticCtorDiagnostic(owner, evt);

        return null;
    }

    /// <summary>
    /// Class-level scan companion: reports the member shapes that cannot be
    /// cleaned at all — readonly non-exempt members that don't qualify for
    /// Clear (ASC002/003), readonly members that are null at cleanup time
    /// (ASC010), and disposable members with no initializer (ASC009) — even
    /// when only the containing type carries [AutoStaticsCleanup]. These must
    /// fail the build rather than be skipped silently. Merely-filtered shapes
    /// (instance, const, manual events, non-auto properties, exempt readonly)
    /// stay silent. Members carrying their own [AutoStaticsCleanup] are
    /// skipped here — the member-level Validate* path already reports them —
    /// and [NoAutoStaticsCleanup] members are opted out entirely.
    /// </summary>
    public static void ValidateTypeMembers(
        INamedTypeSymbol type,
        INamedTypeSymbol attr,
        INamedTypeSymbol noAttr,
        System.Action<Diagnostic> report)
    {
        foreach (var member in type.GetMembers())
        {
            if (HasAttribute(member, attr)) continue;
            if (noAttr != null && HasAttribute(member, noAttr)) continue;

            switch (member)
            {
                case IFieldSymbol { IsStatic: true, IsConst: false, IsImplicitlyDeclared: false } f:
                    if (f.IsReadOnly)
                    {
                        if (ReadonlyTypeIsExempt(f.Type)) break;
                        if (ReadonlyIsNullAtCleanup(f.Type, GetFieldInitializerSyntax(f)))
                            report(Diagnostic.Create(ReadonlyNullAtCleanup, FirstLocationOf(f), f.Name));
                        else if (!CanCleanReadonlyField(f))
                            report(Diagnostic.Create(ReadonlyNotSupported, FirstLocationOf(f), f.Name));
                    }
                    else if (GetFieldInitializerSyntax(f) == null && HasDisposeMethod(f.Type))
                    {
                        report(Diagnostic.Create(DisposableNeedsInitializer, FirstLocationOf(f), f.Name));
                    }
                    break;

                case IPropertySymbol { IsStatic: true, IsIndexer: false, IsImplicitlyDeclared: false } p
                    when IsAutoProperty(p):
                    if (p.SetMethod == null)
                    {
                        if (ReadonlyTypeIsExempt(p.Type)) break;
                        var init = GetPropertyInitializerSyntax(p);
                        if (ReadonlyIsNullAtCleanup(p.Type, init))
                            report(Diagnostic.Create(ReadonlyNullAtCleanup, FirstLocationOf(p), p.Name));
                        else if (!CanCleanReadonly(p.Type, init))
                            report(Diagnostic.Create(PropertyNeedsSetter, FirstLocationOf(p), p.Name));
                    }
                    else if (!p.SetMethod.IsInitOnly
                             && GetPropertyInitializerSyntax(p) == null
                             && HasDisposeMethod(p.Type))
                    {
                        report(Diagnostic.Create(DisposableNeedsInitializer, FirstLocationOf(p), p.Name));
                    }
                    break;
            }
        }
    }

    public static Diagnostic CreatePartialDiagnostic(INamedTypeSymbol type, ISymbol attributedSymbol)
    {
        // Walk to the first non-partial type in the chain so the message points at the offender.
        INamedTypeSymbol offender = type;
        for (var t = type; t != null; t = t.ContainingType)
            if (!IsPartial(t))
                offender = t;

        return Diagnostic.Create(MustBePartial, AnchorLocation(offender, attributedSymbol), offender.ToDisplayString());
    }

    public static Diagnostic CreateStaticCtorDiagnostic(INamedTypeSymbol type, ISymbol attributedSymbol) =>
        Diagnostic.Create(StaticConstructorNotSupported, AnchorLocation(type, attributedSymbol), type.Name);

    private static Location AnchorLocation(INamedTypeSymbol typeForFallback, ISymbol attributedSymbol)
    {
        if (attributedSymbol is INamedTypeSymbol)
            return LocationForTypeIdentifier(typeForFallback) ?? FirstLocationOf(attributedSymbol);
        return FirstLocationOf(attributedSymbol);
    }

    /// <summary>
    /// Returns the location of the type's identifier token, or null if the
    /// type has no source declaration (e.g. metadata).
    /// </summary>
    public static Location LocationForTypeIdentifier(INamedTypeSymbol type)
    {
        var sr = type.DeclaringSyntaxReferences.FirstOrDefault();
        if (sr?.GetSyntax() is TypeDeclarationSyntax tds)
            return tds.Identifier.GetLocation();
        return null;
    }

    public static Location FirstLocationOf(ISymbol symbol)
    {
        var sr = symbol.DeclaringSyntaxReferences.FirstOrDefault();
        return sr == null ? Location.None : sr.GetSyntax().GetLocation();
    }
}
