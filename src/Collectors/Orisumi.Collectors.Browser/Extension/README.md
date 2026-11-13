# Orisumi Chrome 采集扩展

从仓库根目录构建本机采集器和扩展。

```sh
dotnet build Orisumi.slnx
npm --prefix src/Collectors/Orisumi.Collectors.Browser/Extension ci
npm --prefix src/Collectors/Orisumi.Collectors.Browser/Extension run build
```

在 Chrome 扩展页启用开发者模式，加载构建产物目录 `src/Collectors/Orisumi.Collectors.Browser/ChromeExtension`。复制扩展 ID，然后在仓库根目录注册本机采集器。

```sh
dotnet run --no-build --project src/Orisumi.Cli -- browser register-chrome EXTENSION_ID
```

扩展安装和 Chrome 启动时会自动同步，之后每分钟继续采集。注册本机采集器后，下一轮会自动重试。数据写入 `.orisumi/raw/browser.sqlite`。

`history_page_title_observations` 按浏览器配置和 URL 追加 Chrome 历史返回的非空标题初值和变化；`history_pages` 视图返回最新已知标题。`observed_at_utc_ms` 记录采集器首次看到当前标题的时间。标题有独立的回填进度，重载新版扩展即可为现有库回填。可以通过 `browser`、`profile_id` 和 `url` 关联 `visits`；这个标题是采集时看到的标题，不代表某次历史访问时的标题。

修改扩展源码后，重新运行构建命令并在 Chrome 扩展页重载。运行 `npm test` 可验证历史同步的断点重试和数据库重建行为。

## 书签的采集与分层

书签在同一个 `browser.sqlite` 中分为事实表和查询视图。采集器监听创建、修改、移动、删除、排序通知；启动时和每分钟读取书签树，用于建立初值、对齐离线期间的变化和读取最近使用时间。树读取期间如果发生事件，丢弃这份顺序不确定的快照并重试。没有变化的周期快照不新增事实。

事实表保留初值和变化，删除书签只追加“不存在”，保留已采集历史。同一次观测产生的多条事实共享 `observation_id`，在一个事务中写入。事件先保存到扩展的本地待发送队列，接收确认后才移除，重试复用同一个事件 ID。

| 表 | 字段与含义 |
|---|---|
| `bookmarks` | `bookmark_key` 为库内关联键；`browser`、`profile_id`、`bookmark_id` 共同标识来源节点；`node_kind` 为书签或文件夹；`added_at_utc_ms` 为源端创建时间。身份信息未知时为空，后续可补全 |
| `bookmark_presence_events` | `present` 表示存在或已消失 |
| `bookmark_title_events` | `title` 为书签或文件夹的名称 |
| `bookmark_url_events` | `url` 为书签的目标网址，文件夹没有此记录 |
| `bookmark_location_events` | `parent_id` 为父文件夹，`position` 为从 0 开始的排序位置；根节点两者为空 |
| `bookmark_last_used_observations` | `last_used_at_utc_ms` 为浏览器报告的最近打开时间，未知值不会覆盖已知记录；它不能列出每一次使用 |

五张变化表都使用 `bookmark_key` 关联身份，使用 `observation_id` 关联观测。`bookmark_observations` 的 `event_id` 是扩展生成的重试去重标识；`run_id`、`run_sequence` 表示采集会话与会话内顺序；`observed_at_utc_ms` 是采集器收到通知或读到快照的时间；`kind` 区分 `snapshot`、`created`、`changed`、`moved`、`removed`、`reordered`。`observation_id` 是入库顺序，也用于还原历史状态。

`capture_runs` 保存书签采集会话的 `run_id`、`browser`、`profile_id`、`scope='bookmarks'` 和 `started_at_utc_ms`。当前扩展无法可靠取得关闭时刻，`stopped_at_utc_ms` 留空表示结束尚未确认。新会话的首次快照重新记录当前节点的初值。

查询层使用视图，从事实表还原数据，不维护另一套可写状态：

