using System.Collections.Immutable;
using System.Text.Json;
using CodeAtlas.Framework;
using CodeAtlas.Contracts;
using CodeAtlas.Analysis.Core;
using CodeAtlas.Application;
using CodeAtlas.Language.CSharp;
using CodeAtlas.Storage;
using CodeAtlas.Presentation;
using CodeAtlas.Composition;
using Microsoft.Extensions.DependencyInjection;

[assembly: AtlasModule("atlas.quality.regression", "独立正反例回归", ArchitecturalRole.Test)]

namespace CodeAtlas.Regression;

public static class Program
{
    private static readonly CSharpIndexer Indexer = new();
    private static readonly ArchitectureRules Rules = new();
    private static readonly List<string> Passed = [];

    public static async Task<int> Main(string[] args)
    {
        string root = Path.GetFullPath(args.FirstOrDefault() ?? ".");
        string temporary = Path.Combine(root, "artifacts", "regression", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            AnalyzerCases();
            HierarchyCases();
            await StorageCases(temporary);
            await ViewModelCases();
            await HierarchyNavigationCases();
            await FocusMapCases();
            await ExpansionCases();
            RegistrationCases();
            await InventoryCases(temporary);
            await SelfCases(root);
            Console.WriteLine(JsonSerializer.Serialize(new { Status = "passed", Assertions = Passed.Count, Passed }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            // The target is a newly generated child of this run's artifacts folder.
            if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
        }
    }

    private static SourceGraph Graph(string code, string language = ".cs") => Indexer.Index(new("fixture",
        [new("fixture" + language, "fixture.csproj", language, code)], []));
    private static ImmutableArray<AnalysisFinding> Findings(string code)
    {
        SourceGraph graph = Graph("using CodeAtlas.Framework;\n" + code);
        return graph.Findings.AddRange(Rules.Evaluate(graph));
    }
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + name);
        Passed.Add(name);
    }
    private static void Has(string code, string rule, string name) => Check(Findings(code).Any(item => item.Rule == rule), name);

