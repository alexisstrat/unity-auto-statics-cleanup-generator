using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace AutoStaticsCleanup;

[Generator]
public class AutoStaticsCleanupGenerator : IIncrementalGenerator
{
    private const string AttributeFullName = "Unity.Scripting.LifecycleManagement.AutoStaticsCleanupAttribute";
    private const string NoAttributeFullName = "Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanupAttribute";

    // Names of symbols emitted into the generated file. Centralised so emission
    // and tests share a single source of truth.
    private const string CleanupBaseTypeFullName = "UnityEngine.PlayModeScopeAutoCleanup";
    private const string NestedClassName = "UnityEngine_PlayModeScopeAutoCleanup_Both_AutoCleanupType";
    private const string StaticFieldName = "_UnityEngine_PlayModeScopeAutoCleanup_Both_AutoCleanupType";
    private const string CompilerGeneratedAttr = "[System.Runtime.CompilerServices.CompilerGenerated]";

    // -----------------------------------------------------------------
    //  Diagnostics
    // -----------------------------------------------------------------

    private static readonly DiagnosticDescriptor MustBePartial = new(
        "ASC001",
        "Type must be 'partial'",
        "Type '{0}' must be declared 'partial' (and so must every enclosing type) to use [AutoStaticsCleanup]",
        "AutoStaticsCleanup",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor ReadonlyNotSupported = new(
        "ASC002",
        "[AutoStaticsCleanup] cannot be applied to readonly fields",
        "Field '{0}' is readonly; [AutoStaticsCleanup] requires a settable field",
        "AutoStaticsCleanup",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor PropertyNeedsSetter = new(
        "ASC003",
        "[AutoStaticsCleanup] requires a property setter",
        "Property '{0}' has no usable setter; [AutoStaticsCleanup] requires a settable property (init-only setters are not callable from Cleanup)",
        "AutoStaticsCleanup",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor ManualEventNotSupported = new(
        "ASC004",
        "[AutoStaticsCleanup] does not support manual events",
        "Event '{0}' has explicit 'add'/'remove' accessors; [AutoStaticsCleanup] only supports field-like events",
        "AutoStaticsCleanup",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor NestedInGenericNotSupported = new(
        "ASC005",
        "[AutoStaticsCleanup] does not support types nested inside generic types",
        "Type '{0}' is nested inside a generic type; closed generic instantiations cannot be discovered for cleanup",
        "AutoStaticsCleanup",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor MemberMustBeStatic = new(
        "ASC006",
        "[AutoStaticsCleanup] requires a static member",
        "Member '{0}' is not static; [AutoStaticsCleanup] only applies to static fields, properties, and events",
        "AutoStaticsCleanup",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor ConstFieldNotSupported = new(
        "ASC007",
        "[AutoStaticsCleanup] cannot be applied to const fields",
        "Field '{0}' is const; const fields cannot be reset and [AutoStaticsCleanup] has no effect",
        "AutoStaticsCleanup",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    // -----------------------------------------------------------------
    //  Cacheable models
    // -----------------------------------------------------------------

    private enum MemberKind : byte { Assign, Event }

    private readonly struct LocationInfo : IEquatable<LocationInfo>
    {
        public string FilePath { get; init; }
        public TextSpan Span { get; init; }
        public LinePositionSpan LineSpan { get; init; }

        public Location ToLocation() => Location.Create(FilePath ?? "", Span, LineSpan);

        public bool Equals(LocationInfo other) =>
            FilePath == other.FilePath && Span.Equals(other.Span) && LineSpan.Equals(other.LineSpan);

        public override bool Equals(object obj) => obj is LocationInfo other && Equals(other);

        public override int GetHashCode() =>
            unchecked((FilePath?.GetHashCode() ?? 0) * 31 + Span.GetHashCode());

        public static LocationInfo From(SyntaxReference syntaxRef)
        {
            if (syntaxRef == null) return default;
            var loc = syntaxRef.GetSyntax().GetLocation();
            return new LocationInfo
            {
                FilePath = loc.SourceTree?.FilePath ?? "",
                Span = loc.SourceSpan,
                LineSpan = loc.GetLineSpan().Span,
            };
        }
    }

    private readonly struct DiagnosticInfo : IEquatable<DiagnosticInfo>
    {
        public string DescriptorId { get; init; }
        public string MessageArg { get; init; }
        public LocationInfo Location { get; init; }

        public bool Equals(DiagnosticInfo other) =>
            DescriptorId == other.DescriptorId
            && MessageArg == other.MessageArg
            && Location.Equals(other.Location);

        public override bool Equals(object obj) => obj is DiagnosticInfo other && Equals(other);

        public override int GetHashCode() =>
            unchecked((DescriptorId?.GetHashCode() ?? 0) * 31 + (MessageArg?.GetHashCode() ?? 0));

        public Diagnostic ToDiagnostic()
        {
            var d = DescriptorId switch
            {
                "ASC001" => MustBePartial,
                "ASC002" => ReadonlyNotSupported,
                "ASC003" => PropertyNeedsSetter,
                "ASC004" => ManualEventNotSupported,
                "ASC005" => NestedInGenericNotSupported,
                "ASC006" => MemberMustBeStatic,
                "ASC007" => ConstFieldNotSupported,
                _ => null,
            };
            return d == null ? null : Diagnostic.Create(d, Location.ToLocation(), MessageArg);
        }
    }

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
        public string SelfTypeDecl { get; init; }
        public string MemberName { get; init; }
        public MemberKind Kind { get; init; }
        public string DelegateTypeFq { get; init; }
        public bool RequiresGuard { get; init; }
        public string Initializer { get; init; }
        public int SourceOrder { get; init; }
        public ImmutableArray<string> FileUsings { get; init; }

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
            SelfTypeDecl == other.SelfTypeDecl &&
            MemberName == other.MemberName &&
            Kind == other.Kind &&
            DelegateTypeFq == other.DelegateTypeFq &&
            RequiresGuard == other.RequiresGuard &&
            Initializer == other.Initializer &&
            SourceOrder == other.SourceOrder &&
            ChainEquals(FileUsings, other.FileUsings) &&
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
    }

    private readonly struct ExtractResult : IEquatable<ExtractResult>
    {
        public ImmutableArray<ResetEntry> Entries { get; init; }
        public ImmutableArray<DiagnosticInfo> Diagnostics { get; init; }

        public bool Equals(ExtractResult other) =>
            ArrEquals(Entries, other.Entries) && ArrEquals(Diagnostics, other.Diagnostics);

        public override bool Equals(object obj) => obj is ExtractResult other && Equals(other);

        public override int GetHashCode() =>
            unchecked((Entries.IsDefault ? 0 : Entries.Length) * 31
                      + (Diagnostics.IsDefault ? 0 : Diagnostics.Length));

        private static bool ArrEquals<T>(ImmutableArray<T> a, ImmutableArray<T> b) where T : IEquatable<T>
        {
            if (a.IsDefault) return b.IsDefault;
            if (b.IsDefault || a.Length != b.Length) return false;
            for (var i = 0; i < a.Length; i++)
                if (!a[i].Equals(b[i]))
                    return false;
            return true;
        }
    }

    // -----------------------------------------------------------------
    //  Pipeline
    // -----------------------------------------------------------------

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var results = context.SyntaxProvider.ForAttributeWithMetadataName(
            AttributeFullName,
            predicate: static (_, _) => true,
            transform: static (ctx, ct) => Extract(ctx, ct));

        // Diagnostics — emitted regardless of whether entries are present.
        context.RegisterSourceOutput(results, static (spc, r) =>
        {
            if (r.Diagnostics.IsDefault) return;
            foreach (var d in r.Diagnostics)
            {
                var diag = d.ToDiagnostic();
                if (diag != null) spc.ReportDiagnostic(diag);
            }
        });

        // Source — collect, group by containing type, emit one file per group.
        // Roslyn's per-tree caching means an emitted file with byte-identical
        // (name, text) skips downstream parsing on the next run, so editing one
        // attributed type doesn't force the others to re-parse.
        context.RegisterSourceOutput(results.Collect(), static (spc, all) =>
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

            foreach (var group in byType)
            {
                var deduped = DeduplicateByMember(group.Value);
                if (deduped.Length == 0) continue;
                var fileName = MakeFileName(deduped[0]);
                spc.AddSource(fileName, GenerateSource(deduped));
            }
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
        var diags = ImmutableArray.CreateBuilder<DiagnosticInfo>();
        var compilation = ctx.SemanticModel.Compilation;

        // Member-level dispatch: route every supported symbol kind into Add* so
        // the methods can emit ASC006 / ASC007 / ASC004 for misuse instead of
        // silently dropping the attribute.
        switch (ctx.TargetSymbol)
        {
            case INamedTypeSymbol type:
                CollectTypeMembers(type, compilation, entries, diags, ct);
                break;
            case IFieldSymbol field:
                AddField(entries, diags, field, compilation);
                break;
            case IPropertySymbol prop when !prop.IsIndexer:
                AddProperty(entries, diags, prop, compilation);
                break;
            case IEventSymbol evt:
                AddEvent(entries, diags, evt, compilation);
                break;
        }

        return new ExtractResult
        {
            Entries = entries.ToImmutable(),
            Diagnostics = diags.ToImmutable(),
        };
    }

    private static void CollectTypeMembers(
        INamedTypeSymbol typeSymbol,
        Compilation compilation,
        ImmutableArray<ResetEntry>.Builder entries,
        ImmutableArray<DiagnosticInfo>.Builder diags,
        CancellationToken ct)
    {
        // Verify the partial chain once at the type level — if it fails, every
        // member would emit the same diagnostic, which is just noise.
        if (!IsPartialChain(typeSymbol))
        {
            diags.Add(MakePartialDiagnostic(typeSymbol, typeSymbol));
            return;
        }

        // Precompute the type-level facts once and pass them through. Without
        // this, every static member of a type with N attributed members would
        // call ToDisplayString and BuildPartialChain N times against the same
        // INamedTypeSymbol.
        var ctx = TypeContext.For(typeSymbol);
        var usingsCache = new Dictionary<SyntaxTree, ImmutableArray<string>>();

        foreach (var member in typeSymbol.GetMembers())
        {
            ct.ThrowIfCancellationRequested();
            if (HasAttribute(member, NoAttributeFullName)) continue;

            switch (member)
            {
                case IFieldSymbol { IsStatic: true, IsConst: false, IsImplicitlyDeclared: false } f:
                    AddField(entries, diags, f, compilation, ctx, usingsCache);
                    break;
                case IPropertySymbol { IsStatic: true, IsIndexer: false, IsImplicitlyDeclared: false } p:
                    AddProperty(entries, diags, p, compilation, ctx, usingsCache);
                    break;
                case IEventSymbol { IsStatic: true, IsImplicitlyDeclared: false } e:
                    AddEvent(entries, diags, e, compilation, ctx, usingsCache);
                    break;
            }
        }
    }

    /// <summary>
    /// Cached type-level facts shared by every member of a type during a
    /// type-level scan. <see cref="ContainingTypeKey"/>, <see cref="Namespace"/>,
    /// <see cref="PartialChain"/>, <see cref="SelfTypeDecl"/>, and
    /// <see cref="HasGenericOuter"/> all depend only on the containing type, so
    /// computing them once per scan instead of once per member halves the
    /// extraction cost in the common type-level path.
    /// </summary>
    private readonly struct TypeContext
    {
        public string ContainingTypeKey { get; init; }
        public string Namespace { get; init; }
        public ImmutableArray<string> PartialChain { get; init; }
        public string SelfTypeDecl { get; init; }
        public bool HasGenericOuter { get; init; }

        public static TypeContext For(INamedTypeSymbol type) => new()
        {
            ContainingTypeKey = TypeKey(type),
            Namespace = NamespaceOf(type),
            PartialChain = BuildPartialChain(type),
            SelfTypeDecl = TypeDecl(type),
            HasGenericOuter = AutoStaticsCleanupGenerator.HasGenericOuter(type),
        };
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
        ImmutableArray<DiagnosticInfo>.Builder diags,
        IFieldSymbol field,
        Compilation compilation,
        TypeContext? ctx = null,
        Dictionary<SyntaxTree, ImmutableArray<string>> usingsCache = null)
    {
        if (!field.IsStatic)
        {
            diags.Add(MakeMemberDiagnostic("ASC006", field));
            return;
        }

        if (field.IsConst)
        {
            diags.Add(MakeMemberDiagnostic("ASC007", field));
            return;
        }

        var owner = field.ContainingType;
        var c = ctx ?? TypeContext.For(owner);

        if (c.HasGenericOuter)
        {
            diags.Add(MakeNestedInGenericDiagnostic(owner, field));
            return;
        }

        if (!IsPartialChain(owner))
        {
            diags.Add(MakePartialDiagnostic(owner, field));
            return;
        }

        if (field.IsReadOnly)
        {
            diags.Add(new DiagnosticInfo
            {
                DescriptorId = "ASC002",
                MessageArg = field.Name,
                Location = LocationInfo.From(field.DeclaringSyntaxReferences.FirstOrDefault()),
            });
            return;
        }

        var (initText, initNs) = GetFieldInitializer(field, compilation);
        entries.Add(new ResetEntry
        {
            ContainingTypeKey = c.ContainingTypeKey,
            Namespace = c.Namespace,
            PartialChain = c.PartialChain,
            SelfTypeDecl = c.SelfTypeDecl,
            MemberName = field.Name,
            Kind = MemberKind.Assign,
            RequiresGuard = TypeRequiresGuard(field.Type),
            Initializer = initText,
            InitializerNamespaces = initNs,
            SourceOrder = SourceOrderOf(field),
            FileUsings = usingsCache != null ? GetUsingsCached(field, usingsCache) : GetUsings(field),
        });
    }

    private static void AddProperty(
        ImmutableArray<ResetEntry>.Builder entries,
        ImmutableArray<DiagnosticInfo>.Builder diags,
        IPropertySymbol prop,
        Compilation compilation,
        TypeContext? ctx = null,
        Dictionary<SyntaxTree, ImmutableArray<string>> usingsCache = null)
    {
        if (!prop.IsStatic)
        {
            diags.Add(MakeMemberDiagnostic("ASC006", prop));
            return;
        }

        // Expression-bodied properties carry no state.
        if (prop.DeclaringSyntaxReferences.Length > 0
            && prop.DeclaringSyntaxReferences[0].GetSyntax() is PropertyDeclarationSyntax { ExpressionBody: not null })
            return;

        var owner = prop.ContainingType;
        var c = ctx ?? TypeContext.For(owner);

        if (c.HasGenericOuter)
        {
            diags.Add(MakeNestedInGenericDiagnostic(owner, prop));
            return;
        }

        if (!IsPartialChain(owner))
        {
            diags.Add(MakePartialDiagnostic(owner, prop));
            return;
        }

        // No setter, or init-only setter — can't be reset from Cleanup().
        if (prop.SetMethod == null || prop.SetMethod.IsInitOnly)
        {
            diags.Add(new DiagnosticInfo
            {
                DescriptorId = "ASC003",
                MessageArg = prop.Name,
                Location = LocationInfo.From(prop.DeclaringSyntaxReferences.FirstOrDefault()),
            });
            return;
        }

        var (initText, initNs) = GetPropertyInitializer(prop, compilation);
        entries.Add(new ResetEntry
        {
            ContainingTypeKey = c.ContainingTypeKey,
            Namespace = c.Namespace,
            PartialChain = c.PartialChain,
            SelfTypeDecl = c.SelfTypeDecl,
            MemberName = prop.Name,
            Kind = MemberKind.Assign,
            RequiresGuard = TypeRequiresGuard(prop.Type),
            Initializer = initText,
            InitializerNamespaces = initNs,
            SourceOrder = SourceOrderOf(prop),
            FileUsings = usingsCache != null ? GetUsingsCached(prop, usingsCache) : GetUsings(prop),
        });
    }

    private static void AddEvent(
        ImmutableArray<ResetEntry>.Builder entries,
        ImmutableArray<DiagnosticInfo>.Builder diags,
        IEventSymbol evt,
        Compilation compilation,
        TypeContext? ctx = null,
        Dictionary<SyntaxTree, ImmutableArray<string>> usingsCache = null)
    {
        if (!evt.IsStatic)
        {
            diags.Add(MakeMemberDiagnostic("ASC006", evt));
            return;
        }

        // Manual events (with explicit add/remove) — Evt.GetInvocationList()
        // doesn't compile because Evt isn't a delegate field outside the
        // declaring scope of a field-like event.
        if (!IsFieldLikeEvent(evt))
        {
            diags.Add(MakeMemberDiagnostic("ASC004", evt));
            return;
        }

        var owner = evt.ContainingType;
        var c = ctx ?? TypeContext.For(owner);

        if (c.HasGenericOuter)
        {
            diags.Add(MakeNestedInGenericDiagnostic(owner, evt));
            return;
        }

        if (!IsPartialChain(owner))
        {
            diags.Add(MakePartialDiagnostic(owner, evt));
            return;
        }

        entries.Add(new ResetEntry
        {
            ContainingTypeKey = c.ContainingTypeKey,
            Namespace = c.Namespace,
            PartialChain = c.PartialChain,
            SelfTypeDecl = c.SelfTypeDecl,
            MemberName = evt.Name,
            Kind = MemberKind.Event,
            DelegateTypeFq = evt.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            SourceOrder = SourceOrderOf(evt),
            FileUsings = usingsCache != null ? GetUsingsCached(evt, usingsCache) : GetUsings(evt),
        });
    }

    // -----------------------------------------------------------------
    //  Symbol helpers
    // -----------------------------------------------------------------

    private static DiagnosticInfo MakePartialDiagnostic(INamedTypeSymbol type, ISymbol attributedSymbol)
    {
        // Walk to the first non-partial type in the chain so the message points at the offender.
        INamedTypeSymbol offender = type;
        for (var t = type; t != null; t = t.ContainingType)
            if (!IsPartial(t))
                offender = t;

        // Locate the offender's declaration so the squiggly lands on the type
        // identifier (lets the code fix add `partial` precisely there). Falls
        // back to the attributed symbol when the type has no syntax (metadata).
        var offenderRef = offender.DeclaringSyntaxReferences.FirstOrDefault();
        var loc = offenderRef != null
            ? LocationInfoForTypeIdentifier(offenderRef)
            : LocationInfo.From(attributedSymbol.DeclaringSyntaxReferences.FirstOrDefault());

        return new DiagnosticInfo
        {
            DescriptorId = "ASC001",
            MessageArg = offender.ToDisplayString(),
            Location = loc,
        };
    }

    private static LocationInfo LocationInfoForTypeIdentifier(SyntaxReference typeRef)
    {
        if (typeRef.GetSyntax() is TypeDeclarationSyntax tds)
        {
            var loc = tds.Identifier.GetLocation();
            return new LocationInfo
            {
                FilePath = loc.SourceTree?.FilePath ?? "",
                Span = loc.SourceSpan,
                LineSpan = loc.GetLineSpan().Span,
            };
        }
        return LocationInfo.From(typeRef);
    }

    private static bool IsPartialChain(INamedTypeSymbol type)
    {
        for (var t = type; t != null; t = t.ContainingType)
            if (!IsPartial(t))
                return false;
        return true;
    }

    private static bool HasGenericOuter(INamedTypeSymbol type)
    {
        for (var t = type.ContainingType; t != null; t = t.ContainingType)
            if (t.IsGenericType)
                return true;
        return false;
    }

    private static bool IsFieldLikeEvent(IEventSymbol evt) =>
        evt.AddMethod?.IsImplicitlyDeclared ?? true;

    private static DiagnosticInfo MakeMemberDiagnostic(string id, ISymbol member) =>
        new()
        {
            DescriptorId = id,
            MessageArg = member.Name,
            Location = LocationInfo.From(member.DeclaringSyntaxReferences.FirstOrDefault()),
        };

    private static DiagnosticInfo MakeNestedInGenericDiagnostic(INamedTypeSymbol type, ISymbol attributedSymbol)
    {
        var typeRef = type.DeclaringSyntaxReferences.FirstOrDefault();
        var loc = typeRef != null
            ? LocationInfoForTypeIdentifier(typeRef)
            : LocationInfo.From(attributedSymbol.DeclaringSyntaxReferences.FirstOrDefault());
        return new DiagnosticInfo
        {
            DescriptorId = "ASC005",
            MessageArg = type.ToDisplayString(),
            Location = loc,
        };
    }

    private static bool IsPartial(INamedTypeSymbol type)
    {
        foreach (var sr in type.DeclaringSyntaxReferences)
            if (sr.GetSyntax() is TypeDeclarationSyntax tds)
                foreach (var mod in tds.Modifiers)
                    if (mod.IsKind(SyntaxKind.PartialKeyword))
                        return true;
        return false;
    }

    /// <summary>
    /// True when a `default` write should be guarded with `is not null`.
    /// Reference types and any type parameter — guard. Non-nullable value
    /// types — no guard.
    /// </summary>
    private static bool TypeRequiresGuard(ITypeSymbol type)
    {
        if (type is ITypeParameterSymbol) return true;
        return type.IsReferenceType;
    }

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

    private static ImmutableArray<string> BuildPartialChain(INamedTypeSymbol type)
    {
        // Walk inner-to-outer (cheap), then reverse — avoids List.Insert(0)'s
        // O(n²) shifting on deeply nested types.
        var chain = ImmutableArray.CreateBuilder<string>();
        for (var t = type.ContainingType; t != null; t = t.ContainingType)
            chain.Add(TypeDecl(t));
        chain.Reverse();
        return chain.ToImmutable();
    }

    private static (string Text, ImmutableArray<string> Namespaces) GetFieldInitializer(
        IFieldSymbol field, Compilation compilation)
    {
        if (field.DeclaringSyntaxReferences.Length == 0) return (null, ImmutableArray<string>.Empty);
        if (field.DeclaringSyntaxReferences[0].GetSyntax() is not VariableDeclaratorSyntax vds)
            return (null, ImmutableArray<string>.Empty);
        return CaptureInitializer(vds.Initializer?.Value, compilation);
    }

    private static (string Text, ImmutableArray<string> Namespaces) GetPropertyInitializer(
        IPropertySymbol prop, Compilation compilation)
    {
        if (prop.DeclaringSyntaxReferences.Length == 0) return (null, ImmutableArray<string>.Empty);
        if (prop.DeclaringSyntaxReferences[0].GetSyntax() is not PropertyDeclarationSyntax pds)
            return (null, ImmutableArray<string>.Empty);
        return CaptureInitializer(pds.Initializer?.Value, compilation);
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

    private static bool HasAttribute(ISymbol symbol, string fullName)
    {
        foreach (var attr in symbol.GetAttributes())
            if (attr.AttributeClass?.ToDisplayString() == fullName)
                return true;
        return false;
    }

    // -----------------------------------------------------------------
    //  Source generation
    // -----------------------------------------------------------------

    private static string GenerateSource(ImmutableArray<ResetEntry> entries)
    {
        using var stringWriter = new StringWriter();
        using var w = new IndentedTextWriter(stringWriter);

        // Caller has already grouped by containing type, deduped, and ordered.
        EmitFileHeader(w, entries);

        var ordered = entries.OrderBy(e => e.Kind == MemberKind.Event ? 1 : 0)
                             .ThenBy(e => e.SourceOrder)
                             .ToList();
        EmitTypeBlock(w, ordered[0], ordered);

        EmitFileFooter(w);

        w.Flush();
        return stringWriter.ToString();
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

        // Insertion-ordered dedupe: emit System and Unity.Scripting.LifecycleManagement
        // first (matches Unity's layout), then any source-file using actually
        // needed by an initializer.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<string>();
        void Add(string u) { if (seen.Add(u)) ordered.Add(u); }

        Add("using System;");
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
        foreach (var u in ordered) w.WriteLine(u);
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

        foreach (var outer in sample.PartialChain)
        {
            w.WriteLine($"partial class {outer}");
            w.WriteLine("{");
            w.Indent++;
        }

        w.WriteLine($"partial class {sample.SelfTypeDecl}");
        w.WriteLine("{");
        w.Indent++;

        EmitNestedCleanupClass(w, entries);
        EmitStaticReadonlyField(w);

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

    private static void EmitNestedCleanupClass(IndentedTextWriter w, IReadOnlyList<ResetEntry> entries)
    {
        w.WriteLine(CompilerGeneratedAttr);
        w.WriteLine($"class {NestedClassName} : {CleanupBaseTypeFullName}");
        w.WriteLine("{");
        w.Indent++;

        w.WriteLine("public override void Cleanup()");
        w.WriteLine("{");
        w.Indent++;

        foreach (var e in entries)
        {
            if (e.Kind == MemberKind.Event) EmitEvent(w, e);
            else EmitAssign(w, e);
        }

        w.Indent--;
        w.WriteLine("}");

        w.WriteLine($"public {NestedClassName}() : base() {{}}");

        w.Indent--;
        w.WriteLine("}");
    }

    private static void EmitStaticReadonlyField(IndentedTextWriter w)
    {
        w.WriteLine(CompilerGeneratedAttr);
        w.WriteLine($"static readonly {NestedClassName} {StaticFieldName} = new();");
    }

    private static void EmitAssign(IndentedTextWriter w, ResetEntry e)
    {
        var rhs = e.Initializer ?? "default";
        if (e.RequiresGuard)
            w.WriteLine($"if({e.MemberName} is not null) {e.MemberName} = {rhs};");
        else
            w.WriteLine($"{e.MemberName} = {rhs};");
    }

    private static void EmitEvent(IndentedTextWriter w, ResetEntry e)
    {
        w.WriteLine($"if({e.MemberName} != null)");
        w.WriteLine("{");
        w.Indent++;
        w.WriteLine($"foreach({e.DelegateTypeFq} handler in {e.MemberName}.GetInvocationList())");
        w.WriteLine("{");
        w.Indent++;
        w.WriteLine($"{e.MemberName} -= handler;");
        w.Indent--;
        w.WriteLine("}");
        w.Indent--;
        w.WriteLine("}");
    }
}
