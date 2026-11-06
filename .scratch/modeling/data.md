# 数据怎么拿

开发和答题用到的真实数据在哪、怎么读。

- 旧 Heartbeat：使用本机配置的只读连接，查询时设 `PGOPTIONS='-c default_transaction_read_only=on'`。实际维护入口、数据库账号与数据拥有者标识保存在 `.local/operations/modeling-data-20261106.private.md`；查询必须按本人的 `OwnerId` 参数筛选。
- 助手会话：本机 `~/.claude/projects/`、`~/Library/Application Support/Claude/claude-code-sessions/`、`~/.codex/`，位置和格式见 `survey-assistant-sessions.md`。10-30 到 11-02 的 Claude Code 会话另存在 `.local/benchmark/claude-code/`，保持原来的目录结构，原处的删掉以后读这里。
- Git：`~/Code` 下的仓库。

## 注意

- Claude Code 没有设置会话保留期，按默认会删掉旧会话，10-23 以前的已经没了。用户决定先不改保留期，测量基准用到的 10-30 到 11-02 已另存，见上。
- Chrome 等浏览器的本地数据受 macOS 保护，要在系统设置里给 Claude 开完全磁盘访问权限。导洞的浏览器数据来自旧 Heartbeat，用不到这个权限。
- 本地的 IM 库是加密的，导洞不碰。
