# PR #1 检视与本轮基线

检视对象：[PR #1](https://github.com/SoloLevelingAI/CodeAtlas/pull/1)，头提交 `07c2057df673f9fa58f18e369a01c589a6eafb24`，基线 `f84b75503dd8442946023d6ed1da6b04c7821a70`。检视时间：2026-10-18。

直接 Git fetch 无法连接 GitHub；GitHub 连接器可读，取得 PR 的 38 个文件补丁。导入后的本地 Git tree 为 `c3a6560764c689c481e3da36e8686139466cb00a`，与 GitHub 头提交 tree 完全一致。保留 PR 的规范与分工页、诊断规则、回归和旧报告，本轮在独立分支继续；未合并 PR。

后续授权更新：用户允许推送和创建 PR，具体合并前确认。正常网络权限下的本机 GitHub 登录和 Git fetch 已验证可用；已保留本地导入快照，将正式分支接到 PR #1 的真实头提交，并创建 [PR #2](https://github.com/SoloLevelingAI/CodeAtlas/pull/2)。连接器 403 与本机 Git/gh 授权是两条不同通道，未更改连接器权限或放宽全局授权设置。

## 与模块改造目标有关的缺口

1. PR 的 `AT111` 校验源码默认功能 ID 是否有 AtlasFeature，不是启动模块注册。没有 ModuleDescriptor / ModuleRegistry / ProfileResolver，因此不能据此认定模块已经可组合。本轮增加独立运行协议，旧属性含义保持原样。
2. PR 的静态 `AT109` 按整个可见源码图聚合不同提供方，无法证明条件分支或不同 Profile 的运行选择。不能把静态图当作 DI 容器。本轮没有增加互斥的内置替代提供方；独立 Profile 反例验证实际选择、版本和重复接线。
3. 已有 ExpandableMap 使用报告中的 FeatureDefinition；规范与分工页面没有读取启动注册信息。本轮增加运行模块视角，从真实 ResolvedProfile 读取嵌套层级及能力/视图声明，而非交付另一个静态 HTML 页面。
4. PR 的功能可运行并不证明完整分发、性能或热卸载。本轮仍明确列出这些限制，启动检查和浏览器验证分别记录。
5. 原 `Regression.SelfCases` 会固定写入 `artifacts/reports/self.json`。本轮保护检查发现执行回归会刷新用户已有的自分析报告；已改为每次回归专属临时目录。验证脚本先备份并逐份校验历史报告，Web 重新分析也只操作验证副本。本轮较早运行已刷新 self.json，未声称恢复其原始字节；其他历史报告保留原样。

PR 中已经声明的 `ArchitectureGuide.ReadMarkdown`、WPF `MainWindow.Capture` 交互层外部读写警告保留。Web 的 scoped ViewModel 注册仍是宿主组装；未把原警告隐藏成通过。最终构建、回归数量和 Web 截图以本轮交付的验证记录为准。
