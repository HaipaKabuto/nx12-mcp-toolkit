# NX12 Automation Toolkit：MCP 与 Journal Skill

本仓库标明的 NX12 MCP 适配、修复和发布整理 **由本人修改（仓库维护者：[@HaipaKabuto](https://github.com/HaipaKabuto)）**。上游组件保留各自版权；随包 Journal Skill 保持原作者内容，不将其宣称为本人原创。

在已有合法 Siemens NX12 环境中，用 MCP 访问常驻 NXOpen C# Loader，并配套独立的 NXOpen Python journal 技能。该仓库整理了 NX12 适配源码、可移植部署脚本、77 项工具的参数定义及限定范围的实测摘要。

**不包含 Siemens NX、SDK、许可证、NXOpen 运行库、Python 虚拟环境或个人工程。** 使用者需要自行提供已授权的 NX12 安装。项目不是 Siemens 官方产品，也不声称经过 Siemens 认证。

## 下载与部署

- 推荐下载 [GitHub Releases 中的版本化发布包](https://github.com/HaipaKabuto/nx12-mcp-toolkit/releases/latest)，并核对同一版本附带的 `SHA256SUMS.txt`。
- 也可克隆本仓库。随包 `SOURCE-MANIFEST.sha256` 校验的是命名发布 ZIP 内的原始文件；上游 Skill 的 3 个 `.bat` 在 Git 中规范化为 LF、检出时按其 `.gitattributes` 恢复 CRLF。GitHub 自动生成的 “Source code” ZIP 不等同于本项目上传的、带校验和的发布包。
- 安装步骤、离线依赖方式和恢复方法见 [部署说明](docs/INSTALL.md)。

在解压后的仓库根目录，使用已经允许执行脚本的 PowerShell 环境；将参数换成自己的 NX12 安装根和新建工作区。本次公开副本安装测试使用现有 PowerShell 7：

```powershell
.\scripts\Install.ps1 -NxHome '<NX12_INSTALL_ROOT>' -Workspace '<NEW_WORKSPACE>'
.\scripts\Start-NX12.ps1
```

安装后按本地生成的 `data/mcp-config.local.json` 配置宿主：stdio 直接使用独立虚拟环境的 Python 启动 `mcp/run_stdio.py --config data/install.json`，不依赖宿主调用 PowerShell 脚本。`-StateDirectory` 可更换本地状态目录；`scripts/Start-MCP.ps1` 保留为可选包装。安装器不自动改宿主配置、不写全局 NX 环境变量、不修改许可证，也不自动关闭已有 NX。完整命令及配置样例见 [INSTALL](docs/INSTALL.md)。

本次测试环境的 Windows PowerShell 5 执行策略禁止运行 `.ps1`；未修改策略或使用 Bypass。安装与 NX 启动脚本应在使用者已有且允许运行脚本的环境中执行。

> 首次请只使用独立测试零件。编译成功、发现 77 项工具或返回 success 都不能替代几何与保存重开验收。

## 两个组件

|组件|用途|执行方式|
|---|---|---|
|`mcp/`|77 项工具：零件、草图、实体、几何查询、装配、工程图、交换格式及明确尺寸建模计划|Python MCP sidecar → 本机命名管道 → NX 进程中的 C# Loader → NXOpen|
|`skills/nxopen-journal/`|生成、检查及批处理回放 NX12 Python journal；保留上游技能内容|技能文档与脚本，journal 在独立 NX 批处理进程中执行；不等于 MCP 工具|

MCP 适配修订号 **v12** 与 Siemens 产品版本 **NX12** 是两件事。MCP 的本地验收软件版本是 **12.0.2.9**；Journal Skill 上游说明的验证版本是 **12.0.0.27**。本项目未将后者的整套回归伪装成已经在前者重测。

## 工具与实测范围

[77 项工具索引](docs/TOOLS.md) 提供必填参数和类型；[实际 schema JSON](docs/tool-catalog.json) 可供客户端与开发者复用。

本地修订 v12 的代表用例验收为 **77 PASS / 0 FAIL / 0 SKIP / 0 UNSUPPORTED**。每项都有真实调用和必要的语义或视觉判据；此结论只覆盖记录的样例与参数。完整范围、初次失败及修复说明见 [验收摘要](docs/VALIDATION.md)。公开发布包的安装过程仍需在下载者自己的 NX 与依赖环境中验证。

另已针对公开副本实际完成：新虚拟环境安装、本机 NX12 SDK 三源编译、全新 NX12.0.2.9 会话、Python stdio 入口发现 77 项工具，以及 20×30×10 mm 单实体建模、保存关闭重开后体积 6000 mm³ 的最小验收。脱敏结果见 [publication-checks.json](docs/publication-checks.json)；这不是重新执行全部 77 项几何回归，也不保证每台机器均可直接部署。此次 pip 安装沿用现有清华公开镜像配置，另行核对了官方 PyPI 版本元数据。

已检查的综合流程包括：

- 复杂单实体：草图、拉伸、旋转、通孔、R2 圆角、C1 倒角、阵列与并集；保存重开后体积与包围范围一致。
- 三组件装配：6 个持久约束 Solved，轴保留 1 个旋转自由度；另有替换、子装配、定位和已知干涉场景。
- A3 工程图：三视图和轴测、尺寸及双向公差、原生粗糙度与形位注释、标题和技术要求；实际导出 PDF 并检查页面。
- AP214 STEP 导出、导入新 PRT、保存重开，核对实体数、体积、包围范围及重心。
- 重复只读查询与 5 轮保存/关闭/重开，以及独立部署后的最小建模复核。

## 使用前须知

- BOM 为从实际组件树生成的可编辑快照，序号引线为静态图纸点；未提供关联式 BOM 自动更新。
- DWG 验收是实际导出及文件头检查；DXF 做了实体结构解析。未声称外部 CAD 往返和显示均通过。
- 扫掠没有暴露为当前 MCP 工具；这不表示 NX12 SDK 不支持扫掠。也未实现任意已保存草图的通用跨会话编辑。
- 图片计划依赖调用者明确给出的尺寸，不执行独立 OCR；不能把猜测当成图纸要求。
- 曲面不能推广为单一全局法线；重心查询为均匀体积重心。材料标题字符串不等于物理材料密度。
- GB/T 参照图框不是完整国标认证。原生 GDT/粗糙度符号尚未证明关联到特定面。
- Journal Skill 本次只完成静态安装检查。其完整回归含已知崩溃探针和求解器调用，不在安装时自动运行。

## 来源、许可证与致谢

保留并感谢以下上游项目及原作者：

- [@kongbaic](https://github.com/kongbaic) — [NX_MCP-Enhanced](https://github.com/kongbaic/NX_MCP-Enhanced)。
- [@DreamEnding](https://github.com/DreamEnding) — [NX_MCP](https://github.com/DreamEnding/NX_MCP)。
- [@EVOL233awa](https://github.com/EVOL233awa) — [nxopen-journal](https://github.com/EVOL233awa/nxopen-journal)，技能版本 1.5.0，固定来源提交 `c0b1daf8a0c4c646f9e59788557c8cf3fd1cd689`。
- [@Cai-aa](https://github.com/Cai-aa) — [CAD-Agent-Hub](https://github.com/Cai-aa/CAD-Agent-Hub)，参考来源；具体来源关系按 ATTRIBUTION 中核实的范围记录。

这是社区适配与整理，署名不代表原作者审阅、支持或为本仓库背书。各组件按其适用许可证发布并保留版权；详见 [来源与修改说明](docs/ATTRIBUTION.md) 和 [许可证矩阵](docs/LICENSE_MATRIX.md)。Siemens 软件和 SDK 不因本仓库开源而被重新许可。

## 如何引用

请引用所用发布标签或提交，并同时注明实际使用的上游组件。发布后的 `CITATION.cff` 可用于 GitHub 的 “Cite this repository”；本项目没有虚构 DOI。建议的文字形式：

> HaipaKabuto. NX12 Automation Toolkit: MCP and Journal Skill. v1.0.0. https://github.com/HaipaKabuto/nx12-mcp-toolkit

缺陷报告请包含 NX 完整版本、工具名、最小参数及已脱敏日志。不要上传许可证文件、API 密钥、学校或公司工程、完整用户配置。
