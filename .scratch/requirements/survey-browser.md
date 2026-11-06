# 勘探：浏览器

当前已实测 Chrome for Testing 156.0.8078.4 的 MV3 标签、窗口和 Native Messaging。使用全新的浏览器数据目录、受控本机页面、Default 与 Profile 1；真实用户 Profile 的全天量、休眠与重新登录另测。

## 原始数据

扩展取得回调对象，Chrome 将转交对象编码为 UTF-8 JSON，前面加四字节本机端序长度。勘探程序分别留下完整 frame、payload、接收时间、host 启动身份与调用 origin。下面是两个真实样本，内容为受控测试：

```json
{"workerRunId":"5ab2deac-a88b-4e66-96be-4f11e88683ac","sequence":8,"observedAt":"2026-11-06T13:38:27.490Z","kind":"callback","operation":"tabs.onActivated","callbackArgs":[{"tabId":1052337211,"windowId":1052337209}]}
{"workerRunId":"5ab2deac-a88b-4e66-96be-4f11e88683ac","sequence":3,"observedAt":"2026-11-06T13:38:26.750Z","kind":"query","operation":"windows.getAll","arguments":[{"populate":false,"windowTypes":["normal","popup"]}],"requestedAt":"2026-11-06T13:38:26.749Z","completedAt":"2026-11-06T13:38:26.750Z","triggerSequence":2,"callbackArgs":[[{"alwaysOnTop":false,"focused":true,"height":945,"id":1052337209,"incognito":false,"left":22,"state":"normal","top":55,"type":"normal","width":1200}]]}
```

激活回调只含 `tabId/windowId`；URL 和标题来自另外的查询或更新回调。查询请求时间与返回时间分别记录，两个查询各自成立。

## 取得了什么

48 秒受控过程共留下 141 个信封，payload 合计 73,053 字节，加长度头为 73,617 字节；132 个在关闭浏览器前，余下 9 个在收尾。每份 frame 的长度和 payload 均逐字节核对。这是受控序列体量，日常频率尚未测量。

| 场景 | 实际观察 |
| --- | --- |
| 两个窗口 | 各自有 active 标签；active 与窗口 focused 分别出现 |
| 最小化 | 取得 `windows.onFocusChanged(-1)`；保存接口值 |
| 单页应用 | pushState、hash、标题变化都取得 `tabs.onUpdated`；query、fragment、编码保持来源值 |
| 创建、关闭、跨窗口移动 | 取得 onCreated、onRemoved、onDetached、onAttached；同一 URL 仍可有多份事件 |
| 35 秒空闲 | 所有调试连接断开期间保持 Native port，重新连接后仍是同一个 Worker 运行身份 |
| Worker 强制结束 | 新标签事件唤醒新 Worker，取得新的运行身份 |
| Native host 结束 | 扩展继续执行；约 1.6 秒断连空档有 15 个 callback/query 信封未送到 host |
| 断连期间短暂标签 | 新建后关闭的标签已不在重连快照中，来源事件无法事后补回 |
| 两个 Profile | 扩展 ID 与 caller origin 相同，Worker 运行身份不同；origin 本身无法区分 Profile |

事件是瞬时来源，只能留下扩展当时取得的内容。启动快照能得到当前状态，无法说明之前经历了什么。浏览器保留的历史和文件是另一种来源，不能替代窗口焦点和短暂标签事件。

## 权限、生命周期与限制

勘探只申请 `tabs`、`nativeMessaging`，没有网页 host permissions。tabs 权限使接口能交出 URL、pendingUrl、title、favIconUrl；标签更新对象的其他现有属性也原样留下。[Tabs API](https://developer.chrome.com/docs/extensions/reference/api/tabs)

窗口事件与查询采用普通窗口和 popup 的范围。`WINDOW_ID_NONE` 描述扩展当前可见范围内的失焦，也可能与扩展看不到的窗口有关；它自身不证明整台机器上的全部浏览器都失焦。系统前台应用来自独立的 macOS 来源。[Windows API](https://developer.chrome.com/docs/extensions/reference/api/windows)

Native port 由浏览器启动本机程序，每个 port 对应一份进程；连接调用返回并不证明 host 已收到数据，实际落盘才是证据。Chrome 到 host 的单条上限为 64 MiB，反向为 1 MB。[Native Messaging](https://developer.chrome.com/docs/extensions/develop/concepts/native-messaging)

本次在可自由加载扩展的 Chrome for Testing 中完成。普通 Chrome 的扩展安装、浏览器未运行、扩展被禁用、睡眠、磁盘写入失败和真实长期负载分别影响覆盖；日常安装后继续测量。无痕窗口取决于该 Profile 的允许设置。

## 由样本得到的实现选择

- 分别保存回调与随后查询；所有选中事件的回调参数都留下，包括后台标签更新。
- 用扩展本地随机 instance 区别通常情况下的安装，用 Worker run 和 sequence 表达该次执行内的顺序。复制 Profile 可能复制 instance，浏览器未提供稳定的公开 Profile ID。
- 先暂存观测，接收器发布原件根后回执，扩展再移除暂存；Worker 重启继续重放。断连样本证明了这一步的实际需要。
- 暂存选择 storage.local，开放 API 的默认空间上限，保留真实磁盘限制；先保存、后整理。[Storage API](https://developer.chrome.com/docs/extensions/reference/api/storage)

与 macOS 前台来源重合的是浏览器应用的前后台变化。窗口焦点、活动标签和 URL 是浏览器接口补充的事实；三个来源值之间的对应与人的注意力由后续读取决定。

## 后续来源

历史、书签、下载、其他设备标签、网页正文、截图、媒体与网络请求保留为后续候选，逐项重新勘探。此前直接读取本机 History 文件被系统权限拦下的事实仍成立；当前路线通过扩展接口取得事件，没有改变该文件的访问权限。
