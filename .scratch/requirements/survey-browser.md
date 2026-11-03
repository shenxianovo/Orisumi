# 勘探：浏览器

2026-11-03。这一份还没有新采的数据：先整理旧 Heartbeat 采了什么、扩展能采什么，再定勘探程序采哪些。

## 旧 Heartbeat 采了什么

来自 9-12 重写前的代码（`Heartbeat.Collector.Browser`）和旧库。

- 权限只有 `tabs`、`storage`、`unlimitedStorage`、`alarms`，用到的事件是 `tabs.onActivated`、`tabs.onUpdated`、`windows.onRemoved`。
- 记的是**每个窗口的活动标签页**：URL、标题、windowId。URL 规范化后相同就延长这一段，变了就开新的一段。规范化会去掉 query 和 fragment（YouTube 的 `v` 除外），完整 URL 另存。
- 没有监听窗口焦点，所以浏览器在后台、窗口被挡住时也在计时。旧库里有 35 段浏览器时段超过 4 小时，合计 355 小时。
- 7-05 起 3.3 万条，工位 Mac 上因缓冲溢出丢了 15 万条。

## 直接读浏览器文件

Chrome 本机的 History 是一个 SQLite，里面有访问记录、跳转方式、停留时长、搜索词和下载，保留 90 天。这次读取被 macOS 拦下（Operation not permitted），要读得在系统设置里给终端授予权限。本机还有 Edge、Brave 和 Chrome for Testing 的数据目录。

## 扩展能采什么

按 Manifest V3。「持久」表示事后还能补，「瞬时」表示错过就没了。

| 能力 | API | 内容 | 瞬时/持久 |
| --- | --- | --- | --- |
| 标签页 | `tabs` | URL、标题、图标、是否在播放声音、静音、固定、分组、被丢弃、由哪个标签页打开、最后访问时间；创建、更新、激活、移动、关闭等事件 | 瞬时 |
| 窗口焦点 | `windows` | 哪个窗口有焦点，浏览器整体失焦，最小化、全屏 | 瞬时 |
| 标签组 | `tabGroups` | 组名、颜色、折叠 | 瞬时 |
| 历史 | `history` | 每次访问的时间、跳转方式（点链接、地址栏输入、表单提交、刷新等）、访问次数、输入次数 | 持久，90 天 |
| 导航 | `webNavigation` | 每个框架的导航，含重定向、前进后退、单页应用的路由变化、新标签页由谁打开 | 瞬时 |
| 网络请求 | `webRequest`（只观察） | 每个请求的 URL、方法、类型、状态码、发起方、时间 | 瞬时，量很大 |
| 书签、阅读清单 | `bookmarks`、`readingList` | 完整的树和添加时间 | 持久 |
| 下载 | `downloads` | 文件名、来源 URL、引用页、类型、大小、起止时间 | 持久 |
| 空闲和锁屏 | `idle` | active、idle、locked 三种状态，最短 15 秒判定 | 瞬时 |
| 其他设备的标签页 | `sessions` | 最近关闭的标签页和窗口，登录同一账号的其他设备上开着的标签页 | 半持久 |
| 常访问的站点 | `topSites` | 新标签页上的那几个 | 持久 |
| 页面内容 | 内容脚本 | 正文、元信息（标题、描述、作者、canonical）、滚动深度、页面是否可见、选中和复制的文字、点击、表单输入 | 瞬时 |
| 媒体播放 | 内容脚本 | 视频和音频在播什么、进度、是否暂停、倍速，Media Session 里的标题和作者 | 瞬时 |
| 页面快照 | `pageCapture`、`tabs.captureVisibleTab` | 整页存成 MHTML，可见区域截图 | 瞬时 |
| 登录身份 | `identity` | 浏览器登录的账号 | 持久 |
| 设备信息 | `system.*`、`runtime.getPlatformInfo` | CPU、内存、显示器、平台 | 持久 |

## 拿到它的代价

- 内容脚本、网络请求和截图需要 `<all_urls>`，安装时 Chrome 会提示「读取和更改所有网站上的数据」。无痕窗口要另外允许。
- 每个浏览器、每个 Profile 都要装一次。Chrome、Edge、Brave 都能用同一个扩展。
- MV3 的后台是 Service Worker，空闲约 30 秒就被停掉，事件来了再唤醒，内存里的状态会丢。
- 扩展不能直接写本地文件或 SQLite，要经本机的程序：一是 Native Messaging，Chrome 按需启动一个本机程序，需要在 `~/Library/Application Support/Google/Chrome/NativeMessagingHosts/` 放一个登记文件；二是本机 HTTP 服务，要一直开着。

## 勘探程序的方案（待用户确认）

- 一个扩展，订阅上表能订阅的全部事件，把事件对象原样转发，不规范化、不合并。
- 内容脚本采页面元信息、可见性、媒体播放、选中和复制；正文和 MHTML 量大，先只在页面加载完成时各存一份。
- `webRequest` 量很大，先开一天看体量。
- 启动时把 `history`、`bookmarks`、`downloads`、`topSites`、`sessions` 全量拉一次，这几项是持久的，可以补回 90 天。
- 本机程序收到什么写什么，存进 `probes/browser/` 自己的 SQLite。

## 待定的问题

- 走 Native Messaging 还是本机 HTTP 服务？
- 先装 Chrome，还是 Chrome、Edge、Brave 都装？
- 扩展的 `history` 也能拿到这 90 天的访问和跳转方式，但停留时长和搜索词只在 History 文件里。要不要给终端开权限，直接读这个文件？
