# 部署与恢复

## 前置条件

|项目|要求|
|---|---|
|系统|Windows x64；使用者可正常启动自己的 NX12|
|NX|自备合法安装及相应功能许可证；MCP 本地验收为 NX12.0.2.9|
|NXOpen SDK|用户本机安装提供的 managed 程序集；仓库不分发它们|
|C# 编译器|.NET Framework 4.x `csc.exe`，安装脚本仅查找/使用，不升级或安装系统组件|
|PowerShell|安装及 NX 启动使用已有且允许运行脚本的环境；本次实际安装测试为 PowerShell 7|
|宿主 Python|建议使用已验收的 Python 3.12 x64；MCP 依赖独立于 NX 内嵌解释器|
|第三方 Python 包|实测固定版本 mcp 2.3.0、pydantic 2.14.2、pywin32 312；发布准备已核对这些版本的PyPI元数据，仍需核对所用包源及平台wheel|

公开发布包不包含虚拟环境、Siemens DLL、Loader 二进制、许可证或任何预先激活的软件。Loader 由本机 SDK 和三个 C# 源文件构建。不要将较新 NX 的 SDK DLL 复制到 NX12 安装中。

## 1. 取得固定版本

从 [GitHub Releases](https://github.com/HaipaKabuto/nx12-mcp-toolkit/releases/latest) 下载发布包与同版 `SHA256SUMS.txt`。在 PowerShell 用 `Get-FileHash -Algorithm SHA256 <zip>` 核对。解压到可写目录；路径可以含空格。使用标签/提交可复现所用源码，main 分支会变化。

请选 `nx12-mcp-toolkit-v1.0.0-source.zip`，而非 GitHub 自动生成的 “Source code” ZIP。包内 `SOURCE-MANIFEST.sha256` 记录发布包各文件的原始字节，排除清单自身。上游 Skill 的 3 个 `.bat` 保留 Windows 所需的 CRLF；Git 按上游 `.gitattributes` 用 LF 存储，正常检出时恢复 CRLF，因此原始 Git blob/自动源码 ZIP 的这些文件哈希可能与发布包不同。这是换行规则，不是脚本逻辑修改。

## 2. 本地安装与编译

从仓库根执行；两个占位值必须替换：

```powershell
.\scripts\Install.ps1 -NxHome '<NX12_INSTALL_ROOT>' -Workspace '<NEW_WORKSPACE>'
```

安装器创建仓库内的 `data/venv` 和 `data/install.json`，安装依赖并调用本地 Loader 构建。`-Wheelhouse <LOCAL_WHEEL_DIRECTORY>` 用于准备好兼容 wheel 的离线环境；它不会让缺少的依赖自动变成可用。可用 `-StateDirectory` 更换本地状态目录，Start脚本可用 `-ConfigPath` 读取指定配置。精确参数以脚本的帮助和实际校验为准。若实测版本不在所配置包源中，保留失败并准备合适的 wheel，不要悄悄换版本或把未运行的安装当成功。

安装器沿用使用者已有的 pip 包源配置。本次发布前新虚拟环境测试实际使用现有的清华大学公开 PyPI 镜像；另行核对了官方 PyPI 元数据中这些固定版本存在。此处没有把直连官方源安装或任意包源下载成功写成已测事实。

本次测试环境的 Windows PowerShell 5 执行策略禁止运行 `.ps1`，因此安装与启动脚本在已有的 PowerShell 7 中执行。没有修改执行策略、使用 Bypass 或关闭安全防护。使用者也应选择自己已有且允许执行脚本的环境；下面的 MCP stdio 入口直接用 Python，不要求 MCP 宿主运行 `.ps1`。

单独重建 Loader 可使用 `scripts/Build-Loader.ps1`。构建输出使用新目录，保留上一次结果。若 SDK 或编译器缺失，先报告问题；不要升级 NX、改许可证、关闭防护或把不匹配的 SDK 替入安装目录。

## 3. 启动 NX12

```powershell
.\scripts\Start-NX12.ps1
```

启动器读取安装配置，只给新进程设置 NX12 安装根、项目内用户 startup 目录、工作区和必要 PATH。Qt 插件路径也只在该子进程中清理。它不写机器/用户全局环境，不强制终止已有 NX。检测到现存 `ugraf` 时应先核对其部件和保存状态，再由使用者正常退出；不要盲目结束课程或生产会话。

## 4. 配置 MCP stdio

安装器将实际本地路径写入 `data/mcp-config.local.json`，优先按该文件配置宿主；该文件属于被忽略的本地状态，不应上传。默认 `data` 目录的通用 MCP JSON 结构如下；将占位路径换成解压后的仓库路径。使用 `-StateDirectory` 时，虚拟环境、安装 JSON 和配置样例都位于所选状态目录中，按实际生成文件填写：

```json
{
  "mcpServers": {
    "nx12": {
      "command": "<REPO_PATH>\\data\\venv\\Scripts\\python.exe",
      "args": ["<REPO_PATH>\\mcp\\run_stdio.py", "--config", "<REPO_PATH>\\data\\install.json"],
      "cwd": "<REPO_PATH>"
    }
  }
}
```

`mcp/run_stdio.py` 读取安装 JSON、设置 `mcp/src` 为 Python 源路径和本次 NX12/工作区环境。它启用已验收的自动化工具，关闭 experimental/journal 入口。该 Python 入口已完成真实 stdio 与 NX12 最小建模测试。`scripts/Start-MCP.ps1` 只是可选包装；不再推荐把 `powershell.exe` 作为宿主 stdio 命令。stdout 专用于 stdio 协议；不要把欢迎文字或诊断日志混入 MCP 输出。

重新加载宿主的该服务后，通过标准 `initialize`、`tools/list` 核对 77 项工具。工具名与参数见 [TOOLS](TOOLS.md)。`nx_status` 的 `nx_version: v12` 只说明 NX 产品版本，不能单独证明当前 Loader 就是某个适配修订；同时核对本次启动目录和构建记录。

## 5. 第一次实际核验

只在新工作区创建独立测试零件，顺序如下。对象 ID 必须取实际返回值，不能把示例 ID 当永远有效：

1. `nx_status`：确认连接对象是预期 NX12。
2. `nx_create_part`：相对路径 `smoke/box.prt`、`units: mm`。
3. `nx_create_sketch`：XY；从结果获取 sketch ID。
4. `nx_sketch_rectangle`：角点 `(0,0)` 和 `(20,30)`。
5. `nx_finish_sketch`，再 `nx_extrude(distance=10)`。
6. `nx_list_faces`/`nx_list_bodies`：应为 1 实体、体积 6000 mm³、包围范围 `[0,0,0]` 至 `[20,30,10]`。
7. 保存、关闭、相对路径重新打开，再核对实体数、体积、范围及特征。

这个最小用例成功不代表全部功能已经在你的电脑上验收。涉及工程图、装配或交换格式，应再核对相应许可证、几何/求解状态以及实际文件内容。

路径参数只接受工作区相对路径。`nx_status` 返回的原生绝对部件路径，在回传 `nx_open_part` 前必须先确认位于工作区并转成相对路径；不要为绕过这一规则放宽路径边界。

公开副本已在一个环境中实际完成新虚拟环境安装、三源编译、新 NX12.0.2.9 会话和上述独立方块保存重开，见 [发布前检查结果](publication-checks.json)。它证明该次公开入口的最小流程，不代表重新执行全 77 项几何回归或所有电脑部署成功。

STEP 相关安装根解析已移除维护者机器的固定路径回退，改为读取 `NX_HOME` / `UGII_BASE_DIR`；缺失或不满足安装要求时显式报错，不猜测其他 NX 位置。这属于可移植配置修正，没有改变几何算法。

## Journal Skill 的单独安装

把 `skills/nxopen-journal` 放到宿主支持的技能目录，或建立指向该目录的链接。技能的 `SKILL.md` 是入口；它不需要注册成 MCP server。静态验证：

```powershell
python .\skills\nxopen-journal\scripts\check_repo.py .\skills\nxopen-journal
python .\skills\nxopen-journal\scripts\check_journal.py .\skills\nxopen-journal\scripts\tests\01_bracket.py
```

执行 journal 前，为该次进程明确设置 `NX_HOME` 为自己的 NX12 安装根，不使用上游示例默认安装路径。Journal 使用 NX 内嵌 Python，与宿主 MCP Python 解释器不同。技能批处理启动独立 NX，不注入当前 GUI 会话。完整 `RUN_TESTS.bat` 包含求解器调用及已知崩溃探针，未在本次技能安装时运行，也不应作为安装器的隐式步骤。

## 故障与恢复

|现象|检查与处理|
|---|---|
|无法连接管道|确认 NX12、同一普通用户、本次 startup 目录和 Loader 构建一致；检查是否旧 Loader 占用；不要开放 Everyone ACL 或改系统安全服务|
|超时/模态窗口|先只读核对 NX 窗口及当前对象状态；结果未知的建模命令不要重复发送|
|保存或重开失败|保留原始错误、部分产物和源文件；使用新输出名，不自动覆盖|
|标题栏没有当前图纸|显式打开正确的 Drafting sheet；本地复测使用 `nx_export_pdf` 的精确 `sheet_name` 建立上下文后再建标题栏|
|STEP 派生文件已存在|保留它，改用新测试输出名；不覆盖旧 `__imported.prt`|
|重启后旧 ID 失效|重新查询对象。草图缓存、native Tag、注释 selector 都不能被当作跨会话永久句柄|
|依赖安装失败|记录包版本和包源，使用兼容 wheel；不要用编译成功替代启动与实测|

更新前保存本仓库版本、安装配置及独立工作区备份。失败时保留失败版本，正常退出自己的测试会话，再切回此前已验证标签并重新构建；不覆盖课程工程。源码和 ZIP 哈希核验不是已经执行恢复演练的证明。
