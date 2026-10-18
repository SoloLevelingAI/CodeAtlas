using System.Collections.Immutable;
using CodeAtlas.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace CodeAtlas.Composition;

/// <summary>登记逻辑模块及其启动接线；声明校验与服务接线均发生在宿主启动之前。</summary>
public sealed class ModuleRegistry
{
    private readonly Dictionary<string, ModuleDescriptor> modules = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Action<IServiceCollection>> registrations = new(StringComparer.Ordinal);

    /// <summary>登记模块；父级可以稍后登记，同一标识只能有一份权威声明。</summary>
    public ModuleRegistry Register(ModuleDescriptor module, Action<IServiceCollection>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(module);
        if (string.IsNullOrWhiteSpace(module.Id) || string.IsNullOrWhiteSpace(module.Name) ||
            string.IsNullOrWhiteSpace(module.Purpose) || module.Version is null)
            throw new InvalidOperationException("模块必须有 ID、名称、用途和版本。");
        // Normalize default arrays so all consumers can enumerate a descriptor safely.
        module = module with { Provides = module.Provides.IsDefault ? [] : module.Provides,
            Requires = module.Requires.IsDefault ? [] : module.Requires,
            Dependencies = module.Dependencies.IsDefault ? [] : module.Dependencies,
            Views = module.Views.IsDefault ? [] : module.Views };
        if (!modules.TryAdd(module.Id, module)) throw new InvalidOperationException("重复模块：" + module.Id);
        if (configure is not null) registrations.Add(module.Id, configure);
        return this;
    }

    /// <summary>解析 Profile 的父级、必需模块和能力闭包；不修改服务集合。</summary>
    public ResolvedProfile Resolve(ModuleProfile profile) => ProfileResolver.Resolve(modules.Values, profile);

    /// <summary>先校验再接线；失败时不向调用方的服务集合提交部分注册。</summary>
    public ResolvedProfile Apply(IServiceCollection services, ModuleProfile profile)
    {
        ResolvedProfile resolved = Resolve(profile);
        IServiceCollection staged = new ServiceCollection();
        foreach (ServiceDescriptor service in services) staged.Add(service);
        foreach (ModuleDescriptor module in resolved.Modules)
            if (registrations.TryGetValue(module.Id, out var configure)) configure(staged);
        foreach (CapabilityContract capability in resolved.Modules.SelectMany(module => module.Provides))
            if (staged.Count(service => service.ServiceType == capability.ContractType && !service.IsKeyedService) != 1)
                throw new InvalidOperationException($"能力 {capability.Id} 必须接入且只接入一个服务：{capability.ContractType.Name}。");
        if (staged.Any(service => service.ServiceType == typeof(ResolvedProfile)))
            throw new InvalidOperationException("服务集合已经应用过 Profile。");
        staged.AddSingleton(resolved);
        services.Clear();
        foreach (ServiceDescriptor service in staged) services.Add(service);
        return resolved;
    }
}
