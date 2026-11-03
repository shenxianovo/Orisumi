# 勘探：Claude Code 和 Codex 会话

2026-11-03 只读查看本机两家编程助手的会话数据。统计脚本在临时目录，用 `python3 -I` 流式读每个文件；Codex 的 `.zst` 用 Python 3.14 自带的 `compression.zstd` 解压读；sqlite 用 `mode=ro` 打开，没有 WAL 文件的库用 `immutable=1`。时间换算成本地时间时按 UTC+8。

## 概况

**Claude Code**（全部来自桌面应用，`entrypoint` 一律是 `claude-desktop`）

- 会话日志：`~/.claude/projects/<目录编码>/<sessionId>.jsonl`，107 个主会话，另有 70 个子 Agent 日志在 `<sessionId>/subagents/agent-<agentId>.jsonl`。合计 530.5 MB（主会话 483.7 MB，子 Agent 46.0 MB），10.1 万行，全部能解析。
- 同目录下每个会话还有 `tool-results/`（大工具输出和截图，647 个文件 22.3 MB）和 `custom-title.json`。
- 会话索引不在 `~/.claude`，在桌面应用的 `~/Library/Application Support/Claude/claude-code-sessions/<账号>/<组织>/local_<id>.json`，103 个。里面有标题、归档、rewind 和派生关系、每轮的总结。
- `~/.claude/history.jsonl` 只有 32 条，是 9-15 到 10-01 在终端里敲的输入；`~/.claude/file-history/` 是改动文件的备份（13 MB）；`~/.claude/sessions/<pid>.json` 是正在运行的进程。
- 时间跨度 2026-10-23 到今天，12 天。

**Codex**（桌面应用，`originator` 有 `Codex Desktop` 913 个和 `codex_work_desktop` 252 个两种）

- 会话日志：`~/.codex/sessions/YYYY/MM/DD/rollout-<本地时间>-<threadId>.jsonl` 53 个，`~/.codex/archived_sessions/` 1112 个，共 1165 个，其中 1032 个是 zstd 压缩的 `.jsonl.zst`。磁盘上 1222.8 MB，解压后 3822 MB，57.4 万行，全部能解析。
- 索引：`session_index.jsonl`（标题，635 行）、`state_5.sqlite` 的 `threads` 表（1112 个线程，1061 个已归档）、`thread_spawn_edges`（534 条父子边）。
- `thread_history_1.sqlite`（1.2 GB）是从日志投影出来的条目表：16.1 万条 `thread_items`、5629 个 `thread_turns`，每个文件记着读到的字节偏移。
- `logs_2.sqlite` 是程序运行日志，只保留了 10-31 到今天。
- 时间跨度 2026-08-10 到今天。

## 样本

样本里的长文本截短，`…(+N)` 表示后面还有 N 个字符。

### Claude Code

记录类型（主会话里的行数）：`assistant` 21853、`user` 13139、`attachment` 11897、`api-request-blob` 11266、`api-request` 9393、`last-prompt` 5342、`custom-title` 5294、`atis-latch` 5240、`agent-name` 5172、`relocated` 2052、`queue-operation` 1789、`file-history-delta` 784、`system` 767（`stop_hook_summary` 746、`compact_boundary` 9、`informational` 9、`api_error` 3）、`file-history-snapshot` 741、`mode` 341、`api-request-shape` 217、`cost-state` 125、`frame-link` 66，还有几种个位数的。

用户真实输入：

```json
{"parentUuid": "…", "isSidechain": false, "promptId": "56b2…", "type": "user",
 "message": {"role": "user", "content": "你看看这个插件。然后你觉得这个写的怎么样。功能我觉得都行…"},
 "uuid": "9627…", "timestamp": "2026-10-23T11:19:40.423Z",
 "origin": {"kind": "human"}, "promptSource": "sdk", "turnOrigin": "human",
 "turnPosition": {"promptIndex": 0, "turnIndex": 1}, "permissionMode": "bypassPermissions",
 "entrypoint": "claude-desktop", "cwd": "/Users/bytedance/Code/Personal/Heartbeat",
 "sessionId": "0dc2…", "version": "2.1.308", "gitBranch": "main"}
```

