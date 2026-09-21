# Codex 开发轮次任务清单

> 使用方式：按轮次依次交给 Codex。  
> 每轮只执行当前任务，不自动继续下一轮。

---

# 第一轮：初始化项目结构与 Git

请为当前项目初始化基础项目结构。

当前阶段只创建项目骨架、基础文档和 Git，不实现正式产品功能。

## 技术方向

- 正式 Windows 客户端：C# + .NET + WPF
- Streaming ASR 技术验证：允许使用 Python
- 后端当前不创建
- 后续如需小范围 Beta，可增加 Python + FastAPI
- 第一阶段不开发完整 Windows IME
- 第一阶段不开发正式账号、订阅、支付和 License 系统

## 创建以下项目结构

```text
project-root/
├─ README.md
├─ AGENTS.md
├─ .gitignore
├─ .env.example
│
├─ docs/
│  ├─ PRODUCT_SPEC.md
│  ├─ ARCHITECTURE.md
│  ├─ DECISIONS.md
│  ├─ ROADMAP.md
│  └─ TESTING.md
│
├─ src/
│  └─ desktop/
│
├─ prototypes/
│  └─ streaming_asr/
│
└─ tests/
```

## 文档要求

### PRODUCT_SPEC.md

只创建文件并保留占位说明，不自行补充完整产品定义。

### ARCHITECTURE.md

只创建文件并保留占位说明，本轮不设计完整架构。

### DECISIONS.md

记录当前已确认的重要技术决策：

- Windows-first
- 正式客户端采用 C# + .NET + WPF
- Prototype 可以使用 Python
- 不采用 batch-first transcription
- 不优先开发完整 Windows IME
- 当前最高优先级为实时输入延迟
- 当前不实现完整商业化系统
- 架构需要为后续少量外部 Beta 保留扩展能力

### ROADMAP.md

只记录当前 MVP 阶段，不创建半年或一年级别长期规划。

### TESTING.md

预留以下核心指标：

- Audio → first transcript latency
- Audio → stable transcript latency
- continuous transcription lag
- edit command latency
- cleanup latency
- command false trigger rate
- transcription error rate
- crash / unexpected termination
- foreground app compatibility

## AGENTS.md

加入以下规则：

1. 开发前优先阅读：
   - docs/PRODUCT_SPEC.md
   - docs/ARCHITECTURE.md
   - docs/DECISIONS.md
   - docs/ROADMAP.md
2. 不要未经必要性判断创建新的 Markdown 文档。
3. 当前优先级：
   `Latency > Controllability > Intelligence`
4. 不提前开发：
   - 商业化
   - 支付
   - 正式 License
   - 跨平台
   - 手机端
   - 完整 Windows IME
   - 复杂 Agent Framework
5. 每次只处理一个明确能力。
6. 核心架构变化必须记录到 docs/DECISIONS.md。
7. 实现完成后运行相关测试。
8. 不允许把 API Key、Token、密码写入代码或提交 Git。
9. `.env` 不得进入 Git。
10. 项目未来需要支持外部 Beta：
    - 不依赖本机绝对路径
    - 不依赖本机特有工具
    - 配置必须可外置
    - 第三方服务密钥不得直接暴露给测试用户

## Git

- 初始化 Git 仓库
- 默认分支使用 main
- 当前不要创建复杂 Git Flow
- `.gitignore` 至少覆盖：
  - C#/.NET
  - Python
  - Visual Studio
  - VS Code
  - JetBrains
  - `.env`
  - build artifacts
  - local logs
  - temporary audio files
  - local test output

## .env.example

只创建示例变量，不填写真实 Key。

例如：

```text
ASR_API_KEY=
LLM_API_KEY=
```

## README.md

保持极简，只写：

- 项目是什么
- 当前阶段
- 当前技术方向
- 产品和架构文档位置

## 本轮不要做

- 不写正式客户端代码
- 不接入 ASR
- 不创建 Backend
- 不创建数据库
- 不做 UI
- 不创建安装包
- 不做 License
- 不做账号系统
- 不新增大量文档

