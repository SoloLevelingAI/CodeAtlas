using System.Xml;
using System.Xml.Linq;
using CodeAtlas.Contracts;
using CodeAtlas.Framework;
using Microsoft.CodeAnalysis;

namespace CodeAtlas.Language.CSharp;

internal sealed partial class IndexSession
{
    private readonly List<FeatureDefinition> features = [];

    private void ReadFeatureCatalog(CancellationToken cancellationToken)
    {
        foreach (AttributeData attribute in compilation.Assembly.GetAttributes().Where(attribute =>
            attribute.AttributeClass?.ToDisplayString() == "CodeAtlas.Framework.AtlasFeatureAttribute"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (attribute.ApplicationSyntaxReference is not { } reference) continue;
            var args = attribute.ConstructorArguments;
            if (args.Length < 3) continue;
            string Named(string name) => attribute.NamedArguments.FirstOrDefault(pair => pair.Key == name).Value.Value as string ?? "";
            object? kind = attribute.NamedArguments.FirstOrDefault(pair => pair.Key == "Kind").Value.Value;
            if (kind is int value && !Enum.IsDefined(typeof(FeatureKind), value))
                findings.Add(new("AT013", FindingSeverity.Error, "功能类型无效；请使用 Module、Capability、Page 或 Shared。", Location(reference.GetSyntax())));
            features.Add(new(args[0].Value as string ?? "", args[1].Value as string ?? "", args[2].Value as string ?? "",
                args.Length > 3 ? args[3].Value as string : null,
                kind is int number && Enum.IsDefined(typeof(FeatureKind), number) ? (FeatureKind)number : FeatureKind.Capability,
                Named("Inputs"), Named("Outputs"), Location(reference.GetSyntax())));
        }
    }

    private (string Summary, string Inputs, string Outputs) ReadDocumentation(ISymbol symbol, SyntaxNode syntax, CancellationToken cancellationToken)
    {
        XElement? document = null;
        string xml = symbol.GetDocumentationCommentXml(cancellationToken: cancellationToken) ?? "";
        if (!string.IsNullOrWhiteSpace(xml))
        {
            try { document = XElement.Parse(xml); }
            catch (XmlException)
            {
                findings.Add(new("AT016", FindingSeverity.Warning, "XML 文档注释无法解析；修复 summary/param/returns 标签，原文已保留。", Location(syntax), Key(symbol)));
            }
        }
        if (document?.Descendants("inheritdoc").Any() == true)
            findings.Add(new("AT209", FindingSeverity.Unsupported, "暂不展开 inheritdoc 文档继承；请提供本地用途说明或查看契约原文。", Location(syntax), Key(symbol)));
        string Text(XElement? element) => element is null ? "" : string.Join(" ", element.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        string summary = Text(document?.Element("summary"));
        if (symbol is not IMethodSymbol method) return (summary, "", "");
        string inputs = string.Join("\n", method.Parameters.Select(parameter =>
            $"{parameter.Name} : {parameter.Type.ToDisplayString()} — " +
            (Text(document?.Elements("param").FirstOrDefault(item => item.Attribute("name")?.Value == parameter.Name)) is { Length: > 0 } detail ? detail : "缺少参数说明")));
        string output = method.ReturnsVoid || method.MethodKind == MethodKind.Constructor ? "无返回值" :
            method.ReturnType.ToDisplayString() + " — " + (Text(document?.Element("returns")) is { Length: > 0 } detail ? detail : "缺少返回值说明");
        return (summary, inputs.Length == 0 ? "无参数" : inputs, output);
    }
}
