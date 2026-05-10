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
/// Only ASC001 (must be 'partial') is surfaced as a diagnostic. Every other
/// misuse (instance member, const field, readonly field, manual event,
/// expression-bodied property, get-only property, type nested in generic) is
/// silently skipped to match Unity 6.5's source generator. The compiler
/// itself signals the partial-required case via "duplicate definition" once
/// the generated partial collides with the user's non-partial declaration —
/// ASC001 surfaces that pre-codegen with a clearer message and a one-click fix.
/// </summary>
internal static class Validation
{
    public const string AttributeFullName = "Unity.Scripting.LifecycleManagement.AutoStaticsCleanupAttribute";
    public const string NoAttributeFullName = "Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanupAttribute";

    public static readonly DiagnosticDescriptor MustBePartial = new(
        "ASC001",
        "Type must be 'partial'",
        "Type '{0}' must be declared 'partial' (and so must every enclosing type) to use [AutoStaticsCleanup]",
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
    //
    // The Validate* methods return ASC001 only when the generator would
    // actually emit code for this member — i.e. when every silent-skip
    // condition (non-static, const, readonly, manual event, etc.) passes.
    // A static readonly field in a non-partial class produces no
    // diagnostic because the generator silently skips readonly anyway,
    // so there's no "duplicate definition" risk to warn about.

    public static Diagnostic ValidateField(IFieldSymbol field)
    {
        // Silent skips (Unity-parity): no diagnostic.
        if (!field.IsStatic) return null;
        if (field.IsConst) return null;
        if (field.IsReadOnly) return null;

        // Field would be emitted; flag the partial-chain problem before the
        // C# compiler trips over a duplicate-definition error.
        var owner = field.ContainingType;
        if (!IsPartialChain(owner)) return CreatePartialDiagnostic(owner, field);

        return null;
    }

    public static Diagnostic ValidateProperty(IPropertySymbol prop)
    {
        if (!prop.IsStatic) return null;

        // Expression-bodied — silently skipped (carries no state).
        if (prop.DeclaringSyntaxReferences.Length > 0
            && prop.DeclaringSyntaxReferences[0].GetSyntax() is PropertyDeclarationSyntax { ExpressionBody: not null })
            return null;

        // No setter or init-only setter — silently skipped (Unity-parity).
        if (prop.SetMethod == null || prop.SetMethod.IsInitOnly) return null;

        var owner = prop.ContainingType;
        if (!IsPartialChain(owner)) return CreatePartialDiagnostic(owner, prop);

        return null;
    }

    public static Diagnostic ValidateEvent(IEventSymbol evt)
    {
        if (!evt.IsStatic) return null;
        if (!IsFieldLikeEvent(evt)) return null;

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
