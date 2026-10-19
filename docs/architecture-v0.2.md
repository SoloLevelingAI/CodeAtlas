# CodeAtlas 架构 0.2：模块声明与启动组合

状态：第一轮实现，2026-10-18。继承 [0.1](architecture-v0.1.md) 与 A01–A10；不替代旧源码属性或报告 schema 1。

引用聊天的完整 v0.2 文稿及 `CodeAtlas-PR1-ModuleRegistry.zip`、两个同名 patch 在当前会话中不可访问。本文是依据本轮明确需求和实际仓库重建的可执行约定，不能声称逐字恢复了原附件。

## 功能、职责、模块与程序集

功能是用户任务，职责是该任务内部的分工，模块是可独立选择的启动组合单位，程序集是物理交付边界。四者不可互相推断。

`AtlasModule` / `AtlasFeature` 继续描述被分析源码。新增的 `ModuleDescriptor` 描述正在启动的宿主，不会把来源不明的旧代码认证成注册模块。嵌套通过显式 `ParentId` 表达，ID 中的点没有层级语义。一个 DLL 可承载多个逻辑子模块；逻辑叶节点可以只有状态或索引职责，无需虚构服务端口。

## 最小协议与约束

| 对象 | 必须回答的问题 | 本轮行为 |
|---|---|---|
| ModuleDescriptor | 是什么、做什么、版本和父级 | ID 唯一、名称用途非空、父级存在且无环 |
| CapabilityContract | 提供哪种能力、哪个接口和版本 | 稳定 ID、闭合接口 Type、Version；同一 ID 不得换接口 |
| CapabilityRequirement | 需要哪种能力、最低版本、是否可选 | 同接口、同主版本、提供版本不低于最低版本 |
| ModuleDependency | 明确依赖哪个模块 | 必需依赖进入闭包，可选依赖仅在已选时建立顺序 |
| ViewContribution | 放在哪里、谁渲染、使用什么数据 | 宿主槽位、已知 renderer key、显式且可用的数据能力 |
| ModuleProfile | 本次启用哪些模块 | 显式入口，补齐父级和必需依赖，不自动启用所有子模块 |
| ResolvedProfile | 最终启动了什么、缺什么 | 确定的拓扑顺序、视图列表、未启用的可选能力 |

版本采用 `System.Version` 数字版本，示例统一用 `1.0`；暂不支持 SemVer 预发布标签、任意版本区间和迁移协商。模块版本与能力版本各自演进。

所需能力只有一个兼容候选时自动加入提供模块；有多个候选必须在 Profile 显式选择一个。已选择的提供方不兼容时失败，不暗中替换。每个活动能力 ID 和每个 DI 接口只允许一个提供方。可选能力不自动拉入模块，已选但版本冲突仍须失败。父级先于子级接线，因此父模块不能反向依赖子模块提供的能力形成组合环。

## 接线顺序（可执行实现的伪代码）

```text
register descriptor + service-registration callback
validate catalog identities and explicit parent tree
select profile entries + ancestors + required modules
resolve required capabilities until closure stabilizes
check optional availability, interface identity, versions, views and cycles
order dependencies before consumers
copy IServiceCollection to staging collection
run selected callbacks, retaining AddAtlas<TContract,TImplementation> checks
verify every declared capability has exactly one service registration
publish ResolvedProfile, then commit staged registrations
host builds and validates DI container
host renders only contributions it understands
```

注册回调只负责接线，不启动线程、不读写用户文件。接线异常不会向调用方服务集合提交部分结果；这不是对回调自身外部副作用的事务。容器仍由宿主 `ValidateOnBuild` / `ValidateScopes` 验证。

## 已实现的模块与 Profile

`AtlasRuntime.CreateRegistry` 登记分析、报告和探索模块。`atlas.explore` 的回调真实注册 `IExplorerWorkspace → ExplorerWorkspaceAdapter`，要求分析和报告能力。导航、历史与索引是同一实现中的逻辑子模块。Web 宿主登记 `atlas.explore.map`，贡献 `web.main → atlas.explorer`，数据能力为 `atlas.workspace`，同时接入原有 scoped `GraphExplorerViewModel`。

| Profile | 显式入口 | 实际效果 |
|---|---|---|
| web | map、navigation.history、index | 解析为 7 个模块，启动已有交互式 Explorer |
| desktop | navigation.history、index | 旧 `AtlasRuntime.Create` 仍提供 GUI/CLI 原有 7 个端口，无 Web 贡献 |
| headless | analysis、reports | 不注册 IExplorerWorkspace、GraphExplorerViewModel 或视图 |

模块关闭验证是启动服务与视图的验证。当前 Host 仍物理引用所有内置项目，`headless` 不表示发布包已删除 DLL。

## 接到现有 Web，而非另建示例

原有 Explorer 增加“报告中的源码功能 / 运行模块”选择。两种数据均使用原有 `ExpandableMap` 和 `BranchCard`，各自保留展开状态。运行模块读取 `ResolvedProfile.Modules` 的显式父级；选中模块后显示提供/需要能力及 ViewContribution。父子归属不绘成调用证据，运行模块不生成源码调用关系。

Home 只在活动 Profile 确实贡献 `atlas.explorer` 时构造原有 Explorer。原有重新分析、源码、ReportIndex、规范和分工页面继续工作。运行声明不写入 `AnalysisReport`，不改变旧报告的来源、覆盖统计、schema 或原始发现。当前运行模块检查依托已加载报告的工作台显示；无报告时仍先提示生成报告。

## 正例与反例

```csharp
// 正例：接口 v1.2 满足 v1.0；子模块可以与父模块位于同一个项目。
registry.Register(new("search", "探索", "提供工作区", new(0, 2, 0),
    Provides: [new("workspace", typeof(IExplorerWorkspace), new(1, 2))]),
    services => services.AddAtlas<IExplorerWorkspace, ExplorerWorkspaceAdapter>());
registry.Register(new("map", "地图", "查看嵌套结构", new(0, 2, 0), "search",
    Requires: [new("workspace", typeof(IExplorerWorkspace), new(1, 0))],
    Views: [new("map-view", "地图", "web.main", "atlas.explorer", "workspace")]));
// 示例省略 ExplorerWorkspaceAdapter 的报告与分析依赖；完整接线见 AtlasRuntime。
```

以下情况必须拒绝：孤立父级或父级环；能力提供方是 class；同 ID 使用另一个接口；v2.0 提供方满足不了 v1.0 请求；两个活动提供方竞争同一能力；视图数据未声明或不可用；声明提供能力但未接入真实服务；必需依赖缺失或依赖环。仅缺少可选能力时允许启动并报告缺口。对应独立正反例位于 `tests/CodeAtlas.Regression/ModuleCases.cs`。

## C4 与决策

见 [C4 组件视图](c4/modules.md)、[LikeC4 目标模型](architecture/README.md)、[ADR-0001](adr/0001-startup-module-composition.md) 和 [PR #1 检视记录](reviews/pr-1.md)。`docs/architecture/*.c4` 是目标架构的来源；`scripts/sync-likec4.py` 把它投影成 `target-model.json`，并可用 `--diagrams` 导出 Web 上的 PNG。分析报告的 `Target` 字段保存读到的模型。对照结果是 AT122–AT127：没有模型是未知，不是通过。Web `/architecture` 以这些导出图为主视图，旁边列出漂移诊断，并链回代码地图。v0.2 不把“未发现错误”写成完整认证，也不对其他仓库自动发现、DLL 分发闭包、授权裁剪、热卸载或任意 UI 插件作承诺。
