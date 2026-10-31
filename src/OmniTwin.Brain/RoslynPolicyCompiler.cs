using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using OmniTwin.Contracts;

namespace OmniTwin.Brain;

/// <summary>Owned compiler output, never loaded or instantiated by the trusted backend.</summary>
public sealed class CompiledPolicyImage
{
    private readonly byte[] _image;
    internal CompiledPolicyImage(byte[] image, string typeName) { _image = (byte[])image.Clone(); TypeName = typeName; }
    /// <summary>Gets the verified metadata type to load only inside the restricted worker.</summary>
    public string TypeName { get; }
    /// <summary>Returns independent image bytes for the isolated execution handoff.</summary>
    public byte[] CopyImage() => (byte[])_image.Clone();
}

/// <summary>A bounded verification result with no generated runtime object.</summary>
public sealed class PolicyCompilationResult
{
    internal PolicyCompilationResult(CompiledPolicyImage? image, IEnumerable<string> diagnostics)
    { Image = image; Diagnostics = Array.AsReadOnly(diagnostics.Take(20).Select(item => item.Length <= 512 ? item : item.Substring(0, 512)).ToArray()); }
    /// <summary>Gets whether syntax, semantic allowlists, and warning-free emission succeeded.</summary>
    public bool Success => Image is not null;
    /// <summary>Gets an owned image, or null on rejection.</summary>
    public CompiledPolicyImage? Image { get; }
    /// <summary>Gets at most twenty bounded diagnostics.</summary>
    public IReadOnlyList<string> Diagnostics { get; }
}

/// <summary>Compiles bounded C# and verifies actual semantic symbols against a capability allowlist.</summary>
/// <remarks>This filter is defense in depth; generated code still requires OS-enforced execution isolation.</remarks>
public sealed class RoslynPolicyCompiler
{
    private readonly MetadataReference[] _references;
    private static readonly HashSet<string> Types = new(StringComparer.Ordinal)
    {
        "System.Object", "System.Void", "System.Boolean", "System.Byte", "System.SByte", "System.Int16", "System.UInt16",
        "System.Int32", "System.UInt32", "System.Int64", "System.UInt64", "System.Single", "System.Double", "System.Decimal",
        "System.Char", "System.String", "System.Math", "System.MathF", "System.Array", "System.Exception", "System.ArgumentException",
        "System.ArgumentOutOfRangeException", "System.InvalidOperationException", "System.Nullable<T>",
        "System.Collections.Generic.List<T>", "System.Collections.Generic.List<T>.Enumerator", "System.Collections.Generic.Dictionary<TKey, TValue>",
        "System.Collections.Generic.Dictionary<TKey, TValue>.Enumerator", "System.Collections.Generic.KeyValuePair<TKey, TValue>",
        "System.Collections.Generic.HashSet<T>", "System.Collections.Generic.HashSet<T>.Enumerator", "System.Collections.Generic.IReadOnlyList<T>",
        "System.Collections.Generic.IReadOnlyCollection<T>", "System.Collections.Generic.IEnumerable<T>", "System.Collections.Generic.IEnumerator<T>",
        "System.Collections.Generic.ICollection<T>", "System.Collections.Generic.IList<T>", "System.Collections.Generic.IDictionary<TKey, TValue>",
        "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>", "System.Collections.Generic.EqualityComparer<T>",
        "System.Linq.Enumerable", "System.Linq.IOrderedEnumerable<TElement>", "System.Linq.IGrouping<TKey, TElement>"
    };
    private static readonly HashSet<string> ContractTypes = new(StringComparer.Ordinal)
    { nameof(Vector2D), nameof(SegmentedObstacle), nameof(AgentTelemetry), nameof(AgentTargetDirective), nameof(WorldStateSnapshot),
        nameof(AgentRole), nameof(AgentStatus), nameof(ISwarmPolicy), nameof(WorkspaceBounds) };

