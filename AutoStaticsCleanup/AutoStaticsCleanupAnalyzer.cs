using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace AutoStaticsCleanup;

/// <summary>
/// Reports the ASC001-009 diagnostics for misuse of [AutoStaticsCleanup].
/// Lives in the same assembly as the source generator but runs independently
/// — the IDE surfaces these rules even when the generator isn't producing
/// output, and they show up under Solution Explorer's analyzer node with
/// proper severity-config affordances.
///
/// Member-level [AutoStaticsCleanup] runs full shape validation (ASC002-007,
/// ASC009 fire when the explicitly attributed member can't actually be
/// reset). Type-level [AutoStaticsCleanup] verifies the partial chain
/// (ASC001), the static-constructor rule (ASC008), and the shapes that can't
/// be cleaned at all (ASC002/003/009 via Validation.ValidateTypeMembers);
/// merely-unfit members are silently skipped.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class AutoStaticsCleanupAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(
            Validation.MustBePartial,
            Validation.ReadonlyNotSupported,
            Validation.PropertyNeedsSetter,
            Validation.ManualEventNotSupported,
            Validation.MemberMustBeStatic,
            Validation.ConstFieldNotSupported,
            Validation.StaticConstructorNotSupported,
            Validation.DisposableNeedsInitializer);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            // Resolve attribute symbols once per compilation; per-symbol callbacks
            // can then identity-compare via SymbolEqualityComparer instead of
            // ToDisplayString() on every attribute.
            var attr = start.Compilation.GetTypeByMetadataName(Validation.AttributeFullName);
            if (attr == null) return;
            var noAttr = start.Compilation.GetTypeByMetadataName(Validation.NoAttributeFullName);

            start.RegisterSymbolAction(
                c => AnalyzeSymbol(c, attr, noAttr),
                SymbolKind.NamedType,
                SymbolKind.Field,
                SymbolKind.Property,
                SymbolKind.Event);
        });
    }

    private static void AnalyzeSymbol(SymbolAnalysisContext context, INamedTypeSymbol attr, INamedTypeSymbol noAttr)
    {
        var symbol = context.Symbol;
        if (!Validation.HasAttribute(symbol, attr)) return;

        // A member double-marked with [NoAutoStaticsCleanup] is an explicit opt-out;
        // suppress the warning even though the user also attached [AutoStaticsCleanup].
        // Matches what the generator does for the type-level walk.
        if (symbol is not INamedTypeSymbol
            && noAttr != null && Validation.HasAttribute(symbol, noAttr)) return;

        switch (symbol)
        {
            case INamedTypeSymbol type:
                AnalyzeType(context, type, attr, noAttr);
                break;
            case IFieldSymbol field:
                Report(context, Validation.ValidateField(field));
                break;
            case IPropertySymbol { IsIndexer: false } prop:
                Report(context, Validation.ValidateProperty(prop));
                break;
            case IEventSymbol evt:
                Report(context, Validation.ValidateEvent(evt));
                break;
        }
    }

    /// <summary>
    /// Type-level [AutoStaticsCleanup] dispatch: verify the partial chain and
    /// the static-constructor rule, then walk the members for the shapes that
    /// can't be cleaned at all (readonly non-cleanable, disposable without
    /// initializer) — those must error rather than be skipped silently.
    /// Merely-unfit shapes (instance, const, manual event, non-auto property,
    /// exempt readonly) stay silent here; the user's mental model for a
    /// class-level attribute is "reset what's resettable, leave the rest
    /// alone."
    /// </summary>
    private static void AnalyzeType(
        SymbolAnalysisContext context, INamedTypeSymbol type, INamedTypeSymbol attr, INamedTypeSymbol noAttr)
    {
        if (!Validation.IsPartialChain(type))
        {
            context.ReportDiagnostic(Validation.CreatePartialDiagnostic(type, type));
            return;
        }
        if (Validation.HasExplicitStaticConstructor(type))
            context.ReportDiagnostic(Validation.CreateStaticCtorDiagnostic(type, type));

        Validation.ValidateTypeMembers(type, attr, noAttr, context.ReportDiagnostic);
    }

    private static void Report(SymbolAnalysisContext context, Diagnostic diagnostic)
    {
        if (diagnostic != null) context.ReportDiagnostic(diagnostic);
    }
}
