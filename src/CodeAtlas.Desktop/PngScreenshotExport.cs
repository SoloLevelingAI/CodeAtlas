using CodeAtlas.Contracts;
using CodeAtlas.Framework;

namespace CodeAtlas.Desktop;

/// <summary>把已经编码的 PNG 写到磁盘。窗口只负责绘制，不直接创建目录或文件。</summary>
[AtlasComponent("atlas.explore.desktop", ArchitecturalRole.Integration)]
[AtlasProvides(typeof(IScreenshotExport))]
public sealed class PngScreenshotExport : IScreenshotExport
{
    /// <summary>创建目标目录并写入 PNG。路径必须已由调用方限制为 .png。</summary>
    public string WritePng(string path, byte[] png)
    {
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, png);
        return full;
    }
}
