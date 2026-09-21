# Product Spec

# AI 实时语音输入工具 — 产品定义

## 文档定位与当前边界（2026-09-16）

本文描述目标体验，流程中的 Document Engine、AI Cleanup、Intent Router、Voice Edit、Voice Send 均为后续设计，不代表已实现。当前已有默认 HUD／剪贴板输入链路及显式启用的 TSF 实验，真实语音连续输入的稳定性仍待验证。

进度以 [ROADMAP](ROADMAP.md) 为准，实现与技术选型见 [ARCHITECTURE](ARCHITECTURE.md)，验证证据见 [TESTING](TESTING.md) 。[ORIGINAL_PLAN](ORIGINAL_PLAN.md) 保留原样，其 18 个开发轮次不等于当前实验阶段，也不构成本次扩大开发范围的指令。

## 1. 一页纸产品定义

### 1.1 产品是什么

一个运行在桌面系统上的 **全局 AI 输入层**。

目标是在用户正在使用的文本输入框中工作，减少切换窗口。兼容性必须逐应用、逐控件验证；微信、浏览器、编辑器和办公软件是候选场景，不承诺任意文本框可用。当前 TSF 实验仅限 Notepad、Chrome。

它不是传统“录音 → 等待 → 转文字”的语音输入工具，而是一个 **实时、可编辑、可通过自然语言控制的输入系统**。

核心原则：

> 用户说话时，文字应持续出现；默认忠实保留表达。后续可选择轻度整理，并在可验证的目标范围内使用语音编辑；键盘和鼠标始终优先。发送属于单独受控的后续能力。

------

### 1.2 核心体验

#### A. 实时语音输入

用户开始说话后，文字应快速、持续出现，而不是等录音结束后一次性返回。

示例：

用户说：

“我觉得这个，呃，这个方案吧，其实可以先推进一下。”

默认保留 ASR 识别出的措辞，不额外调用 LLM 去口癖。未来用户开启 Light 整理后，允许将稳定的小段整理为：

“我觉得这个方案可以先推进一下。”

------

#### B. 可选的轻度整理（后续）

仅在用户开启 Light 且具备局部替换、冲突检测、撤销能力后，AI 才可在后台进行轻度处理：

- 去除“嗯、啊、那个、就是”等无意义口癖
- 去除明显重复
- 修正明显语病
- 自动补充合理标点
- 在不改变原意的情况下让表达更顺畅

默认原则：

> 默认 None，Light 需主动开启；最小必要修改，可恢复整理前文本。

系统不应为了“写得更高级”而主动大幅改变用户原本的措辞和表达风格。

------

#### C. 语音修改前文

用户无需停止整个输入过程，可以直接用自然语言修改已经输入的文本。

示例：

“把前面的‘明天下午三点’改成‘明天下午四点’。”

“刚才那句话删掉。”

“上一句有点啰嗦，简化一下。”

“前面关于成本那句话说得专业一点。”

“把服务器成本改成交付成本。”

系统定位对应文本，并只修改目标范围。

------

#### D. 键盘和语音混合编辑

键盘、鼠标和语音共同编辑同一个文本状态。

例如：

1. 键盘输入一句话
2. 语音：“这句话简洁一点”
3. 键盘补充几个字
4. 语音：“把刚才补充的内容整合进上一句”
5. 鼠标选中一段文字
6. 语音：“改得正式一点”

键盘输入默认保持原样，AI 不应未经用户指令主动修改用户刚刚键入的内容。

------

#### E. 语音执行操作

部分语音不是需要输入的文字，而是控制命令。

例如：

“发送。”

“撤销刚才的修改。”

“删掉上一句。”

“重新说。”

系统必须区分：

- Dictation：用户希望写入的内容
- Edit：针对已有文本的修改
- Action：发送、撤销等操作

只有通过命令校验的「发送」才作为 Action。普通叙述、引用或小说对白中的「发送」「删掉上一句」仍是正文，不能仅凭关键词执行或吞掉文字。Voice Send 默认关闭。

------

### 1.3 产品核心对象

系统内部不应把内容理解成“一段 ASR 转录字符串”，而应维护一个持续变化的：

**Live Editable Document**

至少包含：

- 当前完整文本
- 当前光标位置
- 当前选区
- 当前句 / 上一句
- 当前段 / 上一段
- 最近新增文本
- 最近修改记录
- 文本来源：voice / keyboard / paste / AI
- Undo history

