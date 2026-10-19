# ADR-0005：用 LikeC4 作为目标模型格式

状态：接受。日期：2026-10-18。

背景：CodeAtlas 需要一份可版本管理的目标架构，用来对照源码模块、运行模块和声明的能力。手写第二套 C4 组件，或只在 Web 里画框，都会和模型漂移，而且不能给其他仓库复用。

决策：`docs/architecture` 下的 LikeC4 源文件是目标模型。`scripts/sync-likec4.py` 调用 `likec4 export json`，投影成 `target-model.json`。`--check` 只比较这份 JSON。Web 架构页嵌入 `likec4 export png` 生成的图，不另写 C4 渲染器；漂移诊断和代码地图链接留在图旁边。分析其他仓库时可以用 `--target` 或该仓库自己的 `docs/architecture/target-model.json` / `codeatlas.architecture.json`。

同一顶层模块内部的嵌套引用视为模块内部，不要求再写一条 `ModuleDependency`。跨顶层模块的类型引用和调用必须在模型里声明依赖。运行时依赖只连接到 `runtime: true` 的模块，避免把 `foundation`、`quality` 这类非运行模块拉进 Profile。

未选择：把 LikeC4 运行时嵌进 Blazor、用 PNG 字节作为跨机器的同步门禁、或在缺少模型时视为通过。PNG 由本机导出后提交；CI 校验 JSON。没有模型时发出 AT122，严重级别为不支持。

后果：改模型必须同时改运行模块目录或接受 AT123–AT127。图的布局以导出的 PNG 为准，页面只负责层级切换、漂移列表和证据链接。