助手的一次回复拆成多条记录，每个内容块一条，共用 `message.id`，`usage` 每条重复一份：

```json
{"type": "assistant", "message": {"model": "claude-opus-5-5", "id": "msg_011C…", "role": "assistant",
  "content": [{"type": "thinking", "thinking": "", "signature": "CAQStgUK…(+876)"}],
  "stop_reason": "tool_use", "usage": {"input_tokens": 2, "cache_read_input_tokens": 42929, "output_tokens": 455, …}},
 "thinkingDurationMs": 585, "thinkingDisplay": "updates", "apiBlockIndex": 0, "requestId": "req_011C…",
 "effort": "high", "uuid": "50b8…", "timestamp": "2026-10-23T11:19:42.551Z", …}
```

工具调用和结果：`assistant` 里的 `tool_use` 块，下一条 `user` 里的 `tool_result` 块，另有结构化的 `toolUseResult` 和权限决定：

```json
{"type": "user", "message": {"role": "user", "content": [{"tool_use_id": "toolu_01FY…", "type": "tool_result",
  "content": [{"type": "text", "text": "Plan shown (5 steps)."}]}]},
 "toolUseResult": [{"type": "text", "text": "Plan shown (5 steps)."}], "sourceToolAssistantUUID": "cc53…",
 "permissionDecision": {"decision": "accept", "source": "config", "reasonType": "mode"}, …}
```

注入的上下文是单独的 `attachment` 记录，带着渲染后发给模型的文本：

```json
{"type": "attachment", "attachment": {"type": "instructions", "files": [
  {"path": "/Users/bytedance/Code/Personal/Heartbeat/AGENTS.md", "type": "Project", "content": "# Agent 规则\n\n## 重写期约束…(+1916)"},
  {"path": "…/memory/MEMORY.md", "type": "AutoMem", "content": "- [用中文回复](reply-in-chinese.md) …"}]},
 "rendered": [{"content": "<system-reminder>\nCodebase and user instructions are shown below…"}], "renderedRole": "user", …}
{"type": "attachment", "attachment": {"type": "date", "date": "2026-10-23"},
 "rendered": [{"content": "<system-reminder>\nToday's date is 2026-10-23.\n</system-reminder>"}], "renderedRole": "system", …}
```

每轮重复追加的一组会话元数据（`last-prompt`、`custom-title`、`agent-name`、`mode`、`atis-latch`）：

```json
{"type": "custom-title", "customTitle": "T1：按新 Core 审查并重写 API 与 Storage", "sessionId": "0dc2…"}
{"type": "agent-name", "agentName": "T1：按新 Core 审查并重写 API 与 Storage", "sessionId": "0dc2…"}
{"type": "last-prompt", "lastPrompt": "Task (from the session that started this one): …", "leafUuid": "a6a1…", "sessionId": "0dc2…"}
```

发给模型的原始请求：`api-request` 记下参数和一串内容哈希，`api-request-blob` 按哈希存消息内容，`api-request-shape` 存系统提示和工具定义：

```json
{"type": "api-request", "id": "ef19…", "timestamp": "2026-10-24T00:59:32.811Z", "version": "2.1.308", "querySource": "sdk",
 "params": {"model": "claude-opus-5-5", "max_tokens": 128000, "thinking": {"type": "adaptive", "display": "updates"}, …},
 "keep": 0, "tail": ["e3d1…", "bffd…", "…(+167 items)"], "requestDigest": "316f…"}
{"type": "api-request-blob", "hash": "e3d1…", "message": {"role": "user", "content": [{"type": "text", "text": "<system-reminder>\nCodebase and user instructions…"}, …]}}
```

会话成本：