## 完成后输出

1. 创建的目录结构
2. 初始化的文件
3. Git 状态
4. 已写入的初始技术决策
5. 当前没有创建、但未来可能需要的内容

完成后停止，不进入下一轮。

---

# 第二轮：技术架构设计

阅读：

- `docs/PRODUCT_SPEC.md`
- `AGENTS.md`
- `docs/DECISIONS.md`
- `docs/ROADMAP.md`

现在不要实现完整产品。

请为 Windows MVP 设计技术架构，并更新：

- `docs/ARCHITECTURE.md`
- `docs/DECISIONS.md`
- `docs/ROADMAP.md`

## 需要重点设计

### 1. Streaming ASR

设计：

- 麦克风音频采集
- streaming audio chunks
- WebSocket / realtime connection
- partial transcript
- stable / final transcript
- reconnect
- interruption handling

不能采用：

```text
Audio
→ 等录音结束
→ 上传完整文件
→ ASR
→ 返回
```

必须支持真正的 incremental transcription。

### 2. Transcript State

设计至少三级状态：

- Interim
- Editable
- Committed

说明：

- 每层什么时候产生
- 什么时候升级
- 后续语音如何修正
- AI cleanup 如何避免和 ASR 状态冲突

### 3. Live Editable Document

需要设计持续变化的 Document State，至少考虑：

- full text
- cursor position
- selection
- sentence boundaries
- paragraph boundaries
- recent input
- source of text
- edit history
- undo
- committed / editable / interim regions

### 4. Keyboard + Voice 同步

说明：

- 用户键盘输入后 Document State 如何更新
- 鼠标选择文本后如何感知
- AI 修改后如何避免和用户键盘操作冲突
- 用户输入过程中 AI 不得抢夺控制权

### 5. Windows 文本交互

比较并设计：

- Clipboard
- SendInput
- UI Automation
- Windows Accessibility APIs
- foreground window detection

说明：

- 哪些适合作为 MVP
- 哪些作为 fallback
- 哪些后续才需要
- 如何抽象成 InputTarget / AppAdapter

### 6. Intent Router

语音必须区分：

```text
DICTATION
EDIT
ACTION
```

EDIT 至少考虑：

```text
REPLACE
DELETE
INSERT
REWRITE
UNDO
```

ACTION 至少考虑：

```text
SEND
CANCEL
UNDO
```

简单命令优先低延迟处理。

不要为了简单指令调用高成本长推理模型。

### 7. Text Reference

设计统一 TextReference 模型，例如：

- current_sentence
- previous_sentence
- current_paragraph
- previous_paragraph
- selected_text
- recent_input
- fuzzy_text

### 8. Edit Operation

所有修改优先转换为结构化操作。

例如：

```json
{
  "type": "replace",
  "target": {
    "type": "fuzzy_text",
    "value": "服务器成本"
  },
  "replacement": "交付成本"
}
```

Document Engine 负责执行。

不要默认让 LLM 重写整个文档。

### 9. AI Cleanup

AI cleanup 必须与实时 ASR 解耦。

推荐：

```text
Streaming ASR
→ UI immediately

Stable semantic chunk
→ Async Cleanup
→ Local span replacement
```

说明：

- cleanup 触发时机
- sliding window
- semantic chunk
- debounce
- 如何避免文本频繁跳动

### 10. Thread / Async Model

明确：

- audio capture task
- ASR connection
- transcript processing
- intent processing
- AI cleanup
- UI update
- document mutation

任何模块都不能阻塞实时显示。

### 11. Local vs Remote

明确哪些能力应该尽量本地：

- Document Engine
- Edit execution
- state management
- simple intent rules
- app detection
- text injection

哪些能力可以远程：

- ASR
- semantic rewrite
- complex intent understanding

### 12. Latency Measurement

架构第一天支持：

- audio_capture_timestamp
- audio_sent_timestamp
- first_partial_timestamp
- stable_transcript_timestamp
- cleanup_start / end
- edit_command_start / end
- document_apply_timestamp

### 13. Prototype 与正式客户端边界

Python Prototype 只用于：

