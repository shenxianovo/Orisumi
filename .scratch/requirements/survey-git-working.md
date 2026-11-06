# 勘探：Git 工作状态与本地历史边界

2026-11-07。本轮只勘探决策全景「Git当前工作与历史边界 / working」，没有改变生产采集范围。当前已采用的引用可达历史见 [Git 模型](../../src/Docs/Orisumi.Docs/content/docs/model/git.mdx)，前轮样本见 [Git 引用与可达历史勘探](survey-git.md)。

一个最小例子：同一 `tracked.txt` 在 HEAD 中是 `committed version\n`，index 中是 `staged version\n`，磁盘工作区是 `working version\n`，旁边还有 untracked 和 ignored 原件。当前 bundle 能恢复 HEAD；把原 index 直接加回仍缺暂存 blob；完整保存这个专用仓库的工作目录和 `.git` 后，源路径移走，五种原内容都能分别恢复。这个例子把「提交版本」「暂存状态」「工作区原字节」分成了实际不同的材料。

## 取得范围与证据

所有样本保留在 `.artifacts/git-working-20261106T173900Z/`。实际 Git 为 `2.54.0 (Apple Git-157)`。本轮原生命令由 Python 调用 Git；每条保存实际 argv、cwd、明确环境覆盖、开始与结束 UTC、退出码、stdout/stderr 原字节，有 stdin 时也原样保存。复制的来源文件另记原位置、读取窗口以及前后和副本的文件清单、长度、SHA-256、模式、链接目标。分析另存 JSON，没有代替原件。

| 证据 | 取得窗口（UTC） | 范围 |
| --- | --- | --- |
| `tracer-report.json`、`commands/0001–0018.*` | 11-06 17:39:11–17:39:12 | 首个工作区/index/HEAD 独立恢复例子 |
| `boundaries-report.json`、`commands/0019–0104.*` | 11-06 17:44:38–17:44:40 | split index、冲突、旧 stash、reflog、不可达对象、多 worktree |
| `windows-report.json`、`commands/0105–0117.*` | 11-06 17:48:14–17:48:15 | 已留样后的覆盖、删除、立即过期与回收 |
| `patch-check/report.json`、其中 5 条命令 | 11-06 23:50:46 | 从既有原 patch 实际恢复，再核对缺失材料 |
| `real-readonly-inventory/` | 11-06 17:49:02 | 仅 Heartbeat 与 qstack 的对象名称/大小、引用范围、索引和日志体量；每仓 14 条只读命令 |
| `real-working-cost/` | 11-06 23:49:36–23:49:50 | 两仓完整文件树 lstat 与 Git 忽略判定；每仓 2 条只读命令 |

提交、merge、stash、删分支、worktree repair/prune、reflog expire、GC、覆盖和删除全部发生在本轮新建的专用合成仓库或其恢复副本中。真实两个仓库只做只读盘点，没有 reset、clean、GC、改 refs 或改配置，也没有寻找其他私有仓库。正文与私有路径清单只留本地 ignored 证据。

## 原始形态与恢复发现

### 原始工作文件、index 和对象各有职责

首例保存的原文件是：

```text
HEAD:tracked.txt       committed version\n
:tracked.txt          staged version\n
工作文件 tracked.txt  working version\n
untracked.bin         untracked\0binary\n
ignored.txt           ignored original\n
```

其中 `\n`、`\0` 是此处展示用转义；样本中是实际换行和 NUL。暂存对象 ID 是 `3ed3870aac84a296bb0711eaf37096151f0c0378`，432 B 的 `committed.bundle` 不含它。`restored-bundle-index/` 已得到原工作文件与 index，但 `cat-file -e` 返回 1、`show :tracked.txt` 返回 128。说明 index 是路径/状态到对象的关系，复制关系不等于复制所指内容。

`raw-snapshot/` 共 16 个普通文件、1,232 B，含工作区与 `.git`。源目录改名后，`restored-full/` 的 HEAD 正文、暂存正文、工作区正文、untracked、ignored 全部字节相等；原 index 字节与整个文件清单的内容哈希/模式/链接也相等，状态原输出相等。恢复库没有 alternates。这里证明的是专用静止样本的文件与 Git 恢复，尚未把这些材料接入产品的 Capture/独立 raw restore，也没有证明任意并发仓库的一致快照。

### split index、冲突和文件形态