语音、键盘、鼠标和 AI 都只是这个 Document 的不同操作入口。

------

### 1.4 MVP 成功标准

按阶段验收，不把完整愿景作为当前联调的完成条件：

1. 用户开始说话后，很快能看到第一批文字
2. 持续说话时，文字持续跟随，而不是录完再统一出现
3. 当前先证明连续多段输入、停止、人工接管和焦点切换安全，写入失败不向其他目标重放
4. 后续在文档状态同步与撤销完成后，才验证可选整理和以下连续语音编辑流程：

“明天下午开个产品会议。”

→ 文本出现

“改成四点。”

→ 修改时间

“加一句请大家提前准备。”

→ 补充文字

“上一句有点啰嗦，优化一下。”

→ 局部优化

“发送。”

→ 当前应用真正执行发送

产品优先级：

**Latency > Controllability > Intelligence**

首先做到快和可控，再增加更复杂的 AI 能力。

------

## 2. 核心用户流程

### Flow 1：实时语音输入

用户聚焦已验证兼容的输入框

↓

触发全局语音输入

↓

立即开始音频采集

↓

Streaming ASR 持续返回 partial transcript

↓

显示 Interim Text：默认模式显示在 HUD；TSF 实验尝试在目标输入框内显示

↓

ASR 判断部分内容稳定

↓

转为 Stable Text

↓

稳定文本立即进入显示／写入路径，不等待 AI。后续具备 Document Engine 后由其维护对应状态

↓

继续接收后续语音

独立旁路：若已具备整理能力且用户开启 Light，AI 异步处理稳定的小段，返回后经目标、版本、原文校验再局部更新；未开启则不发起整理。

------

### Flow 2：边说边纠正自己

当前文本：

“明天下午三点开产品会议。”

用户继续说：

“不对，改成四点。”

↓

仅在 Voice Edit 已开启、存在明确的近期目标且不是引用／对白时，Intent Router 将其确认为 Edit；歧义不自动替换

↓

解析：

target = “三点”

replacement = “四点”

↓

Document Engine 定位对应文本

↓

局部替换

↓

结果：

“明天下午四点开产品会议。”

------

### Flow 3：引用前文修改

当前内容有多句话。

用户说：

“前面那句‘服务器成本比较高’有点啰嗦，优化一下。”

↓

识别为 Edit

↓

提取：

reference_text = “服务器成本比较高”

instruction = “更加简洁”

↓

在 Document 中进行文本 / 模糊匹配

↓

找到对应 span

↓

仅向 LLM 提交目标文本和修改指令

↓

重新检查目标、版本、原文和人工编辑状态，通过后替换该 span，并保存可撤销的原文

------

### Flow 4：位置引用

用户说：

“上一句删掉。”

或：

“第二段正式一点。”

↓

解析 TextReference：

- previous_sentence
- current_sentence
- previous_paragraph
- current_paragraph
- selected_text
- recent_input
- fuzzy_text

↓

Document Engine 获取对应 span

↓

执行 Delete / Rewrite / Replace 等操作

------

### Flow 5：键盘 + 语音

用户使用键盘输入：

“这个项目主要的问题是成本还有交付周期”

↓

Document State 更新

↓

用户说：

“这句话专业一点。”

↓

获取 current_sentence

↓

局部 AI Rewrite

↓

结果：

“当前项目的主要问题集中在成本和交付周期两个方面。”

↓

用户继续键盘编辑

↓

Document State 再次同步

语音和键盘始终操作同一份 Document。

------

### Flow 6：发送

用户完成文本后说：

“发送。”

↓

Intent = ACTION_SEND

↓

不写入“发送”两个字

↓

确认 Voice Send 已开启、命令意图明确、目标与会话未变、应用发送规则已验证；任一条件不满足则不发送

↓

执行 Enter / Ctrl+Enter / App Adapter

↓

消息发送

------

## 3. 技术约束

### 3.1 第一原则：禁止 Batch-first 架构

不能采用：

录音开始
→ 等用户说完
→ 保存完整音频
→ 上传
→ ASR
→ LLM
→ 返回文字

这会造成不可接受的交互延迟。

必须采用：

Microphone
→ Continuous Audio Stream
→ Streaming ASR
→ Partial Transcript
→ Incremental Document Update

------

### 3.2 ASR 与 AI 必须解耦

