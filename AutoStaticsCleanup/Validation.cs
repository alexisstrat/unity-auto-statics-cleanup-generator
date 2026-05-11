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
/// (const, readonly, instance, manual events, etc.) the same way Unity 6.5
/// does — putting the attribute on the class is "reset everything resettable,
/// leave the rest alone." Member-level attribution is an explicit signal that
/// something is intended; if the shape blocks it, that's a footgun worth a
/// warning. ASC001 is the only Error; ASC002-007 are Warnings.
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
        "Field '{0}' is readonly; reset requires either a settable field or a type with a public Clear() method AND a trivial initializer (e.g., 'new()'). The attribute will be ignored.",
        "AutoStaticsCleanup",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor PropertyNeedsSetter = new(
        "ASC003",
        "[AutoStaticsCleanup] requires a property setter",
        "Property '{0}' has no usable setter; [AutoStaticsCleanup] requires a settable property (init-only setters are not callable from Cleanup), so the attribute will be ignored",
        "AutoStaticsCleanup",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ManualEventNotSupported = new(
        "ASC004",
        "[AutoStaticsCleanup] does not support manual events",
        "Event '{0}' has explicit 'add'/'remove' accessors; [AutoStaticsCleanup] only supports field-like events, so the attribute will be ignored",
        "AutoStaticsCleanup",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor MemberMustBeStatic = new(
        "ASC006",
        "[AutoStaticsCleanup] requires a static member",
        "Member '{0}' is not static; [AutoStaticsCleanup] only applies to static fields, properties, and events, so the attribute will be ignored",
        "AutoStaticsCleanup",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ConstFieldNotSupported = new(
        "ASC007",
        "[AutoStaticsCleanup] cannot be applied to const fields",
        "Field '{0}' is const; const fields cannot be reset and [AutoStaticsCleanup] has no effect",
        "AutoStaticsCleanup",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor StaticConstructorNotSupported = new(
        "ASC008",
        "[AutoStaticsCleanup] is incompatible with explicit static constructors",
        "Type '{0}' has an explicit static constructor; [AutoStaticsCleanup]'s nested cleanup-class initialization runs in unspecified order relative to it, which can leave the class re-initialized after cleanup. Remove the static constructor or [AutoStaticsCleanup].",
        "AutoStaticsCleanup",
        DiagnosticSeverity.Warning,
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
    /// True if <paramref name="type"/> is <c>System.IDisposable</c>, implements
    /// it (transitively), or is a generic type parameter constrained to it.
    /// Used to decide whether the generator should emit <c>field?.Dispose()</c>
    /// before reassigning the captured initializer.
    /// </summary>
    public static bool ImplementsIDisposable(ITypeSymbol type)
    {
        if (type == null) return false;
        if (IsSystemIDisposable(type)) return true;
        foreach (var iface in type.AllInterfaces)
            if (IsSystemIDisposable(iface))
                return true;
        if (type is ITypeParameterSymbol tp)
            foreach (var constraint in tp.ConstraintTypes)
                if (ImplementsIDisposable(constraint))
                    return true;
        return false;
    }

    private static bool IsSystemIDisposable(ITypeSymbol type) =>
        type.Name == "IDisposable"
        && type.ContainingNamespace is { Name: "System" } ns
        && ns.ContainingNamespace is { IsGlobalNamespace: true };

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
    /// True if <paramref name="type"/> has a public, parameterless, void
    /// instance method named <c>Clear</c>. Covers the standard collection
    /// types (<c>List&lt;T&gt;</c>, <c>Dictionary&lt;K,V&gt;</c>,
    /// <c>HashSet&lt;T&gt;</c>, …) and any user wrapper that exposes one.
    /// </summary>
    public static bool HasClearMethod(ITypeSymbol type)
    {
        if (type == null) return false;
        foreach (var m in type.GetMembers("Clear"))
            if (m is IMethodSymbol method
                && !method.IsStatic
                && method.Parameters.Length == 0
                && method.ReturnsVoid
                && method.DeclaredAccessibility == Accessibility.Public)
                return true;
        return false;
    }

    /// <summary>
    /// True if <paramref name="expr"/> is a "trivial" initializer in the sense
    /// of Unity's Auto-resolution table — one whose constructed state matches
    /// what <c>Clear()</c> would restore (i.e. an empty container). Used to
    /// gate the Clear strategy on readonly fields: <c>new()</c> and
    /// <c>new T()</c> are trivial; <c>new T() { 1, 2, 3 }</c> isn't (Clear
    /// would empty the collection and not restore the original elements).
    /// </summary>
    public static bool IsTrivialInitializer(ExpressionSyntax expr)
    {
        if (expr == null) return false;
        return expr switch
        {
            ObjectCreationExpressionSyntax oc =>
                (oc.ArgumentList == null || oc.ArgumentList.Arguments.Count == 0)
                && oc.Initializer == null,
            ImplicitObjectCreationExpressionSyntax ioc =>
                ioc.ArgumentList.Arguments.Count == 0
                && ioc.Initializer == null,
            LiteralExpressionSyntax lit =>
                lit.IsKind(SyntaxKind.NullLiteralExpression)
                || lit.IsKind(SyntaxKind.DefaultLiteralExpression),
            DefaultExpressionSyntax => true,
            _ => false,
        };
    }

    /// <summary>
    /// True if <paramref name="field"/> qualifies for the Clear cleanup
    /// strategy: readonly, type has a usable <c>Clear()</c>, and the field's
    /// initializer is trivial. When this is true, the generator emits
    /// <c>_field.Clear();</c> instead of the readonly-skip behavior, and the
    /// analyzer suppresses ASC002.
    /// </summary>
    public static bool CanCleanReadonlyField(IFieldSymbol field) =>
        field.IsReadOnly
        && HasClearMethod(field.Type)
        && IsTrivialInitializer(GetFieldInitializerSyntax(field));

    private static ExpressionSyntax GetFieldInitializerSyntax(IFieldSymbol field)
    {
        if (field.DeclaringSyntaxReferences.Length == 0) return null;
        return field.DeclaringSyntaxReferences[0].GetSyntax() is VariableDeclaratorSyntax vds
            ? vds.Initializer?.Value
            : null;
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

        // Readonly is supported when the type has Clear() AND the initializer
        // is trivial — the generator emits `field.Clear();`. Otherwise ASC002
        // fires (covers "no Clear()" and "non-trivial initializer", both of
        // which mean we can't restore the original state).
        if (field.IsReadOnly && !CanCleanReadonlyField(field))
            return Diagnostic.Create(ReadonlyNotSupported, FirstLocationOf(field), field.Name);

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

        if (prop.SetMethod == null || prop.SetMethod.IsInitOnly)
            return Diagnostic.Create(PropertyNeedsSetter, FirstLocationOf(prop), prop.Name);

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
