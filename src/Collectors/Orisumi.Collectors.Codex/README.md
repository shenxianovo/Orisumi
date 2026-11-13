# Codex 会话采集

在 Orisumi 仓库根目录采集本机 Codex 会话索引中的全部会话，包括已归档会话。

```sh
orisumi codex capture --all
```

采集单个会话时可以指定会话 ID。省略会话 ID 时，CLI 使用当前环境里的 `CODEX_THREAD_ID`。

```sh
orisumi codex capture THREAD_ID
```

采集器只读打开 Codex 的本地状态库和分页消息历史库，把结果写入 `.orisumi/raw/codex.sqlite`。全局选项 `--store DIRECTORY` 可指定输出目录。Codex 数据目录优先使用 `CODEX_HOME`，默认是 `~/.codex`。

事实表保存初值和观测到的变化，查询视图还原最新已知状态。

| 事实表 | 一行记录什么 |
|---|---|
| `threads` | 会话身份 `thread_id` 和来源创建时间 `created_at_utc_ms` |
| `thread_title_observations` | 一次标题初值或变化；显示名称优先于默认标题 |
| `thread_cwd_observations` | 一次工作目录初值或变化 |
| `thread_archive_observations` | 一次归档状态初值或变化 |
| `turns` | 一轮执行的身份：`thread_id`、`turn_id` |
| `turn_timing_observations` | 来源报告的开始和完成时间；未知时间为空 |
| `turn_status_observations` | 一次执行状态初值或变化 |
| `messages` | 消息身份、所属会话和回合、角色、创建时间、`rollout_ordinal` 顺序 |
| `message_content_observations` | 一份发生变化的来源消息 `item_json` |
| `response_token_usage_observations` | 日志中一条响应用量记录，保留来源 `source_line` 和 `recorded_at_utc_ms` |
| `capture_runs` | 一次读取源库的开始、结束和可选 `thread_filter`；它表示导入批次 |

属性观测通过 `run_id` 关联采集时间；源库没有提供每个属性的变化时间，因此不使用会话整体更新时间代替它。连续两次导入之间发生又恢复的变化无法从源库快照中还原。重复读取相同状态不重复写属性。

`current_threads`、`current_turns`、`current_messages` 分别组合最新属性。`response_token_usage` 取每个响应的最后一条已采集用量，统计时使用这个视图，避免把修订重复相加。

用量包含 `total_input_tokens`、`cache_read_input_tokens`、`cache_write_input_tokens`、`total_output_tokens`、`reasoning_output_tokens`；来源未报告的值为空。`model` 来自相应回合的 `turn_context`，无法关联时为空。支持普通 JSONL 和 Zstandard 归档。`_token_usage_rollouts` 只保存文件指纹以跳过未修改文件，是可更新的采集进度。

来源文件和数据库以只读方式访问。移除源记录不会删除已采集事实。运行与分层约定见 [采集与存储](../../../docs/capture-storage.md)。