```text
Microphone
→ Streaming ASR
→ Partial transcript
→ Metrics
```

正式 Windows 客户端：

```text
C# + .NET + WPF
```

不要让 Python Prototype 演化成正式产品架构。

### 14. Beta 扩展

当前不实现 Backend，但架构需支持后续：

```text
Windows Client
      ↓
Minimal Backend
      ├─ beta access
      ├─ config
      ├─ API credential protection
      └─ telemetry
      ↓
ASR / LLM provider
```

要求：

- 第三方 API Key 最终不得打包在客户端
- Document Engine 尽量留本地
- 云端不可用不能损坏本地编辑状态

## 技术原则

- Latency > Controllability > Intelligence
- 不使用 batch-first transcription
- AI 不阻塞实时 ASR
- 不默认全文重写
- 简单编辑优先结构化、本地执行
- 不使用复杂 Agent Framework
- 不做完整 Windows IME
- 不提前开发 Backend
- 不提前设计数据库
- 避免过度工程化

## 输出要求

更新 `ARCHITECTURE.md`，至少包含：

1. System overview
2. Module boundaries
3. Data flow
4. Document model
5. Transcript state machine
6. Intent / Edit model
7. Windows integration strategy
8. Async architecture
9. Latency measurement
10. Failure handling
11. Prototype boundary
12. Beta evolution path
13. Known risks
14. Alternatives considered

同时更新：

- `DECISIONS.md`
- `ROADMAP.md`

最后给出第一个技术 Prototype 的最小范围。

不要开始实现 Prototype。

---

# 第三轮：架构小范围修订

阅读当前 `docs/ARCHITECTURE.md`。

整体架构已经通过初步评审，不要重写或扩大架构范围。

只补充以下设计点。

## 1. Target State Synchronization

当前 DocumentState 是本地 mirror，需要明确：

- 用户键盘修改目标输入框后 Local Document 如何感知
- 鼠标移动光标或改变选区后如何同步
- 如何区分：
  - DocumentEngine 自己产生的修改
  - 用户键盘产生的修改
  - 外部应用自身产生的修改

增加：

```text
TargetStateSynchronizer
```

或等价概念。

建议包含：

```text
TargetSnapshot
{
    text_context
    cursor
    selection
    foreground_app
    timestamp
}
```

当前只设计职责，不实现。

## 2. Interim Rendering Strategy

当前不要强制决定 Interim transcript 一定直接进入 foreground input control。

记录 Pending Architecture Decision：

### Option A

Interim 直接注入目标输入框。

### Option B

Interim 只显示在 HUD / Overlay，Stable / Editable 才写入目标输入框。

### Option C

Hybrid。

说明各方案对以下内容的影响：

- latency
- text jumping
- cursor stability
- Windows app compatibility

最终决策延后到 Streaming ASR Prototype 和 Text Injection Prototype 之后。

## 3. Voice Command False-positive Protection

明确：

- 普通 Dictation 中出现“发送”“撤销”等词，不得直接触发 Action
- ACTION command 需要比普通 Edit 更严格的 confidence / context policy
- 增加 CommandCandidateDetector 或等价概念
- IntentRouter 不应无差别对所有 transcript 执行高成本语义判断
- SEND 原则：
  `宁可漏触发，也不能误发送`

当前不要设计复杂 command mode。

## 4. Input Session

正式定义 InputSession，用于隔离不同 foreground app / input target。

至少考虑：

- session_id
- active target
- DocumentState
- ASR session
- timestamps
- revision
- session status

Foreground app / target 变化时，要明确新旧 InputSession 如何切换。

## 5. InputTarget Strategy

不要把 SendInput 固定为所有文本插入的首选方式。

改为 capability-driven strategy，例如：

- UI Automation
- Clipboard + Paste
- Unicode / SendInput
- app-specific adapter

可考虑 capability：

```text
SET_TEXT
REPLACE_SELECTION
PASTE
UNICODE_INPUT
SEND_SHORTCUT
READ_SELECTION
READ_CONTEXT
```

