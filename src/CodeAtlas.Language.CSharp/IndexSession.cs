using System.Collections.Immutable;
using CodeAtlas.Contracts;
using CodeAtlas.Framework;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeAtlas.Language.CSharp;

// Mutable state belongs to one indexing operation, never to the singleton language adapter.
internal sealed partial class IndexSession(SourceSnapshot snapshot, CSharpCompilation compilation, SyntaxTree[] trees)
{
    private readonly Dictionary<string, CodeNode> nodes = new(StringComparer.Ordinal);
    private readonly List<CodeRelation> relations = [];
    private readonly List<ObservedEffect> effects = [];
    private readonly List<AnalysisFinding> findings = [.. snapshot.InventoryFindings];
    private readonly Dictionary<string, DeclarationInfo> modules = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SourceDocument> documents = snapshot.Documents.ToDictionary(document => document.Path, StringComparer.Ordinal);

    public SourceGraph Run(CancellationToken cancellationToken)
    {
        ReadFeatureCatalog(cancellationToken);
        foreach (SourceDocument document in snapshot.Documents.Where(document => document.Language != ".cs"))
        {
            findings.Add(new("AT200", FindingSeverity.Unsupported,
                $"暂不支持 {document.Language} 的语义分析；原文与位置已保留。", new(document.Path, 1, 1)));
        }

        foreach (SyntaxTree tree in trees)
        {
            ReadModuleDeclaration(tree, cancellationToken);
        }
        foreach (SyntaxTree tree in trees)
        {
            ReadSymbols(tree, cancellationToken);
        }
        foreach (SyntaxTree tree in trees)
        {
            ReadRelations(tree, cancellationToken);
        }

        foreach (Diagnostic diagnostic in compilation.GetDiagnostics(cancellationToken).Where(item => item.Severity == DiagnosticSeverity.Error))
        {
            if (diagnostic.Location.SourceTree is { FilePath.Length: > 0 } tree)
            {
                FileLinePositionSpan span = diagnostic.Location.GetLineSpan();
                findings.Add(new("AT207", FindingSeverity.Unsupported,
                    "源码视图不能确认此处语义（不代表实际构建失败）：" + diagnostic.Id + " " + diagnostic.GetMessage(),
                    new(tree.FilePath, span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1)));
            }
        }

        return new(snapshot.Documents, nodes.Values.OrderBy(node => node.Id, StringComparer.Ordinal).ToImmutableArray(),
            relations.DistinctBy(edge => (edge.From, edge.To, edge.Kind, edge.Source.Path, edge.Source.Line))
                .OrderBy(edge => edge.Source.Path, StringComparer.Ordinal).ThenBy(edge => edge.Source.Line)
                .ThenBy(edge => edge.From, StringComparer.Ordinal).ThenBy(edge => edge.To, StringComparer.Ordinal).ToImmutableArray(),
            effects.DistinctBy(effect => (effect.NodeId, effect.Api, effect.Source.Path, effect.Source.Line)).ToImmutableArray(),
            findings.DistinctBy(finding => (finding.Rule, finding.Source.Path, finding.Source.Line, finding.Message)).ToImmutableArray(), features.ToImmutableArray());
    }

    private void ReadModuleDeclaration(SyntaxTree tree, CancellationToken cancellationToken)
    {
        SemanticModel model = compilation.GetSemanticModel(tree);
        foreach (AttributeListSyntax list in tree.GetRoot(cancellationToken).DescendantNodes().OfType<AttributeListSyntax>()
            .Where(list => list.Target?.Identifier.ValueText == "assembly"))
        {
            foreach (AttributeSyntax attribute in list.Attributes)
            {
                if (model.GetSymbolInfo(attribute, cancellationToken).Symbol is not IMethodSymbol constructor ||
                    constructor.ContainingType.ToDisplayString() != "CodeAtlas.Framework.AtlasModuleAttribute")
                {
                    continue;
                }
                SeparatedSyntaxList<AttributeArgumentSyntax> arguments = attribute.ArgumentList?.Arguments ?? default;
                if (arguments.Count != 3)
                {
                    continue;
                }
                string? feature = model.GetConstantValue(arguments[0].Expression, cancellationToken).Value as string;
                object? roleValue = model.GetConstantValue(arguments[2].Expression, cancellationToken).Value;
                ArchitecturalRole? role = ParseRole(roleValue, attribute);
                string project = documents[tree.FilePath].Project;
                if (!modules.TryAdd(project, new(feature ?? "[未声明]", role, Location(attribute), "module")))
                {
                    findings.Add(new("AT002", FindingSeverity.Error, "同一项目有多个模块默认声明，无法唯一归属。", Location(attribute)));
                    modules[project] = new("[声明冲突]", null, Location(attribute), "conflict");
                }
            }
        }
    }

