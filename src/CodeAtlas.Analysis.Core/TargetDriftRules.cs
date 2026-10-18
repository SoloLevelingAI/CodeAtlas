using System.Collections.Immutable;
using CodeAtlas.Contracts;
using CodeAtlas.Framework;

namespace CodeAtlas.Analysis.Core;

/// <summary>把目标模型与源码图对照。没有模型时结果是未知，不会变成通过。</summary>
[AtlasComponent("atlas.quality.boundaries", ArchitecturalRole.Feature)]
public static class TargetDriftRules
{
    private static readonly SourceLocation ModelLocation = new("docs/architecture/model.c4", 1, 1);

    /// <summary>比较模块、依赖和能力。模型为空时只发出 AT122。</summary>
    [AtlasOperation(EffectKind.Pure)]
    public static ImmutableArray<AnalysisFinding> Evaluate(SourceGraph graph, TargetArchitecture? target, string? problem = null)
    {
        if (target is null)
        {
            string message = string.IsNullOrWhiteSpace(problem)
                ? "未提供目标架构模型。声明与源码是否一致仍是未知，不能把没有漂移诊断当成通过。"
                : "目标模型无法读取：" + problem + " 不能把读取失败当成通过。";
            return [new("AT122", FindingSeverity.Unsupported, message, ModelLocation)];
        }

        var findings = ImmutableArray.CreateBuilder<AnalysisFinding>();
        var modules = target.Modules.ToDictionary(module => module.Id, StringComparer.Ordinal);
        var features = graph.Features.IsDefault ? [] : graph.Features.ToDictionary(feature => feature.Id, StringComparer.Ordinal);
        var nodes = graph.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);

        foreach (FeatureDefinition feature in graph.Features.IsDefault ? [] : graph.Features.Where(feature =>
            feature.Kind == FeatureKind.Module || feature.Kind == FeatureKind.Shared && feature.ParentId is null))
        {
            if (!modules.ContainsKey(feature.Id))
            {
                findings.Add(new("AT123", FindingSeverity.Warning,
                    $"源码模块 {feature.Id} 不在目标模型中。不能把未建模的模块当成已经对齐。", feature.Source));
            }
        }
        foreach (TargetModule module in target.Modules)
        {
            if (!features.ContainsKey(module.Id))
            {
                findings.Add(new("AT124", FindingSeverity.Warning,
                    $"目标模型中的模块 {module.Id} 在源码功能目录里不存在。不能把模型里的名字当成已实现。", ModelLocation));
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (CodeRelation relation in graph.Relations.Where(relation => relation.Kind is RelationKind.TypeReference or RelationKind.Invocation))
        {
            if (!nodes.TryGetValue(relation.From, out CodeNode? from) || !nodes.TryGetValue(relation.To, out CodeNode? to) ||
                IsExample(from) || IsExample(to))
            {
                continue;
            }
            string? left = Owner(from.Feature, features, modules);
            string? right = Owner(to.Feature, features, modules);
            if (left is null || right is null || Allowed(left, right, modules) || !seen.Add(left + "→" + right))
            {
                continue;
            }
            findings.Add(new("AT125", FindingSeverity.Warning,
                $"模块 {left} 使用了 {right} 的 {to.Name}，但目标模型没有声明这条依赖。跨模块引用不能靠目录相邻来推断。",
                relation.Source, from.Id));
        }

        CodeRelation[] provides = graph.Relations.Where(relation => relation.Kind == RelationKind.ProvidesContract &&
            nodes.TryGetValue(relation.From, out CodeNode? from) && !IsExample(from)).ToArray();
        foreach (TargetModule module in target.Modules)
        {
            foreach (string contract in module.Provides.Select(item => item.Contract).Concat(module.Implementations).Distinct(StringComparer.Ordinal))
            {
                if (provides.Any(relation => nodes.TryGetValue(relation.To, out CodeNode? port) && Matches(port, contract)))
                {
                    continue;
                }
                findings.Add(new("AT126", FindingSeverity.Warning,
                    $"模块 {module.Id} 声明了能力 {contract}，但源码里没有对应的 AtlasProvides。声明本身不是实现。", ModelLocation));
            }
        }
        foreach (CodeRelation relation in provides)
        {
            if (!nodes.TryGetValue(relation.From, out CodeNode? implementation) || !nodes.TryGetValue(relation.To, out CodeNode? contract))
            {
                continue;
            }
            string name = ContractName(contract);
            string? owner = Owner(implementation.Feature, features, modules);
            if (owner is not null && modules.TryGetValue(owner, out TargetModule? module) && Declared(module, name))
            {
                continue;
            }
            findings.Add(new("AT127", FindingSeverity.Warning,
                $"{implementation.Name} 用 AtlasProvides 实现了 {name}，但目标模型没有把该能力记在模块 {owner ?? "[未归属]"} 上。实现不能冒充已声明的能力。",
                relation.Source, implementation.Id));
        }

        return findings.ToImmutable();
    }

    private static bool IsExample(CodeNode node) =>
        node.Role == ArchitecturalRole.Test || node.Source.Path.StartsWith("tests/", StringComparison.Ordinal);

    private static string? Owner(string featureId, IReadOnlyDictionary<string, FeatureDefinition> features, IReadOnlyDictionary<string, TargetModule> modules)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? current = featureId;
        while (current is not null && seen.Add(current))
        {
            if (modules.ContainsKey(current))
            {
                return current;
            }
            current = features.TryGetValue(current, out FeatureDefinition? feature) ? feature.ParentId : null;
        }
        return null;
    }

    private static bool Allowed(string from, string to, IReadOnlyDictionary<string, TargetModule> modules)
    {
        if (from == to || IsAncestor(from, to, modules) || IsAncestor(to, from, modules) || Top(from, modules) == Top(to, modules))
        {
            return true;
        }
        var covered = new HashSet<string>(StringComparer.Ordinal);
        for (string? current = to; current is not null; current = modules[current].Parent)
        {
            covered.Add(current);
        }
        for (string? current = from; current is not null; current = modules[current].Parent)
        {
            if (modules[current].Dependencies.Any(covered.Contains))
            {
                return true;
            }
        }
        return false;
    }

    private static string Top(string id, IReadOnlyDictionary<string, TargetModule> modules)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (modules[id].Parent is { } parent && modules.ContainsKey(parent) && seen.Add(id))
        {
            id = parent;
        }
        return id;
    }

    private static bool IsAncestor(string ancestor, string node, IReadOnlyDictionary<string, TargetModule> modules)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? current = modules[node].Parent;
        while (current is not null && seen.Add(current))
        {
            if (current == ancestor)
            {
                return true;
            }
            current = modules.TryGetValue(current, out TargetModule? parent) ? parent.Parent : null;
        }
        return false;
    }

    private static bool Declared(TargetModule module, string contract) =>
        module.Implementations.Contains(contract, StringComparer.Ordinal) ||
        module.Provides.Any(item => item.Contract == contract);

    private static bool Matches(CodeNode node, string contract) =>
        ContractName(node) == contract || node.Symbol.Contains(contract, StringComparison.Ordinal) || node.Id.Contains(contract, StringComparison.Ordinal);

    private static string ContractName(CodeNode node)
    {
        int mark = node.Symbol.IndexOf('·');
        return (mark < 0 ? node.Symbol : node.Symbol[..mark]).Trim();
    }
}
