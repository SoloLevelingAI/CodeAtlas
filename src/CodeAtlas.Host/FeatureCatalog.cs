using CodeAtlas.Framework;

[assembly: AtlasFeature("atlas.analysis", "源码分析", "把仓库源码转换成可追溯的符号、引用和声明事实。", Kind = FeatureKind.Module)]
[assembly: AtlasFeature("atlas.analysis.inventory", "盘点源码", "收集可读文件、识别项目归属，并保留读取失败或不支持的文件。", "atlas.analysis", Inputs = "仓库目录", Outputs = "源码快照和读取问题")]
[assembly: AtlasFeature("atlas.analysis.csharp", "解析 C# 语义", "读取类型、成员、XML 说明和真实静态引用；不能确认的语义逐项报告。", "atlas.analysis", Inputs = "源码快照", Outputs = "符号图、用途说明、静态关系与分析限制")]
[assembly: AtlasFeature("atlas.analysis.run", "编排一次分析", "组合源码读取、语义索引和质量检查，产生一份带覆盖范围的报告。", "atlas.analysis", Inputs = "分析请求", Outputs = "完整分析报告")]

[assembly: AtlasFeature("atlas.explore", "代码探索", "帮助阅读者按任务找到代码，理解用途，并沿证据返回来源。", Kind = FeatureKind.Module)]
[assembly: AtlasFeature("atlas.explore.map", "功能地图页", "先选模块，再沿子功能、类型和成员查找实现；保留地图和查找位置。", "atlas.explore", Kind = FeatureKind.Page, Inputs = "当前报告、搜索条件和选中路径", Outputs = "定位到的功能、方法说明、调用证据与源码行")]
[assembly: AtlasFeature("atlas.explore.navigation", "管理探索状态", "保存当前功能路径、类型、筛选、视角、地图位置和返回历史。", "atlas.explore", Inputs = "导航与搜索动作", Outputs = "可恢复的浏览状态")]
[assembly: AtlasFeature("atlas.explore.navigation.path", "定位功能与成员", "维护当前模块、子功能、类型和成员的位置，沿声明的父级生成可返回路径。", "atlas.explore.navigation")]
[assembly: AtlasFeature("atlas.explore.navigation.find", "按用途查找", "在当前功能范围搜索名称、用途和文件，按所属功能与职责呈现有限数量的结果。", "atlas.explore.navigation")]
[assembly: AtlasFeature("atlas.explore.navigation.history", "恢复浏览位置", "返回或前进时恢复路径、搜索、选中项与地图和滚动位置。", "atlas.explore.navigation")]
[assembly: AtlasFeature("atlas.explore.workspace", "接入可替换工作区", "通过契约载入报告和可用动作，让界面无需认识具体分析器或存储。", "atlas.explore", Inputs = "工作区端口与动作标识", Outputs = "当前图表和动作结果")]

[assembly: AtlasFeature("atlas.reports", "报告管理", "保存和查找分析快照，安全更新已有报告。", Kind = FeatureKind.Module)]
[assembly: AtlasFeature("atlas.reports.store", "读写报告快照", "实现报告创建、读取、列表、替换和删除；中断写入时保留已有报告。", "atlas.reports", Inputs = "报告对象或报告标识", Outputs = "持久化快照或明确的存储错误")]

[assembly: AtlasFeature("atlas.quality", "质量检查", "识别代码边界与信息组织的问题，让规则能被正反例验证。", Kind = FeatureKind.Module)]
[assembly: AtlasFeature("atlas.quality.boundaries", "检查职责边界", "检查越层依赖、契约签名、交互和用例上的已知外部作用，以及可见的提供方注册。", "atlas.quality", Inputs = "语义关系图", Outputs = "定位到源文件的边界问题")]
[assembly: AtlasFeature("atlas.quality.structure", "检查层级与说明", "发现未登记功能、错误父级、缺失方法说明和过大的扁平方法集合。", "atlas.quality", Inputs = "功能目录、类型与方法说明", Outputs = "带修改建议的结构问题")]
[assembly: AtlasFeature("atlas.quality.regression", "验证正反例", "用正常实现、故意违规样例和自身源码检验检查器是否真的发现问题。", "atlas.quality")]

[assembly: AtlasFeature("atlas.runtime", "运行与组装", "选择端口提供方，并提供本地命令和浏览器入口。", Kind = FeatureKind.Module)]
[assembly: AtlasFeature("atlas.runtime.registration", "注册契约提供方", "检查实现声明与重复注册，通过 Fluent API 组装依赖。", "atlas.runtime")]
[assembly: AtlasFeature("atlas.runtime.host", "启动本地工作台", "启动本地服务或执行分析指令；不支持的指令返回明确退出码。", "atlas.runtime")]

[assembly: AtlasFeature("atlas.foundation", "共享基础", "存放多个功能确实共享的声明协议和源位置等基础值对象。", Kind = FeatureKind.Shared)]
[assembly: AtlasFeature("atlas.foundation.declarations", "声明协议", "定义功能层级、代码职责、作用与契约提供方的属性。", "atlas.foundation", Kind = FeatureKind.Shared)]
[assembly: AtlasFeature("atlas.foundation.shared", "共享来源与诊断", "提供源码位置、源文件与诊断等跨功能数据；专用端口仍归各自功能。", "atlas.foundation", Kind = FeatureKind.Shared)]