禁止：

Audio → ASR → LLM → UI

因为 LLM 会阻塞文字显示。

推荐：

Audio
↓
Streaming ASR
↓
Transcript Delta
↓
立即显示

同时：

Stable Semantic Chunk
↓
Async AI Cleanup
↓
局部更新 Document

即：

**ASR 高频更新，AI 低频整理。**

------

### 3.3 文本状态至少分三级

Document 中至少维护：

#### Interim

ASR 尚未确定的内容。

允许频繁变化。

#### Editable

近期已经较稳定，但允许 AI 根据后续上下文进行轻微修正。

#### Committed

已经稳定的前文。

默认情况下 AI 不再主动修改。

只有用户明确下达 Edit 指令时才能修改。

------

### 3.4 不允许全文重写

所有编辑操作默认采用局部 span 修改。

例如：

ReplaceText(start, end, replacement)

RewriteSpan(start, end, instruction)

DeleteSpan(start, end)

InsertText(position, text)

而不是：

rewriteEntireDocument()

目的：

- 降低延迟
- 降低 token 消耗
- 减少误修改
- 提高用户控制感
- 保持光标和文本状态稳定

------

### 3.5 Intent Router

未来开启语音命令后，先用低成本规则识别稳定片段中的候选，再严格校验。普通听写不等待 LLM；未启用命令时保持 Dictation。分类为：

```text
DICTATION
EDIT
ACTION
```

EDIT 可继续细分：

```text
REPLACE
DELETE
INSERT
REWRITE
MOVE
UNDO
```

后续 ACTION 的设计范围：

```text
SEND
CANCEL
UNDO
```

Intent Router 应优先考虑低延迟。

不要为了简单指令调用高成本、长推理模型。

------

### 3.6 编辑指令结构化

自然语言指令最终应转换为结构化 Edit Operation。

例如：

用户：

“把前面那个服务器成本改成交付成本。”

内部：

```json
{
  "intent": "replace",
  "target": {
    "type": "fuzzy_text",
    "value": "服务器成本"
  },
  "replacement": "交付成本"
}
```

用户：

“上一句有点啰嗦。”

内部：

```json
{
  "intent": "rewrite",
  "target": {
    "type": "previous_sentence"
  },
  "instruction": "make_concise"
}
```

Document Engine 负责执行，不让 LLM 直接控制整个输入框。

------

### 3.7 延迟目标

以下为体验目标，尚非实测结论。分别记录首个 HUD 文字和首个目标框文字，不能用前者代替后者；具体计时口径见 [TESTING](TESTING.md) 。

#### ASR 首字可见

目标：

< 500 ms 为优秀目标

应尽量避免 > 1 s

#### 持续转录

文字视觉上持续跟随用户讲话，不得出现数秒级整体滞后。

#### 简单编辑命令

如：

“把三点改成四点”

应尽可能采用本地 / 结构化编辑，避免完整 LLM Rewrite。

#### AI Cleanup

允许比 ASR 略慢，但不得阻塞实时转录。

------

### 3.8 AI Cleanup 原则

用户开启 Light 后，Cleanup 只能进行：

- Filler removal
- Duplicate removal
- Punctuation
- Obvious grammar correction
- Minimal sentence organization

默认禁止：

- 大幅改写风格
- 添加用户没有表达的信息
- 主动总结
- 改变事实
- 为了“专业”而替换大量措辞

Cleanup Level：

- None：默认；不做额外的 AI 改写，ASR 自带标点仍受供应商配置影响。
- Light：后续可选；只处理已稳定且仍有效的近期语音片段。

Medium、自适应文风和整段润色暂不进入首版。人名、专有名词、数字、否定关系、小说对白中的停顿和重复不得被随意改动。局部替换前必须校验会话、目标、版本和原文；人工编辑使旧请求失效。明确的「把 X 改成 Y」属于 Voice Edit，不混入默认 Cleanup。

------

### 3.9 外部应用兼容

第一阶段 Windows 优先。

不要第一版直接开发完整 Windows IME。

优先验证：

Desktop Background App
+
Global Shortcut
+
Audio Capture
+
Streaming ASR
+
Document Engine
+
Clipboard / SendInput / UI Automation
+
Foreground App Detection

要求：

核心架构不能绑定某一个应用。

输入目标必须抽象成：

InputTarget / AppAdapter

以后可针对：