    private static void AnalyzerCases()
    {
        const string clean = """
            [AtlasComponent("calculate", ArchitecturalRole.Contract)] public interface ICalc { int Run(int x); }
            [AtlasComponent("calculate", ArchitecturalRole.Feature), AtlasProvides(typeof(ICalc))]
            public class Calc : ICalc { [AtlasOperation(EffectKind.Pure)] public int Run(int x) => x * 2; }
            [AtlasComponent("calculate", ArchitecturalRole.Presentation)]
            public class ViewModel(ICalc port) { public int Execute() => port.Run(2); }
            """;
        Check(!Findings(clean).Any(item => item.Severity is FindingSeverity.Error or FindingSeverity.Unsupported), "clean contract composition resolves without false errors");
        SourceGraph graph = Graph("using CodeAtlas.Framework;\n" + clean);
        Check(graph.Relations.Any(edge => edge.Kind == RelationKind.Invocation && edge.To.Contains("ICalc.Run", StringComparison.Ordinal)), "interface call targets contract");
        Check(!graph.Relations.Any(edge => edge.Kind == RelationKind.Invocation && edge.From.Contains("ViewModel", StringComparison.Ordinal) && edge.To.Contains("Calc.Run", StringComparison.Ordinal) && !edge.To.Contains("ICalc.Run", StringComparison.Ordinal)), "no invented runtime dispatch to implementation");
        Check(graph.Relations.Any(edge => edge.Kind == RelationKind.ProvidesContract), "provider declaration has separate evidence kind");
        Check(graph.Nodes.Where(node => node.Name is "Calc" or "ViewModel").Select(node => node.Feature).Distinct().Count() == 1 &&
            graph.Nodes.Where(node => node.Name is "Calc" or "ViewModel").Select(node => node.Role).Distinct().Count() == 2, "feature ownership and role are independent axes");
        Has(clean.Replace("ViewModel(ICalc port)", "ViewModel(Calc port)", StringComparison.Ordinal), "AT101", "mutation UI concrete core dependency is rejected");
        Has("""
            [AtlasComponent("f",ArchitecturalRole.Feature)] public class Core { public string Read() => System.IO.File.ReadAllText("x"); }
            """, "AT102", "core hidden file access rejected");
        Has("""
            [AtlasComponent("c",ArchitecturalRole.Contract)] public interface IStore { [AtlasOperation(EffectKind.ExternalRead)] string Read(); }
            [AtlasComponent("f",ArchitecturalRole.Feature)] public class Core(IStore store) { [AtlasOperation(EffectKind.Pure)] public string Read() => store.Read(); }
            """, "AT103", "pure declaration cannot hide explicit port effect");
        Has("[AtlasComponent(\"c\",ArchitecturalRole.Contract)] public interface IPort { int Run() => 1; }", "AT104", "contract default implementation rejected");
        Has("[AtlasComponent(\"c\",ArchitecturalRole.Contract)] public class Data { public int Value => 2; }", "AT104", "contract computed property rejected");
        Has("[AtlasComponent(\"x\",(ArchitecturalRole)999)] public class Wrong {}", "AT004", "invalid role is not assumed core");
        Has("[AtlasComponent(\"x\",ArchitecturalRole.Feature)] public class Wrong { [AtlasOperation((EffectKind)999)] public void M() {} }", "AT005", "invalid effect declaration rejected");
        Has("public class Legacy { public void Run() {} }", "AT001", "undeclared legacy code retained for triage");
        Has("public interface IX {} [AtlasProvides(typeof(IX))] public class Wrong {}", "AT003", "false provider contract rejected");
        Has("public class DynamicCase { public void Run(dynamic x) => x.Unknown(); }", "AT206", "dynamic target explicitly unsupported");
        Has("public class ReflectionCase { public object? Run() => typeof(string).GetMethod(\"X\"); }", "AT205", "reflection target explicitly unsupported");
        Has("public class DelegateCase { public void Run(System.Action x) => x(); }", "AT205", "delegate target explicitly unsupported");
        Has("#if FEATURE_X\npublic class Hidden {}\n#endif\npublic class Visible {}", "AT204", "inactive compile branch explicitly unsupported");
        Has("System.Console.WriteLine(1);", "AT210", "top-level ownership explicitly unsupported");
        SourceGraph other = Graph("<Component />", ".razor");
        Check(other.Documents.Single().Text == "<Component />" && other.Findings.Any(item => item.Rule == "AT200"), "unsupported source retained verbatim");
        SourceGraph overloads = Graph("public partial class C { public void M(int x) {} public void M(string x) {} } public partial class C { public void Other() => M(1); }");
        Check(overloads.Nodes.Count(node => node.Name == "M") == 2 && overloads.Relations.Any(edge => edge.Kind == RelationKind.Invocation && edge.To.EndsWith("(System.Int32)", StringComparison.Ordinal)), "partial type and overload call preserve symbol identity");
        Check(graph.Relations.All(edge => edge.Source.Line > 0 && edge.Source.Path == "fixture.cs"), "all resolved relations carry source evidence");
        Check(!ArchitectureRules.Allows(ArchitecturalRole.Presentation, ArchitecturalRole.DataAccess) &&
            ArchitectureRules.Allows(ArchitecturalRole.Presentation, ArchitecturalRole.Contract), "role matrix prevents UI CRUD coupling");
    }

    private static AnalysisReport Report(string id = "test") => new(1, id, "Test", "fixture", DateTimeOffset.UnixEpoch,
        Graph("using CodeAtlas.Framework; [AtlasComponent(\"sample\",ArchitecturalRole.Feature)] public class Core { public int Run() => 1; }"),
        new(1, 1, 0, 2, 2, 0, 0), [], []);

    private const string HierarchicalSource = """
        using CodeAtlas.Framework;
        [assembly: AtlasFeature("sales", "销售", "完成报价与订单处理", Kind = FeatureKind.Module)]
        [assembly: AtlasFeature("sales.quote", "报价", "计算报价金额", "sales")]
        [assembly: AtlasFeature("stock", "库存", "管理库存数量", Kind = FeatureKind.Module)]
        [assembly: AtlasFeature("stock.count", "盘点", "计算剩余数量", "stock")]
        /// <summary>按商品数量计算报价，不依赖界面与存储。</summary>
        [AtlasComponent("sales.quote", ArchitecturalRole.Feature)]
        public class Quote {
            /// <summary>把单价与数量相乘，返回本次报价总额。</summary>
            /// <param name="price">商品单价。</param>
            /// <param name="count">报价数量。</param>
            /// <returns>本次报价总额。</returns>
            public decimal Total(decimal price, int count) => price * count;
        }
        /// <summary>报价界面使用的计算端口。</summary>
        [AtlasComponent("sales.quote", ArchitecturalRole.Contract)] public interface IQuote { decimal Total(decimal price, int count); }
        /// <summary>维护盘点数量的核心规则。</summary>
        [AtlasComponent("stock.count", ArchitecturalRole.Feature)] public class Stock { public int Count() => 1; }
        """;

