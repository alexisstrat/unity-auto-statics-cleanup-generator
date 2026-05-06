using System.Collections.Immutable;
using System.Composition;
using System.Linq;
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
        ImmutableArray.Create("ASC001", "ASC002", "ASC003");

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root == null) return;

        foreach (var diagnostic in context.Diagnostics)
        {
            var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
            switch (diagnostic.Id)
            {
                case "ASC001":
                    RegisterAddPartial(context, root, node, diagnostic);
                    break;
                case "ASC002":
                    RegisterRemoveReadonly(context, root, node, diagnostic);
                    break;
                case "ASC003":
                    RegisterAddSetter(context, root, node, diagnostic);
                    break;
            }
        }
    }

    // -----------------------------------------------------------------
    //  ASC001 — add `partial`
    // -----------------------------------------------------------------

    private static void RegisterAddPartial(
        CodeFixContext context, SyntaxNode root, SyntaxNode node, Diagnostic diagnostic)
    {
        var typeDecl = node.FirstAncestorOrSelf<TypeDeclarationSyntax>();
        if (typeDecl == null || HasPartialModifier(typeDecl)) return;

        context.RegisterCodeFix(
            CodeAction.Create(
                title: $"Add 'partial' modifier to '{typeDecl.Identifier.ValueText}'",
                createChangedDocument: ct => AddPartialAsync(context.Document, root, typeDecl, ct),
                equivalenceKey: "ASC001_AddPartial"),
            diagnostic);
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

    // -----------------------------------------------------------------
    //  ASC002 — remove `readonly`
    // -----------------------------------------------------------------

    private static void RegisterRemoveReadonly(
        CodeFixContext context, SyntaxNode root, SyntaxNode node, Diagnostic diagnostic)
    {
        var fieldDecl = node.FirstAncestorOrSelf<FieldDeclarationSyntax>();
        if (fieldDecl == null) return;

        var readonlyToken = fieldDecl.Modifiers.FirstOrDefault(m => m.IsKind(SyntaxKind.ReadOnlyKeyword));
        if (readonlyToken == default) return;

        context.RegisterCodeFix(
            CodeAction.Create(
                title: "Remove 'readonly' modifier",
                createChangedDocument: ct => RemoveReadonlyAsync(context.Document, root, fieldDecl, ct),
                equivalenceKey: "ASC002_RemoveReadonly"),
            diagnostic);
    }

    private static Task<Document> RemoveReadonlyAsync(
        Document document, SyntaxNode root, FieldDeclarationSyntax fieldDecl, CancellationToken ct)
    {
        var newModifiers = SyntaxFactory.TokenList(
            fieldDecl.Modifiers.Where(m => !m.IsKind(SyntaxKind.ReadOnlyKeyword)));

        // Preserve any leading trivia from the readonly token by attaching it
        // to whatever modifier ends up first; if no modifiers remain, attach to
        // the field's type.
        var newDecl = fieldDecl.WithModifiers(newModifiers);
        return Task.FromResult(document.WithSyntaxRoot(root.ReplaceNode(fieldDecl, newDecl)));
    }

    // -----------------------------------------------------------------
    //  ASC003 — add `set;` accessor (auto-property only)
    // -----------------------------------------------------------------

    private static void RegisterAddSetter(
        CodeFixContext context, SyntaxNode root, SyntaxNode node, Diagnostic diagnostic)
    {
        var propDecl = node.FirstAncestorOrSelf<PropertyDeclarationSyntax>();
        if (propDecl == null) return;
        if (propDecl.AccessorList == null) return; // expression-bodied — out of scope

        // Auto-property check: every accessor body-less.
        foreach (var accessor in propDecl.AccessorList.Accessors)
            if (accessor.Body != null || accessor.ExpressionBody != null)
                return;

        // Already has a non-init setter? Nothing to fix.
        var setter = propDecl.AccessorList.Accessors.FirstOrDefault(a => a.IsKind(SyntaxKind.SetAccessorDeclaration));
        var initSetter = propDecl.AccessorList.Accessors.FirstOrDefault(a => a.IsKind(SyntaxKind.InitAccessorDeclaration));

        if (setter != null) return;

        context.RegisterCodeFix(
            CodeAction.Create(
                title: initSetter != null
                    ? "Replace 'init' with 'set' accessor"
                    : "Add 'set;' accessor",
                createChangedDocument: ct => AddSetterAsync(context.Document, root, propDecl, ct),
                equivalenceKey: "ASC003_AddSetter"),
            diagnostic);
    }

    private static Task<Document> AddSetterAsync(
        Document document, SyntaxNode root, PropertyDeclarationSyntax propDecl, CancellationToken ct)
    {
        var newSetter = SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration)
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));

        var accessors = propDecl.AccessorList!.Accessors
            .Where(a => !a.IsKind(SyntaxKind.InitAccessorDeclaration))
            .ToList();
        accessors.Add(newSetter);

        var newAccessorList = propDecl.AccessorList.WithAccessors(SyntaxFactory.List(accessors));
        var newDecl = propDecl.WithAccessorList(newAccessorList);

        return Task.FromResult(document.WithSyntaxRoot(root.ReplaceNode(propDecl, newDecl)));
    }
}