```json
{"type": "cost-state", "totalCostUSD": 18.26, "totalAPIDuration": 2184074, "totalLinesAdded": 2521, "totalLinesRemoved": 693,
 "startTime": 1792754367987, "modelUsage": {"claude-opus-5-5": {"outputTokens": 230837, "thinkingTokens": 86477, …}}}
```

桌面应用的会话索引（`local_<id>.json`，节选）：

```json
{"sessionId": "local_0171…", "cliSessionId": "006f…", "cwd": "/Users/bytedance/Code/Personal/Heartbeat",
 "createdAt": 1793419685206, "lastActivityAt": 1793442215493, "model": "claude-opus-5-5",
 "isArchived": true, "title": "观察者运行时模型文档", "titleSource": "auto", "completedTurns": 8,
 "postTurnSummary": {"status_category": "completed", "status_detail": "…"},
 "rewindEdges": [{"parent": "66d6…", "child": "0dc2…", "forkPoint": "5471…", "at": 1792757473055}],
 "spawnedFrom": {"sessionId": "local_84e8…", "title": "T1：按新 Core 审查并重写 API 与 Storage", "sideSession": true}}
```

### Codex

每行是 `{"timestamp", "ordinal", "type", "payload", "metadata"?}`。记录类型（行数）：`event_msg/item_completed` 168831、`event_msg/token_count` 76924、`response_item/reasoning` 70588、`response_item/custom_tool_call_output` 56865、`response_item/custom_tool_call` 56858、`token_usage_record` 46500、`response_item/message`（assistant 18847、user 9245、developer 5469）、`response_item/function_call` 10480 和 `_output` 10652、`turn_context` 7691、`event_msg/task_started` 7225、`task_complete` 6655、`thread_settings_applied` 5520、`response_item/agent_message` 3981、`inter_agent_communication_metadata` 3941、`world_state` 3611、`session_meta` 1526、`realtime_item/transcript_segment` 1591、`compacted` 610、`turn_aborted` 229，还有几种少量的。

同一件事有两份：`response_item` 是模型看到的对话，`event_msg/item_completed` 是界面上显示的条目（`UserMessage`、`CommandExecution`、`FileChange`、`Reasoning`、`AgentMessage`、`McpToolCall`、`SubAgentActivity` 等）。

会话头：

```json
{"timestamp": "2026-11-02T08:39:00.520Z", "ordinal": 0, "type": "session_meta", "payload": {
  "session_id": "01a19b69-…", "id": "01a19b69-…", "timestamp": "2026-11-02T08:38:58.596Z",
  "cwd": "/Users/bytedance/Code/ByteDance", "originator": "Codex Desktop", "cli_version": "0.166.0-alpha.5.1",
  "source": "vscode", "thread_source": "user", "model_provider": "openai", "base_instructions": "<略>",
  "history_mode": "paginated", "context_window": {"window_id": "01a19b69-…"},
  "git": {"commit_hash": "0bac…", "branch": "main", "repository_url": "https://github.com/shenxianovo/ByteDance.git"}}}
```

一条 user 消息里装着几段内容，每段的来源写在 `content_item_kinds`：

```json
{"type": "response_item", "payload": {"type": "message", "role": "user", "content": [
   {"type": "input_text", "text": "# AGENTS.md instructions for /Users/bytedance/Code/ByteDance\n\n<INSTRUCTIONS>…(+2536)"},
   {"type": "input_text", "text": "<environment_context>\n  <cwd>/Users/bytedance/Code/ByteDance</cwd>…(+417)"}],
 "internal_chat_message_metadata_passthrough": {"turn_id": "01a19b69-…", "create_time": 1793608740.949064,
   "content_item_kinds": ["agents_md.instructions", "environments.environment_context"]}}}
```

界面条目里的用户输入和命令执行：

