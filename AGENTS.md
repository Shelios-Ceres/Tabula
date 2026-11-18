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

## 桌面客户端规范

- `desktop/` 是 WinUI 3 原生客户端（.NET 10、Windows App SDK、Win2D），也是项目唯一的客户端；网页版及只为它服务的 canvas-agent、canvas-proxy、网页节点插件和部署配置已移除，需要参考旧实现时查 git 历史，不要再加回网页端代码。
- 分层固定：`InfiniteCanvas.Core` 只放画布模型、规则、存储与模型家族，不引用 WinUI；`InfiniteCanvas.Providers` 只放服务商 HTTP 适配；`InfiniteCanvas.Agents` 只放 ACP 客户端与画布 MCP 工具；界面在 `InfiniteCanvas.App` 的 `Pages/`（页面）与 `Board/`（画布引擎、节点、生成面板、生成调度）。画布相关命名空间用 `Board`，不要再建名为 `Canvas` 的命名空间，避免遮住 WinUI 的 `Canvas` 类型。
- 服务商一律使用各家原生协议（OpenAI Image/Responses、Gemini Interactions/Veo、方舟 Seedream/Seedance/Chat、xAI、DashScope），不要改走 OpenAI 兼容接口；修改适配前先阅读该服务商最新官方 API 参考，并在 `tests/InfiniteCanvas.Tests/ProviderTests.cs` 补请求与响应解析用例。
- 本机 Agent 接入放在 `InfiniteCanvas.Agents`（不引用 WinUI）：与 Agent 对话一律走 ACP（Agent Client Protocol v1，stdio 子进程，`AcpClient`），不要为单个 Agent 写私有适配；客户端不声明 fs / terminal 能力，Agent 用自己的工具。画布能力只通过 `CanvasTools` 里的 MCP 工具提供，由 `CanvasMcpServer` 走按画布随机命名的管道，并以「客户端 exe + `--mcp-bridge`」作为 stdio MCP 声明交给 Agent；客户端外的 Agent 用不带管道名的 `--mcp-bridge` 连到按数据目录固定的共享管道（`SharedCanvasHost`，作用于当前打开的画布），管道一律 `PipeOptions.CurrentUserOnly`；新增或修改工具时在 `AgentTests.cs` 补用例，所有文档读写经 `ICanvasHost.OnUiAsync` 回到 UI 线程，一次 `canvas_apply_ops` 只记一步撤销。
- 画布上的操作统一登记在 `Core/Canvas/CanvasCommands.cs` 的命令表（编号、名称、图标、快捷键、作用范围、可见规则），处理函数在 `Board/CanvasCommands.cs`；节点右键菜单、选区旁边的命令栏（`Board/CommandBar.cs`，由 `CanvasView` 放在选区上方、放不下放下方；各状态放哪些命令由 `CanvasCommands.Bar` 决定，没放上去的归入「更多」）和操作说明都从它生成，新增或改名操作时只改这一处，并在 `CommandTests.cs` 补可见规则和命令栏布局用例；顶部不画命令栏：新建和导入走画布菜单，撤销 / 重做在标题胶囊里；生成面板放在命令栏之外，两者互不遮挡。名称按表里的叫法（创建副本、复制、局部编辑…、停止、只留这张…），打开对话框的名称带「…」；每个命令作用于整个选区，只对单个节点有意义的命令设为 `Single`，多选时自动隐藏；折叠的一批按封面处理。
- 模型能力在 `desktop/src/InfiniteCanvas.Core/Models/ModelFamilies.cs` 按家族声明，不按完整型号精确匹配。OpenAI 的调用方式一律经 `ModelFamilies.OpenAIApi` 取：只有官方写明可作 Responses 图像工具模型的家族（`ResponsesTool`）才显示和发送 Responses 模式。
- SVG 图片节点没有像素：裁剪、拆分、多角度、本地放大、反推提示词和局部编辑都对 SVG 隐藏，显示用 `SvgImageSource`，作为参考图时经 `ImageTools.RasterizeSvgAsync` 转成 PNG；新增针对图片像素的工具时同样要排除 SVG（`Media.IsSvg`）。
- 画布里的媒体文件一律经 `ProjectStore.SaveAsset` / `ImportAsset` 按画布和种类编号（`图片-001.png`、`视频-002.mp4`、蒙版用 `蒙版-001.png`、标注层用 `标注-001.png`），节点标题在用户改名前跟随编号（`ProjectStore.TitleFromAsset`）；不要再用 GUID 或原文件名命名。
- 视频生成暂不在 main：完整实现（服务商视频接口、视频家族、生成面板视频参数、视频任务轮询与续查）保存在 `video-models` 分支。需要时先把该分支更新到最新 main 再合并回来，不要在 main 上零散地加回视频生成代码；main 上视频节点只负责导入、播放和作为素材。
- 一次生成多张的输出用 `Stacks` 叠成一摞（`NodeKeys.Stack` / `StackOpen`）：折叠时只显示第一个成员，对它的移动、删除、编组都作用于整摞（`CanvasView.WithStacks`）；新增针对选中节点的批量操作时要考虑折叠的摞。
- 界面整体走 Windows 11 原生风格（Fluent），设计变量统一在 `App.xaml`：强调色跟随系统，圆角用 WinUI 默认（控件 `ControlCornerRadius` 4、浮层 `OverlayCornerRadius` 8），`CanvasBackgroundBrush` 半透明以透出窗口亚克力，`CardBrush` / `CardStrokeBrush` 为节点卡片，页面内容区用 `SectionCard`；按钮用 `ToolbarButton`（无底色，悬停才有填充）、`ActionButton`、`AccentActionButton`，页面标题用 `PageTitle`。不要在页面里另写颜色或圆角。
- 配色要多彩：按节点种类使用 `App.xaml` 里各主题都定义的 `Kind*Brush`（图片蓝、视频紫、音频橙、文本绿、生成配置品红、分组青），XAML 用 `ThemeResource`，代码和 Win2D 通过 `Palette.Kind` 读取（随 `ActualThemeChanged` 重读）；节点图标块、工具栏图标、连线渐变、小地图和画布封面都用这套颜色，不要另起色板。
- 动效统一走 `Motion`（合成层上的淡入淡出加轻微缩放，Fluent 减速曲线）：元素显隐用 `Motion.ShowHide`，新出现的元素用 `Motion.Appear`，视口移动用 `CanvasView.Glide`，节点位置过渡用 `CanvasView.Move`（逐帧改真实坐标，连线和阴影跟着走）；不要在组件里另写 Storyboard。`OpacityTransition` 等过渡属性不能用在设置了 `RenderTransformOrigin` 的元素上（会抛异常）；`Canvas.SetZIndex` 只接受有限范围，不要传 `int.MaxValue`。`Motion.ShowHide` 不要和投影的 `Translation`（Z 位移）加在同一个元素上，否则点击范围会和画出来的位置错开：外层做显隐动画，里层圆角胶囊挂 `ThemeShadow` 和 Translation（参考 `CanvasView.FloatingBar` 和生成面板）。
- 画布上的浮动表面有三种材质，在「设置 › 外观」里选（`AppSettings.GlassMaterial`，由 `Skin` 应用）：磨砂（默认）、液态、亚克力。磨砂和亚克力都是 `App.xaml` 的 `GlassBrush`（工具栏，`GlassCapsule`）/ `GlassThickBrush`（文字较多的面板和全屏预览，`FloatingPanel`）/ `GlassStrokeBrush`，`Skin` 在运行时改这几个共享画刷的颜色（磨砂：约 34 % 白或石板色填充、白色细边；亚克力：原来的清透值），不要在页面里另写材质。液态玻璃（`Board/Glass`）和它的 `liquid-glass.json` 参数、调参面板（Ctrl+Shift+L，设置里在选了液态时有入口）保持原样，选液态等于 `GlassSettings.Enabled` 打开，调参面板里关掉它会退回亚克力。画布顶部和底部的渐隐带（`CanvasView.DrawFade`）对三种材质都生效，参数用液态玻璃设置里的渐隐项。窗口背景是 `DesktopAcrylicBackdrop`，页面背景保持透明让它透出来。投影用 `ThemeShadow` 挂在圆角那一层并指定接收者；画布节点阴影统一在 Win2D 底层模糊绘制，不要给每个节点加 XAML 阴影。亚克力在窗口失焦时会切到半透明回退色，属正常行为。
- 画布底色（青 / 蓝 / 暖 / 灰，`Core/Settings/Appearance.cs` 的 `CanvasGrounds`）同样由 `Skin` 改 `CanvasBackgroundBrush` 的颜色，Win2D 绘制从同一个画刷读色；窗口主题和界面缩放存在 `GlassSettings`（`Theme`、`UiScale`），由 `Skin.ApplyTheme` 对所有页面生效。画布页的外框分三块：左上标题胶囊（菜单、主页、标题与存盘状态、撤销 / 重做、生成中）、右上胶囊（资料库、助手、今日花费、命令、设置）、右下状态栏（节点数、缩放菜单、小地图、大纲、全屏、帮助）；右侧停靠区同一时间只放助手或液态玻璃调参面板，小地图在状态栏上方。
- 颜色用 `ThemeResource`，Win2D 绘制颜色随 `ActualTheme` 切换，不硬编码浅色或深色。
- 只有图标的按钮必须设置 `AutomationProperties.Name`（与 ToolTip 一致），保证读屏和 UI 自动化能找到。
- C# 内插原始字符串里的 JSON 只要出现连续的 `}}`（如 `...}}"""`），开头必须用 `$$$` 并把插值写成 `{{{x}}}`；`$$` 会编译失败（CS9007）。写完测试要实际编译一次。
- WinUI 3 的 `RoutedEventArgs` 没有 `Handled`，需要拦截事件时使用具体参数类型（如 `PointerRoutedEventArgs`）。
- 代码里创建多行 `TextBox` 时，对象初始化器中先写 `AcceptsReturn = true` 再写 `Text`；顺序反了时单行模式会把第一个换行之后的内容直接丢掉。
- 余额查询走各家原生账务接口，在对应 Provider 的 `BalanceAsync` 实现，凭据用 `ModelChannel.BillingKey` / `BillingId`，和生成用的 API Key 分开，不要拿 API Key 查账；新增或修改时在 `ProviderTests.cs` 补请求、签名格式与金额解析用例。解析生成响应时把服务商返回的 usage 用 `AiProvider.Count` 记入本机账本（Gemini 的图片 / 音频输出 token 单独计）；Google 估算单价和生图预计花费用的各家官方单价（方舟 / 千问 / xAI 按张、Gemini 每档图片 token、OpenAI 尺寸单价表）都在 `Core/Models/Prices.cs` 维护，官方价格变动时按价格页更新并改测试；没有官方单价的组合只按上次同样设置的实际用量估算，不要用第三方测得的数字。
- 资料库按画布存放（`ProjectStore.LibraryDir(画布 ID)`，即 `projects/<画布 ID>/library`），`LibraryStore`、资料库面板和 Agent 的 `library_*` 工具都传当前画布 ID；不要再加全局共享的资料库目录。画布上删除节点不删文件，只有资料库面板「画布文件」页里删除才把文件移到回收站（`ProjectStore.Recycle`，连同用到它的节点；回收成功后用 `CanvasView.ForgetAssets` 一次性整理撤销记录，每条快照只去掉当时正显示这些文件的节点，不要按节点 ID 跨快照删除，否则重新生成过的节点会在撤销时消失）；离开画布时 `ProjectStore.PruneMasks` 只清理没被引用的蒙版和标注层（`ProjectStore.IsMark`），不要再按引用清理图片、视频、音频。批次的名字是摞所在节点（ID 等于摞 ID）的标题，展开或单独列出时用 `Stacks.OwnTitle` 显示它自己的编号。
- 等待动画是数据目录 `loaders/` 里的网页，由 `Board/Loaders.cs` 选取、`NodeView` 用 WebView2 播放；客户端自带的原创动画放在 `App/Assets/Loaders`，只在数据目录没有 `loaders/` 时复制一次。第三方动画（如 Painting Loaders）没有开源许可，只能放在用户本机，不要提交进仓库或打包进客户端；要随客户端分发的只能是我们自己原创的动画。
- 测试实例的界面自动化只用 UI Automation 的 Invoke / SelectionItem / Value，或用 `--mcp-bridge` 走测试实例自己的管道；不要用鼠标坐标点击，测试窗口不在前台时会点到用户的其他窗口。
- 本机验证会在 `%LocalAppData%\InfiniteCanvas` 写入画布和设置；用户正在使用客户端时，不要关闭它或改动它的数据：把编译输出指到临时目录（`-p:BaseOutputPath=...`），并设置环境变量 `INFINITE_CANVAS_DATA` 让测试实例使用独立数据目录。测试结束后删除测试数据，并且只用本机模拟接口和测试 Key，不用真实账号。

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

