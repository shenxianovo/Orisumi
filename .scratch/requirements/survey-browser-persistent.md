# 浏览器持久记录勘探

普通 Chrome 的本地 History 仍受系统访问限制；隔离 Chrome for Testing 的完整数据库与文件取得已跑通。扩展接口能取得历史、书签和下载的视图，但实测少于本地文件保存的内容。据此采用先保存API实际视图的路线，范围与查询方式见[持久记录模型](../../src/Docs/Orisumi.Docs/content/docs/model/browser-persistent.mdx)。这份记录描述来源勘探，产品接入与运行证据见[验收](../../src/Docs/Orisumi.Docs/content/docs/model/validation.mdx#持久记录与重启)。

## 范围与证据

本次由 Agent 按已授权的勘探范围执行。只尝试当前用户普通 Chrome 的已知 Default 路径；受控访问、书签创建与删除、下载、重启都发生在新建的一次性测试 Profile。没有修改日常扩展、Native Messaging 登记或系统访问权限。普通 Chrome 的既有 host 在勘探后仍运行。

| 证据 | 内容 |
| --- | --- |
| `.artifacts/browser-persistent-20261106T161257Z-9ed45d/` | 完整受控一轮；原 API 响应、三组 History/Bookmarks、取得窗口、方法、路径、hash、schema 与核对结果 |
| 同目录 `report.json`、`comparison.json` | Chrome for Testing 156.0.8078.4；39 次接口返回共 12646 B，45 个接口/文件对象重新核对长度与 SHA256 |
| 同目录 `daily-default/provenance.json`、`daily-access-confirmation.json`、`environment.json` | 普通 Chrome 路径的访问失败事实；已安装版本 149.0.7827.54，另有工具版本与执行命令 |
| 前两次尝试的 `report.json`，由 `preceding-attempt.json` 链接 | 首次暴露搜索遗漏重定向；随后修正勘探程序对无返回值接口的序列化；所有先前样本保留 |

原始内容与精确本机位置只留在 ignored 证据目录。下文例子使用这次受控样本的字段与数值，不包含日常访问内容。正式实现没有变化；临时程序运行完删除，源代码副本与版本随样本保存供复现。

## 普通 Chrome 的实际可读性

`~/Library/Application Support/Google/Chrome` 的 Profile 列举得到 `PermissionError / EPERM`。已知 `Default/History` 可以取得文件属性，大小 40304640 B；SQLite `mode=ro` 打开得到 `SQLITE_CANTOPEN`，同一进程权限下普通只读打开明确得到 `EPERM`。因此没有取得日常 History 内容或其 schema，也没有绕过拒绝。

`Default/Bookmarks` 的属性查询为 `ENOENT`。这个结果只说明该精确路径在该时刻未取得文件；Profile 列举被拒，尚未确认当前活动 Profile 的目录身份，也不能推断其他 Profile 没有书签。Cookie、登录库、凭据及其他用户均未遍历。

## 官方能力与实际取得

| 材料 | 官方接口与条件 | 本次受控结果 |
| --- | --- | --- |
| 历史 | `history` 权限；`search` 默认最近一天、最多 100 个页面；`getVisits` 按 URL 取得访问。显式保存查询时间范围与上限。[History](https://developer.chrome.com/docs/extensions/reference/api/history) | 全时段、上限 100000 的 search 返回 3 个 URL；按这些 URL 查到 4 次访问。数据库实际有 4 个 URL、5 次访问 |
| 书签 | `bookmarks` 权限；`getTree` 返回接口可见树，ID 在 Profile 内有效，重启保持。[Bookmarks](https://developer.chrome.com/docs/extensions/reference/api/bookmarks) | 新建文件夹及两个同 URL 书签，改名、重启、删除均留样；文件含 guid、meta_info 等原字段 |
| 下载 | `downloads` 权限；search 默认上限 1000，`limit:0` 请求所有匹配项；`exists` 是可能滞后的检查结果。[Downloads](https://developer.chrome.com/docs/extensions/reference/api/downloads) | 一次真实本地 HTTP 下载完成；API 给出初始和最终 URL，History 库另有有序 URL 链及更多元数据 |
| 最近关闭 | `sessions` 权限；最多 25 项，`lastModified` 单位为 Unix 秒。[Sessions](https://developer.chrome.com/docs/extensions/reference/api/sessions) | 受控关闭一个标签返回一项；未登录测试 Profile 的同步设备查询为空，不外推到日常账号 |

调用时将接口实际交付对象序列化一次，保存响应、参数、请求和返回时刻。查询之间没有共同事务，整轮不是浏览器全状态的同一时刻快照。历史与书签的时间字段保持接口原单位；各自原值不在采集时统一转换。

## 原始形态及差异

受控样本先导航到 `/first?q=unchanged#fragment`，点击链接，经 `/redirect-page` 跳转到 `/arrived?redirect=1#retained`，再重新加载。数据库保存完整 URL 与 fragment、URL ID、逐次访问 ID 和引用关系。

第一条代表样本是重定向中间访问：原 API 对象字段为 `id`、`visitId`、`visitTime`、`referringVisitId`、`transition`、`isLocal`；这轮 `id="2"`、`visitId="2"`、`referringVisitId="1"`、`transition="link"`、`isLocal=true`。完整原返回可从 `report.json` 的 `before-restart / history.getVisits`、参数为受控 `/redirect-page` 的调用定位。它不在 `history.search` 的 URL 列表里，知道 URL 后直接查询才能取得；单纯把搜索结果逐个展开会漏掉这条真实记录。

`visits` 表有 18 列，包括 `visit_duration`、`opener_visit`、`originator_*`、`external_referrer_url` 等。5 条访问都有 `transition` 限定比特，4 条有非零时长；API 的六个字段没有这些列。Chromium 当前源码也将 transition 转成核心枚举；本次表结构与差异以实际 156 样本为准，当前 main 源码只解释映射。[History 实现](https://raw.githubusercontent.com/chromium/chromium/main/chrome/browser/extensions/api/history/history_api.cc)、[访问表](https://chromium.googlesource.com/chromium/src/+/refs/heads/main/components/history/core/browser/visit_database.cc)。这些时长和 `typed` 分类是浏览器事实，不能直接解释为人的注意力或真实手工输入；本次导航由自动化发起。

第二条代表样本是 ID `6` 的保留书签：API 返回改名后的标题、原 URL、父 ID `5` 和位置 `0`；`controlled-closed/Bookmarks` 同一节点另有 `guid`、`date_last_used`、`meta_info`。文件节点字段为 `date_added,date_last_used,guid,id,meta_info,name,type,url`。本次文件与 API 各有 6 个节点，但集合不同：文件有隐藏的空移动书签根 `3`，API 有虚拟根 `0`。数量相等不能证明对象集合相同。[Bookmark 编码](https://raw.githubusercontent.com/chromium/chromium/main/components/bookmarks/browser/bookmark_codec.cc)。

文件书签时间是 Windows epoch 微秒字符串，History 时间为相同纪元的整数，API 访问时间是 Unix 毫秒；本轮访问 ID 与时间换算误差小于 2 微秒，书签时间按接口毫秒精度对应。原件保留各自值，换算只用于核对。[时间定义](https://raw.githubusercontent.com/chromium/chromium/main/base/time/time.h)。

下载的 History 表有 28 列，包含 guid、HTTP 方法、发起标签 URL、原 MIME、下载状态及路径；`downloads_url_chains` 保存 `id,chain_index,url`。本次两段链与 API 的初始/最终 URL 对应，下载文件 63 B 与受控服务原字节一致。实际文件内容与「下载记录」是两种材料；本候选范围收下载记录。[下载表](https://raw.githubusercontent.com/chromium/chromium/main/components/history/core/browser/download_database.cc)。

## 持久性、体量与取得成本

运行中、正常关闭后、再次启动并删除受控条目后，各保存一份完整 History online backup 和 Bookmarks 文件原字节。三份 History 都是 204800 B，`quick_check=ok`；书签前两份各 2264 B，删除后 1823 B。删除前后数据库分别为 4/3 个 URL、5/4 次访问，先前样本仍能独立读出被删除条目。历史、保留书签和下载 ID 在这次同 Profile 重启后相同。

SQLite 采用只读来源连接与 online backup，保留全库而非导出选定行，避免把主文件的某一瞬间复制误作包含已提交内容的一致数据库。每个文件独立记录取得开始与完成时间；在线备份与书签文件不构成跨文件原子快照。[SQLite Backup](https://www.sqlite.org/backup.html)。

这轮只覆盖秒级受控操作，无法推算日常条数、每日增量、最长保留时长或长期运行成本。日常 History 的文件属性不证明能读取其中最早记录；“90 天”仍未在本机实测。未测多设备同步、无痕、企业策略、旧版本迁移、超大结果与分页同时间戳边界。已删除、从未记录或浏览器尚未落盘的内容，也不能由某次文件快照保证补回。

历史与现有 tabs/windows 观测、系统前台应用事实存在 URL/标题/时间重合，仍各留出处。持久记录补以前留下来的材料，不能代替瞬时窗口切换；浏览器状态和人的活动含义继续分开。

## 当前判断与下一步

Agent 判断：完整 History SQLite 与 Bookmarks 原字节保留的信息更多，现有文件取得能力已具备承载形态；普通 Chrome 的读取权限仍是实际卡点。扩展 API 可作为另一路有限视图补充，但当前受控反例已经否定“history.search 加 getVisits 就是完整 History”的前提。是否进入正式接入，需先选定实际可读的 Profile 或明确采用 API 可见范围，并完成对应真实来源验收；本轮没有替用户改变日常扩展权限。

按 Matt 的 `improve-codebase-architecture` 与 `codebase-design` 检查候选的实际设计摩擦：文件/SQLite 走现有 Collector module 的 interface，接口原响应可继续走现有 browser envelope 与 Delivery/NativeReceiver module。当前没有需要抽象的新 seam，没有证据支持额外浏览器归一化层；具体实现与独立审阅留在采用阶段。

勘探程序约定与现行 runner 曾不一致：`orisumi run` 只转发产品 CLI。root 已将 AGENTS 改为一次性勘探原生运行、长期项目登记 slnx；临时 test 注册方案已撤销，Orisumi.slnx 无变化。产品运行、发布与实现验收继续通过 `orisumi`。