| 视图 | 一行的含义 |
|---|---|
| `bookmark_state` | 一个节点的最新已知状态，包括已删除节点 |
| `current_bookmarks` | 一个目前存在的节点，可直接读取名称、网址、目录和位置 |
| `bookmark_history` | 一个节点在一次观测后的完整状态；包含 `observation_id`、观测时间和事件种类 |

`bookmark_history` 承担明细查询，当前状态视图服务直接查询；之后的统计可以继续从这些明细计算。这些是逻辑分层，仍保存在同一个来源数据库中。

例如书签 B 最初叫「Orisumi」，位于「工作」目录第 0 位：

| 操作 | 新增事实 |
|---|---|
| 创建 | `bookmarks` 身份一行；存在 `true`；名称 `Orisumi`；目标 URL；位置 `(工作, 0)` |
| 改名为「Orisumi project」 | 名称一行，其他属性不重复保存 |
| 移到「阅读」目录第 2 位 | 位置 `(阅读, 2)` 一行；受影响的相邻节点也记录新位置 |
| 删除 | 存在 `false` 一行；旧名称、URL 和位置保留 |

历史视图返回 B 的四个状态，当前视图在删除后不再返回 B。删除文件夹时，已知子孙节点也追加消失事实。

```sql
SELECT bookmark_id, title, url, parent_id, position
FROM current_bookmarks
WHERE browser = 'chrome' AND profile_id = 'PROFILE_ID'
ORDER BY parent_id, position;

SELECT observed_at_utc_ms, kind, present, title, url, parent_id, position
FROM bookmark_history
WHERE browser = 'chrome' AND profile_id = 'PROFILE_ID' AND bookmark_id = 'B'
ORDER BY observation_id;
```

快照只能反映读取时看到的结果，不能补出离线期间所有中间变化。事件通知的时间也是接收时间。来源没有提供的精确发生时间不推断。

所有来源表均采用当前结构，不提供旧库迁移。重建输出库后重载扩展，建立新的采集会话；历史回填游标会根据数据库 ID 自动失效。

## 标签页与下载

`capture_runs.scope` 区分 `bookmarks`、`active_tabs`、`downloads`。标签页事件保存在 `tab_observations`，其中 `event_id` 用于重试去重，`run_sequence` 表示同一会话内的顺序。其事实分为：

- `active_tab_focus_events`：当前聚焦的 `window_id`、`tab_id`；两者为空表示没有可采集的前台标签页。
- `tab_url_observations`：某标签页 URL 的初值和变化。
- `tab_title_observations`：某标签页标题的初值和变化，包括单独发生的标题更新。

只观察当前聚焦的普通标签页。进入隐身窗口会结束普通标签页的焦点状态。标签页 ID 只在采集会话内关联。`active_tab_state_history` 还原每次事件后的焦点、URL 和标题，`current_active_tabs` 返回每个采集会话的最后已知状态；旧会话不等于仍在活动。

`downloads` 只保存来源身份，`download_observations` 保存事件、采集会话、时间和来源下载 ID 的关联。创建与变化回调及时入队；定期快照用于建立初值和补齐断线后的当前状态：

- `download_resource_observations`：资源描述 `url`、`final_url`、`started_at_utc_ms`、`total_bytes`、`mime_type`，初值及变化各一条。大小未知时为空，不采集下载进度字节数。
- `download_state_observations`：下载状态 `state` 及来源完成时间 `ended_at_utc_ms`。
- `download_path_observations`：本地文件路径 `local_path`。

`download_history` 还原每次观测后的完整状态，`current_downloads` 返回每个下载的最后已知状态。删除 Chrome 下载列表记录不等于删除本地文件，因此不会据此推断文件不存在。隐身下载不采集。来源回调中未涉及的字段不会清空已经知道的值。

`npm test` 还会构建本机接收端，在临时 SQLite 中验证创建、改名、移动、删除、子树删除、排序、重复投递、相同 ID 的配置隔离，并验证扩展重试与快照期间发生变化的处理。运行需要 .NET SDK 和 `sqlite3`。
