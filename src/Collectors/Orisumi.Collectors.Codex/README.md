# Codex 会话采集

在 Orisumi 仓库根目录采集本机 Codex 会话索引中的全部会话，包括已归档会话。

```sh
dotnet run --project src/Orisumi.Cli -- codex capture --all
```

采集单个会话时可以指定会话 ID。省略会话 ID 时，CLI 使用当前环境里的 `CODEX_THREAD_ID`。

```sh
dotnet run --project src/Orisumi.Cli -- codex capture THREAD_ID
```

采集器只读打开 Codex 的本地状态库和分页消息历史库，把结果写入 `.orisumi/raw/codex.sqlite`。全局选项 `--store DIRECTORY` 可指定输出目录。Codex 数据目录优先使用 `CODEX_HOME`，默认是 `~/.codex`。

- `threads` 每个会话一行，记录显示标题、工作目录、创建和更新时间及当前归档标志 `archived`。Codex 有显示名称时优先采集该名称。
- `messages` 每条用户或助手消息一行，记录角色、时间和 Codex 的原始消息 JSON。消息 ID 使用 Codex 的 `item_id`，与会话 ID、回合 ID 一起构成主键。`rollout_ordinal` 可用于还原会话中的消息顺序。

再次采集时，对照源端和目标库中的行，插入新行并更新有变化的行。源端移除的记录保留在采集库中。会话改名历史和消息编辑历史需要单独采集事件。
