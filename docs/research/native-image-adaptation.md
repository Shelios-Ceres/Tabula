# 原生图片任务适配摘要

本阶段把实时图片生成和编辑统一到原生任务入口，并保留提供商返回的多输出、文本、顺序、会话与元数据。兼容自定义渠道仍沿用原有脚本路径。

## 真实账号实测

已通过用户配置的官方渠道完成以下调用，API Key 未写入本记录：

| 型号 | 已测路径 | 实际结果 |
| --- | --- | --- |
| Seedream 5.0 Pro | 北京区域 Endpoint，1K 文生图；画布原生矩形编辑，单张参考图 | HTTP 200，1024×1024 图片；蓝杯修改为绿杯，图片刷新后仍可从本地恢复 |
| Seedream 5.0 Lite | 独立 Endpoint，2K、stream、sequential auto、max_images=2 | 一次请求得到两张 2048×2048 图片，两个 partial_succeeded 加 completed 终态，全部保存 |
| Gemini 3.1 Flash Image | 官方 `/v1/interactions`，512px、stream、store=false；完整历史续接编辑 | 两次 HTTP 200，均收到 interaction.completed；蓝杯修改为珊瑚橙色，512×512 JPEG 保存成功 |

Ark 请求均明确发送 `watermark: false`。Pro 框选实际发送了 `<bbox>240 283 909 834</bbox>`，引用图片数为 1；Gemini 第二轮输入保留 `user_input → thought → model_output → user_input`，没有丢掉历史思考签名。

真实调用发现并修复了两项模拟夹具未覆盖的问题：

- Ark 首次请求已成功生成图片，但返回的 CDN 链接在浏览器中无法跨域读取并保存。请求改为官方支持的 `response_format: b64_json`，Pro 生成、编辑和 Lite 流式组图均已验证可直接保存，不依赖结果 CDN 的 CORS 设置。
- Gemini 初次请求返回 400，提示 `input[0].type=text` 不被接受。初次请求和云端续接请求均改为 `[{type: user_input, content: ...}]`；无状态多轮则提交历史步骤加当前 user_input。修复后的流式生成和多轮编辑已通过。

对应 Ark 6 项、Google 11 项定向测试通过。另修正了 Pro/Lite 切换时旧分辨率档位的显示，避免界面显示值与请求归一结果不一致。

本轮未调用 GPT Image 2.5，也未实测其他 Gemini 型号、联网搜索、Pro 图层拆分及云端存储会话续接，不据此宣称全部功能均已通过真实账号验收。结果保存在工作台生成记录和本地“Ark 与 Gemini 原生实测”画布中。

## 官方来源