```json
{"type": "event_msg", "payload": {"type": "item_completed", "turn_id": "01a18a77-…", "item": {"type": "UserMessage",
  "id": "01a18a77-…", "client_id": "cc30…", "content": [{"type": "text", "text": "\n# Files mentioned by the user:\n\n## 计算机学院2027届本科毕业论文…"}]},
  "started_at_ms": 1793324460896, "completed_at_ms": 1793324460896}}
{"type": "event_msg", "payload": {"type": "item_completed", "item": {"type": "CommandExecution",
  "command": ["/bin/zsh", "-lc", "rg --files '/Users/bytedance/Downloads/…'"], "cwd": "file:///Users/bytedance/Code/Personal/Heartbeat",
  "parsed_cmd": [{"type": "list_files", …}], "status": "completed", "aggregated_output": "…(+1161)", "exit_code": 0,
  "duration": {"secs": 0, "nanos": 7708}}}}
```

推理只有密文：

```json
{"type": "response_item", "payload": {"type": "reasoning", "id": "rs_0e83…", "summary": [], "encrypted_content": "gAAAAABq6Ew4…(+1606)"}}
```

一轮的开始和结束：

```json
{"type": "event_msg", "payload": {"type": "task_started", "turn_id": "01a19b69-6e4e-…", "turn_attribution": {"turn_trigger": "composer", …}, "started_at": 1793608740}}
{"type": "event_msg", "payload": {"type": "task_complete", "turn_id": "01a19b69-6e4e-…", "last_agent_message": "…", "duration_ms": 119977, "time_to_first_token_ms": 10058}}
```

每轮的环境，带时区：

```json
{"type": "turn_context", "payload": {"turn_id": "…", "cwd": "/Users/bytedance/Code/ByteDance", "model": "gpt-6.1-sol", "effort": "high",
  "approval_policy": "never", "sandbox_policy": {"type": "danger-full-access"}, "current_date": "2026-11-02", "timezone": "Asia/Shanghai", …}}
```

语音对话的转写：

```json
{"type": "realtime_item", "payload": {"realtime_session_id": "5d8c…", "type": "transcript_segment", "role": "user", "text": "呃,目前你开在平行加密那个任务,然后目前应该是做到…"}}
```

标题索引 `session_index.jsonl`：

```json
{"id": "019feade-…", "thread_name": "你好呀", "updated_at": "2026-08-10T08:51:58.005329Z"}
```

## 频率和体量

**Claude Code**

- 一天新开 4 到 14 个主会话（按每个会话第一条记录的本地日期数，12 天共 107 个）。
- 用户亲手输入（`origin.kind` 为 `human` 的 `user` 记录）12 天 688 条，每天中位数 58、最多 89。
- 单个会话中位数 3.3 MB、747 行；最大 33.4 MB、1460 行。
- 体量的大头是工具结果：`tool_result` 所在的 `user` 记录占 44%（233 MB），助手 17.6%，请求原文（blob + shape + request）25%，注入上下文 11.7%。用户亲手输入只有 1.3 MB。
- 按主要 cwd：Heartbeat 及其子目录 83 个，ByteDance 及子目录 15 个，其余是 me、homepage、resume、两个 fumadocs 插件、一个 worktree 和一个桌面应用的临时工作区。
- 工具调用 13412 次，Bash 6879、MCP 2947、Read 791、Write 696、Edit 628。

**Codex**

- 用户线程（`thread_source` 为 `user`）里的用户消息 4092 条，分布在 86 天中的 81 天，每天中位数 48、最多 201（来自 `thread_history_1` 的 `userMessage` 条目，按 `threads.thread_source` 过滤）。按月：8 月 675、9 月 1070、10 月 2205、11 月前三天 142。
- 线程 1112 个：用户 526、子 Agent 534、Agent 新建 34、语音 16、Agent fork 2。
- 单个文件解压后中位数 1.6 MB、233 行；最大 69.6 MB、11138 行（压缩后 9.2 MB）。压缩比约 3 倍。
- 体量一半是界面条目 `item_completed`（1.9 GB），工具输出 23%，推理密文 6%，压缩摘要 `compacted` 5%。
- 按 cwd（每个文件的第一个 session_meta）：Heartbeat 639，ByteDance 307，`~/.codex/worktrees/*/Heartbeat` 几十个，其余是十来个个人项目。

