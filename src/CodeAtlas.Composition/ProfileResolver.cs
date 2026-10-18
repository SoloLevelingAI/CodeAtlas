using System.Collections.Immutable;
using CodeAtlas.Contracts;

namespace CodeAtlas.Composition;

/// <summary>纯启动校验：显式父级、能力版本、依赖闭包和视图数据必须一致。</summary>
public static class ProfileResolver
{
    /// <summary>解析确定性的拓扑顺序；含糊提供方、循环或不兼容均给出原因并停止。</summary>
    public static ResolvedProfile Resolve(IEnumerable<ModuleDescriptor> descriptors, ModuleProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(profile.Id) || profile.Modules.IsDefaultOrEmpty)
            throw new InvalidOperationException("Profile 必须有 ID 和至少一个模块。");
        // Also accept descriptors supplied without ModuleRegistry.
        var catalog = new Dictionary<string, ModuleDescriptor>(StringComparer.Ordinal);
        foreach (ModuleDescriptor raw in descriptors)
        {
            var module = raw with { Provides = raw.Provides.IsDefault ? [] : raw.Provides,
                Requires = raw.Requires.IsDefault ? [] : raw.Requires, Dependencies = raw.Dependencies.IsDefault ? [] : raw.Dependencies,
                Views = raw.Views.IsDefault ? [] : raw.Views };
            if (string.IsNullOrWhiteSpace(module.Id) || string.IsNullOrWhiteSpace(module.Name) || string.IsNullOrWhiteSpace(module.Purpose) ||
                module.Version is null || !catalog.TryAdd(module.Id, module))
                throw new InvalidOperationException("无效或重复模块：" + module.Id);
        }
        foreach (ModuleDescriptor module in catalog.Values)
        {
            var ancestors = new HashSet<string>(StringComparer.Ordinal) { module.Id };
            string? parent = module.ParentId;
            while (parent is not null)
            {
                if (!catalog.TryGetValue(parent, out var ancestor)) throw new InvalidOperationException($"模块 {module.Id} 缺少父级 {parent}。");
                if (!ancestors.Add(parent)) throw new InvalidOperationException("模块父级循环：" + module.Id);
                parent = ancestor.ParentId;
            }
            foreach (CapabilityContract capability in module.Provides)
                ValidateCapability(capability.Id, capability.ContractType, capability.Version);
            foreach (CapabilityRequirement requirement in module.Requires)
                ValidateCapability(requirement.Id, requirement.ContractType, requirement.MinimumVersion);
            if (module.Provides.Select(capability => capability.Id).Distinct(StringComparer.Ordinal).Count() != module.Provides.Length)
                throw new InvalidOperationException("模块重复声明能力：" + module.Id);
        }
        foreach (var group in catalog.Values.SelectMany(module => module.Provides.Select(capability => (capability.Id, capability.ContractType)))
            .Concat(catalog.Values.SelectMany(module => module.Requires.Select(capability => (capability.Id, capability.ContractType))))
            .GroupBy(item => item.Id, StringComparer.Ordinal))
            if (group.Select(item => item.ContractType).Distinct().Count() != 1)
                throw new InvalidOperationException("能力接口冲突：" + group.Key);

        var selected = new HashSet<string>(StringComparer.Ordinal);
        var edges = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        void Include(string id)
        {
            if (!catalog.TryGetValue(id, out var module)) throw new InvalidOperationException("Profile 缺少模块：" + id);
            if (!selected.Add(id)) return;
            edges[id] = new(StringComparer.Ordinal);
            if (module.ParentId is { } parent) { Include(parent); edges[id].Add(parent); }
            foreach (ModuleDependency dependency in module.Dependencies.Where(dependency => !dependency.Optional))
            { Include(dependency.Id); edges[id].Add(dependency.Id); }
        }
        foreach (string id in profile.Modules) Include(id);
        bool changed;
        do
        {
            int before = selected.Count;
            foreach (string id in selected.Order(StringComparer.Ordinal).ToArray())
                foreach (CapabilityRequirement requirement in catalog[id].Requires.Where(requirement => !requirement.Optional))
                {
                    var active = Providers(requirement.Id).Where(item => selected.Contains(item.Module.Id)).ToArray();
                    if (active.Length > 1) throw new InvalidOperationException("多个能力提供方：" + requirement.Id);
                    var candidates = active.Length > 0 ? active : Providers(requirement.Id).Where(item => Compatible(item.Capability, requirement)).ToArray();
                    if (candidates.Length != 1 || !Compatible(candidates[0].Capability, requirement))
                        throw new InvalidOperationException($"模块 {id} 需要唯一兼容能力 {requirement.Id} >= {requirement.MinimumVersion}（同主版本）。");
                    Include(candidates[0].Module.Id);
                    if (id != candidates[0].Module.Id) edges[id].Add(candidates[0].Module.Id);
                }
            changed = before != selected.Count;
        } while (changed);

