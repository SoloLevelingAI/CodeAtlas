using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeAtlas.Framework;

[assembly: AtlasModule("atlas.runtime.control", "本机控制传输", ArchitecturalRole.Integration)]

namespace CodeAtlas.Automation;

/// <summary>一条可审查的桌面指令，不包含脚本或任意代码。</summary>
[AtlasComponent("atlas.runtime.control", ArchitecturalRole.Contract)]
public sealed class ControlRequest
{
    public int Version { get; set; } = 1;
    public string Command { get; set; } = "status";
    public Dictionary<string, string> Arguments { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>桌面指令执行结果；失败始终有明确错误。</summary>
[AtlasComponent("atlas.runtime.control", ArchitecturalRole.Contract)]
public sealed class ControlResponse
{
    public bool Ok { get; set; }
    public object? Result { get; set; }
    public string? Error { get; set; }
}

/// <summary>版本化的本机 JSON 协议；当前用户隔离、长度限制和超时均由传输层执行。</summary>
public static class ControlProtocol
{
    public const int MaximumFrame = 4 * 1024 * 1024;

    /// <summary>为当前用户和命名会话生成稳定、无路径分隔符的管道名。</summary>
    public static string PipeName(string session)
    {
        if (session.Length is < 1 or > 50 || session.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            throw new ArgumentException("会话名只能包含 1–50 个英文字母、数字、横线或下划线。");
        string user = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserDomainName + "\\" + Environment.UserName)))[..16];
        return $"CodeAtlas.v1.{user}.{session}";
    }

    /// <summary>先写入四字节长度，再写入 UTF-8 JSON；禁止超过协议上限。</summary>
    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken token)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message);
        if (payload.Length > MaximumFrame) throw new InvalidDataException("控制消息超过 4 MiB 上限。");
        byte[] length = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, payload.Length);
        await stream.WriteAsync(length, token); await stream.WriteAsync(payload, token); await stream.FlushAsync(token);
    }

    /// <summary>读取有界消息；不信任对端提供的长度。</summary>
    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        byte[] length = new byte[4]; await stream.ReadExactlyAsync(length, token);
        int count = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(length);
        if (count is <= 0 or > MaximumFrame) throw new InvalidDataException("无效控制消息长度。");
        byte[] payload = new byte[count]; await stream.ReadExactlyAsync(payload, token);
        return JsonSerializer.Deserialize<T>(payload) ?? throw new InvalidDataException("空控制消息。");
    }

    /// <summary>向已运行的同用户桌面会话发送指令；没有窗口时明确失败。</summary>
    public static async Task<ControlResponse> SendAsync(string session, ControlRequest request, CancellationToken token)
    {
        await using var pipe = new NamedPipeClientStream(".", PipeName(session), PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(3000, token);
        await WriteAsync(pipe, request, token);
        return await ReadAsync<ControlResponse>(pipe, token);
    }
}