    private static void HierarchyCases()
    {
        SourceGraph graph = Graph(HierarchicalSource);
        Check(graph.Features.Length == 4 && graph.Features.Single(feature => feature.Id == "sales.quote").ParentId == "sales", "explicit parent hierarchy is read from source");
        CodeNode total = graph.Nodes.Single(node => node.Name == "Total" && node.ParentId == "T:Quote");
        Check(total.Summary.Contains("总额", StringComparison.Ordinal) && total.Inputs.Contains("商品单价", StringComparison.Ordinal) && total.Outputs.Contains("报价总额", StringComparison.Ordinal), "summary parameter and return explanations read from XML docs");
        Check(!Rules.Evaluate(graph).Any(item => item.Rule == "AT012" && item.NodeId == total.Id), "documented method is not falsely marked missing");
        Has("[AtlasComponent(\"missing\",ArchitecturalRole.Feature)] public class C {}", "AT010", "role alone does not satisfy feature hierarchy contract");
        Has("[assembly:AtlasFeature(\"x\",\"X\",\"purpose\",\"missing\")] public class C {}", "AT013", "missing parent reported with repair guidance");
        Has("[assembly:AtlasFeature(\"x\",\"X\",\"purpose\",\"y\")] [assembly:AtlasFeature(\"y\",\"Y\",\"purpose\",\"x\")] public class C {}", "AT013", "feature cycle rejected");
        Has("[assembly:AtlasFeature(\"x\",\"X\",\"purpose\")] [assembly:AtlasFeature(\"x\",\"X\",\"duplicate\")] public class C {}", "AT013", "duplicate feature identity rejected");
        Has("[assembly:AtlasFeature(\"x\",\"X\",\"\")] public class C {}", "AT011", "empty feature purpose reported");
        Has("[assembly:AtlasFeature(\"page\",\"Page\",\"purpose\",Kind=FeatureKind.Page)] public class C {}", "AT015", "page without input and completion result reported");
        Has("public class C { public int Run(int x) => x; }", "AT012", "method without explanation reported");
        Has("public class C { public int Run(int x) => x; }", "AT017", "method input and result documentation gaps reported");
        Has("public class C {" + string.Join(" ", Enumerable.Range(0, 31).Select(index => $"public void M{index}() {{}}")) + "}", "AT014", "large flat method set reported independently of pagination");
    }

