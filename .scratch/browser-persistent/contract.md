# 浏览器持久记录候选契约

状态：Agent 按本轮勘探提出的候选，尚未进入正式采集。依据 [浏览器持久记录勘探](../requirements/survey-browser-persistent.md)、现行原件与浏览器观测模型。普通 Chrome 的 History 仍因系统权限不能读取；本契约没有把受控测试成功写成日常来源已接通。

## 一个具体例子

同一测试 Profile 内，页面 A 经中间地址 B 跳到 C 后重新加载。完整 History 保存 A、B、C 及每次访问；history.search 只列 A、C，随后逐 URL getVisits 也不能自动找到 B。候选先保留完整可取得的 History 数据库及其原路径、取得方法和时间；若另采扩展响应，它是一份有明确查询参数与可见范围的原件。后续读出才能比较和关联两路事实。

本次反例、字段和复现位置固定在 `.artifacts/browser-persistent-20261106T161257Z-9ed45d/`；三轮数据库/文件和 39 次 API 返回均保留。正式设计不得以前两者互相可重建为由丢弃未经证明能恢复的字段。

## 文件路线

每个明确选择的 Chrome 用户数据目录下的 Profile 单独作为来源。候选 source 名为 `browser-persistent`，origin 使用该 Profile 的绝对路径；确切名称在正式采用时与来源清单统一。Chrome 产品名、版本和取得方法进入可取得的出处，不凭扩展 ID 猜 Profile 路径。

| 逻辑路径 | 实际位置 | 取得方式与原件 |
| --- | --- | --- |
| `History` | `<选定 Profile>/History` | 现有 `FileInputMethod.SqliteBackup`；完整 online backup，包括原 schema 与所有表，不只投影 urls/visits/downloads |
| `Bookmarks` | `<选定 Profile>/Bookmarks` | 现有 `FileInputMethod.FileBytes`；取得时的完整原字节，保留空树、未知字段、GUID、原时间字符串与顺序 |

两个路径固定声明，逐轮检查，可选只表示明确不存在时没有取得该项。权限拒绝、坏库、类型错误、读取中消失都保留失败，不等同于空历史或空书签。链接按既有文件规则留下链接事实，不能把未取得 SQLite 快照称为成功。已保存旧版本持续可恢复。

来源身份保留用户数据目录与 Profile 范围；URL ID、visit ID、bookmark ID、download ID 都按来源交来的类型保存。两个 Profile 内相同数字不合并。同一个 Profile 的复制、重置、重建和跨机迁移不能只凭旧 ID 自动判为同一身份。

正式复用 Collector 时，沿用采集根的整轮开始与完成窗口；当前清单不提供逐文件的取得起止。本次勘探另外保存了逐文件起止作为测量证据。完整库保证的是 SQLite 可读一致快照，History 与 Bookmarks 并非跨文件事务。文件尚未持久化的浏览器内存状态不凭空补写。当前权限条件不满足时先记录具体失败，不修改权限、不更换高权限进程绕过。

## 接口路线

若以后采用 API 补充，沿用 `orisumi-browser-v1` 的 query 观测，保留 operation、完整 arguments、requestedAt、observedAt、完整 callbackArgs/error 与安装 instance/run/sequence。NativeReceiver 继续保存收到的 payload 字节。这里不新增归一化的历史、书签或下载实体格式。

| 调用 | 取得范围与必须保留的条件 |
| --- | --- |
| `history.search` | 保存 text/startTime/endTime/maxResults。结果是接口列出的页面集合；即使未达到上限，也不能由此证明覆盖全库 |
| `history.getVisits` | 保存传入 URL 与返回数组；此次 URL 怎样取得也可由触发观测追溯。未知 URL 不能假设已遍历 |
| `bookmarks.getTree` | 完整接口返回树；不重排、不以 URL 去重，不丢弃与文件表示不同的根 |
| `downloads.search` | 显式查询范围和 limit；`limit:0` 可请求全部匹配记录，响应体积须实测。文件路径、exists 与实际下载内容各按来源事实解释 |
| `sessions.getRecentlyClosed` | 保存最多 25 项的受限结果，不能称为完整会话历史；同步设备范围单独说明 |

上述权限尚未加入日常扩展。样本仅证明测试版本中这些调用可用，不包含日常 API 历史回填、超大响应、时间边界分页或中断续取的生产保证。历史遍历属于明确的新工作量；若正式采用，先以一个真实 Profile 完成限量返回、重叠时间边界、断连重试、保存回执与独立恢复的端到端验收，再扩展范围。

下载候选保存记录，不因路径出现就自动递归读取下载文件；最近关闭接口候选也没有扩大到整个 Profile 或 Sessions 二进制目录。Cookie、登录材料及未勘探目录不属于本契约。

## 采用前验收条件

1. 明确真实 Profile 路径与所需普通读取权限；权限拒绝留下可区分的失败。真实来源要独立验收，受控 Profile 成功不能替代。
2. 已知重定向、重载、同 URL 多书签与真实下载的原字段全部留下；schema 不受固定列名单裁剪。完整 History 恢复后仍有中间重定向和限定比特。
3. 原始文件或数据库关闭、移动或已不存在时，保存材料仍能经公开恢复接口恢复；逐对象 hash、SQLite quick_check、原文件字节与取得方法对应。只恢复当前根不能替代旧版本的验证。
4. 同一持续来源实例中可选 Bookmarks 后来出现能够取得；明确缺席与读取失败区分。删除条目后的新快照保留新状态，旧根仍能恢复删除前内容。
5. API 路线如采用，原响应未知字段、空结果、错误、请求范围、同 Profile 重启与多 Profile 身份分别实测；search 的不完整反例必须保留，不能写成全库备份承诺。
6. 正式实现复用现有 Collector 或 browser 观测的 module interface，经独立审阅和 `orisumi verify`。若出现真实规模或重试问题，再据证据设计所需 seam；本候选不先建通用分页平台。

本轮已完成受控样本、哈希核对、独立打开副本与删除前后保留；尚未完成真实 Profile 原件取得、产品存储接入、公开恢复接口验收和日常运行。是否采用及采用范围由后续明确交办决定。
