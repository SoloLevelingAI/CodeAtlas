using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using CodeAtlas.Automation;
using CodeAtlas.Contracts;
using CodeAtlas.Exploration;
using CodeAtlas.Framework;
using CodeAtlas.Storage;

[assembly: AtlasModule("atlas.quality.regression", "桌面状态与控制回归", ArchitecturalRole.Test)]

namespace CodeAtlas.Desktop.Regression;

/// <summary>验证状态历史、懒加载、完整性和控制协议，不依赖真实窗口或用户数据。</summary>
public static class Program
{
    private static readonly List<string> Passed = [];
    private static SourceLocation At(int line = 1) => new("fixture.cs", line, line);
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        Passed.Add(label);
    }
    /// <summary>用夹具报告确认分工只引用已有诊断，且空风险不会写成通过。</summary>
    private static void DivisionCases()
    {
        CodeNode contract = Node("IPort", null, "m.f", ArchitecturalRole.Contract);
        CodeNode application = Node("UseCase", null, "m.f", ArchitecturalRole.Application);
        CodeNode integration = Node("Adapter", null, "m.f", ArchitecturalRole.Integration);
        CodeNode composition = Node("Root", null, "m.f", ArchitecturalRole.Composition);
        CodeNode data = Node("Store", null, "m.f", ArchitecturalRole.DataAccess);
        CodeNode feature = Node("Core", null, "m.f", ArchitecturalRole.Feature);
        CodeNode presentation = Node("Page", null, "m.f", ArchitecturalRole.Presentation);
        CodeRelation[] relations =
        [
            new("Adapter", "IPort", RelationKind.ProvidesContract, At(), "provides"),
            new("Root", "IPort", RelationKind.Registration, At(), "registers"),
            new("UseCase", "IPort", RelationKind.TypeReference, At(), "uses")
        ];
        AnalysisReport clean = BoardReport([contract, application, integration, composition, data, feature, presentation], relations, []);
        DivisionReading board = DivisionBoard.Read(clean);
        Check(board.Banner == DivisionBoard.ClearBanner && board.Slices.All(slice => slice.Status == DivisionBoard.Clear && slice.Assignment == DivisionBoard.Claim), "干净切片只写仍需人工确认，可以认领");
        Check(board.Slices.All(slice => !slice.Status.Contains("通过", StringComparison.Ordinal) && !slice.Assignment.Contains("通过", StringComparison.Ordinal)), "分工状态不出现通过");
        Check(board.Slices.Single(slice => slice.Role == ArchitecturalRole.Integration).Ports.Any(port => port.Kind == "提供声明" && port.Name == "IPort"), "适配切片列出提供的契约");
        Check(board.Slices.Single(slice => slice.Role == ArchitecturalRole.Composition).Ports.Any(port => port.Kind == "实际注册" && port.Name == "IPort"), "组合切片列出实际注册");
        Check(board.Slices.Single(slice => slice.Role == ArchitecturalRole.Application).Ports.Any(port => port.Kind == "依赖契约" && port.Name == "IPort"), "用例切片列出依赖的契约");
        Check(board.Slices.Single(slice => slice.Role == ArchitecturalRole.Contract).Ports.Any(port => port.Kind == "切片内契约"), "契约切片列出自己的端口");
        Check(board.ClaimableCount == board.Slices.Length && board.BlockedCount == 0 && board.Inbox.SymbolCount == 0, "没有阻碍时切片可认领且待整理为空");
        Check(board.Slices.Single(slice => slice.Role == ArchitecturalRole.Contract).FeaturePath == "m / m.f", "切片路径沿功能目录");

        DivisionReading risks = DivisionBoard.Read(BoardReport([contract, application, integration, composition, data, feature, presentation], relations,
        [
            new("AT101", FindingSeverity.Error, "越层", At(), feature.Id),
            new("AT105", FindingSeverity.Warning, "交互 IO", At(), presentation.Id),
            new("AT106", FindingSeverity.Error, "签名", At(), contract.Id),
            new("AT107", FindingSeverity.Error, "用例 IO", At(), application.Id),
            new("AT108", FindingSeverity.Error, "未声明提供", At(), composition.Id),
            new("AT109", FindingSeverity.Error, "重复", At(), composition.Id),
            new("AT110", FindingSeverity.Warning, "未注册", At(), integration.Id),
            new("AT118", FindingSeverity.Warning, "容器", At(), composition.Id),
            new("AT130", FindingSeverity.Warning, "整图", At(), presentation.Id)
        ]));
        Check(risks.Slices.Single(slice => slice.Role == ArchitecturalRole.Feature).Risks.Any(risk => risk.Rule == "AT101"), "AT101 记在功能核心");
        Check(risks.Slices.Single(slice => slice.Role == ArchitecturalRole.Presentation).Risks.Select(risk => risk.Rule).Order(StringComparer.Ordinal).SequenceEqual(["AT105", "AT130"]), "AT105 与 AT130 记在交互切片");
        Check(risks.Slices.Single(slice => slice.Role == ArchitecturalRole.Contract).Status == DivisionBoard.Blocked && risks.Slices.Single(slice => slice.Role == ArchitecturalRole.Contract).Assignment == DivisionBoard.Pause, "AT106 使契约切片交接受阻");
        Check(risks.Slices.Single(slice => slice.Role == ArchitecturalRole.Application).Risks.Any(risk => risk.Rule == "AT107"), "AT107 记在用例切片");
        Check(risks.Slices.Single(slice => slice.Role == ArchitecturalRole.Composition).Risks.Select(risk => risk.Rule).Order(StringComparer.Ordinal).SequenceEqual(["AT108", "AT109", "AT118"]), "注册类风险记在组合切片");
        Check(risks.Slices.Single(slice => slice.Role == ArchitecturalRole.Integration).Risks.Any(risk => risk.Rule == "AT110"), "AT110 记在提供方切片");
        Check(risks.Slices.Single(slice => slice.Role == ArchitecturalRole.DataAccess).Status == DivisionBoard.Clear && risks.Slices.Single(slice => slice.Role == ArchitecturalRole.DataAccess).Assignment == DivisionBoard.Claim, "没有诊断的切片不被连坐");

        CodeNode loose = Node("Loose", null, "missing.feature", ArchitecturalRole.Feature);
        CodeNode bare = new("Bare", "Bare", "Bare", "class", null, "m.f", null, EffectKind.Unspecified, At(4), At(), "type");
        DivisionReading held = DivisionBoard.Read(BoardReport([contract, loose, bare], [],
        [
            new("AT010", FindingSeverity.Warning, "未登记", At(), loose.Id),
            new("AT001", FindingSeverity.Warning, "未声明", At(4), bare.Id),
            new("AT109", FindingSeverity.Error, "没挂上", At(), null)
        ]));
        Check(held.Inbox.SymbolCount == 2 && held.Inbox.Findings.Count(item => item.Rule is "AT001" or "AT010") == 2, "AT001/AT010 和未登记符号进入待整理");
        Check(held.Slices.All(slice => slice.SampleNodeId != loose.Id && slice.SampleNodeId != bare.Id) && held.Banner == DivisionBoard.MixedBanner, "待整理符号不进入可认领切片");
        Check(held.Inbox.UnplacedRisks.Any(item => item.Rule == "AT109"), "挂不上切片的交接风险仍保留");
        Check(held.Slices.Single().Assignment == DivisionBoard.Claim, "目录内的其他切片仍可认领");

        DivisionReading cautious = DivisionBoard.Read(clean with { Findings = [new("AT206", FindingSeverity.Unsupported, "盲区", At())] });
        Check(cautious.Banner.StartsWith(DivisionBoard.BlindBanner, StringComparison.Ordinal) && cautious.ClaimableCount == cautious.Slices.Length && cautious.Slices.All(slice => slice.Assignment == DivisionBoard.Claim && slice.Status == DivisionBoard.Clear), "未支持诊断不把空风险写成通过，也不抹掉可认领切片");
        Check(DivisionBoard.Read(null).Banner == DivisionBoard.NoReportBanner && DivisionBoard.Read(null).ClaimableCount == 0, "无报告不能分工");

        CodeNode other = Node("OtherCore", null, "m.other", ArchitecturalRole.Feature);
        AnalysisReport cross = BoardReport([application, other], [new("UseCase", "OtherCore", RelationKind.TypeReference, At(), "cross")], []);
        DivisionSlice useCase = DivisionBoard.Read(cross).Slices.Single(slice => slice.Role == ArchitecturalRole.Application);
        Check(useCase.Status == DivisionBoard.Clear && useCase.Ports.All(port => port.Name != "OtherCore"), "规则未报出的跨功能关系不另造交接风险或契约");
        DivisionSlice blockedUse = DivisionBoard.Read(cross with { Findings = [new("AT101", FindingSeverity.Error, "越层", At(), application.Id)] })
            .Slices.Single(slice => slice.Role == ArchitecturalRole.Application);
        Check(blockedUse.Status == DivisionBoard.Blocked && blockedUse.Risks.Single().Rule == "AT101" && blockedUse.Ports.All(port => port.Name != "OtherCore"), "AT101 只作为已有证据阻塞切片");

        Check(EvidenceLocator.MapHref("fixture", "IPort", "fixture.cs", 3, clean.Graph) is { } href && href.Contains("node=IPort", StringComparison.Ordinal) && href.Contains("report=fixture", StringComparison.Ordinal), "证据优先链回符号");
        Check(EvidenceLocator.MapHref("fixture", null, "fixture.cs", 2, clean.Graph) is { } fileHref && fileHref.Contains("file=", StringComparison.Ordinal) && fileHref.Contains("line=2", StringComparison.Ordinal), "没有符号时链回报告原文");
        Check(EvidenceLocator.MapHref("fixture", null, "missing.cs", 1, clean.Graph) is null, "快照里没有的文件不伪装成可定位");
        Check(EvidenceLocator.Excerpt(clean.Graph, "fixture.cs", 2).Any(line => line.Number == 2 && line.Text.Contains("two", StringComparison.Ordinal)), "证据片段来自报告原文");
    }

    private static AnalysisReport BoardReport(IEnumerable<CodeNode> nodes, IEnumerable<CodeRelation> relations, IEnumerable<AnalysisFinding> findings)
    {
        ImmutableArray<CodeNode> nodeList = nodes.ToImmutableArray();
        ImmutableArray<CodeRelation> relationList = relations.ToImmutableArray();
        ImmutableArray<AnalysisFinding> findingList = findings.ToImmutableArray();
        ImmutableArray<FeatureDefinition> features = [Feature("m"), Feature("m.f", "m"), Feature("m.other", "m")];
        return new(1, "fixture", "Fixture", ".", DateTimeOffset.UnixEpoch,
            new([new("fixture.cs", "fixture", ".cs", "// one\n// two\nvoid Run() {}")], nodeList, relationList, [], [], features),
            new(1, 1, 0, nodeList.Length, nodeList.Length, 0, relationList.Length), findingList, []);
    }

    private static AnalysisReport Report(ImmutableArray<CodeNode> nodes, ImmutableArray<FeatureDefinition> features) =>
        new(1, "fixture", "Fixture", ".", DateTimeOffset.UnixEpoch,
            new([new("fixture.cs", "fixture", ".cs", "// one\n// two\nvoid Run() {}"), new("view.xaml", "fixture", ".xaml", "<Grid />")], nodes, [], [], [], features),
            new(2, 1, 1, nodes.Length, nodes.Length, 0, 0), [], []);
    private static CodeNode Node(string id, string? parent, string feature, ArchitecturalRole role, string summary = "用于回归的源码说明") =>
        new(id, id, id, parent is null ? "class" : "method", parent, feature, role, EffectKind.Unspecified, At(3), At(), "type", Summary: summary);
    private static FeatureDefinition Feature(string id, string? parent = null) => new(id, id, "功能用途", parent, parent is null ? FeatureKind.Module : FeatureKind.Capability, "输入", "结果", At());

    /// <summary>独立构造错误与大规模样例，检查真实行为和失败路径。</summary>
    public static async Task<int> Main(string[] args)
    {
        try
        {
            AnalysisReport report = Report([Node("Type", null, "m.f", ArchitecturalRole.DataAccess), Node("Run", "Type", "m.f", ArchitecturalRole.DataAccess),
                Node("Contract", null, "m.f", ArchitecturalRole.Contract), Node("Scoped", "Type", "m.other", ArchitecturalRole.Presentation)],
                [Feature("m"), Feature("m.f", "m"), Feature("m.other", "m")]);
            var index = new ReportIndex(report);
            var guide = ArchitectureGuide.Load();
            Check(guide.Rules.Length == 10 && guide.Migration.Length == 9, "GUI 与 CLI 的规范资源完整且有整理路线");
            Check(guide.Rules.All(r => r.MarkdownPath == "docs/standards/" + r.Id + ".md" && ArchitectureGuide.ReadMarkdown(r.Id).Contains(r.Requirement, StringComparison.Ordinal)), "每条规范对应真实 Markdown 且应用内原文与要求一致");
            Check(guide.Assess(null).All(a => a.Status == "尚未分析仓库"), "无报告不能通过架构认证");
            AnalysisReport missingEvidence = report with { Coverage = report.Coverage with { UnclassifiedSymbols = 1 } };
            Check(guide.Assess(missingEvidence).Where(a => a.Rule.FindingRules.Length > 0 && !a.Rule.ExternalEvidence).All(a => a.Status.Contains("证据不足", StringComparison.Ordinal)), "缺少声明不伪装成符合规范");
            AnalysisReport knownViolation = report with { Findings = [new("LD002", FindingSeverity.Warning, "fixture issue", At(), "Run")] };
            ArchitectureAssessment vmAssessment = guide.Assess(knownViolation).Single(a => a.Rule.Id == "A03");
            Check(vmAssessment.EvidenceCount == 1 && vmAssessment.Evidence[0].NodeId == "Run" && vmAssessment.Status.StartsWith("存在问题", StringComparison.Ordinal), "人工问题映射规范并保留可定位证据");
            Check(guide.Assess(report).Single(a => a.Rule.Id == "A07").Status.Contains("仍需人工确认", StringComparison.Ordinal), "没有模块诊断不能认证为通过");
            Check(guide.Assess(report).Single(a => a.Rule.Id == "A09").Status.Contains("仍需人工确认", StringComparison.Ordinal), "没有整图集合不能认证浏览性能");
            Check(guide.Assess(report).Single(a => a.Rule.Id == "A10").Status.Contains("独立回归", StringComparison.Ordinal), "反例闭环不能凭空图认定");
            Check(guide.Rules.Single(r => r.Id == "A07").FindingRules.Contains("AT110") && guide.Rules.Single(r => r.Id == "A09").FindingRules.Contains("AT130") && guide.Rules.Single(r => r.Id == "A10").ExternalEvidence, "A07/A09 有自动诊断且 A10 仍要求独立回归");
            AnalysisReport moduleViolation = report with { Findings = [new("AT109", FindingSeverity.Error, "duplicate", At(), "Run")] };
            Check(guide.Assess(moduleViolation).Single(a => a.Rule.Id == "A07").Status.StartsWith("存在问题", StringComparison.Ordinal), "重复提供方映射到模块规范");
            DivisionCases();
            Check(index.MaterializedCount == 2, "初始只实现根节点与原文入口");
            var session = new ExplorerSession(); session.Load(index);
            session.Select("Run"); ExplorerItem item = session.Selected!;
            Check(item.Parent?.Title == "Type" && item.Parent.Parent?.Title == "数据访问 / CRUD", "方法归入功能内的职责与类型");
            Check(session.Breadcrumb.Contains("m.f", StringComparison.Ordinal), "路径包含所属子功能");
            Check(ReferenceEquals(item, index.Resolve("Run")), "反复定位复用同一对象");
            Check(session.Source.Contains("void Run", StringComparison.Ordinal), "源码来自报告原文");
            session.Select("Contract"); session.Undo(); Check(ReferenceEquals(session.Selected, item), "撤销选择恢复原实例");
            session.Redo(); Check(session.Selected?.Id == "node:Contract", "重做选择");
            session.Expand("Type", true); session.Expand("Type", false); session.Undo(); Check(index.Resolve("Type").IsExpanded, "撤销折叠恢复展开项");
            session.Search("回归"); Check(session.SearchResults.Count == 4 && session.Selected?.Title == "Contract", "搜索不替换主目录选择");
            ExplorerItem scoped = index.Resolve("Scoped"); Check(scoped.Parent?.Title == "Type" && scoped.Parent.Id.StartsWith("context:", StringComparison.Ordinal), "覆盖功能声明的方法仍保留类型上下文");
            session.SetView("graph"); session.Undo(); Check(session.View == "tree", "视图切换可撤销");
            int history = session.History.Count; session.TrackVisibleModule(index.Roots[0]); Check(session.History.Count == history, "滚动同步不污染历史");
            Check(index.Resolve("file:view.xaml").Summary.Contains("暂不支持", StringComparison.Ordinal), "非 C# 原文不遗漏");
            session.Load(new ReportIndex(report)); Check(!session.CanUndo && session.Selected is null && session.Query.Length == 0, "替换报告释放旧状态与历史");
            var bounded = new ActionHistory(3); int value = 0;
            for (int i = 0; i < 10; i++) { int before = value, next = i + 1; bounded.Execute(() => value = before, () => value = next); }
            Check(bounded.Count == 3, "历史有明确容量上限"); bounded.Undo(); bounded.Execute(() => value = 9, () => value = 42); Check(!bounded.CanRedo && value == 42, "新分支截断旧重做栈");
            var malformed = new ReportIndex(Report([Node("Legacy", null, "unknown", ArchitecturalRole.Feature)], [Feature("cycle.a", "cycle.b"), Feature("cycle.b", "cycle.a")]));
            Check(malformed.Roots.Any(r => r.Title == "待整理代码") && malformed.Resolve("Legacy").Title == "Legacy", "循环和未登记功能保留待整理");
            bool rejected = false;
            try { _ = new ReportIndex(Report([Node("a", "b", "m", ArchitecturalRole.Feature), Node("b", "a", "m", ArchitecturalRole.Feature)], [Feature("m")])); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected, "符号父级循环被明确拒绝而非卡死");

            var many = ImmutableArray.CreateBuilder<CodeNode>();
            for (int i = 0; i < 120; i++)
            {
                string type = "Type" + i; many.Add(Node(type, null, "large", ArchitecturalRole.Feature));
                for (int j = 0; j < 200; j++) many.Add(Node(type + ".Method" + j, type, "large", ArchitecturalRole.Feature));
            }
            Stopwatch timer = Stopwatch.StartNew();
            var large = new ReportIndex(Report(many.ToImmutable(), [Feature("large")]));
            long buildMilliseconds = timer.ElapsedMilliseconds;
            Check(large.MaterializedCount == 2, "24120 个符号不会预先构造整棵 UI 树");
            timer.Restart(); IReadOnlyList<ExplorerItem> hits = large.Search("Method"); long searchMilliseconds = timer.ElapsedMilliseconds;
            Check(hits.Count == 100 && large.MaterializedCount < 150, "大报告搜索限制结果且只实现所需祖先");

            using var stream = new MemoryStream();
            await ControlProtocol.WriteAsync(stream, new ControlRequest { Command = "select", Arguments = new() { ["id"] = "含中文的方法" } }, default);
            stream.Position = 0; ControlRequest decoded = await ControlProtocol.ReadAsync<ControlRequest>(stream, default);
            Check(decoded.Arguments["id"] == "含中文的方法", "协议 UTF-8 与长度帧往返");
            using var invalid = new MemoryStream(new byte[] { 255, 255, 255, 127 }); rejected = false;
            try { _ = await ControlProtocol.ReadAsync<ControlRequest>(invalid, default); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "超大控制帧在分配内存前被拒绝");
            rejected = false; try { _ = ControlProtocol.PipeName("../wrong"); } catch (ArgumentException) { rejected = true; }
            Check(rejected && ControlProtocol.PipeName("trial") == ControlProtocol.PipeName("trial"), "会话名校验与稳定用户隔离");
            if (args.Length > 0)
            {
                AnalysisReport real = await ReportFile.LoadAsync(args[0]);
                var actual = new ReportIndex(real);
                foreach (CodeNode node in real.Graph.Nodes) Check(actual.Resolve(node.Id).Source == node.Source, "真实源位置保留 / " + node.Id);
            }
            Console.WriteLine(JsonSerializer.Serialize(new { passed = true, assertions = Passed.Count, largeSymbols = many.Count, buildMilliseconds, searchMilliseconds, tests = Passed }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
