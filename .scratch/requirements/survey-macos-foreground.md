# 勘探：macOS 前台应用观测

当前采用 macOS 上手动运行的应用级观察：查询初始前台、接收实际交来的 AppKit 通知、逐条保存公开属性与出处。依据是决策全景的「前台应用与键鼠观测 / foreground-only」「瞬时事件接收方式 / local-first」「事件时间与顺序 / separate」「原样保存与一致性 / source-native」。这些技术选择由 Agent 在用户授权下采用；用户确认的目标与实现选择分别记录。

例如，从 A 切到 B，本轮 workspace 先交付 B 激活，再交付 A 失活。两行按交付顺序各存一份；以后理解活动时段时，再按通知语义解释。同一应用内部切窗和改标题则属于另一种来源能力。

## 实际来源与样本

本轮在 macOS 27.0（26A428）运行真实 AppKit helper。初始能力查询曾取得 `com.apple.loginwindow`，应用启动时间为 `null`；这些值按接口返回保存。受控交互阶段另建三个 AppKit 测试应用 A、B、C，分别记录激活请求、应用自身的激活与失活回调、窗口成为 key window 的回调及标题修改。以实际完成回调核对动作发生，独立比较 Orisumi 收到的通知。

| 动作 | 应用侧完成证据 | Orisumi 取得结果 |
| --- | --- | --- |
| 约 1 秒间隔切换 | 7 次激活；请求间隔 1,001–1,038 毫秒 | 7/7 次对应激活通知 |
| 约 0.2 秒间隔切换 | 15 次激活；请求间隔 201–203 毫秒 | 15/15 次对应激活通知 |
| 测试应用激活和失活 | 各 22 次回调 | 各 22 次通知；PID、次数、分类顺序和单应用内部顺序一致 |
| 同应用窗口操作 | 4 次窗口成为 key window、1 次改标题 | 该阶段没有新增应用级通知 |

整轮得到 49 条、46,150 字节：1 条 started、1 条初始 snapshot、46 条 notification、1 条 stopped。通知还包括原前台应用的失活与恢复激活。序号连续 1–49，采集根均无失败；49 份原件逐份通过 `orisumi run raw restore` 恢复，独立核对 SHA-256、长度、LF、来源、取得方式、运行身份与序号，全部通过。结束时恢复原前台并退出测试应用。

22 次切换中，workspace 都先交付新应用激活，再交付旧应用失活；21 次测试应用之间的切换，其应用自身回调则先失活、后激活。helper 的激活回调比应用自身完成回调早 5–23 毫秒。两条通道各自记录真实顺序，按类别及单应用核对，不构造统一的源端时间线。

原始样本与实验输出保留在本机：

- `.artifacts/foreground-survey-20261106/`：初始能力查询、Apple 接口文档与最初的保存恢复样本。
- `.artifacts/foreground-interaction-20261106/controlled-9b61a1f4/`：自有测试应用、动作与回调记录、49 条恢复原件、逐次对账及汇总。
- `.artifacts/foreground-network-20261106/`：网络访问限制的对照与开发入口尝试。

