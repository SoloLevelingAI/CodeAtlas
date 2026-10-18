using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Data;
using System.Windows.Media;
using CodeAtlas.Contracts;
using CodeAtlas.Exploration;
using CodeAtlas.Framework;

namespace CodeAtlas.Desktop;

/// <summary>把规范、报告证据和渐进整理路线呈现为可阅读并定位的窗口。</summary>
[AtlasComponent("atlas.quality.standards", ArchitecturalRole.Presentation)]
public sealed class ArchitectureWindow : Window
{
    private readonly StackPanel detail = new() { Margin = new Thickness(22) };
    private readonly AnalysisReport? report;
    private readonly IStandardsReader standards;
    private readonly Action<string> locate;

    /// <summary>构建规范阅读窗口；所有证据来自传入快照，不执行自动重构。</summary>
    /// <param name="report">当前报告，也可为空以浏览规范。</param>
    /// <param name="standards">规范原文端口。</param>
    /// <param name="locate">把证据定位回主工作区的回调。</param>
    public ArchitectureWindow(AnalysisReport? report, IStandardsReader standards, Action<string> locate)
    {
        this.report = report; this.standards = standards; this.locate = locate;
        Style = (Style)System.Windows.Application.Current.FindResource(typeof(Window));
        Title = "架构规范 · 现状与整理路线"; Width = 1120; Height = 800; MinWidth = 850; MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(241, 245, 242));
        var guide = ArchitectureGuide.Load();
        var shell = new DockPanel { Margin = new Thickness(20) };
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        header.Children.Add(Text("架构规范 " + guide.Version, 23, true));
        header.Children.Add(Text(guide.Intent, 13));
        header.Children.Add(Text((report?.Name ?? "尚未分析仓库") + " · 自动检查 + 人工证据；无诊断不代表全符合", 12));
        DockPanel.SetDock(header, Dock.Top); shell.Children.Add(header);
        var back = new Button { Content = "返回代码地图，保留原位置", Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        back.Click += (_, _) => Close(); DockPanel.SetDock(back, Dock.Bottom); shell.Children.Add(back);
        var tabs = new TabControl(); shell.Children.Add(tabs);
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(275) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
        var list = new ListBox { ItemsSource = guide.Assess(report), DisplayMemberPath = nameof(ArchitectureAssessment.Label), BorderThickness = new Thickness(0), Padding = new Thickness(6), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        list.ItemContainerStyle = new Style(typeof(ListBoxItem));
        list.ItemContainerStyle.Setters.Add(new Setter(AutomationProperties.NameProperty, new Binding(nameof(ArchitectureAssessment.Label))));
        list.ItemContainerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 9, 8, 9)));
        list.SelectionChanged += (_, _) => { if (list.SelectedItem is ArchitectureAssessment assessment) Show(assessment); };
        grid.Children.Add(list);
        var reading = new ScrollViewer { Content = detail, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetColumn(reading, 1); grid.Children.Add(reading);
        tabs.Items.Add(new TabItem { Header = "规范与符合性", Content = grid });
        var plan = new StackPanel { Margin = new Thickness(22) };
        plan.Children.Add(Text("LaunchDesk：先迁移一个纵向功能", 21, true));
        plan.Children.Add(Text("以下是待执行的整理方案，不是已完成重构。每一步先保持原行为，再检查依赖边界。", 13));
        foreach (string step in guide.Migration) plan.Children.Add(Text(step, 14));
        tabs.Items.Add(new TabItem { Header = "整理路线（待执行）", Content = new ScrollViewer { Content = plan, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        var support = new StackPanel { Margin = new Thickness(22) };
        support.Children.Add(Text("已实现", 18, true));
        support.Children.Add(Text("C# 结构与规则分析、功能目录、搜索、关系图、只读源码与行号、GUI/CLI、面板调宽与自动换行。", 14));
        support.Children.Add(Text("未实现", 18, true));
        support.Children.Add(Text("源码编辑保存、自动重构、运行时追踪、DLL 热卸载、客户分发闭包验证和 AI 管理。", 14));
        support.Children.Add(Text("报告与分析", 18, true));
        support.Children.Add(Text("分析仓库会读取源码并生成新快照；打开历史报告只查看已经保存的结果。浏览目录不会重新扫描，更不会执行目标项目。", 14));
        foreach (SupportStatement item in report?.Support ?? []) support.Children.Add(Text(item.Capability + " · " + item.Status + "\n" + item.Detail, 13));
        tabs.Items.Add(new TabItem { Header = "已实现与不支持", Content = new ScrollViewer { Content = support, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        Content = shell; list.SelectedIndex = 0;
    }

    /// <summary>展示规则解释、整理建议以及最多三十条可追溯证据。</summary>
    private void Show(ArchitectureAssessment assessment)
    {
        detail.Children.Clear();
        detail.Children.Add(Text(assessment.Label, 21, true));
        var markdown = new Button { Content = "对应 Markdown：" + assessment.Rule.MarkdownPath, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12) };
        markdown.Click += (_, _) => new Window { Owner = this, Title = assessment.Rule.MarkdownPath, Width = 820, Height = 650, MinWidth = 400, MinHeight = 300, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new TextBox { Text = standards.ReadMarkdown(assessment.Rule.Id), IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), FontSize = 14,
                Margin = new Thickness(20), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } }.Show();
        detail.Children.Add(markdown);
        detail.Children.Add(Text(assessment.Status, 14, true));
        foreach (var section in new[] { ("规范要求", assessment.Rule.Requirement), ("为什么这样分", assessment.Rule.Reason),
            ("当前能检查到哪里", assessment.Rule.Automation), ("如何整理", assessment.Rule.Change), ("验收条件", assessment.Rule.Acceptance) })
        { detail.Children.Add(Text(section.Item1, 14, true)); detail.Children.Add(Text(section.Item2, 13)); }
        detail.Children.Add(Text($"当前证据：{assessment.EvidenceCount} 条（显示前 {assessment.Evidence.Length} 条）", 14, true));
        if (assessment.EvidenceCount == 0) detail.Children.Add(Text("没有可用诊断不能推出符合规范。需要结合源码声明、人工审阅与行为回归。", 13));
        foreach (AnalysisFinding finding in assessment.Evidence)
        {
            string? id = finding.NodeId is { } node && report!.Graph.Nodes.Any(n => n.Id == node) ? node
                : report!.Graph.Documents.Any(d => d.Path == finding.Source.Path) ? "file:" + finding.Source.Path : null;
            var button = new Button { Content = Text(finding.Rule + " · " + finding.Message + "\n" + finding.Source.Path + ":" + finding.Source.Line, 12),
                IsEnabled = id is not null, Margin = new Thickness(0, 0, 0, 8), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            if (id is not null) button.Click += (_, _) => { locate(id); Close(); };
            detail.Children.Add(button);
        }
    }

    /// <summary>生成统一换行的阅读文本，避免固定面板裁切。</summary>
    private static TextBlock Text(string value, double size, bool bold = false) => new()
    { Text = value, TextWrapping = TextWrapping.Wrap, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        Foreground = new SolidColorBrush(Color.FromRgb(33, 62, 55)), Margin = new Thickness(0, 0, 0, 12), LineHeight = size * 1.7 };
}
