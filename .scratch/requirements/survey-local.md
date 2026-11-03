# 勘探：本机能直接读到的来源

2026-11-03 速记。只看了位置、格式和体量，没有写勘探程序。macOS 的完全磁盘访问权限还没开，下面标「受保护」的要开了以后再看。

## Magpie

位置 `~/.config/magpie/`，常驻托盘，开机启动。

- `usage.jsonl`：33 行，10-27 到 11-02，全部是 Magpie 自己探测模型列表的请求（`kind: model_list`, `source: probe`），没有实际对话请求的 Token 数。一行的字段：时间、provider、账号、模型、输入/输出/缓存 Token、耗时、状态码、错误、端点。
- `quota-history.json`：每个账号每个额度窗口的剩余百分比，Codex 从 10-08 起 845 个采样点，Grok 2 个。样本：`{"at": "...", "left": 94, "start": "...", "resetsAt": "..."}`。
- `quotas.json`：每个账号当前的套餐和额度窗口。`balance-history.json`：OpenRouter 余额，2 个点。
- 同目录还有登录凭据文件，勘探不读。
- 待查：每次请求的 Token 统计存在哪，还是只在 Magpie 内存里。

## Shell 历史

- `~/.zsh_history` 1907 行，8-10 到今天。前面一段只有命令，后面一段带时间戳（`: 时间戳:耗时;命令`），格式中途变过。
- `~/.bash_history` 很小，8-11 之后没有更新。
- 没有记录在哪个目录执行、退出码。

## Git

- `~/Code` 下 32 个仓库。用户名 shenxianovo 的提交：Heartbeat 770 个，其余个人仓库从几个到 288 个不等。工作仓库用的可能是公司账号，没有统计。
- 提交是持久的，任何时候都能补，带作者时间、提交时间、改了哪些文件。

## IM

- 飞书：`~/Library/Application Support/LarkShell/` 共 16 GB，能读到目录。消息、联系人、日历、邮件各是一个库（`messages.db` 1.4 GB、`contact.db` 160 MB），文件头不是 SQLite 的明文头，是加密的。
- QQ、微信：数据在 `~/Library/Containers/` 里，受保护。
- Telegram：`tdata` 目录能读，内容是加密的。
- 推论：本地 IM 库多半加密，要么解密，要么走官方导出或接口，要么从截图和前台标题间接看到。

## 文件

- 下载 5.1 GB，文稿 40 MB，桌面 6.7 MB，三处合计 2932 个文件。
- 截图默认存桌面，现在桌面上没有截图。

## 受保护，开权限后再看

- Chrome、Edge、Brave 的 History（90 天的访问、停留时长、搜索词）。
- macOS 自己的使用记录：`~/Library/Application Support/Knowledge/`、`~/Library/Biome/`。屏幕使用时间的数据在这里，可能包括过去一段时间的应用使用、网页使用、正在播放的媒体，可以事后补。
- QQ、微信的本地数据。
- 备忘录、照片、日历、提醒事项、信息。

## 要联网或登录的

- 哔哩哔哩收藏夹和观看历史、X 收藏夹：要用账号登录后的接口或浏览器里的页面。
- 飞书：官方开放接口。
