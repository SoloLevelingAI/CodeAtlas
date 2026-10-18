using CodeAtlas.Framework;

[assembly: AtlasModule("atlas.explore.desktop.state", "桌面探索状态", ArchitecturalRole.Presentation)]
[assembly: AtlasFeature("atlas.explore.desktop", "桌面代码工作台", "以本地窗口浏览功能层级、关系、源码和分析限制。", "atlas.explore", Kind = FeatureKind.Page, Inputs = "分析报告与用户动作", Outputs = "保留位置的功能图和源码证据")]
[assembly: AtlasFeature("atlas.explore.desktop.state", "维护桌面浏览状态", "用稳定对象和有界动作历史维护选择、展开与定位，不复制源码图。", "atlas.explore.desktop")]
[assembly: AtlasFeature("atlas.explore.desktop.search", "索引与按用途查找", "建立一次索引，按功能、职责和类型组织代码，保留未声明项。", "atlas.explore.desktop")]
[assembly: AtlasFeature("atlas.runtime.cli", "独立命令行", "无界面执行分析、搜索与源码查询，以 JSON 和退出码报告结果。", "atlas.runtime")]
[assembly: AtlasFeature("atlas.runtime.control", "本机桌面控制", "通过当前用户专用的命名管道查询和控制桌面会话。", "atlas.runtime")]
[assembly: AtlasFeature("atlas.quality.standards", "规范与架构整理", "用版本化规范解释诊断，并按功能内职责给出可认领切片；缺少证据不算通过。", "atlas.quality", Kind = FeatureKind.Page, Inputs = "规范与当前分析报告", Outputs = "逐项判断、交接风险、源码证据与待执行的整理步骤")]
