# 修改记录

本清单所述维护工作由本人修改（仓库维护者 @HaipaKabuto），日期 2026-11-10。原版权和许可保留，准确 MCP 上游基线提交未知。

- v12：修复 inch 单位参数及 UTF8 草图名传递与原生 Name 读回。
- 修复旧标题栏 success 但没有可见表格，使用原生可编辑7×4表；增加 auto-size-text 防止长文字 ####。仍要求明确打开 Drafting sheet。
- 保全包含空格的注释引用，新增绑定会话/部件/tag 的唯一 ANNTAG 选择器并拒绝歧义。
- 显式 NX12 STEP214Importer，新派生文件拒覆盖，验证实体数/范围/体积/重心及重开。
- 中心标记增加原生 tag 诊断，不把重复 JournalIdentifier 误当图形错误。
- 本次公开整理：参数化 NX_HOME/工作区与本地状态目录，独立 Python 环境、三源本机 SDK 编译、子进程环境隔离、脱敏的77项 schema/验收范围与引用信息。
- 增加 `mcp/run_stdio.py` 直接 Python stdio 入口。安装器生成的本地 MCP 配置使用虚拟环境 Python、该入口和 `--config <LOCAL_INSTALL_JSON>`；`-StateDirectory` 可更换状态目录，PowerShell MCP包装保留为可选方式。
- 本次环境 Windows PowerShell 5 执行策略禁止脚本，实际安装和启动测试使用现有 PowerShell 7；未修改策略或使用 Bypass，不再推荐把 `powershell.exe` 作为宿主 stdio 命令。
- 移除 STEP 安装根解析中的本机固定路径回退，改读 `NX_HOME` / `UGII_BASE_DIR`，缺失或不匹配时显式失败。此项与直接 stdio 入口属于可移植配置修正，没有改变几何算法。
- 公开副本已实际完成新虚拟环境安装、NX12 SDK三源编译、全新NX12.0.2.9会话、stdio发现77项工具和20×30×10 mm单实体6000 mm³保存关闭重开最小验收。实际pip使用现有清华公开镜像，并另核对官方PyPI版本；此项不代表重新运行全部77工具几何回归。脱敏记录见 `docs/publication-checks.json`。
- 公开副本将 mcp/pyproject.toml 的错误 MIT 标签改为 GPL-3.0-only，保留上游 MIT 全文。源码添加维护修改日期说明；不改变 NX 建模逻辑。
- 独立 nxopen-journal Skill 保持固定来源内容；没有把其上游53个用例声称为本机整套实测。
