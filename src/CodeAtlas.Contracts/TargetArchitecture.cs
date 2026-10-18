using System.Collections.Immutable;
using CodeAtlas.Framework;

namespace CodeAtlas.Contracts;

/// <summary>从 LikeC4 投影出的目标架构。它描述打算成为什么样，不证明源码已经如此。</summary>
[AtlasComponent("atlas.analysis.run", ArchitecturalRole.Contract)]
public sealed record TargetArchitecture(
    int Schema,
    string Format,
    TargetSystem System,
    ImmutableArray<TargetHost> Hosts,
    ImmutableArray<TargetModule> Modules,
    ImmutableArray<TargetView> Views);

/// <summary>系统上下文中的软件系统。</summary>
[AtlasComponent("atlas.analysis.run", ArchitecturalRole.Contract)]
public sealed record TargetSystem(string Id, string Name, string Owner, string Domain);

/// <summary>一个宿主及其启用的模块。</summary>
[AtlasComponent("atlas.analysis.run", ArchitecturalRole.Contract)]
public sealed record TargetHost(
    string Id, string Name, string Technology, string Owner, string Domain, ImmutableArray<string> Modules);

/// <summary>模型中的一项能力。optional 只对需求有意义。</summary>
[AtlasComponent("atlas.analysis.run", ArchitecturalRole.Contract)]
public sealed record TargetCapability(string Id, string Contract, string Version, bool Optional = false);

/// <summary>逻辑模块。运行模块会与 ModuleRegistry 对照；implementations 是源码里应出现的契约。</summary>
[AtlasComponent("atlas.analysis.run", ArchitecturalRole.Contract)]
public sealed record TargetModule(
    string Id, string Name, string Owner, string Domain, string? Parent, bool Runtime,
    ImmutableArray<TargetCapability> Provides, ImmutableArray<TargetCapability> Requires,
    ImmutableArray<string> Dependencies, ImmutableArray<string> Implementations);

/// <summary>一张已经由 LikeC4 导出的视图。</summary>
[AtlasComponent("atlas.analysis.run", ArchitecturalRole.Contract)]
public sealed record TargetView(string Id, string Title, string Level, string Diagram);

/// <summary>读取目标模型的结果。模型为空表示未知，不能当成已通过。</summary>
[AtlasComponent("atlas.analysis.inventory", ArchitecturalRole.Contract)]
public sealed record TargetModelLoad(TargetArchitecture? Model, string? Problem);