SendInput 仍可作为快捷键和 Action 的主要低层手段。

## 本轮只更新

- `docs/ARCHITECTURE.md`
- 必要时 `docs/DECISIONS.md`

## 不要做

- 不开始写业务代码
- 不开始 Streaming ASR Prototype
- 不增加 Backend
- 不新增无关文档
- 不扩大产品范围

完成后输出修改摘要并停止。

---

# 第四轮：Streaming ASR Prototype

阅读：

- `docs/PRODUCT_SPEC.md`
- `docs/ARCHITECTURE.md`
- `docs/DECISIONS.md`
- `docs/ROADMAP.md`
- `docs/TESTING.md`
- `AGENTS.md`

本轮只验证 Streaming ASR latency。

不要开发正式 Windows 客户端。

## Prototype 目标

实现：

```text
Microphone
→ Streaming Audio Chunks
→ Realtime ASR
→ Partial Transcript
→ Stable Transcript
→ Latency Metrics
```

允许使用 Python。

代码放在：

```text
prototypes/streaming_asr/
```

## 必须支持

1. 持续采集麦克风
2. streaming / realtime 方式发送音频
3. 持续接收 partial transcript
4. partial transcript 一到立即展示
5. 接收 stable / final transcript
6. 不等整段录音结束
7. 连续说话至少 2 分钟
8. 网络异常明确报错
9. 支持安全退出
10. API Key 从环境变量读取

## 必须测量

至少记录：

- recording start
- first audio chunk sent
- first partial transcript
- first stable transcript
- each transcript update
- session duration

输出：

- first transcript latency
- stable transcript latency
- continuous lag

## 测试场景

至少验证：

1. 单句中文
2. 连续 30 秒中文
3. 连续 2 分钟中文
4. 中英文混说
5. 快速说话
6. 中间停顿后继续
7. 多次开始 / 停止
8. 网络短暂异常

## 当前不要做

- AI Cleanup
- Document Engine
- Windows Text Injection
- Keyboard Synchronization
- Voice Edit
- Voice Send
- WPF UI
- Backend
- License
- Installer

## 完成条件

完成后更新：

```text
docs/TESTING.md
```

记录真实测试结果。

如果 latency 不理想：

不要继续开发后续模块。

先分析：

- 参数
- audio chunk
- provider / model
- 网络
- 客户端处理

完成后停止。

---

# 第五轮：ASR 方案确认与优化

基于 Streaming ASR Prototype 的实测结果，确认正式 ASR 技术方案。

本轮不要增加其他产品功能。

## 对比内容

至少比较：

- first transcript latency
- partial refresh frequency
- stable / final latency
- 中文准确率
- 中英文混说
- 数字和英文缩写
- endpointing
- reconnect
- 长时间 streaming 稳定性
- API 成本

## 如果当前方案不满足要求

优先排查：

- audio chunk size
- buffering
- VAD
- endpointing
- WebSocket / realtime 参数
- provider
- model
- 网络延迟
- 本地处理延迟

不要在 ASR 体验不达标时继续堆后续功能。

## 输出

将最终决策更新到：

```text
docs/DECISIONS.md
```

并在 `docs/TESTING.md` 保留对比结果。

完成后停止。

---

# 第六轮：Windows Desktop Shell

ASR 验证通过后，开始正式 Windows Client。

代码放在：

```text
src/desktop/
```

使用：

```text
C#
.NET
WPF
```

## 本轮目标

实现：

- Background app
- System tray
- Global hotkey
- Microphone state
- Streaming ASR integration
- 基础 Voice HUD
- 基础错误状态

目标：

```text
在任意应用中
按快捷键
→ 开始实时语音识别
→ 再次操作后停止
```

## 不要做

- 不做复杂 UI
- 不做 AI Cleanup
- 不做 Voice Edit
- 不做 Voice Send
- 不做正式 Document Engine
- 不做 Backend
- 不做 License

完成后运行相关测试并停止。

---

# 第七轮：Windows Text Injection Prototype

本轮验证：

> ASR 结果是否能稳定进入不同 Windows 应用输入框。

## 需要测试