## 瞬时还是持久

两家都写本地文件，事后能读，但都会搬动、复制或删掉。

**Claude Code**

- 会话日志按行追加。自动压缩（compaction）在同一个文件里追加一条 `compact_boundary`，压缩前的记录保留：看到的 9 次压缩，边界前都还有几百到一千多行。
- rewind 生成一个新的会话文件，把分叉点之前的记录复制过去：一例里父文件 295 条带 uuid 的记录，子文件复制了其中 290 条，uuid 不变，`sessionId` 改成新会话的。父子两个文件都留着，关系只记在桌面应用索引的 `rewindEdges` 和 `priorCliSessionIds` 里。
- 「派生会话」（一个会话开出的另一个会话）是独立的主会话，第一条用户消息以 `Task (from the session that started this one)` 开头，关系记在桌面索引的 `spawnedFrom`（18 个）。
- 归档只是桌面索引里的 `isArchived`（78 个），日志文件原地不动。
- 被删的会话：`history.jsonl` 里 9-15 到 10-01 的 32 条输入指向 12 个会话，这 12 个日志全都不在了。`~/.claude/.last-cleanup` 记着今天 16:15 做过一次清理，`.claude.json` 的 `firstStartTime` 是 10-23。**推论**：Claude Code 会定期删掉旧会话（默认 30 天），或者 10-23 前后整个目录被重置过；两种情况都意味着要在删除前采走。
- 子 Agent 后台运行时的输出文件在 `/private/tmp/claude-501/…`，是临时目录。

**Codex**

- 归档会把文件从 `sessions/` 移到 `archived_sessions/`，`threads.rollout_path` 跟着改。
- 旧文件被压缩成 `.jsonl.zst`，原文件删掉，没有两份并存。`sessions/` 和 `archived_sessions/` 里最晚的压缩文件都是 10-26 的，10-27 之后的都还是明文。压缩后的文件修改时间等于最后一条记录的时间（1161 个文件差不到 1 小时）。
- 961 个线程的 `rollout_path` 还写着 `.jsonl`，实际文件是 `.jsonl.zst`。
- 压缩（compaction）追加一条 `compacted`，带 `replacement_history`（替换后的上下文）和窗口编号，之前的记录保留。
- 一个线程会跨好几个文件，见怪癖。
- `logs_2.sqlite` 只有最近 3 天，是滚动的。
- 删没删过会话看不出来：`threads` 1112 行和 1112 个单 id 文件一一对应，`session_index` 的 566 个 id 都在 `threads` 里。

## 拿不到的部分

- **推理内容。** Claude 的 7681 个 thinking 块里 7214 个（94%）正文是空字符串、只有签名，另外 467 个是一两句的摘要（`thinkingDisplay` 为 `updates`）。Codex 的 70588 条推理全是 `encrypted_content`，有 20893 条（30%）带摘要，原文为空。
- **工具输出截断。** Claude 13438 个工具结果里 90 个超过约 30 KB，模型只看到 2 KB 预览，全文另存在 `tool-results/*.txt`，路径写在结果里。Codex 67517 个工具输出里 5608 个（8%）带 `Warning: truncated output (original token count: …)`；界面条目里的 `aggregated_output` 最长到 1048607 字节，**推论**是 1 MiB 的上限。
- **图片。** Claude 用户贴的图以 base64 内嵌在日志里（8 张），浏览器截图另存为 `tool-results/*.jpg`（488 张）。Codex 用户贴的图是内嵌的 data URL（135 张），生成的图在 `~/.codex/generated_images/`（50 个目录 64 MB），粘贴的长文本存成 `~/.codex/attachments/<id>/已粘贴的文本.txt`（47 个），日志里只留路径（410 行引用）。
- **10-23 之前的 Claude 会话。** 只剩 `history.jsonl` 里的 32 条输入文本。
- **子 Agent 在 Claude 里的完整上下文。** fork 型子 Agent 只记一条 `fork-context-ref`（父会话 id、父会话最后一条 uuid、上下文长度 296），不复制父会话内容。