    private void ReadSymbols(SyntaxTree tree, CancellationToken cancellationToken)
    {
        SemanticModel model = compilation.GetSemanticModel(tree);
        foreach (SyntaxNode syntax in tree.GetRoot(cancellationToken).DescendantNodes())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsDeclaration(syntax))
            {
                continue;
            }
            ISymbol? symbol = model.GetDeclaredSymbol(syntax, cancellationToken);
            if (symbol is null || symbol is not (INamedTypeSymbol or IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol))
            {
                continue;
            }
            string id = Key(symbol);
            DeclarationInfo declaration = Declaration(symbol, documents[tree.FilePath].Project);
            EffectKind effect = ReadEffect(symbol);
            string kind = symbol switch
            {
                INamedTypeSymbol named => named.TypeKind == TypeKind.Interface ? "interface" : "type",
                IMethodSymbol method => method.MethodKind == MethodKind.Constructor ? "constructor" : "method",
                IPropertySymbol => "property", IEventSymbol => "event", _ => "field"
            };
            var documentation = ReadDocumentation(symbol, syntax, cancellationToken);
            nodes.TryAdd(id, new(id, symbol.Name, symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat), kind,
                symbol.ContainingType is { } parent ? Key(parent) : null, declaration.Feature, declaration.Role,
                effect, Location(syntax), declaration.Source, declaration.Basis, HasBehavior(syntax), documentation.Summary, documentation.Inputs, documentation.Outputs));
        }
    }

    private void ReadRelations(SyntaxTree tree, CancellationToken cancellationToken)
    {
        SemanticModel model = compilation.GetSemanticModel(tree);
        SyntaxNode root = tree.GetRoot(cancellationToken);
        foreach (SyntaxNode syntax in root.DescendantNodes())
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? owner = Owner(syntax, model, cancellationToken);
            if (syntax is InvocationExpressionSyntax or ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax)
            {
                ReadInvocation(syntax, model, owner, cancellationToken);
            }
            if (syntax is SimpleNameSyntax && owner is not null)
            {
                ISymbol? symbol = model.GetSymbolInfo(syntax, cancellationToken).Symbol;
                INamedTypeSymbol? type = symbol as INamedTypeSymbol ?? symbol?.ContainingType;
                if (type is not null && nodes.ContainsKey(Key(type)) && owner != Key(type))
                {
                    relations.Add(new(owner, Key(type), RelationKind.TypeReference, Location(syntax), type.ToDisplayString()));
                }
                if (symbol is IPropertySymbol property && IsExternalEffect(property))
                {
                    effects.Add(new(owner, property.ContainingType.ToDisplayString() + "." + property.Name, Location(syntax)));
                }
            }
            if (syntax is BaseTypeDeclarationSyntax declaration && model.GetDeclaredSymbol(declaration, cancellationToken) is INamedTypeSymbol named)
            {
                ReadProviders(named, declaration);
            }
            if (syntax is GlobalStatementSyntax)
            {
                findings.Add(new("AT210", FindingSeverity.Unsupported, "暂不为顶层语句建立成员归属；原文保留。", Location(syntax)));
            }
        }
        foreach (SyntaxTrivia trivia in root.DescendantTrivia(descendIntoTrivia: true).Where(trivia =>
            trivia.GetStructure() is IfDirectiveTriviaSyntax or ElifDirectiveTriviaSyntax or DefineDirectiveTriviaSyntax))
        {
            findings.Add(new("AT204", FindingSeverity.Unsupported,
                "暂不求值项目条件编译；仅索引当前源码视图中的活动语法，其他分支原文保留。", Location(trivia.GetStructure()!)));
        }
    }

    private void ReadInvocation(SyntaxNode syntax, SemanticModel model, string? owner, CancellationToken cancellationToken)
    {
        IMethodSymbol? method = model.GetSymbolInfo(syntax, cancellationToken).Symbol as IMethodSymbol;
        if (method is null)
        {
            findings.Add(new("AT206", FindingSeverity.Unsupported, "无法唯一解析调用/构造目标：" + Short(syntax), Location(syntax), owner));
            return;
        }
        if (method.MethodKind == MethodKind.DelegateInvoke || IsReflection(method))
        {
            findings.Add(new("AT205", FindingSeverity.Unsupported,
                "暂不追踪委托/反射的运行时目标：" + Short(syntax), Location(syntax), owner));
        }
        if (owner is null)
        {
            return;
        }
        string target = Key(method.ReducedFrom ?? method);
        if (nodes.ContainsKey(target))
        {
            relations.Add(new(owner, target, RelationKind.Invocation, Location(syntax), Short(syntax)));
        }
        if (IsExternalEffect(method))
        {
            effects.Add(new(owner, method.ContainingType.ToDisplayString() + "." + method.Name, Location(syntax)));
        }
        if (method.ContainingType.ToDisplayString() == "CodeAtlas.Composition.AtlasRegistration" && method.Name == "AddAtlas" && method.TypeArguments.Length == 2)
        {
            string port = Key(method.TypeArguments[0]);
            string provider = Key(method.TypeArguments[1]);
            if (nodes.ContainsKey(port) && nodes.ContainsKey(provider))
            {
                relations.Add(new(provider, port, RelationKind.Registration, Location(syntax), Short(syntax)));
            }
        }
    }

    private void ReadProviders(INamedTypeSymbol type, SyntaxNode syntax)
    {
        foreach (AttributeData attribute in type.GetAttributes().Where(attribute =>
            attribute.AttributeClass?.ToDisplayString() == "CodeAtlas.Framework.AtlasProvidesAttribute"))
        {
            if (attribute.ConstructorArguments.FirstOrDefault().Value is not INamedTypeSymbol contract)
            {
                continue;
            }
            SourceLocation location = attribute.ApplicationSyntaxReference is { } reference ? Location(reference.GetSyntax()) : Location(syntax);
            if (contract.TypeKind != TypeKind.Interface || !type.AllInterfaces.Any(candidate => SymbolEqualityComparer.Default.Equals(candidate, contract)))
            {
                findings.Add(new("AT003", FindingSeverity.Error, "提供方声明必须指向自身实现的接口。", location, Key(type)));
            }
            if (nodes.ContainsKey(Key(contract)))
            {
                relations.Add(new(Key(type), Key(contract), RelationKind.ProvidesContract, location, "显式提供方声明；不等同运行时已注册。"));
            }
        }
    }

    private DeclarationInfo Declaration(ISymbol symbol, string project)
    {
        for (ISymbol? current = symbol; current is not null; current = current.ContainingType)
        {
            AttributeData? attribute = current.GetAttributes().FirstOrDefault(attribute =>
                attribute.AttributeClass?.ToDisplayString() == "CodeAtlas.Framework.AtlasComponentAttribute");
            if (attribute?.ApplicationSyntaxReference is { } reference && attribute.ConstructorArguments.Length == 2)
            {
                SyntaxNode syntax = reference.GetSyntax();
                return new(attribute.ConstructorArguments[0].Value as string ?? "[未声明]",
                    ParseRole(attribute.ConstructorArguments[1].Value, syntax), Location(syntax), current == symbol ? "explicit" : "type");
            }
        }
        return modules.GetValueOrDefault(project) ?? new("[未声明]", null, null, "missing");
    }

    private ArchitecturalRole? ParseRole(object? value, SyntaxNode syntax)
    {
        if (value is int number && Enum.IsDefined(typeof(ArchitecturalRole), number))
        {
            return (ArchitecturalRole)number;
        }
        findings.Add(new("AT004", FindingSeverity.Error, "无法识别的职责声明；不会默认归入核心。", Location(syntax)));
        return null;
    }

    private EffectKind ReadEffect(ISymbol symbol)
    {
        AttributeData? attribute = symbol.GetAttributes().FirstOrDefault(attribute =>
            attribute.AttributeClass?.ToDisplayString() == "CodeAtlas.Framework.AtlasOperationAttribute");
        if (attribute is null) return EffectKind.Unspecified;
        if (attribute.ConstructorArguments.FirstOrDefault().Value is int value && Enum.IsDefined(typeof(EffectKind), value))
            return (EffectKind)value;
        if (attribute.ApplicationSyntaxReference is { } reference)
            findings.Add(new("AT005", FindingSeverity.Error, "无法识别的作用声明。", Location(reference.GetSyntax()), Key(symbol)));
        return EffectKind.Unspecified;
    }

    private string? Owner(SyntaxNode syntax, SemanticModel model, CancellationToken cancellationToken)
    {
        foreach (SyntaxNode parent in syntax.AncestorsAndSelf().Where(IsDeclaration))
        {
            ISymbol? symbol = model.GetDeclaredSymbol(parent, cancellationToken);
            if (symbol is not null && nodes.ContainsKey(Key(symbol)))
            {
                return Key(symbol);
            }
        }
        return null;
    }

    private static bool IsDeclaration(SyntaxNode syntax) => syntax is BaseTypeDeclarationSyntax or BaseMethodDeclarationSyntax
        or PropertyDeclarationSyntax or EventDeclarationSyntax || syntax is VariableDeclaratorSyntax { Parent.Parent: FieldDeclarationSyntax or EventFieldDeclarationSyntax };

    private static bool HasBehavior(SyntaxNode syntax) => syntax switch
    {
        BaseMethodDeclarationSyntax method => method.Body is not null || method.ExpressionBody is not null,
        PropertyDeclarationSyntax property => property.ExpressionBody is not null ||
            property.AccessorList?.Accessors.Any(accessor => accessor.Body is not null || accessor.ExpressionBody is not null) == true,
        _ => false
    };

    private static string Key(ISymbol symbol) => symbol.OriginalDefinition.GetDocumentationCommentId()
        ?? symbol.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);

    private static SourceLocation Location(SyntaxNode syntax)
    {
        FileLinePositionSpan span = syntax.GetLocation().GetLineSpan();
        return new(span.Path.Replace('\\', '/'), span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1);
    }

    private static string Short(SyntaxNode syntax)
    {
        string value = syntax.ToString().Replace('\r', ' ').Replace('\n', ' ');
        return value.Length <= 240 ? value : value[..237] + "...";
    }

    private static bool IsReflection(IMethodSymbol method) =>
        method.ContainingNamespace.ToDisplayString().StartsWith("System.Reflection", StringComparison.Ordinal) ||
        method.ContainingType.ToDisplayString() == "System.Type" && method.Name is "GetMethod" or "GetMethods" or "GetProperty" or "InvokeMember";

    private static bool IsExternalEffect(ISymbol symbol)
    {
        string type = symbol.ContainingType?.ToDisplayString() ?? "";
        return type is "System.IO.File" or "System.IO.Directory" or "System.IO.FileStream" or
            "System.IO.FileInfo" or "System.IO.DirectoryInfo" or "System.IO.StreamReader" or "System.IO.StreamWriter" or
            "System.Net.Sockets.TcpClient" or "System.Net.Sockets.Socket" or "System.Net.Http.HttpClient" or "System.Net.Dns" ||
            type == "System.Diagnostics.Process" && symbol.Name == "Start" ||
            type is "System.DateTime" or "System.DateTimeOffset" && symbol.Name is "Now" or "UtcNow" ||
            type == "System.Guid" && symbol.Name == "NewGuid" || type == "System.Random";
    }

    private sealed record DeclarationInfo(string Feature, ArchitecturalRole? Role, SourceLocation? Source, string Basis);
}