至少覆盖：

- Notepad
- Chrome
- ChatGPT
- 微信
- 飞书
- Word
- Cursor
- VS Code

## 比较

- UI Automation
- Clipboard + Paste
- Unicode / SendInput

## 设计

实现：

```text
InputTarget
```

以及 capability-driven strategy。

不要把某一种文本写入方式硬编码成唯一方案。

## 需要记录

- 应用兼容性
- 输入延迟
- 光标稳定性
- Selection 行为
- Clipboard 副作用
- 用户正在键盘输入时是否冲突

## 不要做

- 不做 AI Cleanup
- 不做 Voice Edit
- 不做 Voice Send
- 不做复杂 App Adapter

完成后更新 `TESTING.md` 并停止。

---

# 第八轮：Live Document Engine

本轮实现正式 Document State。

## 至少支持

```text
full_text
cursor_position
selection
sentence_boundaries
paragraph_boundaries
recent_input_spans
regions
edit_history
undo_stack
revision
active_target
```

## Region 状态

```text
Interim
→ Editable
→ Committed
```

## 本轮要求

- Document Engine 是本地权威状态
- 所有 mutation 结构化
- 支持 revision
- 支持 undo grouping
- 支持 conflict detection
- 不允许 UI 直接修改 Document State
- 不允许 LLM 直接拥有全文

## 当前不要做

- 不加入 LLM Cleanup
- 不加入复杂 Intent
- 不加入 Voice Send

完成后测试状态转换与冲突处理并停止。

---

# 第九轮：Keyboard + Voice Synchronization

实现：

```text
TargetStateSynchronizer
```

目标：

键盘、鼠标、语音和 DocumentState 能保持同步。

## 必须验证

```text
语音输入
→ 键盘修改
→ 继续语音
→ 鼠标移动光标
→ 再次语音
```

以及：

```text
语音输入
→ 用户选中一段文字
→ 键盘替换
→ DocumentState 正确同步
```

## 规则

- 用户键盘 / 鼠标操作优先于后台 AI
- 外部编辑发生后，旧异步任务需要 revalidate 或 cancel
- App / target 改变时切换 InputSession
- 不允许跨 App 错误复用 DocumentState

完成后测试并停止。

---

# 第十轮：Light AI Cleanup

本轮加入异步轻量 AI Cleanup。

## 默认允许

- 去口癖
- 去重复
- 补标点
- 明显语病修正
- 最小程度语序整理

## 默认禁止

- 大幅改变措辞
- 自动总结
- 改写用户风格
- 添加用户没有表达的信息
- 全文 Rewrite

## 架构要求

```text
ASR
→ Immediate Display
```

同时：

```text
Stable Semantic Chunk
→ Async Cleanup
→ Span Replacement
```

Cleanup 不能阻塞 ASR。

## 冲突要求

AI 返回结果前必须检查：

- document revision
- source region
- source text hash
- target span
- user activity

如果用户已经修改对应文本：

- discard
- cancel
- revalidate

不要强行覆盖。

完成后记录 cleanup latency 和误修改情况。

---

# 第十一轮：Intent Router

本轮实现：

```text
DICTATION
EDIT
ACTION
```

并加入：

```text
CommandCandidateDetector
```

## 原则

简单命令优先：

- deterministic rules
- short grammar
- local parsing

复杂模糊情况才允许调用远程模型。

不要使用复杂 Agent Framework。

## 必须避免

普通句子：

```text
“明天下午发送新版测试包给他们”
```

不能因为包含“发送”就触发 SEND。

完成后测试 intent accuracy 和 false trigger rate。

---

# 第十二轮：Voice Edit

先实现最高价值的小集合：

```text
“把 X 改成 Y”
“上一句删掉”
“撤销”
“上一句简洁一点”
“选中的内容优化一下”
```

## 所有修改必须转成 EditOperation

例如：

```json
{
  "type": "replace",
  "target": {
    "type": "fuzzy_text",
    "value": "服务器成本"
  },
  "replacement": "交付成本"
}
```

## 规则