## 怪癖

逐条对照 survey.md 的清单，后面是这次新发现的。

- **标题存在哪里。** Codex：日志里没有标题；`session_index.jsonl` 只追加，改名就再加一行，635 行对应 566 个 id，54 个改过名，最后一行算数，和 `threads.name` 完全一致（566/566）；`threads.title` 是第一条用户消息（1112 里 1091 个相同），子 Agent 线程没有名字。Claude：日志里有 `custom-title` 和 `agent-name`，桌面索引里另有 `title` 和 `titleSource`（auto 82、tool 18），100 个能对上的会话两边标题全部一致。
- **重读时会重复。** Claude 的「`last-prompt`、`custom-title`、`agent-name`、`mode`、`atis-latch`」这一组每轮重新追加一次，一个会话里 `custom-title` 有 3 到 151 条，中位数 44；这次看到的会话里标题值都没变过。Codex 的 `session_index` 改名也是追加。
- **时区。** Claude 日志的 6.5 万个时间戳全是 `2026-10-23T11:19:40.423Z` 这种 UTC 毫秒，记录里没有时区字段，只有 `date` 附件写着本地日期；桌面索引、`history.jsonl`、`cost-state` 用毫秒时间戳。Codex 记录的时间戳也是 UTC 毫秒，文件名是本地时间（1165 个文件全部比 session_meta 晚 8 小时），`turn_context` 写着 `timezone: Asia/Shanghai` 和 `current_date`。清单里「Claude Code 按历史重读时，时区变化会让同一条记录看起来不一样」这次没复现：存下的时间戳都带 `Z`。**推论**：差异出在读取方把 UTC 换成本地时间、或按本地日期切分的那一步。
- **时间精度和格式五花八门。** Codex 一个文件里就有：ISO 毫秒（`timestamp`）、整数秒（`started_at`）、毫秒（`started_at_ms`）、浮点秒带微秒（`create_time`）、秒加纳秒（命令的 `duration`）；`session_index` 是微秒 ISO；sqlite 里秒和毫秒各一列。
- **文件顺序和时间顺序不完全一致。** Claude 4.8 万条带时间戳的消息类记录里 1865 条比上一条早，大多早 1 到 15 毫秒（附件和助手消息交错写入），最大的一次早 12 小时。
- **子 Agent 怎么关联父会话。** Claude：子 Agent 日志放在父会话目录的 `subagents/` 下，`sessionId` 用父会话的，另有 `agentId`、`isSidechain: true`；旁边的 `.meta.json` 写着 `agentType`、`description` 和父会话里那次调用的 `toolUseId`；父会话的工具结果 `toolUseResult` 里也有 `agentId`。Codex：子 Agent 是独立文件，`session_meta.parent_thread_id`、`agent_nickname`（例如 `Beauvoir`）、`thread_source: subagent`，`state_5` 的 `thread_spawn_edges` 有 534 条边；子文件开头复制了父会话的 `session_meta`（361 个文件因此有两个 session_meta）和父会话的历史，复制来的记录标着 `inherited_user_message: true`，时间戳全改成了子 Agent 启动的那一刻。
- **同一会话跨文件。** Codex 有 37 个线程跨 2 到 5 个文件。续接的文件名是 `rollout-<时间>-<threadId>_<新id>.jsonl`，`session_meta.history_base` 指向上一个文件（`thread_id`、`end_ordinal_exclusive`、`end_byte_offset`），`ordinal` 接着编号；例子里第一个文件写到 1231 行，续接文件从 1222 开始，**推论**是从中间分了叉（编辑或回退），之后的几行只留在旧文件里。Claude 的 rewind 是复制前缀到新文件，见上一节。
- **同一个东西有不同名字。** Codex 的 cwd 在 `turn_context` 里是路径，在 `CommandExecution` 里是 `file:///…` 的 URI。Claude 的项目目录名把 `/` 和 `.` 都换成 `-`（`-Users-bytedance-Code-Personal-Heartbeat--claude-worktrees-…`），反推不回原路径，真实路径要看 `relocated` 记录或每条记录的 `cwd`。Codex 线程和 Claude 会话都有两套 id：Claude 的 `cliSessionId` 和桌面的 `local_<id>`，Codex 的线程 id 和 `context_window.window_id`。
- **注入内容和用户真实输入怎么分。** Claude 把注入内容写成单独的 `attachment` 记录（`instructions` 是 AGENTS.md、CLAUDE.md 和记忆，`session_context` 是邮箱和 git status，`date`、`environment`、`nested_memory`、`skill_listing` 等），用户输入是 `origin.kind: human` 的 `user` 记录（687 条）。不是用户亲手写的 `user` 记录还有：后台任务通知（`<task-notification>`，76）、派生会话完成的汇报（以 `<system-reminder>` 开头，69）、中断标记 `[Request interrupted by user]`（79）、技能正文 `Base directory for this skill`（`isMeta`，75）、压缩摘要（`isCompactSummary`，9）、其他会话发来的消息（`origin.kind: peer`）。Codex 把注入内容和用户输入放进同一条 user 消息，按段标 `content_item_kinds`：`user.text` 6370 段、`environments.environment_context` 1031、`agents_md.instructions` 851、`plugins.recommendations` 474、`skills.selected_skill_instructions` 292、`user.image` 123、`user.heartbeat`（定时任务触发）3、`multi_agent.subagent_notification` 2 等；另有 1099 条插件推荐消息没有这层标注。界面条目 `UserMessage` 只有用户那一段。
- **一次交互的边界。** Claude：同一轮的记录共用 `promptId`，用户记录带 `turnPosition`（`promptIndex`、`turnIndex`），一轮结束有 `stop_hook_summary`；忙时排队的消息记成 `queue-operation`（enqueue、dequeue）。Codex：`task_started` 到 `task_complete` 或 `turn_aborted`，共用 `turn_id`，子 Agent 的轮次还带 `root_turn_id`；`thread_turns` 表里 5332 轮完成、189 轮中断、105 轮失败。
- **一条回复拆成几条。** Claude 的 23912 条 assistant 记录只对应 10273 个 API 回复，每个内容块一条，`usage` 每条都重复。
- **Claude 会话内存有发给模型的原文。** `api-request` 加 `api-request-blob` 按内容哈希存了每次请求的完整消息，能还原模型实际看到的上下文，包括所有 system-reminder。Codex 的 `world_state` 存了每轮的环境快照（AGENTS.md 正文、技能清单、时区）。
- **语音也在会话里。** Codex 有 16 个语音线程，用户和助手的话都转成 `transcript_segment` 文本，共 1591 段。