    private static async Task HierarchyNavigationCases()
    {
        AnalysisReport report = Report() with { Graph = Graph(HierarchicalSource) };
        var backend = new FakeWorkspace(report);
        var vm = new GraphExplorerViewModel(backend);
        await vm.LoadAsync();
        Check(vm.ChildFeatures().Length == 2 && vm.VisibleNodes().Length == 0, "overview shows modules rather than a global flat type list");
        vm.SetFeature("sales");
        Check(vm.ChildFeatures().Single().Id == "sales.quote" && vm.VisibleNodes().Length == 0, "module opens only its next level");
        vm.SetFeature("sales.quote");
        Check(vm.VisibleNodes().Any(node => node.Name == "Quote") && vm.VisibleNodes().Any(node => node.Name == "IQuote") && !vm.VisibleNodes().Any(node => node.Name == "Stock"), "feature keeps its own core and contract without mixing unrelated core");
        vm.Select("T:Quote");
        Check(vm.VisibleNodes().Single().Name == "Total", "type opens only its members");
        vm.Select("M:Quote.Total(System.Decimal,System.Int32)");
        Check(vm.VisibleNodes().Single().Name == "Total" && vm.CurrentClass?.Name == "Quote", "selecting method retains class context");
        vm.SetQuery("本次报价总额");
        Check(vm.VisibleNodes().Single().Name == "Total", "search matches method purpose");
        vm.SetFeature("stock");
        vm.Back();
        Check(vm.State.FeatureId == "sales.quote" && vm.State.ClassId == "T:Quote" && vm.State.Query == "本次报价总额", "back restores hierarchy class and purpose query");
        Check(vm.FeaturePath("sales.quote").Select(feature => feature.Id).SequenceEqual(["sales", "sales.quote"]), "breadcrumbs follow declared parents");
        vm.SetQuery("");
        vm.SetFeature(null);
        vm.SetQuery("库存");
        Check(vm.VisibleNodes().All(node => node.Feature == "stock.count"), "global search retains feature ownership");
        int stockCount = vm.MatchingNodeCount;
        vm.SetViewport(new(25, 40, 1.1));
        Check(vm.MatchingNodeCount == stockCount && vm.FeatureCount("stock") == report.Graph.Nodes.Count(node => node.Feature == "stock.count"), "viewport changes preserve query and ancestor counts");
        backend.CurrentReport = report with { Graph = Graph(HierarchicalSource.Replace("库存", "仓储", StringComparison.Ordinal)) };
        await vm.ExecuteAsync("geometry-recalculate");
        Check(vm.MatchingNodeCount == 0 && vm.FeatureLabel("stock.count").Contains("仓储", StringComparison.Ordinal), "replacing report invalidates query and feature labels even when filters remain unchanged");
        vm.SetQuery("仓储");
        Check(vm.MatchingNodeCount == stockCount && vm.RootOf("stock.count") == "stock", "new query uses replaced report and rebuilt hierarchy");
        var memberGraph = Graph("""
            using CodeAtlas.Framework;
            [assembly: AtlasFeature("ui", "UI", "navigation")]
            [assembly: AtlasFeature("ui.back", "Back", "restore position", "ui")]
            [AtlasComponent("ui",ArchitecturalRole.Presentation)] public class Screen {
                [AtlasComponent("ui.back",ArchitecturalRole.Presentation)] public void Back() {}
                public void Load() {}
            }
            """);
        var memberVm = new GraphExplorerViewModel(new FakeWorkspace(report with { Graph = memberGraph }));
        await memberVm.LoadAsync();
        memberVm.SetFeature("ui.back");
        memberVm.Select("T:Screen");
        Check(memberVm.State.FeatureId == "ui.back" && memberVm.VisibleNodes().Single().Name == "Back", "entering parent type preserves the explicitly selected member subfeature");
    }

