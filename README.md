# AI Real-Time Voice Input

面向 Windows 的实时语音输入工具：优先让文字随说随出现，再逐步加入可控的局部编辑能力。

## 当前实现

已有 C# 桌面托盘、快捷键、麦克风采集和流式 ASR。默认模式在 HUD 显示临时识别结果，通过剪贴板粘贴稳定文本；另有显式启用的 TSF 实验，验证 Notepad、Chrome 中的输入框内实时文字。

目前仍在排查真实语音联调的稳定性，尚未完成完整产品验收。AI 轻整理、个人词典、语音编辑和语音发送均未实现。当前阻塞和下一步以 [ROADMAP](docs/ROADMAP.md) 为准。

## 技术方向

- 正式客户端：C# + .NET + WPF；当前 ASR 主候选为 Paraformer Realtime v2。
- Python 用于 ASR 对比验证，C++ 用于受限的 TSF 实验。
- 当前没有后端；详细选型与实现边界见 [ARCHITECTURE](docs/ARCHITECTURE.md) 。

## 文档入口

- [PRODUCT_SPEC](docs/PRODUCT_SPEC.md)：产品目标、体验边界和竞品借鉴。
- [ARCHITECTURE](docs/ARCHITECTURE.md)：当前技术组成与后续架构设计。
- [DECISIONS](docs/DECISIONS.md)：取舍及其理由。
- [ROADMAP](docs/ROADMAP.md)：当前进度、阻塞和下一步。
- [TESTING](docs/TESTING.md)：验证方法与历史实测证据。
- [ORIGINAL_PLAN](docs/ORIGINAL_PLAN.md)：保留的原始计划，仅供追溯，不代表当前进度或执行顺序。
