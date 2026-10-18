# 架构 0.2 第一轮验证记录

日期：2026-10-18，Windows x64，.NET SDK 10.0.400。独立分支 `codex/module-registry-v02`，起点内容与 PR #1 头提交 `07c2057` 完全一致；未合并 PR #1。

## 实际改造

- 新增 ModuleDescriptor、ModuleRegistry、ProfileResolver，支持显式嵌套、模块依赖、接口能力和同主版本兼容。
- atlas.explore 的接线回调真实注册工作区；Web 贡献真实构造既有 Explorer。web 与 headless 的服务/视图不同。
- 原有 ExpandableMap / BranchCard 读取 ResolvedProfile，实现运行模块的嵌套、选择、能力与视图详情；原有源码视图、ReportIndex、重新分析和 PR #1 规范/分工页面保留。
- 原有 AddAtlas API 与 singleton 语义保持，旧报告不添加运行模块字段。
- 修复自回归写入固定 self.json 的问题，将回归输出放入每次运行的临时目录。

## 可复现验证

```powershell
./scripts/verify-modules.ps1 -Browser -SkipNuGetAudit -Port 5298 -HeadlessPort 5299
```

`-SkipNuGetAudit` 仅因本机在线漏洞源不可达而使用，不表示完成漏洞审计。需要 Python、.NET SDK 和 Playwright/可执行浏览器；本机通过 PLAYWRIGHT_MODULE 和 BROWSER_EXECUTABLE 使用已有安装。

| 检查 | 结果 |
|---|---|
| dotnet build CodeAtlas.slnx -c Release -m:1 | 全部 16 个项目成功，0 警告、0 错误 |
| 核心回归 | 178 项通过，含 40 项新增模块/兼容检查 |
| 桌面状态回归 | 60 项通过，含 24,120 符号夹具 |
| 新增模块 Web 交互 | 17 项通过 |
| 原嵌套结构 | 13 项通过 |
| 原关系图/状态 | 16 项通过 |
| PR #1 规范、分工与证据 | 15 项通过 |
| 原浏览器综合回归 | 19 项通过 |
| Markdown 与应用资源同步 | 通过 |
| 修复后的完整运行：原有 5 份报告哈希 | 全部保持该轮开始时的字节 |

模块浏览器检查包含：真实嵌套历史子模块、能力与视图详情、源码与运行模式切换保留分支、既有图形渲染、无伪造源码关系、真实重新分析、实际 LaunchDesk 阅读报告、1200px 窗口，以及 headless 不构造探索组件且禁用页面返回 404。

原始报告保护的边界：本轮较早运行原有回归时 self.json 已被重新生成，未恢复其会话开始时的字节。desktop-self、launchdesk-raw、launchdesk-reading、z-launchdesk 四份历史报告与最早记录的哈希一致。修复后已为 5 份报告留存验证前副本，并验证完整回归不再覆盖它们。

日志在 artifacts/module-verification/，截图与浏览器 JSON 在 artifacts/。该目录不提交到 Git，交付包另附截图和精简证据。

## 已知限制

自分析仍有未支持项与说明缺口；错误为 0 不等于完整通过。启动 Profile 只证明服务/视图的组合，不证明 DLL 已从交付包移除。未实现跨进程插件、任意控件加载、SemVer 预发布/范围协商、授权分发闭包或运行时热卸载。导航、历史、索引属于共享实现中的逻辑子模块，尚非各自可物理卸载的插件。

WPF 项目已构建并通过非窗口回归，本轮只进行了真实 Web 界面的交互验证。运行模块工作台当前需要先有可加载的报告。

GitHub 连接器创建独立分支返回 403 `Resource not accessible by integration`。随后在用户明确授权写入后，以正常网络权限验证本机 GitHub CLI：SoloLevelingAI 登录有效，仓库权限为 ADMIN；受限环境中的“登录失效”提示不足以判断实际凭据。已通过 Git/gh 推送独立分支，并创建 [草稿 PR #2](https://github.com/SoloLevelingAI/CodeAtlas/pull/2)，基于 PR #1 分支，仅审阅本轮模块改造。原提交快照保留在 codex/module-registry-v02-local-snapshot，正式分支接到 PR #1 的真实历史上，接入前后代码 tree 完全一致。合并仍需展示具体 PR 与检查结果并经用户确认；未合并任何 PR。原增量和完整 patch 继续可用。
