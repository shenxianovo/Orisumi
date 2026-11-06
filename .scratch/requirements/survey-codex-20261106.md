# Codex 本机来源盘点（2026-11-06）

本记录保留这次只读勘探时的来源形态与计数。正式采集范围见[来源模型](../../src/Docs/Orisumi.Docs/content/docs/model/sources.mdx#codex)，当前采用与替代方案见[决策全景](../orisumi-roots/README.md)。计数描述盘点当时的来源，后续实际采集可能不同。

## 观察范围与结果

2026-11-06 枚举本机目录，并在 SQLite 只读连接的一个读事务内查询表结构与计数。

| 对象 | 观察 |
| --- | --- |
| `sessions/` | 59 个文件，200794916 字节 |
| `archived_sessions/` | 1134 个文件，1049393908 字节 |
| 两处日志格式 | 137 个 `.jsonl`、1056 个 `.jsonl.zst` |
| `session_index.jsonl` | 91635 字节 |
| `state_5.sqlite` | 主文件 4300800 字节；使用 WAL，旁边存在 WAL 与 SHM 文件 |
| `threads`、`thread_spawn_edges` | 1140 行、541 行 |
| 其他状态 | 12 个项目、12 条项目目录关系、1 个会话分组、10 条会话关联记录 |
| 会话关联记录 | 6 个已归档工作树、3 个 PR、1 个工作树；这张表名为 `thread_attachments`，内容不是粘贴的长文本文件 |
| `attachments/` | 51 个文件，2412145 字节；JSON 和文本 |
| `generated_images/` | 50 个 PNG，66614803 字节 |
| `visualizations/` | 677 个文件，256533751 字节；包含产物、代码和工具依赖 |

旧勘探已发现日志通过路径引用粘贴的长文本；图片和文本目录的存在及体量已在本轮核对。可视化目录本轮只枚举类型和体量，尚未逐项确认与会话的关联或哪些依赖可重建。

## 发现与边界

- 会话日志、完整标题索引、状态库、粘贴附件和生成产物分布在不同位置，分别有直接出处。
- `state_5.sqlite` 除 `threads`、`thread_spawn_edges` 外，还有项目、分组和关联记录；迁移表包含 BLOB 值。按两张已知表导出会遗漏这些内容。
- 状态库使用 WAL。主库和旁文件分别做普通文件复制，不能保证得到同一个数据库状态；取得方式需要使用 SQLite 的一致性机制。
- 数据库内部一致与日志、索引、附件之间的同时性分别判断。
- `thread_history_1.sqlite` 在已有勘探中被识别为日志投影，见[助手会话勘探](survey-assistant-sessions.md)。这次盘点聚焦 `state_5.sqlite`，没有遍历所有 Codex 数据库。
- 本记录的观察只包括文件形态、目录体量、表结构及计数，没有核对每份产物的会话归属。正式采集与恢复的验收另行记录。

## 官方资料

- [SQLite 隔离](https://sqlite.org/isolation.html)
- [在线备份 API](https://sqlite.org/c3ref/backup_finish.html)
- [运行中数据库的备份](https://sqlite.org/howtocorrupt.html#backup_or_restore_while_a_transaction_is_active)
