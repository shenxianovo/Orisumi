# 勘探：旧 Heartbeat

旧库中的行是目前可取得的历史材料。2026-11-06 完成本人一天的数据导洞，在一个一致只读事务取得时段、事件、缺口及必要关联行，再脱离旧服务恢复到独立 PostgreSQL。随后按 [导出契约](../../src/Docs/Orisumi.Docs/content/docs/model/heartbeat.mdx)完成全部所选本人历史，见 [全历史验收](../../src/Docs/Orisumi.Docs/content/docs/model/validation.mdx#旧-heartbeat-完整历史)。本页保留一天样本的范围与字段发现。

## 样本与取得范围

本人标识来自已有 ignored 运维记录中的明确 `OwnerId`，先核对 `Users` 中恰好有一行。连接位置、标识原值、实际 SQL、命令参数与原始行只保存在 `.artifacts/heartbeat-survey-20261106/`。原记录中的真实行示例也原字节留在该目录的 `survey-heartbeat-before.md`。

样本日期是 Asia/Shanghai 的 2026-10-30，对应 UTC 半开区间 `[2026-10-29T16:00:00Z, 2026-10-30T16:00:00Z)`。先试的 11-02 有时段和事件但没有当天缺口；选择 10-30 是为了同时覆盖三种事实。之前的查询与超时输出均保留。

| 表 | 本轮行数 | 原生 binary COPY 字节 | 范围 |
| --- | ---: | ---: | --- |
| Segments | 3,435 | 1,257,619 | 本人、起点在当天或从此前跨入当天的完整时段 |
| Events | 65,768 | 14,634,838 | 本人、时间在当天的事件 |
| FactGaps | 642 | 85,407 | 本人、按既有 ticks 解释选中的完整缺口 |
| Streams | 8 | 1,333 | 三张事实表实际引用的本人流 |
| Subjects | 4 | 403 | 所选流实际引用的本人主体 |
| Devices | 2 | 293 | 所选主体实际引用的本人设备 |
| AppIdentities | 40 | 2,029 | 所选事实及设备当前应用实际引用的身份 |
| Apps | 39 | 1,801 | 所选应用身份实际引用的应用 |
| Users | 1 | 95 | 本人身份行 |
| __EFMigrationsHistory | 33 | 1,705 | 当前模式的迁移记录 |

合计 69,972 行、15,985,523 字节，约 15.25 MiB。其中时段包含 system 2,777 条、browser 649 条、vrchat.account 9 条；事件均来自 system。1 条时段从前一天跨入，4 条跨出当天，保存整行和原起止值。

成功原件位于 `.artifacts/heartbeat-survey-20261106/day-20261106T152338Z/`。`*.pgcopy` 是来源直接交付的原生二进制导出；`transaction.stdout` 保留整个会话的原输出，`evidence-index.json` 给出各表原件在其中的准确字节区间。两处内容逐字节相同。`samples.private.stdout` 有两条时段、事件、缺口和流的可读行示例，来自独立恢复后的查询，是便于查看的读出，原件仍是 COPY 字节。

## 当前模式与跨表关系

来源 PostgreSQL 是 18.4，UTF8。所选 10 张表共有 60 列，类型包含 bigint、integer、uuid、boolean、text、带长度的 varchar、timestamptz 和 jsonb。当前列顺序、名称、类型 OID、完整类型名、可空性和约束定义均在同一导出事务内取得；33 条迁移记录不能替代这些实际模式事实。

事实通过 `(OwnerId, StreamId)` 引用 Streams，再通过 `(OwnerId, SubjectId)` 引用 Subjects。Subjects 的 DeviceId 外键仅引用 Devices.Id，没有把所有者写进外键。因此导出关联设备时仍加本人条件，并核对所需 ID 集合是否完全取得。AppIdentities 和 Apps 是共享查找表，本轮只拿本人事实及所选设备实际引用的行。

本轮逐表检查有 OwnerId 的行均属于本人，Users 恰好匹配本人；每一级需要的 ID 集合与取得集合相等。独立恢复又验证全部 9 个外键。这些检查限定的是本轮所选行；其他人的内容没有读取。关联表表示取得快照中的现存状态，不自动代表采样日当天的历史状态。

## 一致性与实际代价

通过已有 SSH 连接，在现有数据库容器内运行 `psql -X`。会话明确设置默认只读、单语句 30 秒超时及锁等待 3 秒；事务使用 `REPEATABLE READ READ ONLY`。对所选表取得事务级 ACCESS SHARE 锁后再读模式与数据，结束执行 ROLLBACK。来源服务、数据、权限和配置均保持原状。

每张表 COPY 前后采样服务器时刻和 `pg_current_snapshot()`；所有表及事务末尾的 snapshot 相同。读取顺序是事实行、从原生字段取得关联键、关联行，始终在同一连接和事务中。模式查询的时刻保留在相邻服务器采样时间界定的区间内，没有为它们声称独立的精确时间点。[PostgreSQL 的 Repeatable Read 定义](https://www.postgresql.org/docs/18/transaction-iso.html#XACT-REPEATABLE-READ)提供同一事务内的稳定读视图。

这一轮从本机发起到完成为 43.17 秒，事务内首末服务器采样相隔 31.09 秒；各表 COPY 中时段约 5.78 秒、事件 10.34 秒、缺口 7.86 秒。这些是本次网络、服务器和数据分布下的观察，不能线性外推全部历史。

将事实和整个关联闭包合成一条大查询，两次触发 30 秒语句超时。缩小投影仍未解决；改成在同一事务内先取得事实，再按实际外键取关联行后完成。时间索引也有差异：Events 有 OwnerId/Timestamp 索引，Segments 的时间位于 OwnerId/Source/StartTime 组合中，FactGaps 没有时间索引。本轮没有改索引。

## 字段如何恢复

本机通过 Homebrew 安装同主版本 PostgreSQL 18.6，为本轮建立专用临时数据目录，仅使用 Unix socket 启动实例。按保存的列类型建空表，导入全部 COPY，先加主键及检查约束，再加外键；重新按原查询和排序导出。10 个表全部字节相同，74 个约束通过，包括 53 个 NOT NULL、10 个主键、9 个外键、2 个检查约束。验证结束后实例已停止，原件、失败输出、恢复数据和命令记录留存。

| 本轮实际覆盖 | 发现及恢复条件 |
| --- | --- |
| SQL NULL | Events.AppIdentityId 全部为 NULL；Segments 有 9 个，Devices 当前身份有 1 个，Streams 采集器实例有 5 个，Subjects 设备关联有 1 个。按原生 NULL 标记恢复 |
| JSONB | 三个 jsonb 列顶层均为对象；478 个时段 payload 含直接子项 JSON null。SQL NULL 与 JSON null 分开保留 |
| 文本 | 应用、设备、主体中有非 ASCII 字符；2,105 个时段 payload 含非 ASCII，2,777 个 payload 的 JSON 表示含换行转义。无需按行切割内容 |
| 时间 | 2,782 个时段起点及 65,714 个事件时间有毫秒以下部分；保存 timestamptz 原生字段，恢复后字节相同 |
| 缺口 | Start/End 为 bigint，原整数全部恢复；EstimatedFactsLost 本轮均有值。时间单位解释另记 |

`jsonb` 在来源库中已经不保留输入 JSON 的空白、键顺序和重复键；本轮保存的是来源当前值的导出表示，不能追回写入前的 JSON 文本。[PostgreSQL JSON 类型](https://www.postgresql.org/docs/18/datatype-json.html)说明了这个边界。timestamptz 保存时间点，原始输入时区也不在列值中；取得时的 TimeZone=UTC 随元信息记录。[日期时间类型](https://www.postgresql.org/docs/18/datatype-datetime.html#DATATYPE-DATETIME-INPUT-TIMESTAMPS)说明了时区行为。

Binary COPY 依赖列类型和数据库版本，官方不保证任意版本与架构间可移植。本轮证明的是这批实际类型在来源 18.4 与本机 18.6 间的完整往返。独立恢复需要保存模式、列顺序和版本，不能只留二进制文件。[COPY 的二进制格式](https://www.postgresql.org/docs/18/sql-copy.html#SQL-COPY-NOTES)给出格式与限制。

## 已知历史与缺口

2026-11-03 的既有只读勘探给出的历史基线是：数据库约 2.1 GB，键鼠事件约 270 万行、时段约 28 万行、缺口约 65 万行；最早材料到 2026-02-26。浏览器记录从 7 月起，VRChat 从 9 月起；这些是此前观察，不是本轮全历史重新计数。

原始活动发生时是瞬时的；现在数据库里留下的行可以事后取得。已经丢弃的事件无法通过导出补回。此前样本记录过积压及缓冲溢出引发的长时间缺口；当前导洞保存了现存缺口和原因列，保留了「此处没有采到」的来源事实。

既有历史还记录了 Windows 早期没有窗口标题、应用身份在 Windows 进程名与 macOS bundle ID 间不同、采集器版本导致 payload 和 Revision 语义变化。库内的当前行不能补出未曾记录的标题、输入文字或所有中间修订。当前迁移记录也不能证明每条旧事实由哪个程序版本产生。

当前缺口表 Start/End 的单位只有 bigint，既有勘探记为 .NET ticks。本轮按 UTC ticks 换算日期窗口选取 642 条，并保留这个采样假设。整数原值恢复已证明；「所有旧写入方均使用 UTC ticks」仍需要当前来源契约或其他证据，不能仅由数值类型推出。

本轮覆盖本人一天与必要关联行，未读取 Recaps 等已有理解、AppIcons 正文或其他扩展材料，也未取得全历史、已删除行、旧行版本和数据库运行配置。部分表有默认值、索引等运行结构；当前恢复证明所选字段与约束，不等同重建旧应用数据库。

## 与其他来源的重合

浏览器时段与系统中的浏览器前台时段重合，前者留下网页层面的材料。VRChat 世界时段与系统中的 VRChat 应用时段也会重合。前台标题可能同时出现通信与文档材料。这些来源各自保留，不在勘探中合并；多台设备都有前台也不能直接解释成人的注意力。

## 推论与下一步

一天样本支持「本人及必要关联行的一致导出」。后续在本机用原生 text COPY 完成同值往返，正文比 binary 多 20.27%，详见 [文本格式实测](../../src/Docs/Orisumi.Docs/content/docs/model/heartbeat.mdx#恢复验收)。

Agent 已采用全本人 Users、Devices、Subjects、Streams 独立保存，Segments、Events、FactGaps 全历史，以及实际引用的共享应用闭包。一次原生文本导出已取得 3,748,506 行、960,756,574 字节正文，同事务模式与查询证据一起通过现有 `capture files` 保存。从采集根恢复至新目录后，在拒绝原目录及 IP 网络访问的独立 PostgreSQL 中完成全量导入与逐字节重导核对。当前没有增加永久采集器或周期任务。
