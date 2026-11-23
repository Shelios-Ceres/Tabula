# AGENTS.md

本文档用于约束本项目中的 AI / 自动化开发行为。开发时优先遵循本文件，其次遵循用户当前消息。

## 基本原则

- 先读现有代码，再动手修改，优先沿用项目已有结构和写法。
- 写代码保持最少行数，能简单实现就不要引入复杂抽象。
- 标准格式、协议、解析、压缩、加密、日期等通用能力优先使用成熟稳定的库，不要手写底层实现，除非用户明确要求或项目已有实现必须沿用。
- 不要为了“兼容更多场景”写大量分支，只实现当前明确需要的功能。
- 项目尚未上线，不需要兼容旧数据；本地存储结构调整时直接按新设计修改，不写旧字段兼容或数据迁移兜底，除非用户明确要求。
- 每次写完代码，不需要检查语法，不需要执行构建，用户会自己做。
- 不要改无关文件，不要顺手重构。
- 如果工作区已有用户改动，不要回滚，不要覆盖；只在必要范围内追加修改。

## 反复提醒沉淀

- 如果开发过程中总是遇到某个问题，或者用户反复提醒同一个注意事项，需要把该注意事项补充到本文件。
- 补充时写成明确、可执行的规则，避免只写模糊描述。
- 新规则应放到最相关的章节；找不到合适章节时放到“项目注意事项”。

## 桌面客户端核心红线

- `desktop/` 是 WinUI 3 原生客户端（.NET 10、Windows App SDK、Win2D），也是项目唯一的客户端；网页版及只为它服务的 canvas-agent、canvas-proxy、网页节点插件和部署配置已移除，需要参考旧实现时查 git 历史，不要再加回网页端代码。
- 分层固定：`Tabula.Core` 只放画布模型、规则、存储与模型家族，不引用 WinUI；`Tabula.Providers` 只放服务商 HTTP 适配；`Tabula.Agents` 只放 ACP 客户端与画布 MCP 工具；界面在 `Tabula.App` 的 `Pages/`（页面）与 `Board/`（画布引擎、节点、生成面板、生成调度）。画布相关命名空间用 `Board`，不要再建名为 `Canvas` 的命名空间，避免遮住 WinUI 的 `Canvas` 类型。
- App 项目目标框架是 `net10.0-windows10.0.22621.0`（ComputeSharp.D2D1.WinUI 3.2.0 需要 22621 SDK；最低运行版本仍是 `TargetPlatformMinVersion` 10.0.17763），并开启 `AllowUnsafeBlocks`。着色器写成 `Board/Glass/` 里的 `ID2D1PixelShader` 结构体，编译期由 ComputeSharp 生成；HLSL 里不要在采样之外提前 `return`（FXC 会报 X4000），只保留一个出口。
- 颜色用 `ThemeResource`，Win2D 绘制颜色随 `ActualTheme` 切换，不硬编码浅色或深色。
- 只有图标的按钮必须设置 `AutomationProperties.Name`（与 ToolTip 一致），保证读屏和 UI 自动化能找到。
- C# 内插原始字符串里的 JSON 只要出现连续的 `}}`（如 `...}}"""`），开头必须用 `$$$` 并把插值写成 `{{{x}}}`；`$$` 会编译失败（CS9007）。写完测试要实际编译一次。
- WinUI 3 的 `RoutedEventArgs` 没有 `Handled`，需要拦截事件时使用具体参数类型（如 `PointerRoutedEventArgs`）。
- 代码里创建多行 `TextBox` 时，对象初始化器中先写 `AcceptsReturn = true` 再写 `Text`；顺序反了时单行模式会把第一个换行之后的内容直接丢掉。
- 测试实例的界面自动化只用 UI Automation 的 Invoke / SelectionItem / Value，或用 `--mcp-bridge` 走测试实例自己的管道；不要用鼠标坐标点击，测试窗口不在前台时会点到用户的其他窗口。UI Automation 驱动不了的键盘和鼠标操作（Enter / Esc / 输入法、拖动、框选、右键平移等）可以用 computer use 在测试实例上实测（用户 2026-11-19 同意）：每次操作前截图确认前台窗口就是测试实例（标题栏和进程路径），只操作测试实例，不碰用户自己的客户端和其他窗口。
- 本机验证会在 `%LocalAppData%\InfiniteCanvas` 写入画布和设置；用户正在使用客户端时，不要关闭它或改动它的数据：把编译输出指到临时目录（`-p:BaseOutputPath=...`），并设置环境变量 `TABULA_DATA` 让测试实例使用独立数据目录。结束测试实例只能按自己启动时拿到的 PID，不要按进程名、映像名或窗口标题结束进程（`taskkill /IM`、`/FI "WINDOWTITLE …"`、`Stop-Process -Name`），用户的客户端和测试实例同名同标题，会被一起关掉。测试结束后删除测试数据，并且只用本机模拟接口和测试 Key，不用真实账号。