        var provided = selected.SelectMany(id => catalog[id].Provides.Select(capability => (Module: catalog[id], Capability: capability))).ToArray();
        foreach (var group in provided.GroupBy(item => item.Capability.Id, StringComparer.Ordinal))
            if (group.Count() > 1) throw new InvalidOperationException("多个能力提供方：" + group.Key);
        foreach (var group in provided.GroupBy(item => item.Capability.ContractType))
            if (group.Count() > 1) throw new InvalidOperationException("多个能力注册同一接口：" + group.Key.Name);
        var unavailable = ImmutableArray.CreateBuilder<string>();
        foreach (string id in selected)
        {
            foreach (ModuleDependency dependency in catalog[id].Dependencies.Where(dependency => dependency.Optional && selected.Contains(dependency.Id)))
                edges[id].Add(dependency.Id);
            foreach (CapabilityRequirement requirement in catalog[id].Requires.Where(requirement => requirement.Optional))
            {
                var provider = provided.FirstOrDefault(item => item.Capability.Id == requirement.Id);
                if (provider.Capability is null) { unavailable.Add(id + ": " + requirement.Id); continue; }
                if (!Compatible(provider.Capability, requirement)) throw new InvalidOperationException("可选能力版本不兼容：" + requirement.Id);
                if (id != provider.Module.Id) edges[id].Add(provider.Module.Id);
            }
        }
        var ordered = ImmutableArray.CreateBuilder<ModuleDescriptor>();
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var done = new HashSet<string>(StringComparer.Ordinal);
        void Visit(string id)
        {
            if (done.Contains(id)) return;
            if (!visiting.Add(id)) throw new InvalidOperationException("模块依赖循环：" + id);
            foreach (string dependency in edges[id].Order(StringComparer.Ordinal)) Visit(dependency);
            visiting.Remove(id); done.Add(id); ordered.Add(catalog[id]);
        }
        foreach (string id in selected.Order(StringComparer.Ordinal)) Visit(id);
        var views = ordered.SelectMany(module => module.Views).ToImmutableArray();
        if (views.Select(view => view.Id).Distinct(StringComparer.Ordinal).Count() != views.Length)
            throw new InvalidOperationException("重复视图贡献 ID。");
        foreach (ModuleDescriptor module in ordered)
            foreach (ViewContribution view in module.Views)
            {
                if (string.IsNullOrWhiteSpace(view.Id) || string.IsNullOrWhiteSpace(view.Label) ||
                    string.IsNullOrWhiteSpace(view.Slot) || string.IsNullOrWhiteSpace(view.Renderer) ||
                    !provided.Any(item => item.Capability.Id == view.DataCapability) ||
                    !module.Requires.Any(requirement => requirement.Id == view.DataCapability) && !module.Provides.Any(capability => capability.Id == view.DataCapability))
                    throw new InvalidOperationException("视图必须声明可用数据能力和宿主渲染标识：" + view.Id);
            }
        return new(profile.Id, ordered.ToImmutable(), views, unavailable.Order(StringComparer.Ordinal).ToImmutableArray());

        IEnumerable<(ModuleDescriptor Module, CapabilityContract Capability)> Providers(string capabilityId) =>
            catalog.Values.SelectMany(module => module.Provides.Where(capability => capability.Id == capabilityId).Select(capability => (module, capability)));
    }

    private static bool Compatible(CapabilityContract capability, CapabilityRequirement requirement) =>
        capability.ContractType == requirement.ContractType && capability.Version.Major == requirement.MinimumVersion.Major && capability.Version >= requirement.MinimumVersion;

    private static void ValidateCapability(string id, Type contract, Version version)
    {
        if (string.IsNullOrWhiteSpace(id) || contract is null || !contract.IsInterface || contract.ContainsGenericParameters || version is null)
            throw new InvalidOperationException("能力必须有 ID、闭合接口类型和版本：" + id);
    }
}
