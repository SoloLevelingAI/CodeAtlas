using System.Text.Json;
using CodeAtlas.Contracts;
using CodeAtlas.Framework;

namespace CodeAtlas.Storage;

/// <summary>从仓库或显式路径读取目标模型 JSON。文件访问留在适配器，不进入用例。</summary>
[AtlasComponent("atlas.analysis.inventory", ArchitecturalRole.DataAccess)]
[AtlasProvides(typeof(ITargetModelSource))]
public sealed class TargetModelReader : ITargetModelSource
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>解析投影后的 JSON。格式不对时抛出，由读取方法转成未知。</summary>
    public static TargetArchitecture Parse(string json)
    {
        TargetArchitecture? model = JsonSerializer.Deserialize<TargetArchitecture>(json, Options);
        if (model is null || model.System is null || string.IsNullOrWhiteSpace(model.System.Id) || model.Modules.IsDefault)
        {
            throw new JsonException("目标模型缺少系统或模块。");
        }
        return model;
    }

    /// <summary>约定路径按顺序尝试。显式路径优先，缺失和损坏都不表示通过。</summary>
    public TargetModelLoad Read(string repositoryRoot, string? explicitPath)
    {
        string? path = Resolve(repositoryRoot, explicitPath);
        if (path is null)
        {
            return new(null, null);
        }
        try
        {
            return new(Parse(File.ReadAllText(path)), null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return new(null, error.Message);
        }
    }

    private static string? Resolve(string repositoryRoot, string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            string full = Path.IsPathRooted(explicitPath) ? explicitPath : Path.Combine(repositoryRoot, explicitPath);
            return File.Exists(full) ? Path.GetFullPath(full) : null;
        }
        foreach (string relative in new[] { "docs/architecture/target-model.json", "codeatlas.architecture.json" })
        {
            string candidate = Path.Combine(repositoryRoot, relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }
}