## 按模块按需查阅

改动下列模块或审查 PR 前，必须先读对应的 `docs/rules/` 规范文档（各分卷从本文档无损拆出，与本文件具备同等约束效力）：

| 涉及模块 / 场景 | 必读规范文档 |
| :--- | :--- |
| 画布命令表 / `CommandBar` / `Ctrl+K` / `ComposerPanel` / 助手底栏 / `PictureView` / `Stacks` 批次 | [canvas-ui.md](docs/rules/canvas-ui.md) |
| Fluent 风格 / `Kind*Brush` / `Motion` / 磨砂·液态·亚克力玻璃 / `Skin` / 外框与大纲停靠区 / `GlassSettings` | [glass-theme-and-motion.md](docs/rules/glass-theme-and-motion.md) |
| 服务商原生协议 / ACP 与 MCP 管道 / `ModelFamilies` / SVG / 媒体编号 / 视频分支 / 余额与 `Prices.cs` / 资料库 / `Loaders` / ONNX 许可 | [agents-providers-and-storage.md](docs/rules/agents-providers-and-storage.md) |
| 审查 PR（需求价值与实现质量分判）、建议关闭或合并 `main` | [pr-review.md](docs/rules/pr-review.md) |

## 文档规范

- README 保持简洁，只放项目介绍、核心功能、快速开始和文档入口。
- `docs/index.md` 放给 AI 使用的文档索引，不要再放到 `docs/content/docs/` 内容目录里。
- 详细功能介绍写到 `docs/content/docs/overview/features.mdx`。
- 后续待办写到 `docs/content/docs/progress/todo.mdx`。
- 已实现但还需要用户测试确认的事项写到 `docs/content/docs/progress/pending-test.mdx`。
- `docs/content/docs/progress/pending-test.mdx` 用来记录这个版本实际做了哪些可测试变更；`CHANGELOG.md` 的 `Unreleased` 只保留对这些变更的版本级归纳，避免逐条照搬实现细节。
- 每次重大改动（新增/调整/删除功能、接口或工具，影响用户可感知行为）完成后，都要在 `CHANGELOG.md` 的 `Unreleased` 追加一条记录，按 `[新增]` / `[调整]` / `[修复]` / `[优化]` 前缀分类，用一句中文归纳；纯内部重构、格式化、无用户可感知影响的小改动可不记。
- 每次 todo 事项完成后，先从 `docs/content/docs/progress/todo.mdx` 移到 `docs/content/docs/progress/pending-test.mdx`，不要直接写进正式功能说明；用户确认测试通过后再更新 `docs/content/docs/overview/features.mdx`。
- 每次任务完成前，都要根据实际变更检查并更新 `docs/content/docs/progress/todo.mdx` 和 `docs/content/docs/progress/pending-test.mdx`；如果功能或待办没有变化，也要确认无需修改。
- 文档不要写过期日期；除非用户明确要求记录具体时间。

## 发版本流程

- 发版本时，先把 `CHANGELOG.md` 的 `Unreleased` 变更整理成新的版本记录，并保留空的 `Unreleased` 标题。
- 按当前版本号提升一个版本，更新根目录 `VERSION`。
- 将当前未提交的代码全部提交到 Git。
- 提交完成后，给当前提交打最新版本号对应的 tag，例如 `v0.0.5`。
- 发版本流程中不要执行编译、测试或构建，除非用户明确要求。

## 项目注意事项

- 新增或调整超时、重试次数、大小限制、并发上限等会改变实际行为的边界值前，必须先向用户说明适用环节、默认值和失败后的处理方式，并取得确认；不要把经验值当成纯内部实现静默加入。
- Agent 通信协议版本与消息存储版本必须独立管理；消息存储格式升级时必须先备份再迁移，遇到未知版本、损坏清单或冲突备份时拒绝覆盖原文件，不得按记录数量或文件大小静默裁剪历史元数据。