| 实测形态 | 原样材料与结果 |
| --- | --- |
| split index | 一个主 `index` 加一个 `sharedindex.*`；完整复制后 `ls-files --stage --debug -z` 原输出相等。只从派生副本删除 sharedindex，读取返回 128 |
| CRLF 工作文件 | `.gitattributes` 的 `text eol=lf` 让暂存 blob 为 LF，工作文件仍是 CRLF；分别核对两层字节 |
| intent-to-add | 原 index 保留该状态；完整复制后的状态与 index 调试输出一致 |
| 三方冲突 | index 中 stages 1/2/3 分别恢复 `base\n`、`main\n`、`side\n`；工作区冲突标记和原 `MERGE_HEAD` 各自保持 |
| 文件形态 | 可执行位、符号链接目标、文件名中的换行、正文中的 NUL 均在原副本与恢复副本核对 |

`index` 与 `sharedindex.*` 的依赖是 Git 自身的存储结构，见 [repository layout](https://git-scm.com/docs/gitrepository-layout)。本轮保存并核对了上述模式与链接；没有把 mtime、inode、所有权、ACL、扩展属性列为完整恢复保证。

### 旧 stash、reflog 和无引用对象

合成仓库连续保存两个 stash，另删除一个分支，并写入一个从未被任何 ref 引用的 blob。当前 bundle 可以恢复最新 stash 的可达内容；旧 stash、已删分支独有提交、无引用 blob 在仅从该 bundle 恢复的库中都不存在。

本轮将 `cat-file --batch-all-objects --batch-check=%(objectname)` 的 stdout 原字节直接交给 `pack-objects --stdout`，得到 17 对象、1,167 B 的 Git 原生 pack，命令输出和输入均保留。同时复制原 reflog 与其余仓库文件。随后仅在专用源仓库执行立即过期与 `gc --prune=now`，三个对象都消失，最新 stash 仍在。恢复时在派生副本中用该 pack 重建对象目录：旧 stash 工作正文、其 untracked 父提交正文、被删分支正文、无引用 blob 都恢复，`fsck --full --strict` 成功，原 reflog 文件仍逐字节相等。

因此「对象仍可取得」和「原 reflog 记下了什么」是两个材料范围。原生 pack 保存 Git 对象内容，原 `.git/objects` 文件另随原副本保留；pack 的重新编码不能冒充原 loose/pack 文件字节。[cat-file 文档](https://git-scm.com/docs/git-cat-file)说明该枚举也包括 alternates 中的对象；真实引入这种取得方式时需把实际外部依赖范围说清。本轮端到端例子没有 alternates。

### 多 worktree

主 worktree 与一个 detached linked worktree 各有不同的暂存、工作区内容。它们共享 common-dir，但各自的 HEAD、index、HEAD reflog 和实际工作文件不同。保存 common-dir 只能拿到管理材料，不能代替 linked worktree 所在目录的工作文件。

本机 Git 的 `bundle --all HEAD` 还把 detached worktree 的 HEAD 作为归档头保存。实际 `commands/0094.stdout` 包含：

```text
638e1e401ba5f281c905b8e24e2e8e87749e5d03 worktrees/worktree-linked/HEAD
```

从 bundle mirror clone 后该提交对象存在；这项实测不能推导出 index、工作文件或旧 reflog 也在 bundle 中。

完整保存两份工作目录及 common-dir 后，把源路径都移走。复制品内原有路径指针使 linked worktree 读取失败；只对恢复副本执行 `worktree repair` 后，两份 index、工作文件、状态及 detached HEAD 都恢复。原样副本保持不变。Git 官方说明同时移动 main 与 linked worktree 时需在新位置 repair，见 [worktree repair](https://git-scm.com/docs/git-worktree)。

## 丢失窗口与可追溯性

| 材料 | 实际丢失机制与本轮证据 |
| --- | --- |
| 未暂存、未提交的工作文件 | 覆盖或删除即可失去。专用例子的旧工作正文在覆盖前后都没有对应 Git blob；此前文件副本仍保留它和已删除的 untracked 文件 |
| index 当前状态 | 下一次 add/reset/merge 等会改变状态。它引用的对象可能继续存在，但对象本身不表达完整旧 index；本轮已取得原 index 与依赖对象，未测任意操作的自动保留时间 |
| reflog 与旧 stash | 原 reflog 条目能过期或被删除，所依赖对象随后可能回收；显式立即过期加 GC 的受控例子已观察到三种对象消失 |
| linked worktree 管理状态 | 指定工作路径缺失时，专用例子的 `worktree prune --expire=now` 删除了管理 HEAD/index/reflog，随后 GC 删除 detached 独有提交和暂存 blob；之前副本仍可恢复 |

官方默认是 reflog 一般 90 天、当前 tip 不可达条目 30 天；GC 对象宽限期一般两周，失联 worktree 管理目录一般三个月。它们受实际配置和触发操作影响，不能当作用户材料最短保留保证。本轮用立即过期参数验证丢失机制，没有等待这些默认时长，也未观测真实两仓日常回收频率。[reflog](https://git-scm.com/docs/git-reflog)、[gc](https://git-scm.com/docs/git-gc)。

能追溯的下界是取得时仍在磁盘/对象库/日志中的内容，加此前已保留的副本。已经覆盖的从未入库正文、已经回收的对象与已经删除的日志无法由当前仓库补回。本轮没有测量每日工作文件改写次数或每天新增体量；当前存量不能冒充日增量。

## 真实两个仓库的体量

下面是 2026-11-06 23:49 UTC 的一次完整 lstat 观察。原路径与每文件 stat 保存在 `real-working-cost/`；仅统计名称、类型、逻辑长度、模式与 stat 元数据，没有读取工作文件正文。tracked 用该轮开头的 `ls-files`；其余文件由正常用户/系统生效配置下的 `check-ignore --no-index --stdin -z -v --non-matching` 判定，根 `.git` 独立列出。忽略规则原输出也保留，格式依据见 [check-ignore](https://git-scm.com/docs/git-check-ignore)。

| 范围 | Heartbeat 普通文件数 | Heartbeat 逻辑字节 | qstack 普通文件数 | qstack 逻辑字节 |
| --- | ---: | ---: | ---: | ---: |
| tracked | 234 | 6,266,811 | 14 | 108,653 |
| untracked、未被忽略 | 2 | 24,709 | 0 | 0 |
| ignored | 228,698 | 22,436,977,118 | 1,139 | 5,267,683 |
| 根 `.git` | 355 | 111,530,949 | 69 | 99,183 |
| 整个目录合计 | 229,289 | 22,554,799,587 | 1,222 | 5,475,519 |
| 其中 `.artifacts` | 178,005 | 18,541,835,684 | 1,137 | 5,191,895 |
| 其中 `.scratch` | 39 | 2,567,970 | 0 | 0 |
| 其中 `probes` | 2 | 24,709 | 0 | 0 |

后三行是前面总量的子集。Heartbeat 另有 6,727 个符号链接（其中 `.artifacts` 5,464），qstack 没有；没有跟随链接读取目标。此次两个非 ignored untracked 文件就是本轮临时勘探程序，收口后归档删除，不能把它们当成稳定每日基数。

Heartbeat 总量约 22.55 GB，其中 ignored 约 22.44 GB；qstack 总量约 5.48 MB。Heartbeat 扫描 28,570 目录耗时 13.96 秒，qstack 191 目录耗时 0.17 秒；两仓 lstat 错误均为 0，初始 tracked 名单没有缺失普通叶节点。逻辑长度按每条路径相加，重复文件及硬链接多路径分别计数，包含样本、原始输出、派生恢复副本、发布与其他生成物；它不是唯一内容量、实际占盘量、压缩量或传输成本。扫描期间其他开发工作可以变化，本轮记录读取窗口，不宣称目录原子快照。统计输出先写仓库外临时目录，结束后搬入本轮证据，表中不含这次统计自身的新输出。

`.artifacts` 已知同时承载本轮及此前来源勘探原件、原始命令输出、导出材料、恢复副本与生成物。没有逐项证明它们可由别处再生，所以 ignored 规则不能作为可丢弃判定；当前 18.54 GB 也不能全部说成独有材料。`.scratch` 是当前本地方案与勘探记录。现有 Git 提交归档不覆盖这些未提交/ignored 文件。来源样本和最终恢复副本之间确有重合，本轮照实保留，没有用去重分析替换原件。

较早的对象盘点发生在 17:49 UTC，不能与六小时后的文件统计拼成同一快照：Heartbeat 枚举 30,995 个本地及 alternate 对象，当前 refs/HEAD 可达 24,140，含 reflog 可达 24,448；相应差集是 6,855 与 6,547。qstack 三者均为 38。差集仅说明那次分次命令观测的范围差异，没有证明每个对象独有、重要、损坏或已不可恢复。两仓原 index 前后哈希与 stat 一致，未观察到 split index；Heartbeat 的日志和 worktree 管理文件合计 109 个/1,086,176 B，qstack 为 4 个/1,489 B。该次命令禁用了用户/系统配置，完整环境与范围随原输出保存；最后成本盘点则使用正常生效忽略配置。

## 三条候选路线

以下是供采用决策比较的候选，没有隐式扩大日常任务范围。工作文件原件与 Git 管理状态/依赖对象可分别取得；两者的范围需要各自明确。

| 候选 | 能保存与恢复什么 | 具体缺口与代价 |
| --- | --- | --- |
| 完整工作目录，加实际 git-dir/common-dir 和所选 linked worktree | 对已选路径保留 tracked、untracked、ignored 原字节，并保留 index、日志、状态文件及物理对象文件；专用静止仓库已实际恢复 | Heartbeat 当前整目录约 22.55 GB、229,289 普通文件及 6,727 链接，qstack 约 5.48 MB。不能仅复制顶层目录就假定包含外部 common-dir、linked 工作文件、alternates 或链接目标；并发读取仍有时间窗。频率、首次内容读取/压缩与增量成本未测 |
| Git 可见工作文件，加单独明确的 ignored 材料；Git 管理状态和对象另采 | 可见部分含 tracked 与未忽略 untracked；Heartbeat 此轮约 6.29 MB，qstack 0.109 MB。index/共享 index、reflog/状态文件和其对象可独立保留；本轮原生全对象 pack 加原管理材料已恢复旧历史 | 所选 ignored 部分需要明确位置和范围事实，不能由 ignore 推出价值。没有纳入的 ignored 原件就是具体缺口；例如 `.artifacts` 的原样样本与 `.scratch` 方案。只加 index 或只按当前 refs 取对象，都已被反例证明不足。分次选择/读取与工作区本身仍可变化 |
| 只保存 staged/unstaged patch，依附当前 bundle | 实测 218 B staged patch + 216 B unstaged patch 在相同基线顺序 apply 后，恢复简单 tracked 文本的 HEAD/index/working 三种正文 | 同一次实际恢复没有 untracked、ignored，原 index 字节不相等；原 reflog、旧 stash、不可达对象也不由这两份 diff 表达。归一化后的 diff 不能直接代替磁盘原字节；本轮 CRLF 工作/暂存已展示其区别。复杂冲突、split index 与完整元数据的 patch 恢复未验证 |

依据现有「原样留下」目标，patch 适合补充阅读，不能单独承担这一来源的原件范围。完整目录路线对 qstack 的当前成本很小，对 Heartbeat 则有明确的 22.55 GB 基数；选择分开取得的路线时，应把 ignored 中实际要保留的材料明确列出来，并将尚未纳入部分保持为范围缺口。这里是 Agent 从测量作出的建议，采用及采集频率仍由后续决定表达。

## 最小下一步与未验证部分

采用之前先选一个明确仓库与范围，把本轮已经验证的「原工作字节 + index/日志/状态 + 对象依赖」接成单轮原件，再从产品 raw restore 的输出恢复。验收至少覆盖首例五种正文、旧 stash/reflog、split index、三方冲突和两 worktree；来源原位置不可读时仍成功，缺共享 index/缺暂存 blob 的负向例子必须失败。对 Heartbeat 全目录或明确的 ignored 子集另测一次实际读取、原件编码与恢复成本，再定周期；lstat 的 14 秒不是取得 22.55 GB 正文的耗时。

本轮没有覆盖 reftable、sparse index、SHA-256 对象库、真实 alternates、自定义 Git 过滤器、LFS 外部实体、子模块完整工作状态、并发 index 替换/GC、空目录和完整系统文件元数据，也没有限制文件系统权限来证明强隔离。实际恢复采用源路径改名、没有 alternates、只输入已存材料；这与系统拒读来源的受限恢复证明是不同强度。新增正文和历史的日频率、跨日保留表现留待日常实际测量。

临时程序按本轮完成规则归档到证据的 `probe-source/` 后移除；原样样本、取得事实与恢复证据全部保留。本轮只增加这份 scratch 勘探记录与本地证据，没有改生产代码、公开模型、决策图或持续任务。
