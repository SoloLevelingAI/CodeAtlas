using System.Collections.Immutable;
using CodeAtlas.Framework;

namespace CodeAtlas.Contracts;

/// <summary>启动时提供的能力；接口类型和主版本共同构成兼容边界。</summary>
[AtlasComponent("atlas.runtime.registration", ArchitecturalRole.Contract)]
public sealed record CapabilityContract(string Id, Type ContractType, Version Version);

/// <summary>所需能力；只接受相同主版本且不低于最低版本的提供方。</summary>
[AtlasComponent("atlas.runtime.registration", ArchitecturalRole.Contract)]
public sealed record CapabilityRequirement(string Id, Type ContractType, Version MinimumVersion, bool Optional = false);

/// <summary>显式模块依赖；可选依赖不会被 Profile 自动加入。</summary>
[AtlasComponent("atlas.runtime.registration", ArchitecturalRole.Contract)]
public sealed record ModuleDependency(string Id, bool Optional = false);

/// <summary>宿主解释的视图入口；不含 UI 控件、程序集加载或可执行脚本。</summary>
[AtlasComponent("atlas.runtime.registration", ArchitecturalRole.Contract)]
public sealed record ViewContribution(string Id, string Label, string Slot, string Renderer, string DataCapability);

/// <summary>一个逻辑模块的不可变声明；父级显式指定，层级与程序集数量无关。</summary>
[AtlasComponent("atlas.runtime.registration", ArchitecturalRole.Contract)]
public sealed record ModuleDescriptor(string Id, string Name, string Purpose, Version Version, string? ParentId = null,
    ImmutableArray<CapabilityContract> Provides = default, ImmutableArray<CapabilityRequirement> Requires = default,
    ImmutableArray<ModuleDependency> Dependencies = default, ImmutableArray<ViewContribution> Views = default);

/// <summary>选择的模块入口；解析器补齐父级和必需依赖，不隐式启用所有子模块。</summary>
[AtlasComponent("atlas.runtime.registration", ArchitecturalRole.Contract)]
public sealed record ModuleProfile(string Id, ImmutableArray<string> Modules);

/// <summary>完成兼容性校验的启动快照；可选缺口保留原因，不能视为完整分发认证。</summary>
[AtlasComponent("atlas.runtime.registration", ArchitecturalRole.Contract)]
public sealed record ResolvedProfile(string Id, ImmutableArray<ModuleDescriptor> Modules,
    ImmutableArray<ViewContribution> Views, ImmutableArray<string> UnavailableOptionalCapabilities);