- [Google 图片生成](https://ai.google.dev/gemini-api/docs/image-generation)
- [Google Interactions API](https://ai.google.dev/gemini-api/docs/interactions-overview)
- [Google Interactions 请求格式定义](https://ai.google.dev/api/interactions-api-v1)
- [Google API versions](https://ai.google.dev/gemini-api/docs/api-versions)
- [Volcengine Ark 图片生成](https://docs.volcengine.com/docs/82379/1541523)
- [Volcengine Ark 模型能力](https://docs.volcengine.com/docs/82379/2582774)
- [Volcengine Ark 组图与图层](https://docs.volcengine.com/docs/82379/2582775)
- [Volcengine Ark 流式与搜索](https://docs.volcengine.com/docs/82379/1824137)
- [OpenAI Images API](https://developers.openai.com/api/docs/guides/image-generation)

## 能力映射

Seedream 的比例预设为应用按名义分辨率面积换算的尺寸，不标称为官方推荐尺寸表：比例保持精确，整数尺寸使用预设专用的 8px 网格，且不超过该分辨率的正方形总像素。手动自定义尺寸没有新增对齐限制。预设通过现有 `imageNative.ark.size` 保存并作为 `size: 宽x高` 提交，不添加 Ark 未定义的 `aspect_ratio` 参数。支持 11 种比例，含 `9:21`；例如 2K 的 9:21 为 1320×3080，Lite 4K 为 2664×6216。19 项定向测试覆盖 66 个型号/档位/比例组合、请求字段和 Endpoint/水印保持，浏览器验证了选项及档位联动。

Nano Banana Pro 的官方比例枚举没有 `9:21`，只有横向 `21:9`；本应用不将横向选项自行扩展为官方未声明的竖向选项。依据：[Interactions ImageResponseFormat](https://ai.google.dev/api/interactions-api-v1)及[官方图片尺寸表](https://ai.google.dev/gemini-api/docs/image-generation#aspect-ratios-and-image-size)。

Nano Banana Pro (`gemini-3-pro-image`) 默认自动思考，公开图像文档未提供可选档位；界面展示说明，请求省略 `thinking_level`。Nano Banana 2 与 2 Lite 可选 `minimal/high`，应用的“自动”表示省略参数并使用服务默认值。Pro 支持 10 种宽高比及 1K/2K/4K。依据：[官方思考说明](https://ai.google.dev/gemini-api/docs/image-generation#thinking-process)与[可调思考级别](https://ai.google.dev/gemini-api/docs/image-generation#controlling-thinking-levels)。

参数交互已修正：画布原生设置的 Select 菜单挂载到设置浮层的非滚动根节点，滚动内容单独包裹，避免点击选项被误认为外部点击或菜单被裁剪。浏览器已复现旧版本“点击 16:9 后仍是 1:1”，并确认修复后 16:9 保存、面板保持展开、外部点击正常关闭。对应 17 项能力与请求测试通过；本次参数交互验收没有发送付费生图请求。

| 提供商 | 原生入口与能力 |
| --- | --- |
| Google | `/v1/interactions`，可选 `v1beta`；Flash、Pro、Lite 按型号限制尺寸、搜索和 thinking；支持图文交错、多输出顺序、搜索建议与会话 store，默认无状态；历史图片使用本地资源引用。当前请求输出 MIME 枚举仅 JPEG，实际返回文件仍按其 MIME 保存。 |
| Ark | `/api/v3/images/generations`；Pro 支持点/框坐标（0..999）及 PNG 图层 group/bbox（0..1000）；Lite 支持顺序组图、流式和 web search；支持 PNG/JPEG。官方水印默认开启，本应用按用户选择默认关闭并显式发送 `watermark: false`，也可手动开启。 |
| OpenAI | Images API 与可选 Responses；支持原生 mask、SSE 事件、会话及多输出。Responses 模式单次请求数量为 1。 |
| Canvas / Workbench / Agent / Plugin | 统一任务结果，保存全部图片、文本、输出顺序、会话和提供商元数据；重试使用冻结快照，导出时保留本地资源引用。 |

## 验收范围

验证各提供商和型号的请求路由、参数 gate、单次数量上限、参考图限制、流式事件、原生遮罩、坐标/图层元数据、图文顺序、会话续接、历史本地 marker、重试快照及资源导出导入。53 项定向行为测试通过。本地模拟接口已验证工作台 Lite 单次流式组图、Google 图文与历史续接、画布 Google 多图及来源详情、Pro 图层成组和点/框编辑；这些不代表真实账号生成质量验收。后续真实账号实测结果见下文；构建与类型检查未执行。

Batch、显式 cache 控制和自定义 safety 不属于当前实时创作范围，不能据此宣称已支持。

## Endpoint 与遗漏补齐

渠道的每个 Seedream 型号可以填写独立的 `endpointId`，型号名称仍用于 Pro/Lite 能力判断，实际请求的 `model` 使用接入点 ID。留空走官方 Model ID。绑定型号须与控制台接入点一致；界面不根据 `ep-…` 字符串猜测型号。官方集成说明也要求同时填写接入点与对应模型，见[接入点配置](https://www.volcengine.com/docs/6893/1359486?lang=zh)。当前账号的北京区域 Pro/Lite 接入点已通过下述真实调用；其他账号或区域仍需分别验证。

生成快照将绑定保存到 `imageNative.ark.endpoint`，同时记录渠道与型号。修改渠道不会改变旧请求的重试目标，空接入点也会冻结；切换型号或渠道时重新解析对应绑定。工作台、画布及 Agent/插件共用该行为，原生设置显示当前接入点，并提供“使用渠道当前绑定”。

本轮源码审查与官方文档核对后修正了以下遗漏：

- Pro 点/框坐标改为 `round(fraction * 1000)` 后限制到 0–999，图层返回框仍使用独立的 0–1000 规则，参见[交互编辑指南](https://docs.volcengine.com/docs/82379/2582775)。
- Ark 流式支持具名事件及只在 JSON `type` 中标记的事件，保留部分失败的错误代码。
- Google 在发出请求前校验最多 14 张参考图；对象、角色、风格的能力说明不被误作未分类图片的拒绝条件，参见[图片生成说明](https://ai.google.dev/gemini-api/docs/image-generation)。
- 插件数量按实际型号、请求方式和参考图数量归一；Agent 的矩形选区在创建生成流程前拒绝颠倒的坐标。
- 会话历史复用已保存的生成图资源，同一输出不再因 Google 图片块或 Responses 历史格式额外写入 Blob；删除旧记录时会保留后续会话、画布和素材仍在引用的图片，仅清理本次删除记录关联的未引用资源。

本轮运行了 35 项相关前端定向测试，全部通过；独立浏览器的本地模拟接口验证了 Endpoint 配置、`model: ep-pro-qa`、关闭水印及历史绑定展示。Agent schema 测试放在 `canvas-agent/tests/native-image-schema.test.ts`，该目录尚未安装自身依赖，未完成运行；该阶段没有安装新依赖或运行构建；后续真实调用记录见下文。
