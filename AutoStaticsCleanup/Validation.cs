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
        "[AutoStaticsCleanup] cannot be applied to readonly fields",
        "Field '{0}' is readonly; [AutoStaticsCleanup] requires a settable field, so the attribute will be ignored",
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
            return Diagnostic.Create(ReadonlyNotSupported, FirstLocationOf(field), field.Name);

        var owner = field.ContainingType;
        if (!IsPartialChain(owner)) return CreatePartialDiagnostic(owner, field);

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
