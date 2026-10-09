# 无限画布 · Windows 桌面客户端

用 WinUI 3 原生重写的无限画布客户端：亚克力界面、Win2D 画布，五家服务商（OpenAI、Google Gemini、火山方舟、xAI、千问）全部按官方原生协议直连。架构与进度见 [桌面客户端规划](../docs/content/docs/progress/desktop-client-plan.zh-CN.mdx)。

## 环境

- Windows 10 1809 及以上（推荐 Windows 11）
- .NET 10 SDK
- 首次编译会从 NuGet 下载 Windows App SDK、Win2D 等依赖

## 编译与运行

```bash
dotnet build desktop/InfiniteCanvas.sln
```

```bash
desktop/src/InfiniteCanvas.App/bin/x64/Debug/net10.0-windows10.0.19041.0/win-x64/InfiniteCanvas.exe
```

程序为非打包自包含形式，不需要安装 Windows App SDK 运行时。也可以用支持 .NET 10 的 Visual Studio 2026 打开 `desktop/InfiniteCanvas.sln`，选择 x64 直接运行。

运行测试：

```bash
dotnet test desktop/tests/InfiniteCanvas.Tests
```

## 目录

| 目录 | 内容 |
| --- | --- |
| `src/InfiniteCanvas.Core` | 画布模型、连线规则、撤销历史、项目与设置存储、模型家族能力表 |
| `src/InfiniteCanvas.Providers` | 五家服务商原生适配 |
| `src/InfiniteCanvas.Agents` | 本机 Agent 接入：ACP 客户端、画布 MCP 工具与命名管道服务 |
| `src/InfiniteCanvas.App` | WinUI 3 界面：`Pages`（画布列表、画布、设置）、`Board`（画布引擎、节点、生成面板、生成调度） |
| `tests/InfiniteCanvas.Tests` | 核心规则与请求 / 响应解析测试 |

## 数据与安全

- 画布、媒体文件和设置保存在 `%LocalAppData%\InfiniteCanvas`，不会上传到任何服务器；删除的画布进入回收站。
- API Key 用 Windows DPAPI 按当前用户加密保存，生成请求由客户端直接发往服务商，走系统代理设置。
- 未处理异常记录在 `%LocalAppData%\InfiniteCanvas\crash.log`。
- 提示词库保存在 `%LocalAppData%\InfiniteCanvas\prompts.json`，只在本机。这个版本不包含视频生成，视频节点只用来放导入的视频；视频生成保存在 `video-models` 分支。
- 画布右侧的「助手」通过 ACP 启动本机 Agent（默认 Claude Code、Kimi Code、Grok Build、DeepSeek Harness，命令可在设置里改），Agent 的工作目录是 `%LocalAppData%\InfiniteCanvas\agent\<画布 ID>`；画布工具只对这次会话开放，走本机命名管道，不开网络端口。客户端外的 Agent（如 Claude Code）可把 `InfiniteCanvas.exe --mcp-bridge` 登记为 MCP 服务器，操作当前打开的画布（设置里「允许外部 Agent 连接」，默认开启）；所有画布管道只允许当前 Windows 用户连接。Agent 自身的登录、模型与计费由各家 Agent 负责。
- 本地放大使用 `%LocalAppData%\InfiniteCanvas\tools\realesrgan` 下的 [Real-ESRGAN-ncnn-vulkan](https://github.com/xinntao/Real-ESRGAN/releases)（`realesrgan-ncnn-vulkan.exe` 与 `models` 文件夹），可额外放入 [upscayl](https://github.com/upscayl/upscayl/tree/main/resources/models) 的 `high-fidelity-4x`、`ultrasharp-4x` 模型（社区模型，仅限非商用）。在本机显卡上运行，图片不上传。