    private static async Task FocusMapCases()
    {
        string source = HierarchicalSource.Replace("price * count;", "price * count + new Stock().Count();", StringComparison.Ordinal) +
            "[AtlasComponent(\"sales.quote\", ArchitecturalRole.Feature)] public class Empty {}" +
            "[AtlasComponent(\"sales.quote\", ArchitecturalRole.Feature)] public class Many {" +
            string.Join(" ", Enumerable.Range(0, 8).Select(index => $"public int M{index}() => {index};")) + "}";
        AnalysisReport report = Report() with { Graph = Graph(source) };
        var vm = new GraphExplorerViewModel(new FakeWorkspace(report));
        await vm.LoadAsync();
        Check(vm.CurrentMap().Nodes.Select(node => node.FeatureId).SequenceEqual(["sales", "stock"]), "focus overview contains declared root modules");
        Check(vm.CurrentMap().Edges.Any(edge => edge.From == "f:sales" && edge.To == "f:stock"), "root map aggregates real cross-module references");
        vm.SetFeature("sales");
        vm.SetViewport(new(15, 25, 1.2));
        Check(vm.CurrentMap().Nodes.Single().FeatureId == "sales.quote", "module focus shows only the next functional level");
        vm.SetQuery("unmatched-filter");
        vm.SetFeature("sales.quote");
        Check(vm.State.Query == "" && vm.VisibleNodes().Length > 0, "structural navigation clears stale query in the destination");
        Check(vm.CurrentMap().Nodes.Any(node => node.SymbolId == "T:Quote") && vm.CurrentMap().Neighbors.Any(node => node.FeatureId == "stock.count"), "leaf focus separates local types and true external neighbors");
        Check(vm.CurrentMap().Edges.All(edge => edge.EvidenceIndex >= 0 && edge.EvidenceIndex < report.Graph.Relations.Length), "all focus dependency edges retain source evidence indices");
        vm.UpFeature();
        Check(vm.State.FeatureId == "sales" && vm.State.Viewport == new ViewportState(15,25,1.2), "parent navigation restores parent viewport without using back history");
        vm.SetFeature("sales.quote");
        vm.SetQuery("Quote");
        vm.Select("T:Quote");
        Check(vm.State.Query == "" && vm.VisibleNodes().Single().Name == "Total", "type name search does not filter its member list after entry");
        FocusMap typeMap = vm.CurrentMap();
        vm.Select("M:Quote.Total(System.Decimal,System.Int32)");
        Check(ReferenceEquals(vm.CurrentMap(), typeMap) && vm.State.ClassId == "T:Quote", "method selection inspects details without drilling into an empty method level");
        vm.SetViewport(new(3, 4));
        Check(ReferenceEquals(vm.CurrentMap(), typeMap), "viewport updates reuse focus graph without recomputing relations");
        vm.UpLevel();
        Check(vm.Selected?.Id == "T:Quote" && vm.CurrentClass?.Id == "T:Quote", "up from member selects containing type");
        vm.UpLevel();
        Check(vm.State.ClassId is null && vm.State.FeatureId == "sales.quote", "up from type returns to functional context");
        vm.Select("T:Empty");
        Check(vm.VisibleNodes().Length == 0 && vm.CurrentMap().Anchor?.SymbolId == "T:Empty", "empty type retains a source-bearing anchor");
        vm.UpLevel();
        Check(vm.State.ClassId is null && vm.VisibleNodes().Any(node => node.Name == "Empty"), "empty type has a working structural exit");
        vm.Select("T:Many");
        Check(vm.CurrentMap().Total == 8 && vm.CurrentMap().Nodes.Length == 6, "large local maps use bounded pages without losing total count");
        vm.Select("M:Many.M7");
        Check(vm.CurrentMap().Page == 1 && vm.CurrentMap().Nodes.Any(node => node.SymbolId == "M:Many.M7"), "selecting a member on a later map page brings it into view");
        vm.UpFeature();
        vm.UpFeature();
        vm.UpFeature();
        Check(vm.State.FeatureId is null && vm.State.ClassId is null, "structural upward navigation reaches overview from any depth");
    }

    private static async Task ExpansionCases()
    {
        string source = HierarchicalSource + "[AtlasComponent(\"sales.quote\", ArchitecturalRole.Feature)] public class Many {" +
            string.Join(" ", Enumerable.Range(0, 8).Select(index => $"public int M{index}() => {index};")) + "}";
        var backend = new FakeWorkspace(Report() with { Graph = Graph(source) });
        var vm = new GraphExplorerViewModel(backend);
        await vm.LoadAsync();
        var map = new ExpandableMap(vm);
        var roots = map.Branches;
        MapBranch sales = roots.Single(branch => branch.Node.FeatureId == "sales");
        map.Toggle(sales);
        Check(roots.All(root => map.Branches.Contains(root)), "expanding keeps every existing root and its exact coordinates");
        MapBranch quote = map.Branches.Single(branch => branch.Node.FeatureId == "sales.quote");
        map.Toggle(quote);
        var visible = map.Branches;
        vm.SetFeature("stock");
        vm.SetQuery("some query");
        Check(map.Branches == visible, "directory navigation and search do not replace or rebuild expanded canvas");
        map.Toggle(sales);
        Check(map.Branches.SequenceEqual(roots), "collapse hides descendants without removing sibling roots");
        map.Toggle(sales);
        Check(map.Branches.SequenceEqual(visible), "reopening restores nested expansion states and original positions");
        MapBranch many = map.Branches.Single(branch => branch.Node.SymbolId == "T:Many");
        map.Toggle(many);
        var firstMembers = map.Branches.Where(branch => branch.ParentKey == many.Key).ToArray();
        Check(firstMembers.Length == 6 && map.Remaining(many) == 2, "large branches expand in bounded batches with explicit remaining count");
        map.More(many);
        Check(map.Branches.Count(branch => branch.ParentKey == many.Key) == 8 && firstMembers.All(member => map.Branches.Contains(member)), "load more appends without replacing existing members");
        MapBranch method = map.Branches.First(branch => branch.ParentKey == many.Key);
        Check(map.ChildCount(method) == 0, "method is a terminal detail node with no fake expansion");
        Check(map.Edges.All(edge => map.Branches.Any(branch => branch.Key == edge.From) && map.Branches.Any(branch => branch.Key == edge.To)), "expanded dependencies never target invisible nodes");
        map.CollapseAll();
        Check(map.Branches.SequenceEqual(roots), "explicit collapse all returns to retained root positions");
        backend.CurrentReport = Report("changed");
        await vm.LoadAsync();
        Check(map.Branches.All(branch => branch.ParentKey is null) && !map.IsExpanded(sales.Key), "report replacement clears obsolete expansion state");
    }