## 拿到它的代价

- 都是本机文件和 sqlite，读取不需要登录和接口。Codex 的 sqlite 在运行时被写，只读打开要用 `mode=ro`；`thread_history_1` 没有 WAL 文件，`mode=ro` 打不开，要用 `immutable=1`。
- 持久的部分能事后补，但有窗口期：Claude 的旧会话会消失（见上），Codex 会压缩和搬家。按文件路径记会失效，按会话 id 能找回。
- 需要常驻的只有瞬时的边角：Claude 子 Agent 的临时输出、Codex 的滚动日志。
- 读 Claude 要读两处：`~/.claude/projects` 和桌面应用目录里的会话索引（标题、归档、rewind、派生关系只在后者）。读 Codex 要读日志、`session_index.jsonl` 和 `state_5.sqlite`（归档状态、父子边）。
- 版本号：Claude 每条记录都有 `version`，这 12 天只有 2.1.308 和 2.1.309 两个版本，记录类型一样。Codex 的 `session_meta.cli_version` 三个月里有 28 个版本（0.147.0-alpha.6.5 到 0.166.0-alpha.5.1），在统计的记录类型上看不出差别：`world_state`、`item_completed`、`custom_tool_call`、`content_item_kinds` 在最早的版本里就有。`state_5` 里有一条名为 `legacy_to_paginated_v1` 的迁移记录，所有文件都是 `history_mode: paginated`。**推论**：早期文件可能被迁移重写过，现在看到的是统一后的格式；文件修改时间和最后一条记录吻合，这一点没能证实。
- 两家的格式都没有公开文档，记录类型多、字段随版本加（例如 Claude 的 `api-request` 系列、Codex 的 `realtime_item`）。

