# 勘探：Git 引用与可达历史

2026-11-06。本轮验证决策全景的「Git保存内容层次 / objects」与「Git当前工作与历史边界 / committed」。这里记录来源发现；采用后的格式与行为见[Git模型](../../src/Docs/Orisumi.Docs/content/docs/model/git.mdx)。

## 样本与取得方式

最终样本目录为 `.artifacts/git-survey-Lj2Hvk/`。临时勘探程序经 `Orisumi.slnx` 登记和 `orisumi verify` 运行，完成后已清理；完整源码、package 与 lockfile 保留在最终样本的 `probe-source/`。Git 为 `2.54.0 (Apple Git-157)`。记录共 369 条实际 Git 命令，每条分别保存 argv、cwd、明确的环境覆盖、开始/结束 UTC 时间、退出码、stdout/stderr 原字节；bundle 原文件保持原字节。HEAD 和仓库配置另存原字节及读取位置、时间。

真实 Heartbeat 只读取得 refs、HEAD、git-dir/common-dir、对象格式、是否 shallow 和 local config，没有修改其工作区、引用或配置；没有读取 me 正文。提交、分支、重置、删除引用和配置写入全部发生在本轮新建的 `.artifacts/git-survey-*/` 合成仓库中。

测试仓库包含重命名、二进制、中文路径、多个分支、annotated tag、remote-tracking refs、符号引用、detached HEAD、两次 stash、暂存与未提交文件、LFS pointer 和子模块 gitlink。采 A 后改写引用并采 B，再将源仓库和子仓库改名为 `isolated-source/`、`isolated-submodule-source/`，确认原路径均不存在。恢复命令仅传入已保存 bundle；恢复仓库没有 `objects/info/alternates`。

| 归档 | bundle 字节 | 逐对象比较 | 逐提交 checkout 并比较所有 blob 字节 | 结果 |
| --- | ---: | ---: | ---: | --- |
| A | 2576 | 26 个 commit/tree/blob/tag | 7 个提交版本 | 空库 verify、mirror clone、fsck 与字节比较均通过 |
| B | 1255 | 12 个 commit/tree/blob | 2 个提交版本 | 同上；A 中被改写后不可达的历史仍可由 A 恢复 |

上述 7 个提交包含 stash 保存工作树、索引和 untracked 时生成的提交。恢复时逐对象比较全部可达对象的原字节，再逐版本实际 checkout，覆盖重命名前后、二进制和非 ASCII 路径。测试中的 LFS 文件按 pointer blob 核对；gitlink 不作为主仓库 blob 核对。

验证证据：`.artifacts/runs/20261106T141555Z-verify-changed-e3f3c29f00af42e398d06bc5bc73b3ea/`，10 项全部通过，包含本导洞 1 项以及现有 117 项测试。前次成功样本 `git-survey-53VwMu/`、`git-survey-3czT4G/` 和首次失败样本 `git-survey-KhesJU/` 均保留。

## 原始数据形态与引用语义

A 的 `bundle list-heads` 输出中有下面两行；完整原输出在 `commands/0045.stdout`：

```text
eaea62b9eb1761a9a280580d8908f96a7ca350a1 refs/stash
d168271d53b5638ab5c3a02ba5a9b28fd2537a97 HEAD
```

