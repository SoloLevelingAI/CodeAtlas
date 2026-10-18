# LaunchDesk 真实源码阅读样本

从本地 LaunchDesk Git checkout 的指定提交提取源码到 `artifacts/examples`，不修改该 checkout，不运行目标代码，不联网推送。

```powershell
python examples/launchdesk/build-example.py --repository C:/Your/LaunchDesk --commit 890cc463106358f8643592d99ddd14601fb09593
./scripts/start-desktop.ps1 -Report ./artifacts/reports/launchdesk-reading.json
./CodeAtlas.cmd assess --report ./artifacts/reports/launchdesk-reading.json
```

先运行 `scripts/verify-desktop.ps1` 或构建 `artifacts/validation`，以生成示例脚本调用的 CLI。脚本需要 Python 3.9+ 和 Git。

`reading-map.json` 是人工阅读清单：每个选择器必须唯一匹配真实符号；方法归属可覆盖类型归属。清单补充用途时明确标记人工说明，不冒充原始 XML 注释。派生报告完整保留原报告的源码、行号、静态关系、原始诊断和覆盖统计。重复运行生成确定的映射，但分析时间会更新。

输出：`launchdesk-raw.json` 原始分析、`launchdesk-reading.json` 阅读分组、`launchdesk-provenance.json` 提交和逐文件哈希、`launchdesk-mapping-evidence.json` 逐项匹配证据。所有生成内容均在 `artifacts/`，不会随源码默认提交。

本例不是通用自动分类器，不是已完成的 LaunchDesk 重构，也不验证私有许可或 AI 模块的分发隔离。实际整理方法见 [架构规范](../../docs/architecture-v0.1.md)。