    /// <summary>Resolves only required runtime metadata and the shared contracts assembly.</summary>
    public RoslynPolicyCompiler()
    {
        var names = new HashSet<string>(new[] { "System.Private.CoreLib.dll", "System.Runtime.dll", "System.Collections.dll", "System.Linq.dll", "System.Runtime.Extensions.dll", "netstandard.dll" }, StringComparer.OrdinalIgnoreCase);
        var trusted = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? throw new InvalidOperationException("Runtime metadata is unavailable.")).Split(Path.PathSeparator);
        _references = trusted.Where(path => names.Contains(Path.GetFileName(path))).Append(typeof(ISwarmPolicy).Assembly.Location)
            .Distinct(StringComparer.OrdinalIgnoreCase).Select(path => (MetadataReference)MetadataReference.CreateFromFile(path)).ToArray();
    }

    /// <summary>Verifies on a background thread with a five-second cooperative compiler deadline and bounded source complexity.</summary>
    public async Task<PolicyCompilationResult> CompileAsync(string source, CancellationToken cancellationToken = default)
    {
        if (source is null || source.Length == 0 || source.Length > 65_536) return Failure("SOURCE_SIZE: provide 1..65536 C# characters.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try { return await Task.Run(() => Compile(source, deadline.Token), deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Failure("COMPILATION_TIMEOUT"); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or InsufficientExecutionStackException)
        { return Failure("COMPILER_REJECTED_SOURCE"); }
    }

    private PolicyCompilationResult Compile(string source, CancellationToken token)
    {
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp14), cancellationToken: token);
        var root = tree.GetRoot(token);
        var nodes = root.DescendantNodesAndSelf().Take(20001).ToArray();
        if (nodes.Length > 20000 || nodes.Any(node => node.Ancestors().Take(129).Count() > 128)) return Failure("SOURCE_COMPLEXITY_LIMIT");
        if (nodes.Any(node => node is TypeOfExpressionSyntax or AttributeSyntax or UnsafeStatementSyntax or PointerTypeSyntax or FunctionPointerTypeSyntax or
            StackAllocArrayCreationExpressionSyntax or ImplicitStackAllocArrayCreationExpressionSyntax or ExternAliasDirectiveSyntax or GlobalStatementSyntax ||
            node is DestructorDeclarationSyntax) ||
            root.DescendantTokens().Any(item => item.IsKind(SyntaxKind.UnsafeKeyword) || item.IsKind(SyntaxKind.ExternKeyword)))
            return Failure("CAPABILITY_SYNTAX: unsafe, native, metadata, attributes, top-level code and destructors are forbidden.");
        var declarations = nodes.OfType<BaseTypeDeclarationSyntax>().ToArray();
        if (declarations.Length != 1 || declarations[0] is not ClassDeclarationSyntax declaration ||
            !declaration.Modifiers.Any(SyntaxKind.PublicKeyword) || !declaration.Modifiers.Any(SyntaxKind.SealedKeyword))
            return Failure("POLICY_SHAPE: declare exactly one public sealed class implementing ISwarmPolicy.");
        var compilation = CSharpCompilation.Create("OmniTwin.Generated." + Guid.NewGuid().ToString("N"), new[] { tree }, _references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release,
                allowUnsafe: false, deterministic: true, nullableContextOptions: NullableContextOptions.Enable));
        var diagnostics = compilation.GetDiagnostics(token).Where(item => item.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning).ToArray();
        if (diagnostics.Length != 0) return new PolicyCompilationResult(null, diagnostics.Select(Format));
        var model = compilation.GetSemanticModel(tree);
        var policy = model.GetDeclaredSymbol(declaration, token)!;
        var contract = compilation.GetTypeByMetadataName(typeof(ISwarmPolicy).FullName!)!;
        if (!policy.AllInterfaces.Any(item => SymbolEqualityComparer.Default.Equals(item, contract)) ||
            !policy.InstanceConstructors.Any(item => item.DeclaredAccessibility == Accessibility.Public && item.Parameters.Length == 0))
            return Failure("MISSING_CONTRACT: public ISwarmPolicy with a parameterless constructor is required.");
        var name = policy.ToDisplayString();
        if (!name.StartsWith("OmniTwin.Generated.", StringComparison.Ordinal) || name.Length > 200 ||
            name.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '_' or '.')))
            return Failure("POLICY_NAMESPACE: use an ASCII type name in OmniTwin.Generated.");
        var violations = new List<string>();
        foreach (var node in nodes)
        {
            token.ThrowIfCancellationRequested();
            if (node is UsingDirectiveSyntax directive)
            {
                var symbol = model.GetSymbolInfo(directive.Name!, token).Symbol;
                if (symbol is INamespaceSymbol ns && ns.ToDisplayString() is not ("System" or "System.Collections.Generic" or "System.Linq" or "OmniTwin.Contracts"))
                    violations.Add("FORBIDDEN_NAMESPACE: " + ns.ToDisplayString());
            }
            if (node is ExpressionSyntax or TypeSyntax)
            {
                var type = model.GetTypeInfo(node, token);
                // Roslyn represents array foreach with this implicit non-generic interface conversion.
                // Permit only that compiler conversion, rather than exposing the System.Collections namespace.
                var arrayIteration = type.Type is IArrayTypeSymbol && node.Parent is CommonForEachStatementSyntax loop && loop.Expression == node &&
                    type.ConvertedType?.ToDisplayString() == "System.Collections.IEnumerable";
                if (!Allowed(type.Type, policy) || (!Allowed(type.ConvertedType, policy) && !arrayIteration))
                    violations.Add("FORBIDDEN_TYPE at line " + Line(node) + ": " + type.Type?.ToDisplayString() + " -> " + type.ConvertedType?.ToDisplayString());
                var symbol = model.GetSymbolInfo(node, token).Symbol;
                if (symbol is IAliasSymbol alias) symbol = alias.Target;
                if (symbol is ITypeSymbol referenced && !Allowed(referenced, policy)) violations.Add("FORBIDDEN_TYPE: " + referenced.ToDisplayString());
                if (symbol is IMethodSymbol method)
                {
                    if (!Allowed(method.ContainingType, policy) || !Allowed(method.ReturnType, policy) ||
                        method.TypeArguments.Any(argument => !Allowed(argument, policy)) ||
                        method.Name is "GetType" or "MemberwiseClone" or "Finalize" || method.IsExtern)
                        violations.Add("FORBIDDEN_METHOD: " + method.ToDisplayString());
                }
                if (symbol is IPropertySymbol property && (!Allowed(property.ContainingType, policy) || !Allowed(property.Type, policy)))
                    violations.Add("FORBIDDEN_PROPERTY: " + property.ToDisplayString());
                if (symbol is IFieldSymbol field && (!Allowed(field.ContainingType, policy) || !Allowed(field.Type, policy)))
                    violations.Add("FORBIDDEN_FIELD: " + field.ToDisplayString());
            }
            if (violations.Count >= 20) break;
        }
        if (violations.Count > 0) return new PolicyCompilationResult(null, violations.Distinct());
        using var output = new MemoryStream();
        var emit = compilation.Emit(output, cancellationToken: token);
        var emittedDiagnostics = emit.Diagnostics.Where(item => item.Severity is DiagnosticSeverity.Warning or DiagnosticSeverity.Error).ToArray();
        if (!emit.Success || emittedDiagnostics.Length != 0) return new PolicyCompilationResult(null, emittedDiagnostics.Select(Format));
        return new PolicyCompilationResult(new CompiledPolicyImage(output.ToArray(), name), Array.Empty<string>());
    }

    private static bool Allowed(ITypeSymbol? type, INamedTypeSymbol policy)
    {
        if (type is null) return true;
        if (type is IArrayTypeSymbol array) return Allowed(array.ElementType, policy);
        if (type.TypeKind == TypeKind.TypeParameter) return true;
        if (type is not INamedTypeSymbol named) return false;
        if (SymbolEqualityComparer.Default.Equals(named, policy)) return true;
        var ns = named.ContainingNamespace.ToDisplayString();
        var permitted = ns == "OmniTwin.Contracts" ? ContractTypes.Contains(named.Name) :
            Types.Contains(named.OriginalDefinition.ToDisplayString(new SymbolDisplayFormat(typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
                genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters))) || (ns == "System" && named.Name is "Func" or "ValueTuple");
        return permitted && named.TypeArguments.All(argument => Allowed(argument, policy));
    }
    private static int Line(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
    private static string Format(Diagnostic diagnostic) => diagnostic.Id + " line " + (diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1) + ": " + diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture);
    private static PolicyCompilationResult Failure(string diagnostic) => new(null, new[] { diagnostic });
}
