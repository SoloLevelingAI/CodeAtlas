using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAtlas.Automation;
using CodeAtlas.Contracts;
using CodeAtlas.Exploration;
using CodeAtlas.Framework;
using CodeAtlas.Runtime;
using CodeAtlas.Storage;
using Microsoft.Extensions.DependencyInjection;

[assembly: AtlasModule("atlas.runtime.cli", "命令行组合入口", ArchitecturalRole.Composition)]

namespace CodeAtlas.Cli;

/// <summary>独立命令行与 GUI 控制客户端；所有标准输出结果均为 JSON。</summary>
public static class Program
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    /// <summary>执行明确支持的指令；取消、质量问题和无效参数分别返回退出码。</summary>
    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        try
        {
            string command = args.FirstOrDefault() ?? "help";
            if (command is "help" or "--help")
            {
                Print(new { version = 1, commands = new[] {
                    "standards | assess --report <file>",
                    "analyze|check --root <folder> [--reports <folder>] [--id <id>] [--name <name>] [--require-complete] [--require-structured]",
                    "reports [--reports <folder>]", "inspect --report <file>",
                    "search --report <file> --query <text>", "source --report <file> --id <node-id>",
                    "ui <status|open|select|locate|expand|search|view|undo|redo|capture|analyze|cancel|window|scroll> [--session default] [arguments]",
                    "ui open --report <file> | ui analyze --root <folder>",
                    "ui select|locate --id <id> | ui expand --id <id> --expanded true|false",
                    "ui search --query <text> | ui view --mode tree|graph | ui capture --out <png>",
                    "ui window --width 1440 --height 900 [--detail-width 560] | ui scroll --offset 500"
                }, exits = new { success = 0, failure = 1, quality = 2, incomplete = 3, structure = 4, unsupported = 64, cancelled = 130 } });
                return 0;
            }
            if (command == "ui")
            {
                if (args.Length < 2) throw new NotSupportedException("ui 需要子指令。");
                var options = Parse(args[2..], ["session", "report", "root", "id", "query", "expanded", "mode", "out", "width", "height", "detail-width", "offset"]);
                string session = options.GetValueOrDefault("session") ?? "default"; options.Remove("session");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                timeout.CancelAfter(TimeSpan.FromMinutes(2));
                ControlResponse response = await ControlProtocol.SendAsync(session,
                    new ControlRequest { Command = args[1], Arguments = options }, timeout.Token);
                Print(response); return response.Ok ? 0 : 1;
            }
            if (command == "standards")
            {
                _ = Parse(args[1..], []); Print(ArchitectureGuide.Load()); return 0;
            }
            if (command == "assess")
            {
                var options = Parse(args[1..], ["report"]);
                AnalysisReport report = await ReportFile.LoadAsync(Need(options, "report"), cancellation.Token);
                ArchitectureGuide guide = ArchitectureGuide.Load();
                Print(new { guide.Version, report.Name, scope = "现有诊断映射，不等于完整认证；退出码 0 只表示成功生成评估。", assessments = guide.Assess(report) }); return 0;
            }
            if (command is "analyze" or "check")
            {
                var o = Parse(args[1..], ["root", "reports", "id", "name", "require-complete", "require-structured"]);
                string root = Need(o, "root"), directory = o.GetValueOrDefault("reports") ?? DefaultReports;
                using ServiceProvider services = AtlasRuntime.Create(directory);
                AnalysisReport report = await services.GetRequiredService<IAnalysisService>().AnalyzeAsync(
                    new(o.GetValueOrDefault("id") ?? "analysis", o.GetValueOrDefault("name") ?? Path.GetFileName(Path.GetFullPath(root)), root), cancellation.Token);
                await services.GetRequiredService<IReportRepository>().SaveAsync(report, cancellation.Token);
                int errors = report.Findings.Count(f => f.Severity == FindingSeverity.Error);
                int unsupported = report.Findings.Count(f => f.Severity == FindingSeverity.Unsupported);
                int gaps = report.Findings.Count(f => f.Rule is "AT001" or "AT010" or "AT011" or "AT012" or "AT014" or "AT015" or "AT016" or "AT017");
                Print(new { report.Id, report.Coverage, errors, unsupported, structureGaps = gaps, report = Path.GetFullPath(Path.Combine(directory, report.Id + ".json")) });
                if (errors > 0) return 2;
                if (o.ContainsKey("require-complete") && (unsupported > 0 || report.Coverage.UnclassifiedSymbols > 0)) return 3;
                return o.ContainsKey("require-structured") && gaps > 0 ? 4 : 0;
            }
            if (command == "reports")
            {
                var o = Parse(args[1..], ["reports"]);
                using ServiceProvider services = AtlasRuntime.Create(o.GetValueOrDefault("reports") ?? DefaultReports);
                Print(await services.GetRequiredService<IReportRepository>().ListAsync(cancellation.Token)); return 0;
            }
            if (command is "inspect" or "search" or "source")
            {
                var o = Parse(args[1..], command switch { "search" => ["report", "query"], "source" => ["report", "id"], _ => ["report"] });
                AnalysisReport report = await ReportFile.LoadAsync(Need(o, "report"), cancellation.Token);
                var index = new ReportIndex(report);
                if (command == "inspect") Print(new { report.Id, report.Name, report.Coverage, modules = index.Roots.Select(Describe), report.Findings, report.Support });
                if (command == "search") Print(new { limit = 100, results = index.Search(Need(o, "query")).Select(Describe) });
                if (command == "source") { ExplorerItem item = index.Resolve(Need(o, "id")); Print(new { item = Describe(item), source = index.SourceText(item), relations = index.Relations(item) }); }
                return 0;
            }
            throw new NotSupportedException("暂不支持该指令：" + command);
        }
        catch (OperationCanceledException) { Print(new { error = "操作已取消或控制请求超时。" }); return 130; }
        catch (NotSupportedException error) { Print(new { error = error.Message }); return 64; }
        catch (Exception error) { Print(new { error = error.Message }); return 1; }
    }

    /// <summary>列出可供 GUI 和脚本理解的节点字段，不序列化父级循环引用。</summary>
    private static object Describe(ExplorerItem item) => new { item.Id, item.Title, item.Kind, item.Summary, item.Role, item.Source, item.HasChildren };
    /// <summary>拒绝未知、重复和缺少值的参数。</summary>
    private static Dictionary<string, string> Parse(string[] args, string[] allowed)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i++)
        {
            string key = args[i].StartsWith("--", StringComparison.Ordinal) ? args[i][2..] : "";
            if (!allowed.Contains(key, StringComparer.Ordinal)) throw new NotSupportedException("未知参数：" + args[i]);
            string value;
            if (key is "require-complete" or "require-structured") value = "true";
            else if (++i >= args.Length) throw new NotSupportedException("参数缺少值：--" + key);
            else value = args[i];
            if (!result.TryAdd(key, value)) throw new NotSupportedException("重复参数：--" + key);
        }
        return result;
    }
    /// <summary>要求提供指定参数。</summary>
    private static string Need(Dictionary<string, string> args, string key) => args.GetValueOrDefault(key) ?? throw new NotSupportedException("需要 --" + key);
    private static string DefaultReports => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodeAtlas", "reports");
    /// <summary>输出单个 JSON 结果，供脚本稳定读取。</summary>
    private static void Print(object value) => Console.WriteLine(JsonSerializer.Serialize(value, Json));
}
