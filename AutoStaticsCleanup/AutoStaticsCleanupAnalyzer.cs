using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace AutoStaticsCleanup;

/// <summary>
/// Reports the ASC001-007 diagnostics for misuse of [AutoStaticsCleanup].
/// Lives in the same assembly as the source generator but runs independently
/// — the IDE surfaces these rules even when the generator isn't producing
/// output, and they show up under Solution Explorer's analyzer node with
/// proper severity-config affordances.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class AutoStaticsCleanupAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(Validation.MustBePartial);

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

        switch (symbol)
        {
            case INamedTypeSymbol type:
                AnalyzeType(context, type, noAttr);
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
    /// Type-level [AutoStaticsCleanup] dispatch: verify the partial chain once
    /// (so a non-partial type produces a single diagnostic, not one per
    /// member), then run per-member validators on every static field/property/
    /// event that wasn't explicitly opted out.
    /// </summary>
    private static void AnalyzeType(SymbolAnalysisContext context, INamedTypeSymbol type, INamedTypeSymbol noAttr)
    {
        if (!Validation.IsPartialChain(type))
        {
            context.ReportDiagnostic(Validation.CreatePartialDiagnostic(type, type));
            return;
        }

        foreach (var member in type.GetMembers())
        {
            if (noAttr != null && Validation.HasAttribute(member, noAttr)) continue;

            switch (member)
            {
                case IFieldSymbol { IsStatic: true, IsConst: false, IsImplicitlyDeclared: false } f:
                    Report(context, Validation.ValidateField(f));
                    break;
                case IPropertySymbol { IsStatic: true, IsIndexer: false, IsImplicitlyDeclared: false } p:
                    Report(context, Validation.ValidateProperty(p));
                    break;
                case IEventSymbol { IsStatic: true, IsImplicitlyDeclared: false } e:
                    Report(context, Validation.ValidateEvent(e));
                    break;
            }
        }
    }

    private static void Report(SymbolAnalysisContext context, Diagnostic diagnostic)
    {
        if (diagnostic != null) context.ReportDiagnostic(diagnostic);
    }
}