    private static async Task StorageCases(string directory)
    {
        var storage = new JsonReportRepository(Path.Combine(directory, "reports"));
        AnalysisReport report = Report();
        await storage.SaveAsync(report);
        Check((await storage.LoadAsync("test"))?.Graph.Documents[0].Text == report.Graph.Documents[0].Text, "report CRUD create and read retain source");
        await storage.SaveAsync(report with { Name = "Updated" });
        Check((await storage.ListAsync()).Single().Name == "Updated", "report CRUD update and list");
        await ThrowsAsync<ArgumentException>(() => storage.LoadAsync("../escape"), "report path traversal rejected");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => storage.SaveAsync(report, canceled.Token), "canceled save fails without replacing prior report");
        Check((await storage.LoadAsync("test"))?.Name == "Updated" && !Directory.GetFiles(Path.Combine(directory, "reports"), "*.tmp").Any(), "atomic save preserves old report and cleans temporary files");
        string corruptPath = Path.Combine(directory, "reports", "corrupt.json");
        await File.WriteAllTextAsync(corruptPath, "{broken");
        await ThrowsAsync<JsonException>(() => storage.LoadAsync("corrupt"), "corrupt JSON explicitly fails");
        Check(await File.ReadAllTextAsync(corruptPath) == "{broken", "corrupt report remains untouched");
        await storage.DeleteAsync("test");
        Check(await storage.LoadAsync("test") is null, "report CRUD delete");
    }

    private static async Task ViewModelCases()
    {
        var backend = new FakeWorkspace();
        var vm = new GraphExplorerViewModel(backend);
        await vm.LoadAsync();
        string id = vm.Workspace!.Report.Graph.Nodes.First(node => node.Kind == "type").Id;
        vm.SetQuery("Core");
        vm.SetViewport(new(31, 72, 1.4, 123, 45));
        vm.Select(id);
        vm.SetPerspective(ExplorationPerspective.Interaction);
        Check(vm.FocusRole == ArchitecturalRole.Presentation && vm.Selected?.Role == ArchitecturalRole.Feature, "perspective changes focus without changing source facts");
        vm.Back();
        vm.Back();
        Check(vm.State.SelectedId is null && vm.State.Query == "Core" && vm.State.Viewport == new ViewportState(31, 72, 1.4, 123, 45), "back restores search pan zoom and scroll checkpoint");
        vm.Forward();
        Check(vm.Selected?.Id == id, "forward restores selection");
        vm.TogglePin();
        Check(vm.Pins.Contains(id), "pin stores current symbol");
        await vm.ExecuteAsync("geometry-recalculate");
        Check(backend.LastAction == "geometry-recalculate" && vm.Error is null, "generic VM dispatches unrelated backend action without knowing analysis");
        await vm.ExecuteAsync("unsupported");
        Check(vm.Error?.Contains("暂不支持", StringComparison.Ordinal) == true && vm.Workspace is not null && !vm.IsBusy, "unsupported action visible without losing workspace");
        vm.SetViewport(new(Zoom: 99));
        Check(vm.State.Viewport?.Zoom == 1.8, "viewport zoom bounded");
    }

    private static void RegistrationCases()
    {
        var services = new ServiceCollection();
        services.AddAtlas<IProbe, Probe>();
        using ServiceProvider provider = services.BuildServiceProvider();
        Check(provider.GetRequiredService<IProbe>() is Probe, "typed registration resolves declared implementation");
        Throws<InvalidOperationException>(() => services.AddAtlas<IProbe, Probe>(), "duplicate contract registration rejected");
        Throws<InvalidOperationException>(() => new ServiceCollection().AddAtlas<IProbe, UndeclaredProbe>(), "undeclared provider registration rejected");
    }

    private static async Task InventoryCases(string directory)
    {
        string source = Path.Combine(directory, "source");
        Directory.CreateDirectory(Path.Combine(source, "bin"));
        await File.WriteAllTextAsync(Path.Combine(source, "fixture.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        await File.WriteAllTextAsync(Path.Combine(source, "Legacy.cs"), "public class Legacy {}");
        await File.WriteAllTextAsync(Path.Combine(source, "View.xaml"), "<View />");
        await File.WriteAllTextAsync(Path.Combine(source, "bin", "Generated.cs"), "ignored");
        await File.WriteAllBytesAsync(Path.Combine(source, "Encoding.cs"), [0xff, 0xff, 0xff]);
        SourceSnapshot snapshot = await new SourceRepository().ReadAsync(source);
        Check(snapshot.Documents.Length == 4 && !snapshot.Documents.Any(document => document.Path.StartsWith("bin/", StringComparison.Ordinal)), "inventory includes unsupported config and excludes build output");
        Check(snapshot.InventoryFindings.Any(item => item.Rule == "AT203") && snapshot.Documents.Any(document => document.Language == "unsupported-encoding"), "unreadable encoding is preserved as explicit gap");
    }

    private static async Task SelfCases(string root)
    {
        var analyzer = new AnalyzeRepository(new SourceRepository(), Indexer, Rules);
        AnalysisReport report = await analyzer.AnalyzeAsync(new("self", "Code Atlas · 自身源码", root));
        AnalysisFinding[] errors = report.Findings.Where(item => item.Severity == FindingSeverity.Error).ToArray();
        Check(errors.Length == 0, "self analysis has no known boundary errors: " + string.Join("; ", errors.Select(item => item.Message)));
        Check(report.Coverage.CSharpFiles >= 15 && report.Coverage.UnclassifiedSymbols == 0, "self source has explicit roles including regression tests");
        Check(report.Findings.Any(item => item.Severity == FindingSeverity.Unsupported), "self unsupported semantics remain visible");
        Check(report.Graph.Relations.Any(item => item.Kind == RelationKind.Registration), "self typed registrations produce graph evidence");
        CodeNode vm = report.Graph.Nodes.Single(node => node.Name == "GraphExplorerViewModel" && node.Kind == "type");
        Dictionary<string, CodeNode> nodes = report.Graph.Nodes.ToDictionary(node => node.Id);
        Check(report.Graph.Relations.Where(edge => edge.From == vm.Id || nodes[edge.From].ParentId == vm.Id)
            .All(edge => nodes[edge.To].Role is ArchitecturalRole.Presentation or ArchitecturalRole.Contract or ArchitecturalRole.Framework), "self ViewModel depends only on interaction contracts and declarations");
        Check(report.Graph.Relations.All(edge => nodes.ContainsKey(edge.From) && nodes.ContainsKey(edge.To)), "self graph has no dangling relations");
        await new JsonReportRepository(Path.Combine(root, "artifacts", "reports")).SaveAsync(report);
    }

    private static void Throws<T>(Action action, string name) where T : Exception
    {
        try { action(); }
        catch (T) { Check(true, name); return; }
        throw new InvalidOperationException("FAIL: " + name);
    }
    private static async Task ThrowsAsync<T>(Func<Task> action, string name) where T : Exception
    {
        try { await action(); }
        catch (T) { Check(true, name); return; }
        throw new InvalidOperationException("FAIL: " + name);
    }

    public interface IProbe { }
    [AtlasProvides(typeof(IProbe))] public sealed class Probe : IProbe { }
    public sealed class UndeclaredProbe : IProbe { }

    private sealed class FakeWorkspace(AnalysisReport? report = null) : IExplorerWorkspace
    {
        public AnalysisReport? CurrentReport { get; set; } = report;
        public string? LastAction { get; private set; }
        public Task<ExplorerWorkspace> LoadAsync(string? reportId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExplorerWorkspace(CurrentReport ?? Report(), [new("geometry-recalculate", "重新计算几何")], [new("test", "Test", DateTimeOffset.UnixEpoch)]));
        public Task<ExplorerWorkspace> ExecuteAsync(string actionId, string reportId, CancellationToken cancellationToken = default)
        {
            if (actionId != "geometry-recalculate") throw new NotSupportedException("暂不支持：" + actionId);
            LastAction = actionId;
            return LoadAsync(reportId, cancellationToken);
        }
    }
}