- 浏览器
- 微信
- 飞书
- Slack
- ChatGPT
- Claude
- Cursor
- VS Code

增加专用 Adapter。

------

### 3.10 安全限制

任何可能产生不可逆结果的 Action 都需要比普通 Dictation 更严格。

尤其：

SEND

必须：

- 高置信度识别
- 清晰地区分 Dictation 与 Action
- 避免把普通句子中的“发送”误判成命令

第一版宁可漏执行一次 Send，也不要误发送。

------

## 4. UI 参考

UI 原则：

**产品主体不是一个窗口，而是附着在用户当前工作流上的轻量状态界面。**

尽量避免让用户为了语音输入切换到独立 App。

------

### 4.1 默认状态

后台驻留系统托盘。

用户正常使用电脑时不显示主窗口。

------

### 4.2 开始语音

用户触发快捷键后，在当前输入区域附近或屏幕底部显示一个非常轻量的 Voice HUD。

参考形态：

```text
┌──────────────────────────┐
│  ●  ▁▃▆▅▂   Listening... │
└──────────────────────────┘
```

只表达：

- 是否正在监听
- 麦克风输入状态
- 当前状态

不要显示复杂控制按钮。

------

### 4.3 实时文本状态

目标体验是直接在输入框显示文字。当前默认模式的 partial 只在 HUD 显示、stable 才粘贴；输入框内 partial 是 TSF 受限实验，不应混称为相同的实时输入体验。

必要时可以通过视觉差异区分：

```text
已稳定文字 + 正在识别文字
```

例如：

```text
我认为这个方案可以继续推进，
但是成本方面可能还需要进一步确｜认
```

Interim Text 可以采用轻微透明 / 下划线等状态表现。

稳定后恢复正常文本。

不能因为 AI 修改导致整段文字明显闪烁或反复跳动。

------

### 4.4 AI 修改反馈

用户说：

“上一句简洁一点。”

UI 不需要弹出聊天窗口。

短暂显示：

```text
✦ Optimizing previous sentence…
```

修改完成且目标校验通过后，仅替换对应 span，并提供撤销／恢复原文。用户已修改该范围时丢弃迟到结果。

如果修改非常快，可以完全不显示中间状态。

------

### 4.5 Command Feedback

当识别到：

“发送”

短暂显示：

```text
Send
```

通过 3.10 的意图与目标校验后才执行；显示反馈不能替代校验。

当识别到：

“撤销”

显示：

```text
Undo
```

重点是让用户知道系统把这句话理解成了“命令”，而不是 Dictation。

------

### 4.6 歧义处理

如果系统无法确定用户说的“前面那句话”指哪一句：

不要擅自修改。

可以显示一个非常轻量的候选选择：

```text
Which sentence?

1  服务器成本仍然比较高
2  当前交付周期存在风险
```

允许：

- 鼠标点击
- 键盘数字
- 继续说“一”

首版尽量减少这种情况。

------

### 4.7 设置界面

以下为后续设置范围，不代表当前已有完整设置页。按能力落地，不提前放置不可用开关：

General

- 启动快捷键
- 开机启动

Voice

- 麦克风
- 输入语言

AI

- Auto Cleanup：None（默认）／Light（后续）

Commands

- Voice Edit 开关
- Voice Send 开关

个人词典（后续）

- 手动添加、编辑、删除专有词

Debug（开发版本）

- ASR latency
- LLM latency
- Intent
- Current foreground app
- Current document state

不要第一版制作复杂的 Prompt、自定义 Agent、工作流系统或大量 AI Personality 配置。

------

## 5. 首版明确不做

为了避免开发范围失控，v0.1 暂不做：

- 完整 Windows IME
- 手机端
- macOS
- 自动理解所有应用的完整上下文
- 自动根据 App 改变写作风格
- 复杂 Agent
- 长文创作
- 文档级重写
- 多人协作
- 云同步
- 大量个性化设置
- 完整商业化 / 订阅系统

当前不建设登录、License 或商业化接口。外部 Beta 前再单独解决服务凭据保护，不向测试用户分发供应商密钥。

------

## 6. 工程优先级

P0 — 必须先证明：

1. Streaming ASR
2. 实时 partial transcript
3. 已验证目标中的可靠注入和焦点保护
4. Stable / Interim 状态
5. 多段连续输入和停止／重启
6. 键鼠接管及焦点切换时停止迟到写入

