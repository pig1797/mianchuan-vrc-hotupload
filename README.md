# VRC 热上传

作者：眠川

许可证：GNU AGPL-3.0（`AGPL-3.0-only`），完整条款见 [LICENSE](LICENSE)。

Windows 桌面工具，支持 VRCA 模型上传、VRCW 世界上传、PC / Quest 世界配对、文件夹扫描和模型批量下载。本地处理资源包，使用 VRChat 账号登录。

## 二次开发与署名

欢迎在遵守 AGPL-3.0 许可证的前提下进行二次开发、修改和再分发。请保留本项目原有的作者署名与许可声明，并在衍生项目的作者介绍、关于页面或 README 中清晰注明：**原作者：眠川**。

你可以同时署上自己的名字，并说明所做的修改；请勿将原项目冒充为完全独立原创，也不要让衍生版本看起来像是由原作者发布或背书。本说明用于明确署名与来源，不改变 AGPL-3.0 许可证授予的权利及规定的义务；第三方组件的署名与许可仍须保留。

## 快速开始

运行环境：Windows x64、.NET Framework 4.8，以及可访问 VRChat API 和文件服务的网络。无需安装 Unity，无需卡密或自建授权服务器。本项目不是 VRChat 官方产品。

1. 下载并解压完整目录，保留主程序旁的 DLL 和 `tools/`，不要单独移动 EXE。
2. 运行 `MianchuanUploader.exe`，从左下角“账号管理”登录 VRChat；需要验证时，输入邮箱或验证器提供的验证码，再点“验证并登录”。
3. 进入“模型上传”选择 `.vrca`，或进入“世界上传”选择 PC `.vrcw`；有 Quest 世界文件时一起选择。
4. 确认名称、封面及发布状态，点击“开始上传”，在“上传队列”查看最终结果。

完整步骤见 [使用说明](docs/使用说明.md)，包括更新世界、双端上传、签名文本、扫描匹配、批量下载、多账号和常见错误。

只处理你拥有或已获授权使用的资源。最终上传权限、大小限制和安全分析结果由 VRChat 决定；本工具不保证所有输入包均能通过。公开仓库地址：[pig1797/mianchuan-vrc-hotupload](https://github.com/pig1797/mianchuan-vrc-hotupload)。

## 构建

源码构建需要 .NET Framework 的 `csc.exe` 和 x64 MinGW-w64。将 `gcc.exe`、`g++.exe` 加入 PATH，在项目根目录运行：

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

已有 `tools/HotUploadNative.dll` 和 `tools/NativeLzma.dll` 时，可只编译 C# 部分：

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1 -ManagedOnly
```

构建前关闭本目录运行的程序，避免输出文件被占用。当前脚本编译本项目的 C#、CAB 和 LZMA 源码；其余第三方 DLL、类型库和 XZ 工具使用随仓库提供的依赖，不会从源码重建所有第三方组件。

## 文件

| 路径 | 内容 |
| --- | --- |
| `MianchuanUploader.exe` | 桌面入口 |
| `src/WorkspaceLayout.cs` | 页面布局和按钮入口 |
| `src/VrcaGui.cs` | 登录交互、上传队列、扫描和下载界面 |
| `src/VrcaCore.cs` | VRChat 请求、上传、文件校验和会话存储 |
| `src/HotswapTool.cs`、`native/` | 资源包处理及 CAB / LZMA 原生实现 |
| `tools/` | 处理程序、类型库和运行依赖 |
| `docs/`、`licenses/` | 操作说明、第三方许可文本 |
| `_runtime/` | 首次运行后产生的日志、默认封面和缓存，不提交到 Git |

账号会话独立存储在当前用户的 `%LOCALAPPDATA%\Mianchuan.VrcHotUpload`，使用 Windows 当前用户的 DPAPI 保护。不要分享该目录；公开日志前也应检查账号信息和本地路径。

## 许可

本项目维护的代码采用 GNU Affero General Public License 第 3 版。修改和分发时应遵守该许可证；通过网络向用户提供修改后的程序服务时，应按其第 13 条提供对应源码的获取方式。

第三方依赖保留各自的许可证和版权声明，见 [THIRD-PARTY.md](THIRD-PARTY.md)。
