using System.Text.Json;
using CodeAtlas.Contracts;
using CodeAtlas.Framework;

namespace CodeAtlas.Exploration;

/// <summary>从程序集资源读取规范 Markdown。流读取留在适配器，页面只依赖端口。</summary>
[AtlasComponent("atlas.quality.standards", ArchitecturalRole.Integration)]
[AtlasProvides(typeof(IStandardsReader))]
public sealed class EmbeddedStandardsReader : IStandardsReader
{
    /// <summary>按规范 ID 读取嵌入的 Markdown。未知 ID 直接失败，不返回空文当成存在。</summary>
    public string ReadMarkdown(string id)
    {
        using Stream guide = typeof(EmbeddedStandardsReader).Assembly.GetManifestResourceStream("CodeAtlas.ArchitectureStandard.json")
            ?? throw new InvalidOperationException("应用缺少架构规范资源。");
        using JsonDocument document = JsonDocument.Parse(guide);
        if (!document.RootElement.GetProperty("Rules").EnumerateArray().Any(rule => rule.GetProperty("Id").GetString() == id))
        {
            throw new KeyNotFoundException("未知规范 ID：" + id);
        }
        using Stream stream = typeof(EmbeddedStandardsReader).Assembly.GetManifestResourceStream("CodeAtlas.Standards." + id + ".md")
            ?? throw new InvalidOperationException("缺少规范 Markdown：" + id);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
