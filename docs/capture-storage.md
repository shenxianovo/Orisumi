# 采集与存储

每个来源一个 SQLite，文件位于存储目录的 `raw/`：`browser.sqlite`、`codex.sqlite`、`claude.sqlite`、`macos.sqlite`。

例如下载从进行中变成完成，只新增状态事实；修改文件路径只新增路径事实。查询 `current_downloads` 时，由视图组合资源描述、最后状态和最后路径。底层保存独立事实，上层按用途组合，不另存一份宽表。

## 分层与时间

- 事实层：身份、来源事件、状态初值和变化。一个事件带出的多种事实通过观测 ID 或采集会话内序号关联。
- 明细层：`*_history` 视图组合某次观测之后的状态；原始版本表仍可直接查看。
- 当前状态层：`current_*` 视图组合最新已知状态。统计从这些明细或状态计算，当前没有独立的汇总表和调度依赖。

`observed_at_utc_ms` 是通知到达或读取来源的时间；`created_at_utc_ms`、`started_at_utc_ms`、`recorded_at_utc_ms` 等来源时间在各表中明确命名。`observation_id` 是本库的采集顺序，不是跨来源的统一事件时钟。缺失的发生时间为空或没有该字段，不补造。

`capture_runs` 的含义取决于采集方式：macOS 是监听区间，浏览器是扩展采集会话，Codex/Claude 是一次导入区间。后两者不能证明两次导入之间持续接收了事件。浏览器没有可靠的关闭回调，结束时间为空表示未确认。macOS 还记录开启了哪些通道；权限或通道不可用的区间不用于推断连续状态。

`_collector_state`、`_token_usage_rollouts` 和扩展待发送队列是采集进度，可以更新，不作为历史事实查询。

## 各来源

浏览器和 Codex 的表字段见 [浏览器说明](../src/Collectors/Orisumi.Collectors.Browser/Extension/README.md)、[Codex 说明](../src/Collectors/Orisumi.Collectors.Codex/README.md)。

### Claude

| 表或视图 | 内容 |
|---|---|
| `sessions` | 会话身份 `session_id` |
| `source_records` | 被采集日志行的 `source_path`、`source_line`、`record_hash`、所属 `run_id`；用来源位置与内容摘要避免重复导入 |
| `session_title_observations` | `session_id`、`title`、关联日志行的 `observation_id`；custom-title 没有源端时间，不生成改名时间 |
| `messages` | `session_id`、`message_uuid`、`parent_uuid`、`role`、来源 `created_at_utc_ms`、该消息的 `cwd`、可选 `response_id` |
| `message_content_observations` | `content_json` 的初值与修订，以消息身份和 `observation_id` 关联 |
| `response_token_usage_observations` | 每个响应的用量记录及其修订：模型、来源记录时间、总输入、缓存读写、总输出、可选推理 token |
| `current_messages` | 每条消息及最新内容 |
| `current_sessions` | 会话最后已知标题、已采集消息的最早和最晚时间；不把消息工作目录提升为会话属性 |
| `response_token_usage` | 每个响应最后的用量，供汇总使用 |

只读取主会话的用户、助手可见内容（文本、图像、文档）和响应用量，排除 sidechain、用户元消息和压缩摘要。读取 `CLAUDE_CONFIG_DIR`，默认 `~/.claude`。同一份日志中已有的标题变化和内容修订也会按来源顺序保留。完整 JSONL 行可以导入，正在写入的末尾残行下次重读。

### macOS

各事件用 `run_id`、`run_sequence` 和 `observed_at_utc_ms` 标识所属监听区间、顺序和接收时间。

| 表 | 内容 |
|---|---|
| `capture_runs` | 开始、结束、AFK 阈值；`capture_windows`、`capture_input` 表示启用范围 |
| `process_instances` | 一次观察到的进程生命期：`process_instance_id`、`run_id`、PID、bundle ID、应用名称、来源启动时间；PID 被复用时创建新身份 |
| `running_app_observations` | 进程初始可见、出现、消失；引用进程身份 |
| `foreground_app_events` | 前台初值和切换；引用进程身份，空表示无前台应用 |
| `window_focus_instances` | 一段连续窗口焦点的 `focus_id` 及所属进程；不是跨会话稳定窗口 ID |
| `focused_window_events` | 焦点初值或切换，`focus_id` 为空表示没有聚焦窗口 |
| `window_title_observations` | 某段焦点内的标题初值和变化；初始标题与焦点共享序号 |
| `capture_permission_events` | Accessibility / Input Monitoring 的初始授权状态及失去、恢复 |
| `capture_channel_events` | 窗口、输入通道的 `available` 初值和变化，以及 `reason`；监听器中断和恢复会保留边界 |
| `time_zone_events` | 时区初值和变化，供时间线显示本地时间 |
| `afk_events` | active/idle 初值和变化，`idle_ms` 是当次检查到的无输入时长 |
| `system_sleep_events` / `user_session_events` | 系统休眠/唤醒、用户会话激活/停用通知；不伪造启动前的通知 |
| `keyboard_presses` | 一次按键：键码、按下/释放时间、按下时修饰键；自动重复不新增按键，未观测到释放时为空 |
| `mouse_button_events` | 一次鼠标按下/释放：类型、按钮、屏幕点坐标 |

`foreground_app_history`、`running_app_history` 组合进程身份；`window_history` 组合焦点与当时标题。`timeline` 使用这些视图，并显示权限和通道变化。

## 运行与验证

```sh
dotnet build Orisumi.slnx
npm --prefix src/Collectors/Orisumi.Collectors.Browser/Extension test
python3 -m unittest discover -s tests -v
```

测试使用临时源库、日志和输出库，覆盖属性独立变化、A→B→A、重复扫描与重复投递、消息修订、窗口标题、PID 复用、监听中断。真实来源只读；清空输出库不会清空 Codex、Claude 或 Chrome 的来源数据。

```sh
orisumi codex capture --all
orisumi claude capture
orisumi foreground --windows --input
```

浏览器通过已加载的扩展监听、每分钟对齐。Codex/Claude 是一次性命令，由 macOS LaunchAgent 每分钟执行；前台采集常驻。前台采集继承启动环境已经取得的窗口和输入权限。启动配置指向实际构建产物和同一个输出目录。

构建后运行 `python3 scripts/start-capture.py --store .orisumi`，会启动前台常驻进程并安装/启动 Codex、Claude 的每分钟 LaunchAgent。可附加 `--extension-id EXTENSION_ID` 注册浏览器本机接收端，随后在扩展页重载。前台进程 PID 保存在存储目录的 `foreground.pid`，日志在 `logs/`。重新运行脚本会先停止上一次启动的前台进程。重建输出库时先停止监听和导入进程，重建后重载扩展以建立新的初始状态。