- Document Engine 执行修改
- LLM 可以辅助 parse 或 rewrite
- LLM 不直接替换整个 Document
- 修改默认限定到明确 span

完成后测试不同引用方式和修改冲突。

---

# 第十三轮：Text Reference 扩展

扩展支持：

```text
current_sentence
previous_sentence
current_paragraph
previous_paragraph
selected_text
recent_input
fuzzy_text
```

支持类似：

```text
“前面那句 XXXX 改成 XXX”
“刚才服务器那部分简洁一点”
“上一段正式一点”
“前面那句话有点啰嗦，优化一下”
```

## 低置信度处理

不要擅自修改。

优先：

- discard
- retry
- ask / select target
- 保持原文

完成后测试 fuzzy matching 和 reference resolution。

---

# 第十四轮：Voice Actions

加入：

```text
SEND
UNDO
CANCEL
```

## SEND 要求

必须具备：

- command candidate detection
- context validation
- confidence threshold
- active target validation

原则：

```text
False SEND 成本
>
Missed SEND 成本
```

即：

> 宁可漏触发，也不能误发送。

## 本轮测试

- 单独说“发送”
- 正常句子包含“发送”
- 用户修改过程中说“发送”
- App 切换后说“发送”
- 没有有效输入框时说“发送”

完成后停止。

---

# 第十五轮：Minimal Backend

只有在本地 MVP 已经能够稳定使用后进入本轮。

创建：

```text
src/backend/
```

建议：

```text
Python
FastAPI
```

## 只负责

- Beta authorization
- Provider credential protection
- Remote config
- Feature flags
- Basic telemetry
- Version info

## 客户端继续本地负责

- Document Engine
- Edit Execution
- State Management
- InputTarget
- Target Sync
- Undo
- Conflict Handling

不要把核心编辑逻辑迁移到云端。

不要建立复杂商业后台。

---

# 第十六轮：Beta Access

为少量外部测试用户增加轻量授权。

第一版可使用：

```text
测试码
或
白名单
```

后续可增加：

```text
device binding
expire_at
max_devices
status
```

示例：

```text
BETA-X7KF-8P2M
```

当前不是正式商业 License。

不要加入支付、订阅和复杂用户体系。

---

# 第十七轮：Installer / Version / Update

外部测试前解决：

- Installer
- Version
- Upgrade Path

版本示例：

```text
0.1.0
0.1.1
0.1.2
```

第一阶段不要求完整自动更新。

但必须：

- 可以生成可安装版本
- 可以查看版本号
- 可以知道测试用户当前使用哪个版本
- 新版本可以安全覆盖升级

完成后停止。

---

# 第十八轮：Telemetry / Feedback

为 Beta 增加基础 Telemetry。

## 默认允许收集

```text
app_version
os_version
app_category
asr_latency
cleanup_latency
command_latency
error
crash
compatibility
```

## 默认不要上传

- 完整录音
- 完整聊天内容
- 完整文档内容
- 用户敏感文本

如果调试确实需要内容，必须由测试用户明确开启。

## 本轮目标

能够回答：

- 哪个版本容易崩溃
- 哪些 App 兼容差
- ASR 延迟分布
- Cleanup 延迟
- Voice Command 误触发情况

完成后停止。

---

# 通用执行规则

每一轮执行前：

1. 阅读：
   - `AGENTS.md`
   - `docs/PRODUCT_SPEC.md`
   - `docs/ARCHITECTURE.md`
   - `docs/DECISIONS.md`
   - `docs/ROADMAP.md`
2. 检查当前仓库状态。
3. 只执行当前轮。
4. 不主动提前开发下一轮。
5. 不“顺手”增加无关模块。
6. 不新增不必要 Markdown。
7. 实现后运行相关测试。
8. 如果实际代码与文档不一致，先报告，不要静默修改核心设计。
9. 如涉及重大架构变化，更新 `DECISIONS.md`。
10. 完成后输出：
    - 修改内容
    - 测试结果
    - 发现的问题
    - 尚未解决的问题
    - 是否需要更新 Architecture / Decisions
11. 输出后停止。
