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
    private const string AttributeFullName = "Unity.Scripting.LifecycleManagement.AutoStaticsCleanupAttribute";
    private const string NoAttributeFullName = "Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanupAttribute";

    // Names of symbols emitted into the generated file. Centralised so emission
    // and tests share a single source of truth.
    private const string GeneratedClassName = "AutoStaticsCleanup_Generated";
    private const string BindingFlagsConstName = "Flags";
    private const string ValueLocalName = "value";
    private const string FieldLoopVarName = "field";
    private const string PlayModeCallbackName = "OnPlayModeStateChanged";
    private const string OpenGenericResolverName = "ResolveOpenGenericFields";
    private const string DispatchMethodName = "Cleanup";
    private const string PerTypeCleanupSuffix = "_Cleanup";

    // -----------------------------------------------------------------
    //  Data model
    // -----------------------------------------------------------------

    private enum ResetStrategy : byte
    {
        DirectAssign, // Type.Member = init;
        ReflectionAssign, // FieldInfo.SetValue(null, init);
        DirectClear, // Type.Member.Clear();
        ReflectionClear // ((Cast)FieldInfo.GetValue(null))?.Clear();
    }

    /// <summary>
    /// One static member that needs resetting on a play-mode transition.
    /// Immutable; equality is structural so the incremental cache only
    /// invalidates when something semantically changes.
    /// </summary>
    private readonly struct ResetEntry : IEquatable<ResetEntry>
    {
        public string ContainingTypeDisplay { get; init; }
        public string ContainingTypeQualified { get; init; }
        public string MemberName { get; init; }
        public ResetStrategy Strategy { get; init; }
        public string FieldInfoVarName { get; init; }
        public string ReflectionFieldName { get; init; }
        public string InitializerText { get; init; }
        public string TypeQualified { get; init; }
        public bool IsTypeAccessible { get; init; }
        public bool IsValueType { get; init; }
        public string Usings { get; init; }
        public string OpenGenericTypeOf { get; init; }

        public bool Equals(ResetEntry other) =>
            ContainingTypeQualified == other.ContainingTypeQualified &&
            MemberName == other.MemberName &&
            Strategy == other.Strategy &&
            FieldInfoVarName == other.FieldInfoVarName &&
            ReflectionFieldName == other.ReflectionFieldName &&
            InitializerText == other.InitializerText &&
            TypeQualified == other.TypeQualified &&
            IsTypeAccessible == other.IsTypeAccessible &&
            IsValueType == other.IsValueType &&
            Usings == other.Usings &&
            OpenGenericTypeOf == other.OpenGenericTypeOf;

        public override bool Equals(object obj) => obj is ResetEntry other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                var h = 17;
                h = h * 31 + (ContainingTypeQualified?.GetHashCode() ?? 0);
                h = h * 31 + (MemberName?.GetHashCode() ?? 0);
                h = h * 31 + (ReflectionFieldName?.GetHashCode() ?? 0);
                h = h * 31 + (OpenGenericTypeOf?.GetHashCode() ?? 0);
                return h;
            }
        }
    }

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var entries = context.SyntaxProvider.ForAttributeWithMetadataName(
                AttributeFullName,
                predicate: static (_, _) => true,
                transform: static (ctx, ct) => Extract(ctx, ct))
            .SelectMany(static (arr, _) => arr);

        context.RegisterSourceOutput(entries.Collect(), static (spc, members) =>
        {
            if (members.Length == 0) return;
            spc.AddSource("AutoStaticsCleanup.generated.cs", GenerateSource(Deduplicate(members)));
        });
    }

    private static ImmutableArray<ResetEntry> Deduplicate(ImmutableArray<ResetEntry> entries)
    {
        var seen = new HashSet<(string, string)>();
        var unique = ImmutableArray.CreateBuilder<ResetEntry>(entries.Length);
        foreach (var m in entries)
        {
            var key = (m.ContainingTypeQualified, m.ReflectionFieldName ?? m.MemberName);
            if (seen.Add(key)) unique.Add(m);
        }

        return unique.ToImmutable();
    }

    private static ImmutableArray<ResetEntry> Extract(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
    {
        var compilation = ctx.SemanticModel.Compilation;
        switch (ctx.TargetSymbol)
        {
            case INamedTypeSymbol type:
                return CollectTypeMembers(type, compilation, ct);
            case IFieldSymbol field when field.IsStatic && !field.IsConst:
                return Single(b => AddField(b, field, compilation));
            case IPropertySymbol prop when prop.IsStatic && !prop.IsIndexer:
                return Single(b => AddProperty(b, prop, compilation));
            case IEventSymbol evt when evt.IsStatic:
                return Single(b => AddEvent(b, evt, compilation));
            default:
                return ImmutableArray<ResetEntry>.Empty;
        }

        static ImmutableArray<ResetEntry> Single(Action<ImmutableArray<ResetEntry>.Builder> add)
        {
            var b = ImmutableArray.CreateBuilder<ResetEntry>(1);
            add(b);
            return b.ToImmutable();
        }
    }

    private static ImmutableArray<ResetEntry> CollectTypeMembers(
        INamedTypeSymbol typeSymbol, Compilation compilation, CancellationToken ct)
    {
        var results = ImmutableArray.CreateBuilder<ResetEntry>();

        foreach (var member in typeSymbol.GetMembers())
        {
            ct.ThrowIfCancellationRequested();
            if (HasAttribute(member, NoAttributeFullName)) continue;

            switch (member)
            {
                case IFieldSymbol { IsStatic: true, IsConst: false, IsImplicitlyDeclared: false } f:
                    AddField(results, f, compilation);
                    break;
                case IPropertySymbol { IsStatic: true, IsIndexer: false, IsImplicitlyDeclared: false } p:
                    AddProperty(results, p, compilation);
                    break;
                case IEventSymbol { IsStatic: true, IsImplicitlyDeclared: false } e:
                    AddEvent(results, e, compilation);
                    break;
            }
        }

        return results.ToImmutable();
    }

    private static void AddField(
        ImmutableArray<ResetEntry>.Builder results, IFieldSymbol field, Compilation compilation)
    {
        var owner = field.ContainingType;
        var display = owner.ToDisplayString();
        var qualified = owner.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var usings = GetUsings(field);
        var memberAccessible = IsAccessibleExternally(field);
        var isCollection = HasPublicClear(field.Type);
        var openGeneric = GetOpenGenericTypeOf(owner);

        // Open generic owner — defer to the per-instantiation TypeCache
        // resolver. Initializer text and typed locals are unsafe (may
        // reference type parameters), so we always fall back to defaults.
        if (openGeneric != null)
        {
            if (field.IsReadOnly && !isCollection) return;
            results.Add(new ResetEntry
            {
                ContainingTypeDisplay = display,
                ContainingTypeQualified = qualified,
                MemberName = field.Name,
                Strategy = isCollection ? ResetStrategy.ReflectionClear : ResetStrategy.ReflectionAssign,
                FieldInfoVarName = MakeFieldArrayVarName(display, field.Name),
                ReflectionFieldName = field.Name,
                Usings = usings,
                OpenGenericTypeOf = openGeneric,
            });
            return;
        }

        var typeQualified = field.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var typeAccessible = IsTypeAccessible(field.Type);

        if (field.IsReadOnly)
        {
            // Readonly fields are only useful when they're collections we can Clear().
            if (!isCollection) return;
            results.Add(memberAccessible
                ? new ResetEntry
                {
                    ContainingTypeDisplay = display,
                    ContainingTypeQualified = qualified,
                    MemberName = field.Name,
                    Strategy = ResetStrategy.DirectClear,
                    Usings = usings,
                }
                : new ResetEntry
                {
                    ContainingTypeDisplay = display,
                    ContainingTypeQualified = qualified,
                    MemberName = field.Name,
                    Strategy = ResetStrategy.ReflectionClear,
                    FieldInfoVarName = MakeFieldVarName(display, field.Name),
                    ReflectionFieldName = field.Name,
                    TypeQualified = typeQualified,
                    IsTypeAccessible = typeAccessible,
                    Usings = usings,
                });
            return;
        }

        var initText = GetFieldInitializerText(field, compilation);

        results.Add(memberAccessible
            ? new ResetEntry
            {
                ContainingTypeDisplay = display,
                ContainingTypeQualified = qualified,
                MemberName = field.Name,
                Strategy = ResetStrategy.DirectAssign,
                InitializerText = initText,
                TypeQualified = typeQualified,
                IsTypeAccessible = typeAccessible,
                IsValueType = field.Type.IsValueType,
                Usings = usings,
            }
            : new ResetEntry
            {
                ContainingTypeDisplay = display,
                ContainingTypeQualified = qualified,
                MemberName = field.Name,
                Strategy = ResetStrategy.ReflectionAssign,
                FieldInfoVarName = MakeFieldVarName(display, field.Name),
                ReflectionFieldName = field.Name,
                InitializerText = initText,
                TypeQualified = typeQualified,
                IsTypeAccessible = typeAccessible,
                IsValueType = field.Type.IsValueType,
                Usings = usings,
            });
    }

    private static void AddProperty(
        ImmutableArray<ResetEntry>.Builder results, IPropertySymbol prop, Compilation compilation)
    {
        var owner = prop.ContainingType;
        var display = owner.ToDisplayString();
        var qualified = owner.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var usings = GetUsings(prop);
        var isAuto = IsAutoProperty(prop);

        // Expression-bodied properties carry no state.
        if (!isAuto
            && prop.DeclaringSyntaxReferences.Length > 0
            && prop.DeclaringSyntaxReferences[0].GetSyntax() is PropertyDeclarationSyntax { ExpressionBody: not null })
            return;

        var openGeneric = GetOpenGenericTypeOf(owner);

        // Open generic owner — only auto-properties are reachable (a manual
        // setter can't be invoked without a closed instance).
        if (openGeneric != null)
        {
            if (!isAuto) return;
            var openIsCollection = HasPublicClear(prop.Type);
            if (prop.SetMethod == null && !openIsCollection) return;

            results.Add(new ResetEntry
            {
                ContainingTypeDisplay = display,
                ContainingTypeQualified = qualified,
                MemberName = prop.Name,
                Strategy = openIsCollection && prop.SetMethod == null
                    ? ResetStrategy.ReflectionClear
                    : ResetStrategy.ReflectionAssign,
                FieldInfoVarName = MakeFieldArrayVarName(display, prop.Name + "_BackingField"),
                ReflectionFieldName = BackingFieldName(prop.Name),
                Usings = usings,
                OpenGenericTypeOf = openGeneric,
            });
            return;
        }

        var typeQualified = prop.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var typeAccessible = IsTypeAccessible(prop.Type);

        // Get-only auto-property — only useful when the type is a collection.
        if (isAuto && prop.SetMethod == null)
        {
            if (!HasPublicClear(prop.Type)) return;

            results.Add(IsAccessibleExternally(prop)
                ? new ResetEntry
                {
                    ContainingTypeDisplay = display,
                    ContainingTypeQualified = qualified,
                    MemberName = prop.Name,
                    Strategy = ResetStrategy.DirectClear,
                    Usings = usings,
                }
                : new ResetEntry
                {
                    ContainingTypeDisplay = display,
                    ContainingTypeQualified = qualified,
                    MemberName = prop.Name,
                    Strategy = ResetStrategy.ReflectionClear,
                    FieldInfoVarName = MakeFieldVarName(display, prop.Name + "_BackingField"),
                    ReflectionFieldName = BackingFieldName(prop.Name),
                    TypeQualified = typeQualified,
                    IsTypeAccessible = typeAccessible,
                    Usings = usings,
                });
            return;
        }

        if (prop.SetMethod == null) return;

        var initText = GetPropertyInitializerText(prop, compilation);
        var setterAccessible = IsAccessibleExternally(prop) && IsAccessibleExternally(prop.SetMethod);

        if (setterAccessible)
        {
            results.Add(new ResetEntry
            {
                ContainingTypeDisplay = display,
                ContainingTypeQualified = qualified,
                MemberName = prop.Name,
                Strategy = ResetStrategy.DirectAssign,
                InitializerText = initText,
                TypeQualified = typeQualified,
                IsTypeAccessible = typeAccessible,
                IsValueType = prop.Type.IsValueType,
                Usings = usings,
            });
        }
        else if (isAuto)
        {
            // Auto-property with inaccessible setter — go through the backing field.
            results.Add(new ResetEntry
            {
                ContainingTypeDisplay = display,
                ContainingTypeQualified = qualified,
                MemberName = prop.Name,
                Strategy = ResetStrategy.ReflectionAssign,
                FieldInfoVarName = MakeFieldVarName(display, prop.Name + "_BackingField"),
                ReflectionFieldName = BackingFieldName(prop.Name),
                InitializerText = initText,
                TypeQualified = typeQualified,
                IsTypeAccessible = typeAccessible,
                IsValueType = prop.Type.IsValueType,
                Usings = usings,
            });
        }
        // Manual property with inaccessible setter — no safe way to reset.
    }

    private static void AddEvent(
        ImmutableArray<ResetEntry>.Builder results, IEventSymbol evt, Compilation compilation)
    {
        var owner = evt.ContainingType;
        var display = owner.ToDisplayString();
        var qualified = owner.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var usings = GetUsings(evt);
        var openGeneric = GetOpenGenericTypeOf(owner);

        // Events can only be assigned (=) inside their declaring type. Since
        // we emit a separate class, always reflect on the compiler-generated
        // backing field (same name as the event).
        if (openGeneric != null)
        {
            results.Add(new ResetEntry
            {
                ContainingTypeDisplay = display,
                ContainingTypeQualified = qualified,
                MemberName = evt.Name,
                Strategy = ResetStrategy.ReflectionAssign,
                FieldInfoVarName = MakeFieldArrayVarName(display, evt.Name),
                ReflectionFieldName = evt.Name,
                Usings = usings,
                OpenGenericTypeOf = openGeneric,
            });
            return;
        }

        results.Add(new ResetEntry
        {
            ContainingTypeDisplay = display,
            ContainingTypeQualified = qualified,
            MemberName = evt.Name,
            Strategy = ResetStrategy.ReflectionAssign,
            FieldInfoVarName = MakeFieldVarName(display, evt.Name),
            ReflectionFieldName = evt.Name,
            InitializerText = GetEventInitializerText(evt, compilation),
            TypeQualified = evt.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            IsTypeAccessible = IsTypeAccessible(evt.Type),
            Usings = usings,
        });
    }

    private static string BackingFieldName(string propName) => "<" + propName + ">k__BackingField";
    
    private static bool IsAccessibleExternally(ISymbol symbol)
    {
        // Symbol + every containing type must be public or internal.
        for (var s = symbol; s != null && s.Kind != SymbolKind.Namespace; s = s.ContainingType)
        {
            var a = s.DeclaredAccessibility;
            if (a != Accessibility.Public && a != Accessibility.Internal) return false;
        }

        return true;
    }

    private static bool IsTypeAccessible(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol arr) return IsTypeAccessible(arr.ElementType);

        if (type is INamedTypeSymbol named)
        {
            if (!IsAccessibleExternally(named)) return false;
            // Generic args matter too: Dictionary<PrivateKey, int> isn't externally nameable.
            foreach (var arg in named.TypeArguments)
                if (!IsTypeAccessible(arg))
                    return false;
            return true;
        }

        // Type parameters (T) aren't nameable from outside the generic owner.
        return type is not ITypeParameterSymbol;
    }

    /// <summary>
    /// Returns the C# typeof expression for the unbound generic form of
    /// <paramref name="type"/>, e.g. "global::Singleton&lt;&gt;" for
    /// Singleton&lt;T&gt;. Returns null for non-generic types or types nested
    /// inside generic types (the latter isn't currently supported).
    /// </summary>
    private static string GetOpenGenericTypeOf(INamedTypeSymbol type)
    {
        if (type == null || !type.IsGenericType) return null;

        for (var ct = type.ContainingType; ct != null; ct = ct.ContainingType)
            if (ct.IsGenericType)
                return null;

        var qualified = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var openIdx = qualified.IndexOf('<');
        if (openIdx < 0) return null;
        return qualified.Substring(0, openIdx) + "<" + new string(',', type.Arity - 1) + ">";
    }

    private static bool HasPublicClear(ITypeSymbol type)
    {
        // Walk the hierarchy for a public parameterless instance Clear() method.
        for (var current = type; current != null; current = current.BaseType)
        {
            foreach (var member in current.GetMembers("Clear"))
            {
                if (member is IMethodSymbol
                    {
                        Parameters.Length: 0,
                        IsStatic: false,
                        DeclaredAccessibility: Accessibility.Public
                    })
                    return true;
            }
        }

        return false;
    }

    private static bool IsAutoProperty(IPropertySymbol prop)
    {
        if (prop.DeclaringSyntaxReferences.Length == 0) return false;
        if (prop.DeclaringSyntaxReferences[0].GetSyntax() is not PropertyDeclarationSyntax pds) return false;
        if (pds.ExpressionBody != null || pds.AccessorList == null) return false;
        return pds.AccessorList.Accessors.All(a => a.Body == null && a.ExpressionBody == null);
    }

    private static string GetFieldInitializerText(IFieldSymbol field, Compilation compilation) =>
        field.DeclaringSyntaxReferences.Length > 0
        && field.DeclaringSyntaxReferences[0].GetSyntax() is VariableDeclaratorSyntax vds
            ? GetAccessibleExpression(vds.Initializer?.Value, compilation)
            : null;

    private static string GetPropertyInitializerText(IPropertySymbol prop, Compilation compilation) =>
        prop.DeclaringSyntaxReferences.Length > 0
        && prop.DeclaringSyntaxReferences[0].GetSyntax() is PropertyDeclarationSyntax pds
            ? GetAccessibleExpression(pds.Initializer?.Value, compilation)
            : null;

    private static string GetEventInitializerText(IEventSymbol evt, Compilation compilation) =>
        evt.DeclaringSyntaxReferences.Length > 0
        && evt.DeclaringSyntaxReferences[0].GetSyntax() is VariableDeclaratorSyntax vds
            ? GetAccessibleExpression(vds.Initializer?.Value, compilation)
            : null;

    /// <summary>
    /// Returns the expression text iff every symbol it references is
    /// externally accessible. Returns null otherwise — caller falls back to
    /// default/null.
    /// </summary>
    private static string GetAccessibleExpression(ExpressionSyntax expr, Compilation compilation)
    {
        if (expr == null) return null;

        var model = compilation.GetSemanticModel(expr.SyntaxTree);
        foreach (var node in expr.DescendantNodesAndSelf())
        {
            var symbol = model.GetSymbolInfo(node).Symbol;
            if (symbol == null) continue;

            switch (symbol.Kind)
            {
                case SymbolKind.Field:
                case SymbolKind.Property:
                case SymbolKind.Method:
                case SymbolKind.Event:
                case SymbolKind.NamedType:
                    if (!IsAccessibleExternally(symbol)) return null;
                    break;
            }
        }

        return expr.ToFullString().Trim();
    }

    private static string GetUsings(ISymbol symbol)
    {
        if (symbol.DeclaringSyntaxReferences.Length == 0) return "";
        var root = symbol.DeclaringSyntaxReferences[0].SyntaxTree.GetRoot();

        var sb = new StringBuilder();
        foreach (var u in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
        {
            // ToString() — not ToFullString() — drops surrounding trivia
            // (comments, #region, #if/#endif blocks). Re-emitting that trivia
            // into our generated file would risk reordering the using away
            // from the top of the file, which violates CS1529.
            //
            // Internal trivia between the directive's own tokens IS still
            // included by ToString() — most importantly, the line break inside
            // a multi-line `using Alias = SomeType;`. We collapse those to
            // single spaces so EmitUsings' \n-based split can't tear an alias
            // in half.
            if (sb.Length > 0) sb.Append('\n');
            var text = u.ToString().Trim();
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                sb.Append(c == '\r' || c == '\n' ? ' ' : c);
            }
        }

        // Also expose the symbol's own namespace as a using. The generated
        // file lives at global scope, so an initializer like `new PopupManager()`
        // captured verbatim from inside `namespace Foo { class PopupManager … }`
        // has no way to resolve `PopupManager` unless we add `using Foo;`.
        var ns = symbol.ContainingType?.ContainingNamespace;
        if (ns is { IsGlobalNamespace: false })
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append("using ").Append(ns.ToDisplayString()).Append(';');
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

    private static string MakeFieldVarName(string containingTypeDisplay, string memberName) =>
        SanitizeIdentifier(containingTypeDisplay) + "_" + SanitizeIdentifier(memberName) + "_Field";

    private static string MakeFieldArrayVarName(string containingTypeDisplay, string memberName) =>
        SanitizeIdentifier(containingTypeDisplay) + "_" + SanitizeIdentifier(memberName) + "_Fields";

    private static string SanitizeIdentifier(string raw) => raw
        .Replace('.', '_')
        .Replace('+', '_')
        .Replace('<', '_')
        .Replace('>', '_')
        .Replace(',', '_')
        .Replace(" ", "");

    private static string GenerateSource(ImmutableArray<ResetEntry> entries)
    {
        using var stringWriter = new StringWriter();
        using var w = new IndentedTextWriter(stringWriter);

        w.WriteLine("// <auto-generated/>");
        w.WriteLineNoTabs(string.Empty);
        w.WriteLine("#if UNITY_EDITOR && !UNITY_6000_5_OR_NEWER");
        w.WriteLineNoTabs(string.Empty);

        EmitUsings(w, entries);
        w.WriteLineNoTabs(string.Empty);

        w.WriteLine("[global::UnityEditor.InitializeOnLoad]");
        w.WriteLine($"internal static class {GeneratedClassName}");
        w.WriteLine("{");
        w.Indent++;

        EmitBindingFlagsConst(w);
        EmitFieldInfoCache(w, entries);
        EmitStaticConstructor(w);
        EmitPlayModeCallback(w);
        EmitDispatchMethod(w, entries);
        EmitPerTypeCleanups(w, entries);

        if (entries.Any(e => e.OpenGenericTypeOf != null))
            EmitOpenGenericResolver(w);

        w.Indent--;
        w.WriteLine("}");
        w.WriteLineNoTabs(string.Empty);
        w.WriteLine("#endif");

        w.Flush();
        return stringWriter.ToString();
    }

    private static void EmitUsings(IndentedTextWriter w, ImmutableArray<ResetEntry> entries)
    {
        // Only the user's own usings are propagated — they're needed because
        // initializer expressions are emitted verbatim and may use short type
        // names. Everything we emit ourselves is fully qualified, so the
        // generated file compiles even in assemblies whose asmdefs strip
        // standard references (some Unity packages do).
        var usings = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (string.IsNullOrEmpty(entry.Usings)) continue;
            foreach (var u in entry.Usings.Split('\n'))
            {
                var t = u.Trim();
                if (t.Length > 0) usings.Add(t);
            }
        }

        foreach (var u in usings) w.WriteLine(u);
    }

    private static void EmitBindingFlagsConst(IndentedTextWriter w)
    {
        w.WriteLine($"private const global::System.Reflection.BindingFlags {BindingFlagsConstName} =");
        w.Indent++;
        w.WriteLine(
            "global::System.Reflection.BindingFlags.Static | global::System.Reflection.BindingFlags.NonPublic | global::System.Reflection.BindingFlags.Public | global::System.Reflection.BindingFlags.DeclaredOnly;");
        w.Indent--;
        w.WriteLineNoTabs(string.Empty);
    }

    private static void EmitFieldInfoCache(IndentedTextWriter w, ImmutableArray<ResetEntry> entries)
    {
        // Non-generic owners — one cached FieldInfo per entry, grouped by containing type.
        var nonGenericGroups = entries
            .Where(e => e.OpenGenericTypeOf == null
                        && (e.Strategy == ResetStrategy.ReflectionAssign
                            || e.Strategy == ResetStrategy.ReflectionClear))
            .GroupBy(e => e.ContainingTypeDisplay)
            .ToList();

        if (nonGenericGroups.Count > 0)
        {
            var first = true;
            foreach (var group in nonGenericGroups)
            {
                if (!first) w.WriteLineNoTabs(string.Empty);
                w.WriteLine($"// --- {group.Key} ---");
                foreach (var e in group)
                {
                    w.WriteLine(
                        $"private static readonly global::System.Reflection.FieldInfo {e.FieldInfoVarName} =");
                    w.Indent++;
                    w.WriteLine(
                        $"typeof({e.ContainingTypeQualified}).GetField(\"{EscapeString(e.ReflectionFieldName)}\", {BindingFlagsConstName});");
                    w.Indent--;
                }

                first = false;
            }

            w.WriteLineNoTabs(string.Empty);
        }

        // Open-generic owners — one cached FieldInfo[] per entry, populated at
        // static-init time from Unity's TypeCache.
        var genericGroups = entries
            .Where(e => e.OpenGenericTypeOf != null)
            .GroupBy(e => e.ContainingTypeDisplay)
            .ToList();

        if (genericGroups.Count > 0)
        {
            var first = true;
            foreach (var group in genericGroups)
            {
                if (!first) w.WriteLineNoTabs(string.Empty);
                w.WriteLine($"// --- {group.Key} ---");
                foreach (var e in group)
                {
                    w.WriteLine(
                        $"private static readonly global::System.Reflection.FieldInfo[] {e.FieldInfoVarName} =");
                    w.Indent++;
                    w.WriteLine(
                        $"{OpenGenericResolverName}(typeof({e.OpenGenericTypeOf}), \"{EscapeString(e.ReflectionFieldName)}\");");
                    w.Indent--;
                }

                first = false;
            }

            w.WriteLineNoTabs(string.Empty);
        }
    }

    private static void EmitStaticConstructor(IndentedTextWriter w)
    {
        w.WriteLine($"static {GeneratedClassName}()");
        w.WriteLine("{");
        w.Indent++;
        w.WriteLine($"global::UnityEditor.EditorApplication.playModeStateChanged -= {PlayModeCallbackName};");
        w.WriteLine($"global::UnityEditor.EditorApplication.playModeStateChanged += {PlayModeCallbackName};");
        w.Indent--;
        w.WriteLine("}");
        w.WriteLineNoTabs(string.Empty);
    }

    private static void EmitPlayModeCallback(IndentedTextWriter w)
    {
        w.WriteLine($"private static void {PlayModeCallbackName}(global::UnityEditor.PlayModeStateChange change)");
        w.WriteLine("{");
        w.Indent++;
        w.WriteLine("if (change != global::UnityEditor.PlayModeStateChange.ExitingEditMode &&");
        w.WriteLine("    change != global::UnityEditor.PlayModeStateChange.ExitingPlayMode)");
        w.Indent++;
        w.WriteLine("return;");
        w.Indent--;
        w.WriteLine($"{DispatchMethodName}();");
        w.Indent--;
        w.WriteLine("}");
    }

    private static void EmitDispatchMethod(IndentedTextWriter w, ImmutableArray<ResetEntry> entries)
    {
        w.WriteLineNoTabs(string.Empty);
        w.WriteLine($"private static void {DispatchMethodName}()");
        w.WriteLine("{");
        w.Indent++;
        foreach (var typeDisplay in entries.Select(e => e.ContainingTypeDisplay).Distinct())
            w.WriteLine($"{PerTypeMethodName(typeDisplay)}();");
        w.Indent--;
        w.WriteLine("}");
    }

    private static void EmitPerTypeCleanups(IndentedTextWriter w, ImmutableArray<ResetEntry> entries)
    {
        foreach (var group in entries.GroupBy(e => e.ContainingTypeDisplay))
        {
            w.WriteLineNoTabs(string.Empty);
            w.WriteLine($"// --- {group.Key} ---");
            w.WriteLine($"private static void {PerTypeMethodName(group.Key)}()");
            w.WriteLine("{");
            w.Indent++;
            foreach (var e in group) EmitReset(w, e);
            w.Indent--;
            w.WriteLine("}");
        }
    }

    private static string PerTypeMethodName(string containingTypeDisplay) =>
        SanitizeIdentifier(containingTypeDisplay) + PerTypeCleanupSuffix;

    private static void EmitOpenGenericResolver(IndentedTextWriter w)
    {
        w.WriteLineNoTabs(string.Empty);
        w.WriteLine(
            $"private static global::System.Reflection.FieldInfo[] {OpenGenericResolverName}(global::System.Type openDef, string name)");
        w.WriteLine("{");
        w.Indent++;
        w.WriteLine("var seen = new global::System.Collections.Generic.HashSet<global::System.Type>();");
        w.WriteLine("var result = new global::System.Collections.Generic.List<global::System.Reflection.FieldInfo>();");
        w.WriteLine("foreach (var derived in global::UnityEditor.TypeCache.GetTypesDerivedFrom(openDef))");
        w.WriteLine("{");
        w.Indent++;
        w.WriteLine("var t = derived.BaseType;");
        w.WriteLine(
            "while (t != null && !(t.IsGenericType && !t.IsGenericTypeDefinition && t.GetGenericTypeDefinition() == openDef))");
        w.Indent++;
        w.WriteLine("t = t.BaseType;");
        w.Indent--;
        w.WriteLine("if (t == null || !seen.Add(t)) continue;");
        w.WriteLine($"var fi = t.GetField(name, {BindingFlagsConstName});");
        w.WriteLine("if (fi != null) result.Add(fi);");
        w.Indent--;
        w.WriteLine("}");
        w.WriteLine("return result.ToArray();");
        w.Indent--;
        w.WriteLine("}");
    }

    private static void EmitReset(IndentedTextWriter w, ResetEntry e)
    {
        if (e.OpenGenericTypeOf != null)
        {
            EmitOpenGenericReset(w, e);
            return;
        }

        switch (e.Strategy)
        {
            case ResetStrategy.DirectAssign:
            {
                var rhs = e.InitializerText ?? (e.IsValueType ? "default" : "null");
                w.WriteLine($"{e.ContainingTypeQualified}.{e.MemberName} = {rhs};");
                break;
            }

            case ResetStrategy.ReflectionAssign:
            {
                if (e.IsTypeAccessible)
                {
                    // Typed local — preserves target-typed `new()` and collection
                    // initializers that would lose context through SetValue(object, object).
                    var rhs = e.InitializerText ?? (e.IsValueType ? "default" : "null");
                    w.WriteLine(
                        $"{{ {e.TypeQualified} {ValueLocalName} = {rhs}; {e.FieldInfoVarName}?.SetValue(null, {ValueLocalName}); }}");
                }
                else if (e.IsValueType)
                {
                    // Inaccessible value type — synthesize a default via reflection.
                    w.WriteLine(
                        $"if ({e.FieldInfoVarName} != null) {e.FieldInfoVarName}.SetValue(null, global::System.Activator.CreateInstance({e.FieldInfoVarName}.FieldType));");
                }
                else
                {
                    // Inaccessible reference type — null is always valid.
                    w.WriteLine($"{e.FieldInfoVarName}?.SetValue(null, null);");
                }

                break;
            }

            case ResetStrategy.DirectClear:
                w.WriteLine($"{e.ContainingTypeQualified}.{e.MemberName}?.Clear();");
                break;

            case ResetStrategy.ReflectionClear:
                w.WriteLine(e.IsTypeAccessible
                    ? $"(({e.TypeQualified}){e.FieldInfoVarName}?.GetValue(null))?.Clear();"
                    : $"{e.FieldInfoVarName}?.GetValue(null)?.GetType().GetMethod(\"Clear\", global::System.Type.EmptyTypes)?.Invoke({e.FieldInfoVarName}.GetValue(null), null);");
                break;
        }
    }

    private static void EmitOpenGenericReset(IndentedTextWriter w, ResetEntry e)
    {
        switch (e.Strategy)
        {
            case ResetStrategy.ReflectionAssign:
                w.WriteLine($"foreach (var {FieldLoopVarName} in {e.FieldInfoVarName})");
                w.Indent++;
                w.WriteLine(
                    $"{FieldLoopVarName}.SetValue(null, {FieldLoopVarName}.FieldType.IsValueType ? global::System.Activator.CreateInstance({FieldLoopVarName}.FieldType) : null);");
                w.Indent--;
                break;

            case ResetStrategy.ReflectionClear:
                w.WriteLine($"foreach (var {FieldLoopVarName} in {e.FieldInfoVarName})");
                w.WriteLine("{");
                w.Indent++;
                w.WriteLine($"var {ValueLocalName} = {FieldLoopVarName}.GetValue(null);");
                w.WriteLine(
                    $"{ValueLocalName}?.GetType().GetMethod(\"Clear\", global::System.Type.EmptyTypes)?.Invoke({ValueLocalName}, null);");
                w.Indent--;
                w.WriteLine("}");
                break;
        }
    }

    private static string EscapeString(string s) => s?.Replace("\\", "\\\\").Replace("\"", "\\\"");
}