## 和其他来源的重合

- **Shell 历史。** Claude 有 6879 次 Bash 调用，Codex 有 56425 条 `CommandExecution`，带命令、cwd、退出码、耗时和输出。
- **文件。** Claude 的 `file-history-snapshot`（741）和 `~/.claude/file-history/` 存了改动前的备份，`edited_text_file` 附件记着改了哪；Codex 的 `FileChange`（6973）对新增文件存了全文。
- **Git。** Claude 每条记录有 `gitBranch`；Codex 每个会话有提交、分支和远端地址，`threads` 表也有 `git_sha`、`git_branch`、`git_origin_url`。会话里能看到 Agent 自己的 `git commit` 命令。
- **前台应用。** 会话都在 Claude 桌面应用和 Codex 桌面应用里进行。**推论**：旧 Heartbeat 的前台时段里这两个应用的时段和会话时间重合，没有去库里核对。
- **IM。** 用户会把 IM 里的对话原样贴进会话，例如 Codex 里一条以「某人 に返信:」开头、贴了整段群聊回复的消息。
- **录音。** Codex 的语音线程本身就是录音的转写。
- **浏览器。** Claude 的浏览器面板截图和访问的本地页面 URL 在工具调用里。
- **两家之间。** Codex 的 `external_agent_session_imports.json` 列着 Claude 的会话文件路径，Codex 会读 Claude 的日志。同一个仓库常常两家都开会话（Heartbeat 在两边都是最多的 cwd）。
- **讲述和自述。** 会话里会读 `ME.md`，工具结果里有它的全文（例如一次 33.5 KB 的输出存进了 `tool-results`）。

## 对需求的意义（推论，待确认）

- 一个会话的信息散在日志、索引文件、sqlite 和应用目录好几处，标题、归档、父子关系都在日志外面。原样保存时要把这几处都存下，单存日志会丢关系。
- 同一条记录会出现在好几个文件里（Claude rewind 复制前缀、Codex 子 Agent 复制父历史、Codex 续接文件），而且复制件的时间戳可能被改写。原样保存不做去重，但读的时候要知道这一点。
- 已经持久化的数据也会消失、被压缩、被搬走。Claude 10-23 之前的会话已经没了，这类来源要定期采，不能等到想用时再补。
- 「用户说了什么」和「Agent 被告知了什么」是两层。两家都标出了哪段是用户亲手输入，标法不同；注入内容本身（AGENTS.md、记忆、环境）也是有用的上下文，不该丢。
- 推理过程基本拿不到，能拿到的只有动作（命令、改文件、工具调用）和对话文本。
- Codex 三个月 28 个版本，格式变化快。原样保存时要记下是哪个版本写的。

## 没能核实的部分

- Claude 旧会话消失的原因：自动清理还是目录重置。
- 会话在 resume 和 continue 时文件怎么变：这次只看了已经写完的文件，没有观察正在进行的会话。
- Codex 早期文件是否被迁移重写过。
- 前台应用时段和会话时间的重合，没有去旧 Heartbeat 库里对。
- Codex 粘贴的长文本是只存文件，还是日志里也有全文。
