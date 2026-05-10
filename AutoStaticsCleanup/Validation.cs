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
        "Field '{0}' is readonly; [AutoStaticsCleanup] requires a settable field",
        "AutoStaticsCleanup",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor PropertyNeedsSetter = new(
        "ASC003",
        "[AutoStaticsCleanup] requires a property setter",
        "Property '{0}' has no usable setter; [AutoStaticsCleanup] requires a settable property (init-only setters are not callable from Cleanup)",
        "AutoStaticsCleanup",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ManualEventNotSupported = new(
        "ASC004",
        "[AutoStaticsCleanup] does not support manual events",
        "Event '{0}' has explicit 'add'/'remove' accessors; [AutoStaticsCleanup] only supports field-like events",
        "AutoStaticsCleanup",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NestedInGenericNotSupported = new(
        "ASC005",
        "[AutoStaticsCleanup] does not support types nested inside generic types",
        "Type '{0}' is nested inside a generic type; closed generic instantiations cannot be discovered for cleanup",
        "AutoStaticsCleanup",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor MemberMustBeStatic = new(
        "ASC006",
        "[AutoStaticsCleanup] requires a static member",
        "Member '{0}' is not static; [AutoStaticsCleanup] only applies to static fields, properties, and events",
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

    public static bool HasGenericOuter(INamedTypeSymbol type)
    {
        for (var t = type.ContainingType; t != null; t = t.ContainingType)
            if (t.IsGenericType)
                return true;
        return false;
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
    //  Validation (analyzer-only)
    // -----------------------------------------------------------------

    /// <summary>
    /// Returns a diagnostic if <paramref name="field"/> is not a valid
    /// [AutoStaticsCleanup] target, or null if it is.
    /// </summary>
    public static Diagnostic ValidateField(IFieldSymbol field)
    {
        if (!field.IsStatic)
            return Diagnostic.Create(MemberMustBeStatic, FirstLocationOf(field), field.Name);
        if (field.IsConst)
            return Diagnostic.Create(ConstFieldNotSupported, FirstLocationOf(field), field.Name);

        var owner = field.ContainingType;
        if (HasGenericOuter(owner))
            return CreateNestedInGenericDiagnostic(owner, field);
        if (!IsPartialChain(owner))
            return CreatePartialDiagnostic(owner, field);
        if (field.IsReadOnly)
            return Diagnostic.Create(ReadonlyNotSupported, FirstLocationOf(field), field.Name);

        return null;
    }

    public static Diagnostic ValidateProperty(IPropertySymbol prop)
    {
        if (!prop.IsStatic)
            return Diagnostic.Create(MemberMustBeStatic, FirstLocationOf(prop), prop.Name);

        // Expression-bodied — silently skipped (carries no state).
        if (prop.DeclaringSyntaxReferences.Length > 0
            && prop.DeclaringSyntaxReferences[0].GetSyntax() is PropertyDeclarationSyntax { ExpressionBody: not null })
            return null;

        var owner = prop.ContainingType;
        if (HasGenericOuter(owner))
            return CreateNestedInGenericDiagnostic(owner, prop);
        if (!IsPartialChain(owner))
            return CreatePartialDiagnostic(owner, prop);
        if (prop.SetMethod == null || prop.SetMethod.IsInitOnly)
            return Diagnostic.Create(PropertyNeedsSetter, FirstLocationOf(prop), prop.Name);

        return null;
    }

    public static Diagnostic ValidateEvent(IEventSymbol evt)
    {
        if (!evt.IsStatic)
            return Diagnostic.Create(MemberMustBeStatic, FirstLocationOf(evt), evt.Name);
        if (!IsFieldLikeEvent(evt))
            return Diagnostic.Create(ManualEventNotSupported, FirstLocationOf(evt), evt.Name);

        var owner = evt.ContainingType;
        if (HasGenericOuter(owner))
            return CreateNestedInGenericDiagnostic(owner, evt);
        if (!IsPartialChain(owner))
            return CreatePartialDiagnostic(owner, evt);

        return null;
    }

    public static Diagnostic CreatePartialDiagnostic(INamedTypeSymbol type, ISymbol attributedSymbol)
    {
        // Walk to the first non-partial type in the chain so the message points at the offender.
        INamedTypeSymbol offender = type;
        for (var t = type; t != null; t = t.ContainingType)
            if (!IsPartial(t))
                offender = t;

        var loc = LocationForTypeIdentifier(offender);
        if (loc == null) loc = FirstLocationOf(attributedSymbol);
        return Diagnostic.Create(MustBePartial, loc, offender.ToDisplayString());
    }

    public static Diagnostic CreateNestedInGenericDiagnostic(INamedTypeSymbol type, ISymbol attributedSymbol)
    {
        var loc = LocationForTypeIdentifier(type);
        if (loc == null) loc = FirstLocationOf(attributedSymbol);
        return Diagnostic.Create(NestedInGenericNotSupported, loc, type.ToDisplayString());
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
