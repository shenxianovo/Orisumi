# Orisumi 决策全景

当前起点是先把数据收集下来，原样内容和直接知道的出处一起保存，未来用途保持开放。用户已授权 Agent 自行审阅、选择并推进实现。全景保留全部候选及验证终点，119 个决策分别记录当前采用、条件性安排和后置原因。

- [交互全景](panorama/site/index.html)：13 模块、155 节点、324 候选；查看依据、组合路线、潜在影响和试选导出。
- [当前采用路线](panorama/site/current-route.md)：Agent 采用的方案、接受的代价、阶段和重审条件。
- [Markdown 入口](panorama/site/README.md)：模块子图和逐项正文。
- [建议与替代路线](panorama/site/routes.md)：当前保存、瞬时观测、浏览器、Git、整文件替代、后续读取、多设备和理解分享。
- [确认的范围与原话](confirmations.md)：10 项用户确认的依据；授权内的技术取舍另记为 Agent 采用。
- [已有材料覆盖清单](panorama/site/coverage.md)：39 条当前需求、6 份 ADR、来源清单和原有待定事项的去处。
- [卡点、修正和验证](panorama/work-log.md)。

## 先看一个例子

Codex 一份对话可能同时涉及日志、状态库和图片。选择原样保存后，仍要分别决定日志的压缩表示、状态库范围和附件范围；数据库要验证一致快照，保存完要在测试来源不可用时取回字节、版本和出处。两表导出、整库快照、只存路径和收齐附件会得到不同结果，图中各有分支和检验方法。

## 采用与继续推进

从“当前采用路线”查看当前阶段与下一条来源，再沿保存身份、分块和副本方式查看依据。界面分别显示“✓ 本轮确认”、“Agent采用”、“条件成立再做”和“暂后置”；试选只保存比较方案，保持用户确认和采用记录不变。

图覆盖现有材料和有实质差别的路线。尚未验证的来源能力有具体勘探终点，完整遍历不等于全部未来能力已实现。潜在影响包含全部候选路线，需要结合最终采用的条件判断。

当前独立交付覆盖 Codex/Claude Code 文件保存、macOS 前台与浏览器观测、独立恢复及 Linux 手动副本。前台、两种会话及两个Git仓库由仓库外固定程序和五项用户级系统任务持续采集，日常 Chrome 的扩展与 Native host 也已启用；前台受限 IP 网络下的两轮重启与 40 份恢复通过，日常任务的两个文件采集周期与前台观测共 52 份记录也已逐份恢复。登录启动已配置，真实重新登录、全天、系统状态组合与其他平台分别保留验证范围。聚合结果见[正式验收](../../src/Docs/Orisumi.Docs/content/docs/model/validation.mdx)。用户确认表达目标，模型与代码表达授权内采用的方案；各状态按实际覆盖更新。

## 数据与重建

正文来源是 [graph.json](panorama/graph.json)，生成页由本地 qstack 工具重建。用户确认、Agent 采用或实施证据变化时，先更新 JSON 与相应材料，再生成浏览页。程序化格式和方法在 qstack 的 `skills/qstack-explore/references/`。

从仓库根运行：

```bash
python3 ../qstack/skills/qstack-explore/scripts/decision_graph.py validate .scratch/orisumi-roots/panorama/graph.json
python3 ../qstack/skills/qstack-explore/scripts/decision_graph.py build .scratch/orisumi-roots/panorama/graph.json --output .scratch/orisumi-roots/panorama/site
python3 ../qstack/skills/qstack-explore/scripts/decision_graph.py impact .scratch/orisumi-roots/panorama/graph.json --node 原件存成块
```

交互逻辑已通过 DOM 测试，覆盖采用状态/阶段筛选、试选载入导出及确认/采用隔离。工具的 18 项 Python 测试、skill 结构和相对链接检查通过。此前本地文件协议被预览工具拒绝，实际浏览器布局仍未验收；自主展开已有一次有限场景试跑，新增授权后自主采用分支尚未完成独立行为试跑。Markdown 可独立阅读。
