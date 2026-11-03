# 交接：第三版的第一次建模

2026-11-03 写给开始建模的新会话。这份交接和下面列的文件就是全部上下文，用不着去翻之前的会话。

## 为什么有第三版

用户的比喻：要在山里打隧道，让人、货车、火车都能过。

- 第一版每多一种车就多打一条隧道，每条的标准都不一样。对应的做法是每个数据来源各自建模。
- 第二版设计了一条连飞机、轮船都能过的隧道，甲乙两队从两头对着挖，挖到中间发现对不上，歪了。对应的做法是统一模型在没见过真实来源之前就定下了，接一个来源打一个补丁；设计和实现又分给两个模型，靠文档来回转交。

所以第三版先勘探：看清每个来源实际长什么样，再建模。建模的规则在 `AGENTS.md` 的「建模」一节。

## 先读

1. `AGENTS.md`
2. 发心页 `src/Docs/Orisumi.Docs/content/docs/requirements/belief.mdx`：确认的需求和依赖图，含勘探得出的假设。
3. `.scratch/requirements/survey.md`，以及同目录下的 `survey-*.md`：每个来源的勘探记录。
4. `.scratch/modeling/benchmark.md`：测量基准。
5. `.scratch/requirements/belief.md`：否决的方向和还待定的问题。
6. 文档站的模型分区：导洞的模型。`.scratch/modeling/pilot.md`：导洞的进度、下一步和待定的问题；`benchmark-run-1.md` 是第一次答题的结果。

## 开工前

- 用户 11-03 宣布进入建模，`AGENTS.md` 的「当前阶段」已改。
- 测量基准的参考答案用户 11-03 看过，A5 和 B4 补了键鼠事件的依据。

## 导洞的范围

用户 11-03 确认，已整理进文档站的模型分区 `src/Docs/Orisumi.Docs/content/docs/model/`。

## 怎么拿到数据

- 旧 Heartbeat：`ssh heartbeat-maintenance`，然后 `docker exec heartbeat-db-1 psql -U heartbeat -d heartbeat`。每次连接都设 `PGOPTIONS='-c default_transaction_read_only=on'`，只读。只看用户本人的数据（`OwnerId = 019d9026-def4-74db-bf9a-f854c16a993e`），库里还有另一个人的数据。
- 助手会话：本机 `~/.claude/projects/`、`~/Library/Application Support/Claude/claude-code-sessions/`、`~/.codex/`，位置和格式见 `survey-assistant-sessions.md`。10-30 到 11-02 的 Claude Code 会话另存在 `.local/benchmark/claude-code/`，保持原来的目录结构，原处的删掉以后读这里。
- Git：`~/Code` 下的仓库。

## 要注意的

- Claude Code 没有设置会话保留期，按默认会删掉旧会话，10-23 以前的已经没了。用户决定先不改保留期，测量基准用到的 10-30 到 11-02 已另存，见上。
- Chrome 等浏览器的本地数据受 macOS 保护，要在系统设置里给 Claude 开完全磁盘访问权限。导洞的浏览器数据来自旧 Heartbeat，用不到这个权限。
- 本地的 IM 库是加密的，导洞不碰。
