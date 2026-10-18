using System.Collections.Immutable;
using CodeAtlas.Contracts;
using CodeAtlas.Framework;

[assembly: AtlasModule("atlas.quality.boundaries", "架构质量规则", ArchitecturalRole.Feature)]

namespace CodeAtlas.Analysis.Core;

/// <summary>检查职责越层、契约签名、已知外部作用和可见注册；同时汇总结构说明问题。</summary>
[AtlasProvides(typeof(IArchitectureRules))]
public sealed class ArchitectureRules : IArchitectureRules
{
    /// <summary>根据静态依赖与作用声明识别违规边界，并保留每个问题的源位置。</summary>
    /// <param name="graph">待检查的符号、功能目录与关系事实。</param>
    /// <returns>职责边界与层级说明的诊断列表。</returns>
    [AtlasOperation(EffectKind.Pure)]
    public ImmutableArray<AnalysisFinding> Evaluate(SourceGraph graph)
    {
        var findings = ImmutableArray.CreateBuilder<AnalysisFinding>();
        findings.AddRange(FeatureStructureRules.Evaluate(graph));
        findings.AddRange(BoundaryEvidenceRules.Evaluate(graph));
        var nodes = graph.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);

        foreach (CodeNode node in graph.Nodes.Where(node => node.Role == ArchitecturalRole.Contract && node.HasBehavior))
        {
            findings.Add(new("AT104", FindingSeverity.Error,
                "契约包含方法或属性行为；请把实现放入功能、交互或适配层。", node.Source, node.Id));
        }

        foreach (CodeNode node in graph.Nodes.Where(node => node.Role is null))
        {
            findings.Add(new("AT001", FindingSeverity.Warning,
                "未声明技术职责；保留符号，不自动归入核心。", node.Source, node.Id));
        }

        foreach (CodeRelation relation in graph.Relations)
        {
            if (!nodes.TryGetValue(relation.From, out CodeNode? from) ||
                !nodes.TryGetValue(relation.To, out CodeNode? to) ||
                from.Role is null || to.Role is null)
            {
                continue;
            }

            if (!Allows(from.Role.Value, to.Role.Value))
            {
                findings.Add(new("AT101", FindingSeverity.Error,
                    $"{from.Role} 直接依赖 {to.Role}：{from.Name} → {to.Name}。请通过契约连接。",
                    relation.Source, from.Id));
            }

            if (from.Effect == EffectKind.Pure && relation.Kind == RelationKind.Invocation &&
                to.Effect is EffectKind.ExternalRead or EffectKind.ExternalWrite or EffectKind.Interaction)
            {
                findings.Add(new("AT103", FindingSeverity.Error,
                    $"声明为纯计算的方法调用了 {to.Effect} 操作 {to.Name}。", relation.Source, from.Id));
            }
        }

        foreach (ObservedEffect effect in graph.Effects)
        {
            if (!nodes.TryGetValue(effect.NodeId, out CodeNode? node))
            {
                continue;
            }

            if (node.Role == ArchitecturalRole.Feature || node.Effect == EffectKind.Pure)
            {
                findings.Add(new("AT102", FindingSeverity.Error,
                    $"功能实现/纯计算直接访问外部作用：{effect.Api}。移到端口适配层。",
                    effect.Source, node.Id));
            }
        }

        return findings.DistinctBy(item => (item.Rule, item.Source.Path, item.Source.Line, item.Message))
            .OrderBy(item => item.Source.Path, StringComparer.Ordinal).ThenBy(item => item.Source.Line)
            .ThenBy(item => item.Rule, StringComparer.Ordinal).ToImmutableArray();
    }

    /// <summary>判断职责之间的直接依赖是否符合当前组织规范，允许契约连接与组合根组装。</summary>
    /// <param name="from">依赖发起方的职责。</param>
    /// <param name="to">被依赖元素的职责。</param>
    /// <returns>该直接依赖允许时为 true，否则需通过契约或调整职责。</returns>
    [AtlasOperation(EffectKind.Pure)]
    public static bool Allows(ArchitecturalRole from, ArchitecturalRole to)
    {
        if (from is ArchitecturalRole.Composition or ArchitecturalRole.Test ||
            to == ArchitecturalRole.Framework || from == to)
        {
            return true;
        }

        return to == ArchitecturalRole.Contract && from != ArchitecturalRole.Framework;
    }
}