公开聚合结果与复现步骤见[前台验收](../../src/Docs/Orisumi.Docs/content/docs/model/validation.mdx#前台观测)。原始记录含本机出处，留在忽略目录中。

## 官方接口及事实边界

| 接口 | 直接给出的事实 | 取得方式 |
| --- | --- | --- |
| `NSWorkspace.frontmostApplication` | 此刻接收 key events 的应用，可为 nil | 初始查询独立成条，保持查询原因与空值 |
| `didActivateApplicationNotification` | workspace 即将激活应用，参数中有 `NSRunningApplication` | 保存原通知名称及参数对象字段 |
| `didDeactivateApplicationNotification` | 应用失去激活的通知 | 分别保留实际收到的通知，不预设成对 |
| `willSleepNotification` / `didWakeNotification` | 系统即将睡眠 / 已唤醒 | 保存原通知；唤醒后另做前台查询 |
| `sessionDidResignActiveNotification` / `sessionDidBecomeActiveNotification` | 用户会话切出前 / 切入后 | 保存原语义；会话切入后另做前台查询 |

订阅使用 `NSWorkspace.shared.notificationCenter`，保留 observer，并运行主 run loop。应用对象在接到通知的主线程回调中立即展开；只把编码完成的字节交给后续保存。动态属性有 run loop 缓存，因此激活通知、当次读到的 `isActive` 与应用自己的完成回调各有含义。系统与会话通知没有 `userInfo` 字典；会话切换的契约也没有承诺与所有锁屏解锁一一对应。

接口依据：[frontmostApplication](https://developer.apple.com/documentation/appkit/nsworkspace/frontmostapplication)、[激活通知](https://developer.apple.com/documentation/appkit/nsworkspace/didactivateapplicationnotification)、[失活通知](https://developer.apple.com/documentation/appkit/nsworkspace/diddeactivateapplicationnotification)、[NSRunningApplication](https://developer.apple.com/documentation/appkit/nsrunningapplication)、[系统睡眠](https://developer.apple.com/documentation/appkit/nsworkspace/willsleepnotification)、[系统唤醒](https://developer.apple.com/documentation/appkit/nsworkspace/didwakenotification)、[会话切出](https://developer.apple.com/documentation/appkit/nsworkspace/sessiondidresignactivenotification)、[会话切入](https://developer.apple.com/documentation/appkit/nsworkspace/sessiondidbecomeactivenotification)。本机 SDK 的 `NSWorkspace.h`、`NSRunningApplication.h` 也已核对。

## 身份、时间与原件内容

AppKit 交来对象；本次原件是通知与选定公开属性的直接观察记录。helper 将取得值忠实编码为 UTF-8 JSONL，托管层保存每行原始字节和 LF。保存范围和格式版本写在[前台应用观测](../../src/Docs/Orisumi.Docs/content/docs/model/foreground.mdx)，取得边界的决定见 [0006](../../src/Docs/Orisumi.Docs/content/docs/model/adr/0006-observations-as-received.mdx)。

应用字段包含名称、bundle ID、bundle URL、可执行文件 URL、PID、启动日期、激活与隐藏等状态、activation policy 和 executable architecture。缺失值写 null；字段分别保留。bundle ID、安装路径和 PID 分别可能缺失、变化或复用，因此当前不合成永久身份。可直接用 JSON 表达的其他 userInfo 值照存；无法编码的对象留下键和类型，属于明确的取得边界。

`observedAt` 是本机取得时刻，`sequence` 是本次 helper 输出顺序，`run` 是本次 UUID。来源未给出的事件时间与持久游标保持缺失。采集根记录保存起止时间、主机、采集器版本、helper 实际路径及对应的 run / sequence。`started` 包含系统条件与订阅清单，在注册 observer 之前输出，代表程序开始；订阅前的事件仍未知。

## 采用的实现与取舍

当前采用 Swift helper 处理 AppKit 类型、ARC、订阅和主 run loop；.NET 处理进程生命周期、实际输出字节和 RawStore。每个完整观察行立即保存为一份原件，材料路径 `observation.jsonl`、取得方式 `appkit-observation-jsonl`。这复用现有恢复与校验能力；实际写入成本形成问题时，再测量封批或追加存储。

| 候选 | 收益 | 维护成本 |
| --- | --- | --- |
| C# 直接调用 Objective-C runtime | 单语言，发布结构较直接 | 方法 ABI、对象生命周期、回调注册、字段封送和 run loop 由项目维护 |
| Swift helper | 官方绑定与编译器处理 AppKit 类型；托管侧只接字节与退出结果 | macOS 构建需要 Swift 工具链；随 CLI 携带原生程序并验证进程与协议故障 |

这是授权下的工程选择。当前项目使用通用 net10.0，已有构建工具可编译小 helper；对这一窄接口，后者所需维护面更小。macOS 构建目标位于 `Orisumi.Capturing.csproj`，使用 `xcrun swiftc`，产物随项目引用进入 CLI 和测试输出。运行阶段使用已编译程序。其他平台的前台入口报告平台条件。

达到时长或收到取消时，父进程通过 stdin 请求 stop，继续读取直到正常结束。helper 忽略 SIGINT，由父进程统一收尾；stdin 关闭也会退出。请求后 5 秒仍未退出则强制停止并报告失败，产品和开发入口各自留有更长的有界收尾时间。真实 TTY Ctrl+C 已得到返回码 130，started、snapshot、stopped 均恢复通过。

半行和协议错误行保留原始字节并记录失败。有效行暂时保存失败而随后随失败根保存成功时，仍使用有效行路径并计入已保存观察数。首次保存或协议失败后停止读取；此前完整采集根可恢复，当前待存行尽力保存，管道中未读尾部仍可能失去。进程或系统突然停止也存在未提交窗口。

## 条件、覆盖与剩余测量

生产 helper 只调用 AppKit，不调用 AX，也不申请辅助功能、输入监控或屏幕录制权限。最初临时能力检查读取 AX 信任状态为 true；全新未授权运行身份仍待独立验证。当前在 GUI 用户会话中通过 `orisumi` 手动运行，未安装登录启动服务。

| 范围 | 当前证据与边界 |
| --- | --- |
| 应用查询与受控切换 | 本轮 7 次常速、15 次快切及逐条保存恢复通过；更短间隔和其他应用形态另测 |
| 窗口与标题 | 4 次切窗、1 次改标题没有新增应用级通知；要取得这些内容需要另一个来源能力 |
| 正常停止与重启 | 时长结束、真实 Ctrl+C、各轮独立 run ID 已验证；未运行期间保持缺口 |
| 故障与已有前缀 | 9 项前台测试覆盖即时读回、取消、非零退出、半行、协议乱序、缺少停止、超时、存储失败与原生 SIGINT；开发入口另有 2 项进程生命周期回归 |
| 睡眠与会话变化 | 通知和恢复后重查已实现；动作组合是独立待测范围 |
| 网络 | 实现路径使用本机接口和本地存储；限制网络后的运行证据见验收页，开发工具启动与采集行为分别判断 |
| 长期运行 | 全天条数、峰值、文件数量、CPU、内存、耗电与写入延迟尚未测量 |
| 持久性 | 已提交前缀可恢复；系统断电、磁盘失败和未提交尾部各有验证边界 |

当前证据支持采用手动 macOS 应用级观察。上述其他场景按实际连续使用需要逐项验证；它们不改变本轮已测范围。前台应用是系统状态，关于人的注意力与活动时段的解释留给后续读出。

本轮按 Matt 的深模块架构方法审阅了 AppKit → JSONL → RawStore 的职责与故障边界；实际交付和恢复能够分别定位问题，没有发现值得新增抽象或重写的摩擦。
