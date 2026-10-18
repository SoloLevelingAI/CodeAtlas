using System.Collections.Immutable;
using CodeAtlas.Contracts;
using CodeAtlas.Framework;

namespace CodeAtlas.Analysis.Core;

/// <summary>检查功能层级、页面目的与代码说明是否足以支持逐层理解，返回可定位的整理任务。</summary>
[AtlasComponent("atlas.quality.structure", ArchitecturalRole.Feature)]
public static class FeatureStructureRules
{
    /// <summary>从显式声明与文档事实识别缺失、父级错误、循环和过大的未分组方法集合。</summary>
    /// <param name="graph">保留源码位置的符号图与功能目录。</param>
    /// <returns>需要补充说明或调整结构的问题；不猜测缺失的业务含义。</returns>
    [AtlasOperation(EffectKind.Pure)]
    public static ImmutableArray<AnalysisFinding> Evaluate(SourceGraph graph)
    {
        var findings = ImmutableArray.CreateBuilder<AnalysisFinding>();
        ImmutableArray<FeatureDefinition> definitions = graph.Features.IsDefault ? [] : graph.Features;
        var catalog = definitions.GroupBy(feature => feature.Id).ToDictionary(group => group.Key, group => group.First());
        foreach (var duplicate in definitions.GroupBy(feature => feature.Id).Where(group => group.Count() > 1))
            findings.Add(new("AT013", FindingSeverity.Error, $"功能标识 {duplicate.Key} 重复声明；保留一个权威定义。", duplicate.Last().Source));
        foreach (FeatureDefinition feature in definitions)
        {
            if (string.IsNullOrWhiteSpace(feature.Id) || string.IsNullOrWhiteSpace(feature.Name) || string.IsNullOrWhiteSpace(feature.Purpose))
                findings.Add(new("AT011", FindingSeverity.Warning, $"功能 {feature.Id} 缺少标识、名称或用途；补充 AtlasFeature，说明它完成什么任务。", feature.Source));
            var visited = new HashSet<string>(StringComparer.Ordinal) { feature.Id };
            string? parent = feature.ParentId;
            while (parent is not null)
            {
                if (!visited.Add(parent))
                {
                    findings.Add(new("AT013", FindingSeverity.Error, $"功能 {feature.Id} 的父级形成循环；移除循环关联。", feature.Source));
                    break;
                }
                if (!catalog.TryGetValue(parent, out FeatureDefinition? ancestor))
                {
                    findings.Add(new("AT013", FindingSeverity.Error, $"功能 {feature.Id} 的父级 {parent} 不存在；补充父级声明或修正 Parent。", feature.Source));
                    break;
                }
                parent = ancestor.ParentId;
            }
            if (feature.Kind == FeatureKind.Page && (string.IsNullOrWhiteSpace(feature.Inputs) || string.IsNullOrWhiteSpace(feature.Outputs)))
                findings.Add(new("AT015", FindingSeverity.Warning, $"页面 {feature.Name} 未说明输入或完成结果；补充 Inputs / Outputs，明确用户在此页完成什么。", feature.Source));
        }
        foreach (CodeNode node in graph.Nodes)
        {
            if (!catalog.ContainsKey(node.Feature) && node.Kind is "type" or "interface")
                findings.Add(new("AT010", FindingSeverity.Warning, $"{node.Name} 的功能 {node.Feature} 未登记层级；增加 AtlasFeature 或修正 AtlasComponent。不能只声明技术职责。", node.Source, node.Id));
            if (node.Kind is "method" or "type" or "interface" && string.IsNullOrWhiteSpace(node.Summary))
                findings.Add(new("AT012", FindingSeverity.Warning, $"{node.Name} 缺少用途说明；添加 XML <summary>，描述实际任务而不是重复名称。", node.Source, node.Id));
            if (node.Kind == "method" && (node.Inputs.Contains("缺少参数说明", StringComparison.Ordinal) || node.Outputs.Contains("缺少返回值说明", StringComparison.Ordinal)))
                findings.Add(new("AT017", FindingSeverity.Warning, $"{node.Name} 的输入或返回结果未说明；补充 XML <param> / <returns>，解释参数含义与返回约定。", node.Source, node.Id));
        }
        foreach (var group in graph.Nodes.Where(node => node.Kind == "method" && node.ParentId is not null).GroupBy(node => (node.ParentId, node.Feature)).Where(group => group.Count() > 30))
            findings.Add(new("AT014", FindingSeverity.Warning, $"同一类型/子功能有 {group.Count()} 个方法，缺少进一步组织；按真实子用例拆分 AtlasComponent 功能归属或拆分类，分页不等于结构已解决。", group.First().Source, group.First().ParentId));
        return findings.ToImmutable();
    }
}
