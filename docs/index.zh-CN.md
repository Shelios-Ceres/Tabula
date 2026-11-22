# Tabula（丹青）文档索引

## 项目介绍

- [快速开始](/zh-CN/docs/overview/quick-start)
- [功能介绍](/zh-CN/docs/overview/features)

## 开发文档

- [本地开发](/zh-CN/docs/development/local-development)

## 开源协议

- [开源协议](/zh-CN/docs/business/license)

## 安全

- [漏洞提交](/zh-CN/docs/support/security)

## 项目进度

- [更新日志](/zh-CN/docs/progress/changelog)
- [待测试](/zh-CN/docs/progress/pending-test)
- [TODO](/zh-CN/docs/progress/todo)
- [WinUI 3 桌面客户端规划](/zh-CN/docs/progress/desktop-client-plan)

## 说明

- Tabula（丹青）是 Windows 原生桌面客户端，代码在 `desktop/`；原来的网页版及其配套工具已移除。
- 画布、媒体文件和设置保存在本机 `%LocalAppData%\InfiniteCanvas`，没有云同步。
- API Key 用 Windows DPAPI 加密保存，客户端直接请求各家服务商的原生接口。
