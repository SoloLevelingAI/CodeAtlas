using System.Text.Json;
using CodeAtlas.Analysis.Core;
using CodeAtlas.Application;
using CodeAtlas.Contracts;
using CodeAtlas.Host.Components;
using CodeAtlas.Language.CSharp;
using CodeAtlas.Presentation;
using CodeAtlas.Storage;
using CodeAtlas.Composition;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CodeAtlas.Host;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.FirstOrDefault() is "analyze" or "check")
            {
                return await AnalyzeCommand(args);
            }
            if (args.FirstOrDefault() is "help" or "--help")
            {
                Console.WriteLine("CodeAtlas analyze|check --root <repository> --id <report-id> [--name <label>] [--reports <folder>] [--require-complete] [--require-structured]\nCodeAtlas serve --reports <folder> --urls http://127.0.0.1:5278\nExit: 0 analyzed; 2 quality errors; 3 incomplete support; 4 structure/documentation gaps; 64 unsupported command/option.");
                return 0;
            }
            if (args.FirstOrDefault() != "serve")
            {
                throw new NotSupportedException("暂不支持该指令；使用 help 查看 analyze、check 和 serve。");
            }

            WebApplicationBuilder builder = WebApplication.CreateBuilder(args[1..]);
            builder.Host.UseDefaultServiceProvider(options => { options.ValidateOnBuild = true; options.ValidateScopes = true; });
            builder.WebHost.UseUrls(builder.Configuration["urls"] ?? "http://127.0.0.1:5278");
            builder.WebHost.UseStaticWebAssets();
            string reportDirectory = builder.Configuration["reports"] ?? Path.Combine(Environment.CurrentDirectory, "artifacts", "reports");
            ResolvedProfile profile = WebModules.Configure(builder.Services, reportDirectory, builder.Configuration["profile"] ?? "web");
            builder.Services.AddRazorComponents().AddInteractiveServerComponents();
            WebApplication app = builder.Build();
            app.UseStaticFiles();
            app.MapStaticAssets();
            app.UseAntiforgery();
            app.Use(async (context, next) =>
            {
                if (profile.Views.IsEmpty && (context.Request.Path.StartsWithSegments("/standards") || context.Request.Path.StartsWithSegments("/division")))
                { context.Response.StatusCode = 404; return; }
                await next(context);
            });
            app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
            await app.RunAsync();
            return 0;
        }
        catch (NotSupportedException error)
        {
            Console.Error.WriteLine(error.Message);
            return 64;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    private static void Configure(IServiceCollection services, string reportDirectory)
    {
        CodeAtlas.Runtime.AtlasRuntime.CreateRegistry(reportDirectory).Apply(services, CodeAtlas.Runtime.AtlasRuntime.HeadlessProfile);
    }

    private static async Task<int> AnalyzeCommand(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        bool complete = false;
        bool structured = false;
        for (int index = 1; index < args.Length; index++)
        {
            string key = args[index];
            if (key == "--require-structured")
            {
                structured = true;
                continue;
            }
            if (key == "--require-complete")
            {
                complete = true;
                continue;
            }
            if (key is not ("--root" or "--reports" or "--id" or "--name") || index + 1 >= args.Length)
            {
                throw new NotSupportedException("暂不支持或缺少参数的选项：" + key);
            }
            if (!options.TryAdd(key, args[++index]))
            {
                throw new NotSupportedException("重复的选项：" + key);
            }
        }
        string root = options.GetValueOrDefault("--root") ?? throw new ArgumentException("需要 --root <repository>。");
        string id = options.GetValueOrDefault("--id") ?? "analysis";
        string reportDirectory = options.GetValueOrDefault("--reports") ?? Path.Combine(Environment.CurrentDirectory, "artifacts", "reports");
        var services = new ServiceCollection();
        Configure(services, reportDirectory);
        using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        AnalysisReport report = await provider.GetRequiredService<IAnalysisService>().AnalyzeAsync(new(id,
            options.GetValueOrDefault("--name") ?? Path.GetFileName(Path.GetFullPath(root)), root));
        await provider.GetRequiredService<IReportRepository>().SaveAsync(report);
        int errors = report.Findings.Count(finding => finding.Severity == FindingSeverity.Error);
        int unsupported = report.Findings.Count(finding => finding.Severity == FindingSeverity.Unsupported);
        int structureGaps = report.Findings.Count(finding => finding.Rule is "AT001" or "AT010" or "AT011" or "AT012" or "AT014" or "AT015" or "AT016" or "AT017");
        Console.WriteLine(JsonSerializer.Serialize(new { report.Id, report.Coverage, Errors = errors, Unsupported = unsupported,
            StructureGaps = structureGaps, Structure = structureGaps == 0 ? "no-known-structure-gaps" : "needs-attention",
            Quality = errors == 0 ? "no-known-boundary-violations" : "failed", Completeness = unsupported == 0 && report.Coverage.UnclassifiedSymbols == 0 ? "declared-source-view" : "partial",
            Report = Path.GetFullPath(Path.Combine(reportDirectory, id + ".json")) }));
        if (errors > 0)
        {
            return 2;
        }
        if (complete && (unsupported > 0 || report.Coverage.UnclassifiedSymbols > 0)) return 3;
        return structured && structureGaps > 0 ? 4 : 0;
    }
}
