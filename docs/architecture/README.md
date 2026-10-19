# LikeC4 目标架构

`specification.c4`、`model.c4` 和 `views.c4` 是 CodeAtlas 的目标模型来源。标识符不含点；跨文件引用使用完整限定名。`metadata.atlasId` 对应源码里的 `AtlasFeature` / 运行模块 ID。`runtime` 为 `true` 的模块必须与 Web Profile 解析出的 `ModuleDescriptor` 一致。`provides`、`requires` 写成 `id|契约全名|版本`，可选需求在末尾加 `|true`。`implementations` 列出拥有该 `AtlasProvides` 的类型全名。

同一顶层模块内部的嵌套引用不另记跨模块依赖。跨顶层模块的类型引用或调用必须有 `dependency`。`foundation`、`quality`、`runtime` 不是运行时 `ModuleDependency`，只写在模型里。

同步：

```bash
cd docs/architecture && npm ci
python scripts/sync-likec4.py          # 生成 target-model.json
python scripts/sync-likec4.py --check  # 只比较 JSON
python scripts/sync-likec4.py --diagrams  # 另外导出 PNG 到 Host/wwwroot/architecture
```

分析时 `ITargetModelSource` 读取显式 `--target`，否则读取本文件旁的 `target-model.json`，再否则读取仓库根的 `codeatlas.architecture.json`。读不到时报告 `Target` 为空，并产生 AT122。Web `/architecture` 嵌入导出的 PNG，旁边保留漂移列表和代码地图链接。
