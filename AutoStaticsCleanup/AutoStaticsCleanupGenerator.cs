using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AutoStaticsCleanup;

[Generator]
public class AutoStaticsCleanupGenerator : IIncrementalGenerator
{
    // Names of symbols emitted into the generated file. Centralised so emission
    // and tests share a single source of truth. The method/field names encode
    // the trigger key — "{ScopeType with '.'→'_'}_{ScopeTransitionType}" —
    // and only the PlayModeScope/Both trigger is ever emitted (the only one
    // DelegateAutoCleanup.CreateForPlayMode's public surface allows).
    private const string DelegateCleanupTypeFullName = "UnityEngine.DelegateAutoCleanup";
    private const string CleanupMethodName = "__AutoStaticsCleanup_UnityEngine_PlayModeScope_Both";
    private const string RegistrationFieldName = "__autoCleanup_UnityEngine_PlayModeScope_Both";
    private const string CompilerGeneratedAttr = "[System.Runtime.CompilerServices.CompilerGenerated]";

    // -----------------------------------------------------------------
    //  Cacheable models
    // -----------------------------------------------------------------

    // Numeric order doubles as emission order: all fields, then all
    // properties, then all events (each group in declaration order).
    private enum MemberKind : byte { Field, Property, Event }

    /// <summary>
    /// One static member that needs resetting on a play-mode transition.
    /// Immutable; equality is structural so the incremental cache only
    /// invalidates when something semantically changes.
    /// </summary>
    private readonly struct ResetEntry : IEquatable<ResetEntry>
    {
        public string ContainingTypeKey { get; init; }
        public string Namespace { get; init; }
        public ImmutableArray<string> PartialChain { get; init; }

        /// <summary>
        /// Parallel to <see cref="PartialChain"/>: true where the outer type
        /// is a struct — a `partial class` declaration won't merge with a
        /// struct (CS0261), so the keyword must match per level.
        /// </summary>
        public ImmutableArray<bool> PartialChainIsStruct { get; init; }

        public string SelfTypeDecl { get; init; }
        public bool SelfIsStruct { get; init; }
        public string MemberName { get; init; }
        public MemberKind Kind { get; init; }
        public string DelegateTypeName { get; init; }
        public bool RequiresGuard { get; init; }
        public bool RequiresDispose { get; init; }
        public bool RequiresClear { get; init; }
        public string Initializer { get; init; }
        public int SourceOrder { get; init; }
        public ImmutableArray<string> FileUsings { get; init; }

        /// <summary>
        /// For readonly collection members reset via Clear(): the rendered
        /// statements that restore the initializer's braced elements afterwards
        /// (<c>Foo.Add(1);</c> / <c>Bar["k"] = "v";</c>). Empty for everything
        /// else.
        /// </summary>
        public ImmutableArray<string> PostClearStatements { get; init; }

        /// <summary>
        /// Namespaces of every symbol referenced by the captured initializer
        /// expression. <see cref="EmitFileHeader"/> uses the union of these
        /// across a file's entries to keep only the source-file usings that
        /// are actually needed for the verbatim initializer text to compile.
        /// </summary>
        public ImmutableArray<string> InitializerNamespaces { get; init; }

        public bool Equals(ResetEntry other) =>
            ContainingTypeKey == other.ContainingTypeKey &&
            Namespace == other.Namespace &&
            ChainEquals(PartialChain, other.PartialChain) &&
            BoolChainEquals(PartialChainIsStruct, other.PartialChainIsStruct) &&
            SelfTypeDecl == other.SelfTypeDecl &&
            SelfIsStruct == other.SelfIsStruct &&
            MemberName == other.MemberName &&
            Kind == other.Kind &&
            DelegateTypeName == other.DelegateTypeName &&
            RequiresGuard == other.RequiresGuard &&
            RequiresDispose == other.RequiresDispose &&
            RequiresClear == other.RequiresClear &&
            Initializer == other.Initializer &&
            SourceOrder == other.SourceOrder &&
            ChainEquals(FileUsings, other.FileUsings) &&
            ChainEquals(PostClearStatements, other.PostClearStatements) &&
            ChainEquals(InitializerNamespaces, other.InitializerNamespaces);

        public override bool Equals(object obj) => obj is ResetEntry other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                var h = 17;
                h = h * 31 + (ContainingTypeKey?.GetHashCode() ?? 0);
                h = h * 31 + (MemberName?.GetHashCode() ?? 0);
                h = h * 31 + SourceOrder;
                return h;
            }
        }

        private static bool ChainEquals(ImmutableArray<string> a, ImmutableArray<string> b)
        {
            if (a.IsDefault) return b.IsDefault;
            if (b.IsDefault || a.Length != b.Length) return false;
            for (var i = 0; i < a.Length; i++)
                if (a[i] != b[i])
                    return false;
            return true;
        }

        private static bool BoolChainEquals(ImmutableArray<bool> a, ImmutableArray<bool> b)
        {
            if (a.IsDefault) return b.IsDefault;
            if (b.IsDefault || a.Length != b.Length) return false;
            for (var i = 0; i < a.Length; i++)
                if (a[i] != b[i])
                    return false;
            return true;
        }
    }

    /// <summary>
    /// Wrapper around <see cref="ImmutableArray{T}"/> with structural equality —
    /// the incremental cache uses default <c>Equals</c> on the transform output,
    /// and <c>ImmutableArray</c>'s default equality is reference-based, which
    /// would invalidate the downstream pipeline on every run.
    /// </summary>
    private readonly struct ExtractResult : IEquatable<ExtractResult>
    {
        public ImmutableArray<ResetEntry> Entries { get; init; }

        public bool Equals(ExtractResult other)
        {
            if (Entries.IsDefault) return other.Entries.IsDefault;
            if (other.Entries.IsDefault || Entries.Length != other.Entries.Length) return false;
            for (var i = 0; i < Entries.Length; i++)
                if (!Entries[i].Equals(other.Entries[i]))
                    return false;
            return true;
        }

        public override bool Equals(object obj) => obj is ExtractResult other && Equals(other);

        public override int GetHashCode() => Entries.IsDefault ? 0 : Entries.Length;
    }

    /// <summary>
    /// One containing-type's deduped, ready-to-emit entries. Fanned out from the
    /// collected transform results so <see cref="IIncrementalGenerator"/>'s
    /// per-element caching can skip <see cref="GenerateSource"/> for groups
    /// whose entries didn't change. Equality is structural on
    /// <see cref="ContainingTypeKey"/> + <see cref="Entries"/>.
    /// </summary>
    private readonly struct TypeGroup : IEquatable<TypeGroup>
    {
        public string ContainingTypeKey { get; init; }
        public ImmutableArray<ResetEntry> Entries { get; init; }

        public bool Equals(TypeGroup other)
        {
            if (ContainingTypeKey != other.ContainingTypeKey) return false;
            if (Entries.IsDefault) return other.Entries.IsDefault;
            if (other.Entries.IsDefault || Entries.Length != other.Entries.Length) return false;
            for (var i = 0; i < Entries.Length; i++)
                if (!Entries[i].Equals(other.Entries[i]))
                    return false;
            return true;
        }

        public override bool Equals(object obj) => obj is TypeGroup other && Equals(other);

        public override int GetHashCode() =>
            unchecked((ContainingTypeKey?.GetHashCode() ?? 0) * 31
                      + (Entries.IsDefault ? 0 : Entries.Length));
    }

    // -----------------------------------------------------------------
    //  Pipeline
    // -----------------------------------------------------------------

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var results = context.SyntaxProvider.ForAttributeWithMetadataName(
            Validation.AttributeFullName,
            predicate: static (_, _) => true,
            transform: static (ctx, ct) => Extract(ctx, ct));

        // Fan out: one TypeGroup per containing type. Roslyn compares fanned-out
        // values positionally with structural equality, so editing one attributed
        // file invalidates only that file's group — the other groups skip
        // GenerateSource entirely. The sort below keeps positions stable across
        // runs; without it, dictionary iteration order would shift and cascade-
        // invalidate the downstream cache.
        var grouped = results.Collect().SelectMany(static (all, ct) =>
        {
            var byType = new Dictionary<string, List<ResetEntry>>(StringComparer.Ordinal);
            foreach (var r in all)
            {
                if (r.Entries.IsDefault) continue;
                foreach (var e in r.Entries)
                {
                    if (!byType.TryGetValue(e.ContainingTypeKey, out var list))
                    {
                        list = new List<ResetEntry>();
                        byType[e.ContainingTypeKey] = list;
                    }
                    list.Add(e);
                }
            }

            var groups = ImmutableArray.CreateBuilder<TypeGroup>(byType.Count);
            foreach (var kvp in byType)
            {
                ct.ThrowIfCancellationRequested();
                var deduped = DeduplicateByMember(kvp.Value);
                if (deduped.Length == 0) continue;
                groups.Add(new TypeGroup
                {
                    ContainingTypeKey = kvp.Key,
                    Entries = deduped,
                });
            }

            // Ordinal sort is culture-free and gives a deterministic total order.
            return groups.ToImmutable().Sort(static (a, b) =>
                string.CompareOrdinal(a.ContainingTypeKey, b.ContainingTypeKey));
        });

        context.RegisterSourceOutput(grouped, static (spc, group) =>
        {
            var fileName = MakeFileName(group.Entries[0]);
            spc.AddSource(fileName, GenerateSource(group.Entries));
        });
    }

    private static ImmutableArray<ResetEntry> DeduplicateByMember(List<ResetEntry> entries)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var unique = ImmutableArray.CreateBuilder<ResetEntry>(entries.Count);
        foreach (var e in entries)
            if (seen.Add(e.MemberName))
                unique.Add(e);
        return unique.ToImmutable();
    }

    /// <summary>
    /// Builds the generated file name for a containing type: e.g.
    /// <c>MyNs.Foo.autocleanup.generated.cs</c> or
    /// <c>MyNs.Singleton_T_.autocleanup.generated.cs</c>. Generic syntax is
    /// flattened by replacing &lt;, &gt;, and comma separators with underscores.
    /// </summary>
    private static string MakeFileName(ResetEntry sample)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(sample.Namespace))
        {
            sb.Append(sample.Namespace);
            sb.Append('.');
        }
        foreach (var outer in sample.PartialChain)
        {
            sb.Append(SanitizeForFileName(outer));
            sb.Append('.');
        }
        sb.Append(SanitizeForFileName(sample.SelfTypeDecl));
        sb.Append(".autocleanup.generated.cs");
        return sb.ToString();
    }

    private static string SanitizeForFileName(string typeDecl)
    {
        // "Pair<T1, T2>" -> "Pair_T1_T2_"
        var sb = new StringBuilder(typeDecl.Length);
        for (var i = 0; i < typeDecl.Length; i++)
        {
            var c = typeDecl[i];
            if (c == '<' || c == '>') sb.Append('_');
            else if (c == ',')
            {
                sb.Append('_');
                if (i + 1 < typeDecl.Length && typeDecl[i + 1] == ' ') i++;
            }
            else if (c == ' ') { /* skip */ }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    // -----------------------------------------------------------------
    //  Extraction
    // -----------------------------------------------------------------

    private static ExtractResult Extract(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
    {
        var entries = ImmutableArray.CreateBuilder<ResetEntry>();
        var compilation = ctx.SemanticModel.Compilation;

        // Member-level dispatch: route every supported symbol kind into Add*.
        // Invalid shapes (non-static, const, manual-event, expression-bodied,
        // readonly fields, etc.) are silently skipped here — the analyzer
        // (AutoStaticsCleanupAnalyzer) is the source of truth for diagnostics.
        switch (ctx.TargetSymbol)
        {
            case INamedTypeSymbol type:
                CollectTypeMembers(type, compilation, entries, ct);
                break;
            case IFieldSymbol field:
                AddField(entries, field, compilation);
                break;
            case IPropertySymbol prop when !prop.IsIndexer:
                AddProperty(entries, prop, compilation);
                break;
            case IEventSymbol evt:
                AddEvent(entries, evt, compilation);
                break;
        }

        return new ExtractResult { Entries = entries.ToImmutable() };
    }

    private static void CollectTypeMembers(
        INamedTypeSymbol typeSymbol,
        Compilation compilation,
        ImmutableArray<ResetEntry>.Builder entries,
        CancellationToken ct)
    {
        // Verify the partial chain once at the type level — if it fails, emit
        // nothing for any member (analyzer reports the diagnostic separately).
        if (!Validation.IsPartialChain(typeSymbol)) return;

        // Precompute the type-level facts once and pass them through. Without
        // this, every static member of a type with N attributed members would
        // call ToDisplayString and BuildPartialChain N times against the same
        // INamedTypeSymbol.
        var ctx = TypeContext.For(typeSymbol);
        var usingsCache = new Dictionary<SyntaxTree, ImmutableArray<string>>();

        foreach (var member in typeSymbol.GetMembers())
        {
            ct.ThrowIfCancellationRequested();
            if (Validation.HasAttribute(member, Validation.NoAttributeFullName)) continue;

            switch (member)
            {
                case IFieldSymbol { IsStatic: true, IsConst: false, IsImplicitlyDeclared: false } f:
                    AddField(entries, f, compilation, ctx, usingsCache);
                    break;
                case IPropertySymbol { IsStatic: true, IsIndexer: false, IsImplicitlyDeclared: false } p:
                    AddProperty(entries, p, compilation, ctx, usingsCache);
                    break;
                case IEventSymbol { IsStatic: true, IsImplicitlyDeclared: false } e:
                    AddEvent(entries, e, compilation, ctx, usingsCache);
                    break;
            }
        }
    }

    /// <summary>
    /// Cached type-level facts shared by every member of a type during a
    /// type-level scan. The four fields all depend only on the containing
    /// type, so computing them once per scan instead of once per member
    /// halves the extraction cost in the common type-level path.
    /// </summary>
    private readonly struct TypeContext
    {
        public string ContainingTypeKey { get; init; }
        public string Namespace { get; init; }
        public ImmutableArray<string> PartialChain { get; init; }
        public ImmutableArray<bool> PartialChainIsStruct { get; init; }
        public string SelfTypeDecl { get; init; }
        public bool SelfIsStruct { get; init; }

        public static TypeContext For(INamedTypeSymbol type)
        {
            BuildPartialChain(type, out var chain, out var chainIsStruct);
            return new TypeContext
            {
                ContainingTypeKey = TypeKey(type),
                Namespace = NamespaceOf(type),
                PartialChain = chain,
                PartialChainIsStruct = chainIsStruct,
                SelfTypeDecl = TypeDecl(type),
                SelfIsStruct = type.IsValueType,
            };
        }
    }

    private static ImmutableArray<string> GetUsingsCached(
        ISymbol symbol,
        Dictionary<SyntaxTree, ImmutableArray<string>> cache)
    {
        if (symbol.DeclaringSyntaxReferences.Length == 0) return ImmutableArray<string>.Empty;
        var tree = symbol.DeclaringSyntaxReferences[0].SyntaxTree;
        if (cache.TryGetValue(tree, out var hit)) return hit;
        var result = GetUsingsFromTree(tree);
        cache[tree] = result;
        return result;
    }

    private static void AddField(
        ImmutableArray<ResetEntry>.Builder entries,
        IFieldSymbol field,
        Compilation compilation,
        TypeContext? ctx = null,
        Dictionary<SyntaxTree, ImmutableArray<string>> usingsCache = null)
    {
        if (!field.IsStatic) return;
        if (field.IsConst) return;

        // Readonly fields: exempt types (unmanaged, string, known-immutable,
        // arrays thereof) are silently left alone; the rest qualify only for
        // the Clear strategy — type has a parameterless Clear() and the
        // initializer is a 'new …' expression (braced elements are restored
        // via PostClearStatements) or default/absent.
        var initSyntax = Validation.GetFieldInitializerSyntax(field);
        var canCleanReadonly = false;
        if (field.IsReadOnly)
        {
            if (Validation.ReadonlyTypeIsExempt(field.Type)) return;
            canCleanReadonly = Validation.CanCleanReadonly(field.Type, initSyntax);
            if (!canCleanReadonly) return;
        }

        var owner = field.ContainingType;
        var c = ctx ?? TypeContext.For(owner);

        if (!Validation.IsPartialChain(owner)) return;

        var (initText, initNs) = CaptureInitializer(initSyntax, compilation);

        // Disposable without an initializer: nothing to construct a
        // replacement from after disposing, and resetting to default without
        // disposing would leak the old instance. Skip; the analyzer reports
        // ASC009.
        if (!field.IsReadOnly && initText == null && Validation.HasDisposeMethod(field.Type)) return;

        entries.Add(new ResetEntry
        {
            ContainingTypeKey = c.ContainingTypeKey,
            Namespace = c.Namespace,
            PartialChain = c.PartialChain,
            PartialChainIsStruct = c.PartialChainIsStruct,
            SelfTypeDecl = c.SelfTypeDecl,
            SelfIsStruct = c.SelfIsStruct,
            MemberName = field.Name,
            Kind = MemberKind.Field,
            RequiresGuard = !field.IsReadOnly && TypeRequiresGuard(field.Type),
            RequiresDispose = !canCleanReadonly
                              && initText != null
                              && Validation.HasDisposeMethod(field.Type),
            RequiresClear = canCleanReadonly,
            Initializer = initText,
            InitializerNamespaces = initNs,
            PostClearStatements = canCleanReadonly
                ? ExtractPostClearStatements(field.Name, initSyntax)
                : ImmutableArray<string>.Empty,
            SourceOrder = SourceOrderOf(field),
            FileUsings = usingsCache != null ? GetUsingsCached(field, usingsCache) : GetUsings(field),
        });
    }

    private static void AddProperty(
        ImmutableArray<ResetEntry>.Builder entries,
        IPropertySymbol prop,
        Compilation compilation,
        TypeContext? ctx = null,
        Dictionary<SyntaxTree, ImmutableArray<string>> usingsCache = null)
    {
        if (!prop.IsStatic) return;

        // Only auto-properties are reset: manual and expression-bodied
        // properties are silently skipped even with an explicit member-level
        // attribute (there is no backing state we can reason about). The
        // analyzer reports ASC003 for explicitly attributed manual properties.
        if (!Validation.IsAutoProperty(prop)) return;

        var initSyntax = Validation.GetPropertyInitializerSyntax(prop);

        // Getter-only auto-properties follow the readonly-field rules: exempt
        // types are silently skipped, Clear-strategy shapes are reset in place.
        // Init-only setters can't be written from the cleanup method.
        var canCleanReadonly = false;
        if (prop.SetMethod == null)
        {
            if (Validation.ReadonlyTypeIsExempt(prop.Type)) return;
            canCleanReadonly = Validation.CanCleanReadonly(prop.Type, initSyntax);
            if (!canCleanReadonly) return;
        }
        else if (prop.SetMethod.IsInitOnly)
        {
            return;
        }

        var owner = prop.ContainingType;
        var c = ctx ?? TypeContext.For(owner);

        if (!Validation.IsPartialChain(owner)) return;

        var (initText, initNs) = CaptureInitializer(initSyntax, compilation);

        // Same rule as fields: a settable disposable property with no
        // initializer can't be reset — skip, ASC009 reports it.
        if (!canCleanReadonly && initText == null && Validation.HasDisposeMethod(prop.Type)) return;

        entries.Add(new ResetEntry
        {
            ContainingTypeKey = c.ContainingTypeKey,
            Namespace = c.Namespace,
            PartialChain = c.PartialChain,
            PartialChainIsStruct = c.PartialChainIsStruct,
            SelfTypeDecl = c.SelfTypeDecl,
            SelfIsStruct = c.SelfIsStruct,
            MemberName = prop.Name,
            Kind = MemberKind.Property,
            RequiresGuard = !canCleanReadonly && TypeRequiresGuard(prop.Type),
            RequiresDispose = !canCleanReadonly
                              && initText != null
                              && Validation.HasDisposeMethod(prop.Type),
            RequiresClear = canCleanReadonly,
            Initializer = initText,
            InitializerNamespaces = initNs,
            PostClearStatements = canCleanReadonly
                ? ExtractPostClearStatements(prop.Name, initSyntax)
                : ImmutableArray<string>.Empty,
            SourceOrder = SourceOrderOf(prop),
            FileUsings = usingsCache != null ? GetUsingsCached(prop, usingsCache) : GetUsings(prop),
        });
    }

    private static void AddEvent(
        ImmutableArray<ResetEntry>.Builder entries,
        IEventSymbol evt,
        Compilation compilation,
        TypeContext? ctx = null,
        Dictionary<SyntaxTree, ImmutableArray<string>> usingsCache = null)
    {
        if (!evt.IsStatic) return;

        // Manual events (with explicit add/remove) — Evt.GetInvocationList()
        // doesn't compile because Evt isn't a delegate field outside the
        // declaring scope of a field-like event.
        if (!Validation.IsFieldLikeEvent(evt)) return;

        var owner = evt.ContainingType;
        var c = ctx ?? TypeContext.For(owner);

        if (!Validation.IsPartialChain(owner)) return;

        entries.Add(new ResetEntry
        {
            ContainingTypeKey = c.ContainingTypeKey,
            Namespace = c.Namespace,
            PartialChain = c.PartialChain,
            PartialChainIsStruct = c.PartialChainIsStruct,
            SelfTypeDecl = c.SelfTypeDecl,
            SelfIsStruct = c.SelfIsStruct,
            MemberName = evt.Name,
            Kind = MemberKind.Event,
            // Default display format — namespace-qualified, no global::.
            DelegateTypeName = evt.Type.ToDisplayString(),
            SourceOrder = SourceOrderOf(evt),
            FileUsings = usingsCache != null ? GetUsingsCached(evt, usingsCache) : GetUsings(evt),
        });
    }

    // -----------------------------------------------------------------
    //  Symbol helpers
    // -----------------------------------------------------------------

    /// <summary>
    /// True when a reset write should be guarded with `is not null`:
    /// reference types only. Type parameters count only when constrained to
    /// be reference types (<c>IsReferenceType</c> is false for unconstrained
    /// <c>T</c>, which gets the bare assignment).
    /// </summary>
    private static bool TypeRequiresGuard(ITypeSymbol type) => type.IsReferenceType;

    private static int SourceOrderOf(ISymbol symbol) =>
        symbol.DeclaringSyntaxReferences.Length == 0
            ? 0
            : symbol.DeclaringSyntaxReferences[0].Span.Start;

    private static string TypeKey(INamedTypeSymbol type) =>
        type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    private static string NamespaceOf(INamedTypeSymbol type)
    {
        var ns = type.ContainingNamespace;
        return ns is { IsGlobalNamespace: false } ? ns.ToDisplayString() : "";
    }

    /// <summary>
    /// Renders a type's declaration form using its type parameter names.
    /// Singleton&lt;T&gt; → "Singleton&lt;T&gt;"; Foo → "Foo".
    /// </summary>
    private static string TypeDecl(INamedTypeSymbol type)
    {
        if (type.TypeParameters.Length == 0) return type.Name;
        var sb = new StringBuilder(type.Name);
        sb.Append('<');
        for (var i = 0; i < type.TypeParameters.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(type.TypeParameters[i].Name);
        }
        sb.Append('>');
        return sb.ToString();
    }

    private static void BuildPartialChain(
        INamedTypeSymbol type, out ImmutableArray<string> names, out ImmutableArray<bool> isStruct)
    {
        // Walk inner-to-outer (cheap), then reverse — avoids List.Insert(0)'s
        // O(n²) shifting on deeply nested types.
        var chain = ImmutableArray.CreateBuilder<string>();
        var structFlags = ImmutableArray.CreateBuilder<bool>();
        for (var t = type.ContainingType; t != null; t = t.ContainingType)
        {
            chain.Add(TypeDecl(t));
            structFlags.Add(t.IsValueType);
        }
        chain.Reverse();
        structFlags.Reverse();
        names = chain.ToImmutable();
        isStruct = structFlags.ToImmutable();
    }

    /// <summary>
    /// For a readonly collection member reset via Clear() whose initializer
    /// has braced elements — <c>new() { 1, 2 }</c>, <c>new() { { k, v } }</c>,
    /// <c>new() { [k] = v }</c> — renders the statements that restore the
    /// initial content afterwards: two-expression initializer elements become
    /// indexer writes, everything else becomes <c>.Add(…)</c>.
    /// (C# 12 collection expressions are omitted deliberately — the target
    /// compilers don't parse them.)
    /// </summary>
    private static ImmutableArray<string> ExtractPostClearStatements(
        string memberName, ExpressionSyntax initSyntax)
    {
        if (initSyntax is not BaseObjectCreationExpressionSyntax { Initializer: { } initializer })
            return ImmutableArray<string>.Empty;

        var exprs = initializer.Expressions;
        if (exprs.Count == 0) return ImmutableArray<string>.Empty;

        var builder = ImmutableArray.CreateBuilder<string>(exprs.Count);
        foreach (var expr in exprs)
        {
            if (expr is InitializerExpressionSyntax { Expressions.Count: 2 } complex)
                builder.Add($"{memberName}[{complex.Expressions[0]}] = {complex.Expressions[1]};");
            else
                builder.Add($"{memberName}.Add({expr});");
        }
        return builder.MoveToImmutable();
    }

    private static (string Text, ImmutableArray<string> Namespaces) CaptureInitializer(
        ExpressionSyntax expr, Compilation compilation)
    {
        if (expr == null) return (null, ImmutableArray<string>.Empty);
        var text = expr.ToFullString().Trim();
        var model = compilation.GetSemanticModel(expr.SyntaxTree);

        // Walk every name node in the initializer and resolve it. Anything
        // whose containing namespace is non-global ends up in the set — its
        // using is what keeps the verbatim initializer text compiling.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ns = ImmutableArray.CreateBuilder<string>();
        foreach (var node in expr.DescendantNodesAndSelf())
        {
            var info = model.GetSymbolInfo(node);
            var symbol = info.Symbol;
            if (symbol == null && info.CandidateSymbols.Length > 0)
                symbol = info.CandidateSymbols[0];
            var nsName = NamespaceOfSymbol(symbol);
            if (nsName != null && seen.Add(nsName)) ns.Add(nsName);
        }
        return (text, ns.ToImmutable());
    }

    private static string NamespaceOfSymbol(ISymbol symbol)
    {
        if (symbol == null) return null;
        // Type symbols carry their namespace directly. Members (methods, fields,
        // properties, events) carry a containing type whose namespace is what we want.
        // Bare namespace symbols are skipped — usings target leaf namespaces, not
        // the names that lead to them.
        INamespaceSymbol ns = symbol switch
        {
            INamespaceSymbol => null,
            ITypeSymbol t => t.ContainingNamespace,
            _ => symbol.ContainingType?.ContainingNamespace ?? symbol.ContainingNamespace,
        };
        if (ns == null || ns.IsGlobalNamespace) return null;
        return ns.ToDisplayString();
    }

    private static ImmutableArray<string> GetUsings(ISymbol symbol)
    {
        if (symbol.DeclaringSyntaxReferences.Length == 0) return ImmutableArray<string>.Empty;
        return GetUsingsFromTree(symbol.DeclaringSyntaxReferences[0].SyntaxTree);
    }

    /// <summary>
    /// Collects every using directive in <paramref name="tree"/>. C# only allows
    /// usings at the compilation-unit level and inside namespace declarations, so
    /// we hit those two spots directly instead of walking the whole tree.
    /// </summary>
    private static ImmutableArray<string> GetUsingsFromTree(SyntaxTree tree)
    {
        if (tree.GetRoot() is not CompilationUnitSyntax cu) return ImmutableArray<string>.Empty;

        var builder = ImmutableArray.CreateBuilder<string>();

        foreach (var u in cu.Usings) builder.Add(NormalizeUsing(u));

        foreach (var ns in cu.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>())
            foreach (var u in ns.Usings)
                builder.Add(NormalizeUsing(u));

        return builder.ToImmutable();
    }

    /// <summary>
    /// Returns the directive's text with surrounding trivia stripped (comments,
    /// #region, #if/#endif) and internal newlines collapsed to spaces. The
    /// internal-newline collapse handles multi-line `using Alias = …;` directives
    /// that would otherwise be split across the generated file's deduplication step.
    /// </summary>
    private static string NormalizeUsing(UsingDirectiveSyntax u)
    {
        var text = u.ToString().Trim();
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            sb.Append(c == '\r' || c == '\n' ? ' ' : c);
        }
        return sb.ToString();
    }

    // -----------------------------------------------------------------
    //  Source generation
    // -----------------------------------------------------------------

    // Per-thread reusable buffer for GenerateSource. The default StringWriter
    // ctor allocates a fresh StringBuilder that grows by doubling, producing
    // several KB of intermediate garbage per generated file. Pooling the
    // buffer lets cold runs reuse one allocation per thread.
    [ThreadStatic] private static StringBuilder t_pooledBuilder;

    // Cap pooled capacity so an outsize generated file (rare but possible
    // with hundreds of attributed members) doesn't pin a huge buffer.
    private const int MaxPooledBuilderCapacity = 32 * 1024;

    private static string GenerateSource(ImmutableArray<ResetEntry> entries)
    {
        var sb = t_pooledBuilder;
        if (sb == null) sb = new StringBuilder(2048);
        else { t_pooledBuilder = null; sb.Clear(); }

        try
        {
            using var stringWriter = new StringWriter(sb);
            using var w = new IndentedTextWriter(stringWriter);

            // Caller has already grouped by containing type, deduped, and ordered.
            EmitFileHeader(w, entries);

            // Fields, then properties, then events, each in declaration order.
            var ordered = entries.OrderBy(e => e.Kind)
                                 .ThenBy(e => e.SourceOrder)
                                 .ToList();
            EmitTypeBlock(w, ordered[0], ordered);

            EmitFileFooter(w);

            w.Flush();
            return sb.ToString();
        }
        finally
        {
            if (sb.Capacity <= MaxPooledBuilderCapacity) t_pooledBuilder = sb;
        }
    }

    private static void EmitFileHeader(IndentedTextWriter w, ImmutableArray<ResetEntry> entries)
    {
        w.WriteLine("// <auto-generated/>");
        w.WriteLine("#if !UNITY_6000_5_OR_NEWER");
        w.WriteLine("#pragma warning disable CS0618");

        // Union of namespaces referenced by any captured initializer in this
        // file. We keep a source-file using only if its target namespace shows
        // up here (or is a prefix of one — covers relative type references like
        // `Helpers.Tool` written under `using MyApp;`).
        var requiredNs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            if (e.InitializerNamespaces.IsDefaultOrEmpty) continue;
            foreach (var n in e.InitializerNamespaces) requiredNs.Add(n);
        }

        // Insertion-ordered dedupe: emit Unity.Scripting.LifecycleManagement
        // first, then any source-file using actually needed by an initializer.
        // Every using is followed by a blank line — part of the pinned output
        // layout.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<string>();
        void Add(string u) { if (seen.Add(u)) ordered.Add(u); }

        Add("using Unity.Scripting.LifecycleManagement;");
        foreach (var e in entries)
        {
            if (e.FileUsings.IsDefaultOrEmpty) continue;
            foreach (var u in e.FileUsings)
            {
                if (string.IsNullOrEmpty(u)) continue;
                if (UsingIsNeeded(u, requiredNs)) Add(u);
            }
        }
        foreach (var u in ordered)
        {
            w.WriteLine(u);
            w.WriteLine();
        }
    }

    /// <summary>
    /// True if a source-file using directive should make it into the generated
    /// file. Plain <c>using Foo;</c> directives are kept only when an initializer
    /// references a type whose namespace matches <c>Foo</c> (or a child of
    /// <c>Foo</c>, since the user could have written a relative name).
    /// <c>using static</c> and <c>using Alias = …;</c> directives are always
    /// kept — we can't trace whether their introduced names are referenced
    /// without much heavier analysis, and over-emitting is harmless.
    /// </summary>
    private static bool UsingIsNeeded(string usingDirective, HashSet<string> requiredNs)
    {
        var trimmed = usingDirective.TrimStart();
        if (!trimmed.StartsWith("using ", StringComparison.Ordinal)) return true;

        var body = trimmed.Substring("using ".Length).TrimEnd(';').Trim();
        if (body.StartsWith("static ", StringComparison.Ordinal)) return true;
        if (body.IndexOf('=') >= 0) return true;

        foreach (var req in requiredNs)
            if (req == body || req.StartsWith(body + ".", StringComparison.Ordinal))
                return true;
        return false;
    }

    private static void EmitFileFooter(IndentedTextWriter w)
    {
        w.WriteLine("#pragma warning restore CS0618");
        w.WriteLine("#endif");
    }

    private static void EmitTypeBlock(IndentedTextWriter w, ResetEntry sample, IReadOnlyList<ResetEntry> entries)
    {
        var hasNs = !string.IsNullOrEmpty(sample.Namespace);
        if (hasNs)
        {
            w.WriteLine($"namespace {sample.Namespace}");
            w.WriteLine("{");
            w.Indent++;
        }

        // `partial struct` for value types — matching the user's declaration
        // kind is required for the partial declarations to merge (CS0261).
        for (var i = 0; i < sample.PartialChain.Length; i++)
        {
            w.WriteLine($"partial {(sample.PartialChainIsStruct[i] ? "struct" : "class")} {sample.PartialChain[i]}");
            w.WriteLine("{");
            w.Indent++;
        }

        w.WriteLine($"partial {(sample.SelfIsStruct ? "struct" : "class")} {sample.SelfTypeDecl}");
        w.WriteLine("{");
        w.Indent++;

        EmitCleanupMethod(w, entries);
        EmitRegistrationField(w, OwnerDescription(sample));

        w.Indent--;
        w.WriteLine("}");

        for (var i = 0; i < sample.PartialChain.Length; i++)
        {
            w.Indent--;
            w.WriteLine("}");
        }

        if (hasNs)
        {
            w.Indent--;
            w.WriteLine("}");
        }
    }

    private static void EmitCleanupMethod(IndentedTextWriter w, IReadOnlyList<ResetEntry> entries)
    {
        w.WriteLine(CompilerGeneratedAttr);
        w.WriteLine($"static void {CleanupMethodName}()");
        w.WriteLine("{");
        w.Indent++;

        foreach (var e in entries)
        {
            if (e.Kind == MemberKind.Event) EmitEvent(w, e);
            else EmitAssign(w, e);
        }

        w.Indent--;
        w.WriteLine("}");
    }

    private static void EmitRegistrationField(IndentedTextWriter w, string ownerDescription)
    {
        w.WriteLine(CompilerGeneratedAttr);
        w.WriteLine($"static readonly {DelegateCleanupTypeFullName} {RegistrationFieldName} = "
                    + $"{DelegateCleanupTypeFullName}.CreateForPlayMode({CleanupMethodName}, \"{ownerDescription}\");");
    }

    /// <summary>
    /// The diagnostic string passed as DelegateAutoCleanup's ownerDescription
    /// (surfaced via ToString() when a cleanup fails): the dotted type path
    /// with generic angle brackets flattened to underscores —
    /// <c>MyNs.Outer.Inner</c>, <c>MyNs.Singleton_T_</c>. Commas and spaces
    /// inside generic argument lists are kept (only &lt; and &gt; are
    /// replaced).
    /// </summary>
    private static string OwnerDescription(ResetEntry sample)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(sample.Namespace))
        {
            sb.Append(sample.Namespace);
            sb.Append('.');
        }
        foreach (var outer in sample.PartialChain)
        {
            sb.Append(outer);
            sb.Append('.');
        }
        sb.Append(sample.SelfTypeDecl);
        return sb.Replace('<', '_').Replace('>', '_').ToString();
    }

    private static void EmitAssign(IndentedTextWriter w, ResetEntry e)
    {
        if (e.RequiresClear)
        {
            // Readonly + Clear() strategy: empty the container in place, then
            // restore any braced initializer elements (PostClearStatements) so
            // the post-cleanup state matches the original initializer.
            w.WriteLine($"{e.MemberName}.Clear();");
            if (!e.PostClearStatements.IsDefaultOrEmpty)
                foreach (var s in e.PostClearStatements)
                    w.WriteLine(s);
            return;
        }

        var rhs = e.Initializer ?? "default";
        if (e.RequiresDispose)
        {
            // Dispose the existing value before reassigning. The null guard is
            // merged into the same branch when the field is a reference type so
            // a never-touched field is a single check, not two. The `){` with
            // no space is deliberate — the exact text is pinned by the
            // byte-for-byte output tests.
            if (e.RequiresGuard)
                w.WriteLine($"if({e.MemberName} is not null){{ {e.MemberName}.Dispose(); {e.MemberName} = {rhs}; }}");
            else
                w.WriteLine($"{e.MemberName}.Dispose(); {e.MemberName} = {rhs};");
        }
        else if (e.RequiresGuard)
            w.WriteLine($"if({e.MemberName} is not null) {e.MemberName} = {rhs};");
        else
            w.WriteLine($"{e.MemberName} = {rhs};");
    }

    private static void EmitEvent(IndentedTextWriter w, ResetEntry e)
    {
        w.WriteLine($"if({e.MemberName} != null)");
        w.WriteLine("{");
        w.Indent++;
        w.WriteLine($"foreach({e.DelegateTypeName} handler in {e.MemberName}.GetInvocationList())");
        w.WriteLine("{");
        w.Indent++;
        w.WriteLine($"{e.MemberName} -= handler;");
        w.Indent--;
        w.WriteLine("}");
        w.Indent--;
        w.WriteLine("}");
    }
}