## PR 审查与处理

- 审查 PR 时必须把“需求价值”和“实现质量”分开判断，分别给出结论；实现差不等于需求不需要，需求有价值也不等于当前代码可以合并。
- 需求价值需要单独结合项目方向、用户场景、现有能力和后续规划判断；无法从项目上下文确定是否需要时，必须询问用户，不得仅凭代码质量、作者或改动规模推断需求不需要。
- 实现质量重点检查正确性、安全性、改动范围、重复代码、无关文件、现有结构复用、可维护性、测试与文档以及与最新 `main` 的冲突。改动几十个文件、疑似 AI 批量生成、重复代码多只能作为重点复核或拒绝当前实现的信号，不能单独作为放弃需求的依据。
- 对“需求有价值但实现不合格”的 PR，优先考虑要求作者修改、提取可用思路后自行重做，或把需求保留到 issue/todo；不要直接把需求一起否定。
- 建议关闭 PR 前，必须先向用户分别说明需求价值、实现质量、可保留的思路和建议处理方式，并取得用户明确确认；批量关闭时也要让用户能看清每个 PR 的需求是否仍需保留。
- 可以先在独立分支审查、修复、测试和准备提交；任何合并进 `main` 的操作都必须先说明修复内容、测试结果、风险与冲突，并取得用户明确同意。需要 force-push PR 作者分支时也必须提前说明影响并取得同意。

## 项目注意事项

- 新增或调整超时、重试次数、大小限制、并发上限等会改变实际行为的边界值前，必须先向用户说明适用环节、默认值和失败后的处理方式，并取得确认；不要把经验值当成纯内部实现静默加入。
- Agent 通信协议版本与消息存储版本必须独立管理；消息存储格式升级时必须先备份再迁移，遇到未知版本、损坏清单或冲突备份时拒绝覆盖原文件，不得按记录数量或文件大小静默裁剪历史元数据。