P1（基础链路稳定后，逐项安排）：

1. 最近结果恢复和手动个人词典，分别验证
2. Document Engine、目标状态同步与 Undo，作为自动替换的前提
3. 可选 Light Cleanup
4. Intent Router 与明确选区／“把 X 改成 Y”编辑
5. “上一句删除 / 优化”等更复杂引用

P2：

1. Voice Send
2. App Adapter
3. 更复杂的模糊引用
4. 设置界面

开发过程始终优先测量：

- Audio → first transcript latency
- Audio → stable transcript latency
- Edit command latency
- Cleanup latency
- 错误修改率
- Command 误触发率

不要仅以“功能已经实现”作为完成标准。

## 7. 调研借鉴与采用边界（2026-09-16）

| 官方可观察行为 | 本项目采用的方向 | 当前状态 |
| --- | --- | --- |
| 微信输入法公开版本记录包含语音模型、离线语音和语音整理能力 | 关注连续可见文字、嘈杂环境与中文／中英混说体验；模型选型继续以本项目实测为准 | 不据此切换模型；离线 ASR 未选型 |
| Typeless 提供录音状态反馈、停止后整理文本并插入目标的流程 | 借鉴明确的开始、监听、结束和错误反馈 | 保留本项目流式主链路，不把停止后出稿等同于目标框内逐字输出 |
| Typeless 历史支持复制、重试和音频下载，并有词典功能 | 优先借鉴失败后取回最近结果与手动专有词维护 | 均未实现；音频历史和重试不直接照搬 |
| Typeless 展示去口癖、自我纠正、格式整理及选中文本编辑 | 借鉴可选 Light 和明确选区编辑，强调数字、否定及原文保护 | 等文档同步、冲突检测和撤销具备后验证 |

### 7.1 最近结果恢复

后续最小方案只在内存保留当前／最近一次会话的稳定转录和写入状态，供用户主动复制。新会话替换旧记录，退出即清除；不得默认保存音频或建立长期历史。未完成的 partial 单独标注，不能冒充完整结果。复制是用户动作，不自动粘贴，也不对确认状态不明的写入做重试。不能确认目标已接收时明确标记「写入状态未知」。具体容量上限与清除入口在该能力实现前确定。

### 7.2 手动个人词典

优先覆盖中文人名、小说角色名、产品名和中英混合技术词。词条由用户手动维护，默认在本地保存；只在用户明确启用供应商热词功能且供应商支持时发送所需词条。它不是模型训练，也不保证识别准确；不得靠全文无条件替换修正同音词。不默认监控其他应用键盘来自动学习。

### 7.3 隐私与范围

当前云端 ASR 会接收录音音频；本地 UI 或本地词典不等于离线识别。未来语义编辑仅提交用户指定范围和必要指令，不默认读取整页、整个应用或完整历史。是否用于训练、保存多久、是否云同步是不同问题，应分别说明，不能替供应商承诺零留存。自动文风、全局上下文、云历史与 Agent 能力继续延后。

### 7.4 来源与证据限制

- [微信输入法官方版本记录](https://apps.apple.com/cn/app/微信输入法/id1618175312?platform=ipad) 提及离线语音及语音整理；不能外推到所有平台、版本或所有场景。已查资料没有明确披露当前每条路径采用的模型名称，更不能认定均为本地模型或混元。
- [微信输入法桌面隐私政策](https://wxkeyboardcms-1258476243.file.myqcloud.com/page/article/index/11.html) 描述了上传音频到腾讯服务器完成语音识别的路径；这与部分版本提供离线能力需分别理解。
- Typeless 官方：[听写流程](https://www.typeless.com/help/quickstart/dictate)、[历史与词典](https://www.typeless.com/help/quickstart/history-and-dictionary)、[设置](https://www.typeless.com/help/quickstart/settings)、[选中文本编辑](https://www.typeless.com/ask-anything) 。这些是功能描述，不是本项目已完成的同条件延迟测试。
- [Typeless 数据控制说明](https://www.typeless.com/data-controls) 描述云端转录及可选历史同步等边界；不据此推断其具体 ASR 模型或内部架构。官方旧帮助页与新政策的保留／同步描述需按版本理解，不能笼统写成「全部只在本地」。

调研采用的是可观察交互和明确的数据边界；本文提出的优先级、默认 None 和最小恢复方案是本项目取舍。
