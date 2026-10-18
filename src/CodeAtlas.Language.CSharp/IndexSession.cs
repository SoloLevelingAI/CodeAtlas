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
                    findings.Add(new("AT111", FindingSeverity.Unsupported,
                        "模块声明参数不完整或无法静态读取；不能把该项目视为已登记模块。", Location(attribute)));
                    continue;
                }
                string? feature = model.GetConstantValue(arguments[0].Expression, cancellationToken).Value as string;
                object? roleValue = model.GetConstantValue(arguments[2].Expression, cancellationToken).Value;
                ArchitecturalRole? role = ParseRole(roleValue, attribute);
                if (string.IsNullOrWhiteSpace(feature) || !features.Any(item => item.Id == feature))
                {
                    findings.Add(new("AT111", FindingSeverity.Warning,
                        $"模块默认归属 {feature ?? "[缺失]"} 没有对应的 AtlasFeature。模块清单至少要有可解析的稳定 ID；版本、能力和分发闭包不能据此判为已通过。",
                        Location(attribute)));
                }
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
            string displayed = symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
            string suffix = symbol switch
            {
                INamedTypeSymbol namedType => TypeSignatureSuffix(namedType),
                IMethodSymbol => "",
                IPropertySymbol property => property.Type.ToDisplayString(),
                IFieldSymbol field => field.Type.ToDisplayString(),
                IEventSymbol declarationEvent => declarationEvent.Type.ToDisplayString(),
                _ => ""
            };
            if (suffix.Length > 0)
            {
                displayed = displayed + " · " + suffix;
            }
            nodes.TryAdd(id, new(id, symbol.Name, displayed, kind,
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
        if (method.Name is "AddSingleton" or "AddScoped" or "AddTransient" &&
            method.ContainingNamespace.ToDisplayString() == "Microsoft.Extensions.DependencyInjection" &&
            !(nodes.TryGetValue(owner, out CodeNode? site) &&
                (site.Symbol.Contains("CodeAtlas.Composition.AtlasRegistration", StringComparison.Ordinal) ||
                 site.Role == ArchitecturalRole.Test || site.Source.Path.StartsWith("tests/", StringComparison.Ordinal))))
        {
            findings.Add(new("AT118", FindingSeverity.Warning,
                "发现未经过 AddAtlas 的容器注册。不能确认 AtlasProvides，也不能据此判断模块已经可拆装。",
                Location(syntax), owner));
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
            type is "System.Data.Common.DbConnection" or "System.Data.Common.DbCommand" or "System.Data.SqlClient.SqlConnection" or
            "Microsoft.Data.SqlClient.SqlConnection" or "Microsoft.Data.Sqlite.SqliteConnection" or "Microsoft.EntityFrameworkCore.DbContext" ||
            type == "System.Diagnostics.Process" && symbol.Name == "Start" ||
            type is "System.DateTime" or "System.DateTimeOffset" && symbol.Name is "Now" or "UtcNow" ||
            type == "System.Guid" && symbol.Name == "NewGuid" || type == "System.Random";
    }

    /// <summary>把主构造参数和声明的基类型附到类型显示名上，供契约签名规则读取；不新增符号。</summary>
    private static string TypeSignatureSuffix(INamedTypeSymbol named)
    {
        var parts = new List<string>();
        foreach (IMethodSymbol constructor in named.InstanceConstructors)
        {
            if (constructor.Parameters.Length == 0 || !constructor.DeclaringSyntaxReferences.Any(reference => reference.GetSyntax() is BaseTypeDeclarationSyntax))
            {
                continue;
            }
            foreach (IParameterSymbol parameter in constructor.Parameters)
            {
                if (!SymbolEqualityComparer.Default.Equals(parameter.Type, named))
                {
                    parts.Add(parameter.Type.ToDisplayString());
                }
            }
        }
        if (named.BaseType is { } baseType && !IsTrivialBase(baseType) && named.TypeKind != TypeKind.Interface)
        {
            parts.Add(baseType.ToDisplayString());
        }
        foreach (INamedTypeSymbol implemented in named.Interfaces)
        {
            if (named.BaseType is { } parent && parent.AllInterfaces.Any(candidate => SymbolEqualityComparer.Default.Equals(candidate, implemented)))
            {
                continue;
            }
            parts.Add(implemented.ToDisplayString());
        }
        return string.Join(" · ", parts.Distinct(StringComparer.Ordinal));
    }

    private static bool IsTrivialBase(INamedTypeSymbol type) => type.SpecialType is SpecialType.System_Object or SpecialType.System_ValueType
        or SpecialType.System_Enum or SpecialType.System_Delegate or SpecialType.System_MulticastDelegate
        || type.ToDisplayString() is "System.Object" or "System.ValueType" or "System.Enum" or "System.Delegate" or "System.MulticastDelegate" or "System.Attribute";

    private sealed record DeclarationInfo(string Feature, ArchitecturalRole? Role, SourceLocation? Source, string Basis);
}
