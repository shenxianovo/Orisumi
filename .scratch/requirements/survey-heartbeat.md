# 勘探：旧 Heartbeat

2026-11-03 只读查询旧服务器的 PostgreSQL（`heartbeat-db-1`），只看用户本人的数据。容器状态是 unhealthy，查询正常。

## 概况

- 库 2.1 GB。三张大表：键鼠事件 270 万行（1.7 GB）、时段 28 万行（227 MB）、缺口 65 万行（155 MB）。
- 时间跨度 2026-02-26 到今天，251 天里 247 天至少有一台设备有数据，最长的空档是 4-17 起 3 天。
- 设备四台：家里的 PC（Windows）、旧工位 PC（Windows，8-03 停）、现在的工位 Mac（8-15 起），以及 VRChat 账号。库里还有另一个人的一台设备和 2 万条时段。

## 样本

前台时段（Mac，今天）：

```json
{"title": "QQ(窗口)", "attributes": null, "activityKey": "mac:com.tencent.qq\nQQ(窗口)"}
```

前台时段（Windows，2 月）：

```json
{"title": null, "attributes": null, "activityKey": "chrome\n"}
```

浏览器时段：

```json
{"title": "shenxianovo/Orisumi: A lasting record of your experiences.", "attributes": {"url": "https://github.com/shenxianovo/Orisumi", "site": "github.com", "domain": "github.com", "windowId": 1511859106}, "activityKey": "https://github.com/shenxianovo/Orisumi"}
```

VRChat 时段：世界名、worldId、instanceId（带地区和可见性），换世界之间有一段 `traveling`。

键鼠事件：只有按键位置，没有内容。

```json
{"code": 1, "codeSet": "heartbeat-key-position-v1", "eventType": "mouseButton"}
```

## 频率和体量

- 前台时段：一天几百到上千条。45% 短于 5 秒，60% 短于 10 秒；但时长主要来自 1 分钟到 1 小时的时段。最长的一条 23 小时，是锁屏或没关的电脑。
- 键鼠事件：一天上万条，占库的八成。
- 浏览器时段：从 7-05 起，3.3 万条，分布和前台时段类似。
- VRChat：9-04 起，379 条，一条从十几分钟到几小时。

## 瞬时还是持久

- 前台、键鼠、浏览器都是瞬时的：没采到就没有了。
- VRChat 的世界记录有一部分在 VRChat 自己的日志里，事后能补一些。

## 拿不到的部分

- 2 月到 6 月 Windows 只记了进程名，没有窗口标题，这几个月的时段 100% 没有标题。7 月起才有。
- 工位 Mac 在 9-21 到 10-25 之间整整五周没有前台和键鼠数据，原因是本地积压超出上限后丢弃。库里记下了 55 万条丢失：键鼠 51 万、前台 4 万，浏览器另有 15 万条因缓冲溢出丢失。
- 键鼠只有位置不含内容，是当时有意选的。

## 怪癖

- **同一个应用有好几个名字。** Windows 用进程名（`win:chrome`），Mac 用 bundle id（`mac:com.google.chrome`）。374 个应用身份里只有 5 个产品被手工合并过跨平台的身份：Chrome、VS Code、QQ、飞书、Heartbeat 自己（它有 4 个名字）。剩下的都是单平台的。
- **格式中途变过。** 早期 `activityKey` 没有平台前缀（`chrome\n`），后来有了（`mac:com.tencent.qq\nQQ`）；键码也从 `windows-vk-v1` 换成了 `heartbeat-key-position-v1`。
- **同一个字段在不同来源含义不同。** 前台时段的 Revision 是 1、2、3 这样的修订次数，浏览器时段的 Revision 是毫秒时间戳；缺口表的时间又是 .NET ticks。
- **窗口标题会变。** QQ 的标题在「QQ」「QQ(窗口)」「群公告-某群」之间几秒一跳，每跳一次就是一条新时段。
- **系统界面也算前台应用。** 「任务切换」「Windows 默认锁屏界面」「图片查看器」都作为前台出现。
- **前台不等于人在做什么。** 最近一周，家里 PC 和工位 Mac 同时有前台时段的时间合计约 30 小时：一台没关，人在另一台前。
- **同一台设备换过采集器。** 工位 Mac 先后有两条流：8 月的旧版，9 月的新版，10-26 之后又回到旧版。两条流时间上不重叠，是交替使用的。
- 只看时段推不出「离开」。锁屏、离开在 Windows 上表现为一条锁屏界面的时段，在 Mac 上表现为空档。

## 已经有的理解

- 库里有 39 份由 DeepSeek 生成的叙述，带日期的几份在 9 月初，从前台时段写出「几点回到工位、先开了什么、重心在哪」。只靠前台时段，叙述已经能读出当天在做什么。

## 和其他来源的重合

- 浏览器时段和前台的 Chrome 时段覆盖同一段时间，浏览器的更细。
- VRChat 时段和前台的 VRChat 时段重合，前者带世界信息。
- 前台标题里能看到 QQ 群名、飞书文档名，和 IM、文档来源重合。

## 对需求的意义（推论，待确认）

- 采集端会丢数据，而且一丢就是几周。丢失本身要被记下来，事后才知道哪段是空的、为什么空。
- 同一个来源的格式会随采集器版本变。原样保存的时候，要记下是哪个版本采的。
- 应用的身份靠采集拿不全，跨平台合并需要事后读。
