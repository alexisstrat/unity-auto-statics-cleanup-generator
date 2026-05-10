using System.Collections.Immutable;
using System.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AutoStaticsCleanup;

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(AutoStaticsCleanupCodeFixProvider))]
[Shared]
public sealed class AutoStaticsCleanupCodeFixProvider : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds { get; } =
        ImmutableArray.Create("ASC001");

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root == null) return;

        foreach (var diagnostic in context.Diagnostics)
        {
            if (diagnostic.Id != "ASC001") continue;
            var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
            RegisterAddPartial(context, root, node, diagnostic);
        }
    }

    private static void RegisterAddPartial(
        CodeFixContext context, SyntaxNode root, SyntaxNode node, Diagnostic diagnostic)
    {
        var offender = FirstNonPartialAncestor(node);
        if (offender == null) return;

        context.RegisterCodeFix(
            CodeAction.Create(
                title: $"Add 'partial' modifier to '{offender.Identifier.ValueText}'",
                createChangedDocument: ct => AddPartialAsync(context.Document, root, offender, ct),
                equivalenceKey: "ASC001_AddPartial"),
            diagnostic);
    }

    private static TypeDeclarationSyntax FirstNonPartialAncestor(SyntaxNode node)
    {
        for (var current = node.FirstAncestorOrSelf<TypeDeclarationSyntax>();
             current != null;
             current = current.Parent?.FirstAncestorOrSelf<TypeDeclarationSyntax>())
        {
            if (!HasPartialModifier(current)) return current;
        }
        return null;
    }

    private static Task<Document> AddPartialAsync(
        Document document, SyntaxNode root, TypeDeclarationSyntax typeDecl, CancellationToken ct)
    {
        var partialToken = SyntaxFactory.Token(SyntaxKind.PartialKeyword)
            .WithTrailingTrivia(SyntaxFactory.Space);

        // Place `partial` immediately before the `class`/`struct`/`record` keyword,
        // i.e. as the last modifier — this matches conventional C# ordering.
        var newModifiers = typeDecl.Modifiers.Add(partialToken);
        var newDecl = typeDecl.WithModifiers(newModifiers);

        return Task.FromResult(document.WithSyntaxRoot(root.ReplaceNode(typeDecl, newDecl)));
    }

    private static bool HasPartialModifier(TypeDeclarationSyntax typeDecl)
    {
        foreach (var m in typeDecl.Modifiers)
            if (m.IsKind(SyntaxKind.PartialKeyword))
                return true;
        return false;
    }
}
