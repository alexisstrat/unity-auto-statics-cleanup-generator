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
        ImmutableArray.Create("ASC001", "ASC002", "ASC003", "ASC006");

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
                case "ASC006":
                    RegisterMakeStatic(context, root, node, diagnostic);
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
            if (!HasModifier(current.Modifiers, SyntaxKind.PartialKeyword)) return current;
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
        var newDecl = typeDecl.WithModifiers(typeDecl.Modifiers.Add(partialToken));
        return Task.FromResult(document.WithSyntaxRoot(root.ReplaceNode(typeDecl, newDecl)));
    }

    // -----------------------------------------------------------------
    //  ASC002 — remove `readonly`
    // -----------------------------------------------------------------

    private static void RegisterRemoveReadonly(
        CodeFixContext context, SyntaxNode root, SyntaxNode node, Diagnostic diagnostic)
    {
        var fieldDecl = node.FirstAncestorOrSelf<FieldDeclarationSyntax>();
        if (fieldDecl == null) return;
        if (!HasModifier(fieldDecl.Modifiers, SyntaxKind.ReadOnlyKeyword)) return;

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

        var setter = propDecl.AccessorList.Accessors.FirstOrDefault(a => a.IsKind(SyntaxKind.SetAccessorDeclaration));
        if (setter != null) return; // already has a non-init setter

        var initSetter = propDecl.AccessorList.Accessors.FirstOrDefault(a => a.IsKind(SyntaxKind.InitAccessorDeclaration));
        context.RegisterCodeFix(
            CodeAction.Create(
                title: initSetter != null ? "Replace 'init' with 'set' accessor" : "Add 'set;' accessor",
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

    // -----------------------------------------------------------------
    //  ASC006 — add `static` modifier
    // -----------------------------------------------------------------

    private static void RegisterMakeStatic(
        CodeFixContext context, SyntaxNode root, SyntaxNode node, Diagnostic diagnostic)
    {
        // The instance member can be a field, property, or event; each is its
        // own declaration node kind, so handle them uniformly via MemberDeclarationSyntax.
        var memberDecl = node.FirstAncestorOrSelf<MemberDeclarationSyntax>();
        if (memberDecl == null) return;
        if (memberDecl is not (FieldDeclarationSyntax or PropertyDeclarationSyntax or EventFieldDeclarationSyntax or EventDeclarationSyntax))
            return;
        if (HasModifier(memberDecl.Modifiers, SyntaxKind.StaticKeyword)) return;

        context.RegisterCodeFix(
            CodeAction.Create(
                title: "Add 'static' modifier",
                createChangedDocument: ct => MakeStaticAsync(context.Document, root, memberDecl, ct),
                equivalenceKey: "ASC006_AddStatic"),
            diagnostic);
    }

    private static Task<Document> MakeStaticAsync(
        Document document, SyntaxNode root, MemberDeclarationSyntax memberDecl, CancellationToken ct)
    {
        var staticToken = SyntaxFactory.Token(SyntaxKind.StaticKeyword)
            .WithTrailingTrivia(SyntaxFactory.Space);

        // Add `static` at the end of the modifier list (after accessibility,
        // before the type/return token) — matches conventional C# ordering.
        var newDecl = memberDecl.WithModifiers(memberDecl.Modifiers.Add(staticToken));
        return Task.FromResult(document.WithSyntaxRoot(root.ReplaceNode(memberDecl, newDecl)));
    }

    // -----------------------------------------------------------------
    //  Shared
    // -----------------------------------------------------------------

    private static bool HasModifier(SyntaxTokenList modifiers, SyntaxKind kind)
    {
        foreach (var m in modifiers)
            if (m.IsKind(kind))
                return true;
        return false;
    }
}
