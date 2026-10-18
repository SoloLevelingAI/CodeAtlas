using System.Collections.Immutable;
using CodeAtlas.Contracts;
using CodeAtlas.Framework;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

[assembly: AtlasModule("atlas.analysis.csharp", "C# 语言适配器", ArchitecturalRole.Integration)]

namespace CodeAtlas.Language.CSharp;

/// <summary>把 C# 源码转换为带功能、文档和引用证据的符号图；不执行被分析项目。</summary>
[AtlasProvides(typeof(ICodeIndexer))]
public sealed class CSharpIndexer : ICodeIndexer
{
    private readonly ImmutableArray<MetadataReference> references;
    private readonly MetadataReference declarationReference;

    public CSharpIndexer()
    {
        references = PlatformReferenceCatalog.Load();
        declarationReference = MetadataReference.CreateFromFile(typeof(AtlasModuleAttribute).Assembly.Location);
    }

    /// <summary>创建源码语义视图，解析声明与 XML 说明，提取能确认的静态关系。</summary>
    /// <param name="snapshot">原始文件与读取阶段的问题。</param>
    /// <param name="cancellationToken">取消语义解析的信号。</param>
    /// <returns>符号、功能层级、说明、关系和不能确认的语义问题。</returns>
    public SourceGraph Index(SourceSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        var options = new CSharpParseOptions(LanguageVersion.CSharp14);
        SyntaxTree[] trees = snapshot.Documents.Where(document => document.Language == ".cs")
            .Select(document => CSharpSyntaxTree.ParseText(document.Text, options, document.Path, cancellationToken: cancellationToken)).ToArray();
        bool containsFramework = trees.Any(tree => tree.GetRoot(cancellationToken).DescendantNodes()
            .OfType<ClassDeclarationSyntax>().Any(node => node.Identifier.ValueText == nameof(AtlasModuleAttribute) &&
                node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Any(parent => parent.Name.ToString() == "CodeAtlas.Framework")));
        var globalUsings = CSharpSyntaxTree.ParseText("global using System; global using System.IO; global using System.Linq; " +
            "global using System.Collections.Generic; global using System.Threading; global using System.Threading.Tasks;", options);
        CSharpCompilation compilation = CSharpCompilation.Create("CodeAtlas.SourceView", trees.Append(globalUsings),
            containsFramework ? references : references.Add(declarationReference),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true,
                nullableContextOptions: NullableContextOptions.Enable));

        return new IndexSession(snapshot, compilation, trees).Run(cancellationToken);
    }
}