`--all` 与 `--all HEAD` 的引用输出完全相同，且 detached HEAD 独有提交可以恢复。`HEAD` 单独归档只保留 HEAD 这条入口及可达对象，feature 分支和 stash 独有对象在恢复仓库中不存在。实测符合 [git-rev-list 的 --all 定义](https://git-scm.com/docs/git-rev-list)：refs/ 加 HEAD；文档还说明多 worktree 默认会参与枚举，本轮仅验证单 worktree。

`--all` 是引用可达范围。最新 `refs/stash` 的对象随其保存，前一个 stash 只在 stash reflog 中，恢复仓库找不到那个 commit。暂存而未提交的 blob、工作区修改和 untracked 文件也没有进入 A。因而它保存了一部分 stash 对象，不能代表完整 stash 栈、原索引或工作区。原始 index、stash reflog、未提交样本另存，来源仓库也保留在隔离目录；它们是本轮边界证据，不算 bundle 已覆盖的材料。

## HEAD、符号引用、配置分别保存

B 的源 HEAD 原字节是 `ref: refs/heads/a-current\n`；`main` 与 `a-current` 指向同一提交，mirror clone 后得到 `ref: refs/heads/main\n`。两份原文分别在 `B/before.HEAD`、`B/clone.HEAD`。根据原 HEAD 另行恢复后逐字节相等。

A 的 `refs/remotes/origin/HEAD` 原先符号指向 `refs/remotes/origin/main`，mirror clone 后变成同值的直接引用。`commands/0040.stdout`、`0236.stdout`、`0238.stdout` 分别是来源、clone 后、依据保存的符号引用观测恢复后的 `for-each-ref` 原输出。最后一个与来源逐字节相等。

因此 bundle 中的引用名和对象值足以保留版本入口，符号关系另有证据。普通 clone 会映射分支，试验采用 mirror clone 核对全部 refs；[git-clone 文档](https://git-scm.com/docs/git-clone)明确 mirror 映射包括 remote-tracking 等全部 refs。

原仓库配置另存 `before.config` / `after.config` 原字节。`config --local --no-includes --null --show-origin --show-scope --list` 原输出补充位置和作用域；这个命令范围仅为 local config，未展开 include，也未收集用户和系统配置。clone 生成的 origin 指向 bundle，因此不能用恢复仓库自己的 config 代替来源配置。此轮没有将保存的来源配置直接安装到恢复仓库。

## 取得时间与引用变化

`ref-race/` 在前次 refs 查询后，把 main 从第二版移到第一版，再创建 bundle。`commands/0157.stdout` 记旧值，`0167.stdout` 的 bundle 引用值和 `0168.stdout` 的后读值记新值。这是确定可重复的跨命令变动，说明读取前的 refs 清单不能充当 bundle 的内容清单。

bundle header（由 `bundle list-heads` 读出）是这份归档携带的引用事实；HEAD、symbolic-ref、config、before/after refs 各自有取得时间。前后相等也只说明两次观测相等，不能排除期间改写后又改回。此轮没有注入 Git 内部枚举过程中的多引用事务竞争，也未证明多命令的原子快照。保存窗口和各条观测即可忠实表达已经拿到的材料。

## 自包含条件与失败

已核对当日 git-scm 最新文档（站点列到 Git 2.56.0），实际运行版本见上文。[git-bundle](https://git-scm.com/docs/git-bundle)要求归档有命名引用，完整可达历史可以独立恢复，revision 排除会产生先决对象；[bundle 格式](https://git-scm.com/docs/gitformat-bundle)还区分 prerequisite、对象格式和 filter capability。

实际失败保留在 `commands/`：

| 试验 | 结果 | 含义 |
| --- | --- | --- |
| 空仓库 `bundle create --all` | 128，Refusing to create empty bundle | 空仓库需保存空状态及取得结果；本轮没有伪造 bundle |
| 只传 commit 哈希创建 | 128，同样拒绝空 bundle | 不能用“先枚举哈希，再把哈希直接传 bundle”替代命名引用 |
| `first..HEAD` 增量 bundle 在来源 verify | 0 | 来源已有先决对象，成功只代表对来源可应用 |
| 同一增量 bundle 在新空库 verify | 1，缺 prerequisite commit | 来源 verify 成功不足以证明自包含 |
| 归档端禁用所有协议后直接 clone 本地 bundle | 128，file transport not allowed | 恢复端对明确的本轮 bundle 单独开放 file；网络协议仍关闭 |

A/B 使用不带 revision 排除、时间截断和对象 filter 的 `--all`，随后在新空库 verify、mirror clone、fsck 并逐对象读取。它们证明此夹具的自包含；尚未测 shallow、partial/promisor、alternates、replace、SHA-256、reftable 与 linked worktree。生产范围遇到这些形态时需检查实际对象闭包，不能沿用本夹具的通过结论。

## LFS 与子模块的外部边界

LFS 样本含 pointer blob，实体另放在源仓库 `.git/lfs/objects/`。恢复得到 pointer 原字节，恢复库没有该 LFS 实体目录。样本没有安装或调用 Git LFS；它验证 pointer 与外部实体分离，未验证 LFS 下载、smudge、clean 或真实服务。规范见 [Git LFS spec](https://github.com/git-lfs/git-lfs/blob/main/docs/spec.md)。

子模块样本由独立的子仓库 commit、主仓库 `160000` gitlink 和 `.gitmodules` 组成。主 bundle 恢复 gitlink 与 `.gitmodules`，`cat-file -e <子仓库 commit>` 失败；bundle verify 和 fsck 仍成功。子仓库原目录也已隔离，因此主仓库成功不能算子模块正文恢复。这个结构与 [gitsubmodules](https://git-scm.com/docs/gitsubmodules) 描述的独立历史及 gitlink 一致。

## 频率、追溯、代价与重合

- 本轮合成归档几 KB，不能估计真实仓库大小、增速或全量定期采集代价；真实 Heartbeat 仅取得元信息。32 个仓库及旧提交数量是前次速记，见 `survey-local.md`，本轮没有重统计。
- 追溯范围由本轮可达历史与现存对象决定。改引用后，A 保住先前取得的历史；B 不能补出已不在其可达范围的 A。对象曾经存在于 reflog 不代表今后一直可取。
- 本机 Git 和仓库读权限足以完成此导洞，无需登录、常驻或远端访问。独立恢复另外需要可写的新目录；本轮保护参数没有改变用户配置。
- Git 文件正文与本地文件、助手对话里的代码或操作记录有重合。提交带作者/提交者及时间，但不能据此把一次提交视为一整段人的工作；语义解释留给读取。

## 由发现采用的路线

生产路线现已采用每个显式仓库位置生成一份自包含 bundle，并保存 Git 版本、实际参数、取得窗口、原输出，以及分次取得的 HEAD/符号引用/local 配置事实。现有采集记录根的内容哈希作采集身份；Git 对象 ID 是来源身份，bundle 头表达本份归档的引用值。

空仓库保存拒绝结果并标明未取得 bundle；完整轮经过空库 verify、实际 mirror clone 和 fsck。默认每轮结束后等待300秒，单线程打包解决本次真实重复采集的字节不稳定。生产格式、取消边界与真实仓库验收见[Git模型](../../src/Docs/Orisumi.Docs/content/docs/model/git.mdx)和[验收](../../src/Docs/Orisumi.Docs/content/docs/model/validation.mdx#git-历史)。

仓库自动发现、完整生效配置、未提交状态和其他特殊仓库形态分别保留后续勘探范围。此页中的原始样本、失败输出与源仓库隔离副本继续保留。
