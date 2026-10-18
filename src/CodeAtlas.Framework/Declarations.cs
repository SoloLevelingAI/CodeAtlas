using CodeAtlas.Framework;

[assembly: AtlasModule("atlas.foundation.declarations", "分析声明框架", ArchitecturalRole.Framework)]

namespace CodeAtlas.Framework;

public enum ArchitecturalRole
{
    Contract,
    Feature,
    Presentation,
    DataAccess,
    Integration,
    Application,
    Composition,
    Framework,
    Test
}

public enum EffectKind
{
    Unspecified,
    Pure,
    ExternalRead,
    ExternalWrite,
    Interaction
}

public enum FeatureKind { Module, Capability, Page, Shared }

/// <summary>声明一个功能及其父级、用途和页面输入输出；跨项目用相同标识关联。</summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class AtlasFeatureAttribute(string id, string name, string purpose, string? parent = null) : Attribute
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Purpose { get; } = purpose;
    public string? Parent { get; } = parent;
    public FeatureKind Kind { get; set; } = FeatureKind.Capability;
    public string Inputs { get; set; } = "";
    public string Outputs { get; set; } = "";
}

// Assembly defaults are explicit declarations, never guesses based on folder names.
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class AtlasModuleAttribute(string id, string name, ArchitecturalRole role) : Attribute
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public ArchitecturalRole Role { get; } = role;
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface |
    AttributeTargets.Enum | AttributeTargets.Method | AttributeTargets.Property)]
public sealed class AtlasComponentAttribute(string feature, ArchitecturalRole role) : Attribute
{
    public string Feature { get; } = feature;
    public ArchitecturalRole Role { get; } = role;
}

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Constructor | AttributeTargets.Property)]
public sealed class AtlasOperationAttribute(EffectKind effect) : Attribute
{
    public EffectKind Effect { get; } = effect;
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class AtlasProvidesAttribute(Type contract) : Attribute
{
    public Type Contract { get; } = contract;
}
