# 建议路线与阶段

[返回全景](README.md)

以下组合用于比较替代方案。每条路线只列本阶段关键取舍，全部候选在对应节点；试选独立于用户确认和 Agent 采用记录。

## 当前交付：留下材料并能取回

Claude Code与Codex已选材料可独立恢复；Codex扩到九库、草稿和语音。新Linux固定副本含267根及全部引用内容，新增浏览器与两个Git仓库也已独立恢复。

每个来源范围显式声明；Codex新增范围已在受控实验和日常采集中分别通过验收。267根固定后新增材料单独备份；外部副本仍按需手动取得。

- [交付路线怎样排序](modules/delivery.md#n-e93627fc1fab2325)：按可独立使用的能力推进
- [来源接入顺序](modules/sources.md#n-821855273f39bc59)：丢失窗口与接入成本共同排序
- [Codex日志表示](modules/sources-local.md#n-654ef3908ee1adec)：枚举两处日志并保留原字节
- [Codex状态数据库范围](modules/sources-local.md#n-bb8086babda89015)：状态库一致快照
- [Codex附件与产物范围](modules/sources-local.md#n-c8cf6296fafacb06)：整体保存已盘点的文件材料
- [数据库取得一致性](modules/capture.md#n-cba810bb05a18834)：引擎提供一致快照
- [采集记录与文件清单](modules/records.md#n-1d8f2faf261d23a6)：小采集记录加可复用清单
- [内容取得与来源身份](modules/records.md#n-15c7ee1513e29577)：分开内容标识、取得标识与来源标识
- [原件存成块](modules/storage.md#n-1e55971a7ab3ff58)：内容定义块复用
- [多处都有一份](modules/availability.md#n-ae9194edde188509)：本地保存加外部有版本备份
- [整理何时计算](modules/understanding.md#n-922a57e91c6742aa)：先只留下材料
- [运行保障怎样安排](modules/delivery.md#n-19250ebbdde649cf)：先保留单次结果，需要时外部探活
- [第一阶段怎样验收](modules/delivery.md#n-ae87acd40d922614)：用可重复的小来源验证留下与取回
- [同步交换与提交](modules/availability.md#n-da173cb6e04759f8)：外部完整保存集复制
- [导出与恢复](modules/storage.md#n-da66683208852205)：导出完整记录与内容闭包
- [Codex其他数据库范围](modules/sources-local.md#n-212a8d9d321fc8c3)：直接保留可取得的其他整库

## 当前交付：已有来源持续采集

独立发布程序由五项用户级任务运行，前台持续观察，Codex/Claude Code及Orisumi/qstack两仓库每轮后等待300秒；浏览器另由Native host取得，所有来源共用原件目录。

当前 macOS 用户会话；手动切换、受限 IP 网络与重启恢复、任务装载和停止各有实际证据，真实重新登录、全天与系统状态组合另测。

- [来源接入顺序](modules/sources.md#n-821855273f39bc59)：丢失窗口与接入成本共同排序
- [前台应用与键鼠观测](modules/sources-live.md#n-356bb4ec11f181f1)：第一条导洞只观察前台应用
- [瞬时事件接收方式](modules/capture.md#n-359653aed5ab9c59)：设备上尽快持久记录
- [事件时间与顺序](modules/capture.md#n-0843f43438c1c4a1)：原时间和取得时间分别保留
- [采集启动与执行位置](modules/capture.md#n-f47170f9f44e3fc5)：系统登录启动或现有外部调度
- [第一阶段怎样验收](modules/delivery.md#n-ae87acd40d922614)：用可重复的小来源验证留下与取回

## 当前交付：浏览器观测与断连续传

标签/窗口原回调与独立查询先在扩展暂存，经 Native Messaging 交原件；匹配根回执后清理。受控 Profile 故障恢复及日常 Chrome 实际启用已验。

Chrome 130+ 的 MV3 扩展与本机 host；日常 Profile 已启用，长期体量与系统状态组合分别验证。

- [浏览器观测层次](modules/sources-live.md#n-bba0514698b64e45)：标签与窗口焦点事件
- [浏览器焦点与标签观测](modules/sources-live.md#n-6a613b885484430f)：分别保留标签、窗口与系统前台
- [浏览器事件传输](modules/capture.md#n-b75cd5e6c6494099)：Native Messaging 交本机程序
- [瞬时事件接收方式](modules/capture.md#n-359653aed5ab9c59)：设备上尽快持久记录
- [第一阶段怎样验收](modules/delivery.md#n-ae87acd40d922614)：用可重复的小来源验证留下与取回

## 当前交付：Git历史与独立恢复

显式仓库的HEAD、refs及可达对象以bundle保存；命令原输出与取得时间分开记录，实际mirror clone与fsck通过后判为完整。

普通完整仓库已有合成A/B及两个真实仓库证据；最新stash可达对象在范围内，旧reflog、index、工作区与外部实体另列。当前两个已验证仓库已启用持续任务，其他仓库先明确选择。

- [Git保存内容层次](modules/sources-local.md#n-4b833bed1e02c780)：提交引用及可达对象归档
- [Git当前工作与历史边界](modules/sources-local.md#n-0654de3735dd2271)：先保存HEAD、refs与可达对象
- [内容取得与来源身份](modules/records.md#n-15c7ee1513e29577)：分开内容标识、取得标识与来源标识
- [第一阶段怎样验收](modules/delivery.md#n-ae87acd40d922614)：用可重复的小来源验证留下与取回

## 当前交付：旧Heartbeat本人历史

同一只读事务取得本人完整现存事实与关联，保留COPY文本、模式和命令出处，经现有文件采集保存后在受限环境独立PG恢复。

已验证10表3,748,506行及74约束；文件采集与原数据库取得时间分别记录。当前一次取得，其他表、旧修订与持续更新按实际需要另验。

- [旧Heartbeat历史材料](modules/sources-local.md#n-32da8b7037cad0ce)：本人数据及关联表的一致导出
- [内容取得与来源身份](modules/records.md#n-15c7ee1513e29577)：分开内容标识、取得标识与来源标识
- [第一阶段怎样验收](modules/delivery.md#n-ae87acd40d922614)：用可重复的小来源验证留下与取回
- [导出与恢复](modules/storage.md#n-da66683208852205)：导出完整记录与内容闭包

## 技术替代：整文件保存

相同的原样保存目标，用整文件哈希对象和出处记录实现。静态资料、小文件或低频变化时更值得比较。

在同一真实历史样本上比较恢复、总存储和运行代价后选用；大日志多版本可能比已有分块明显更贵。

- [原件存成块](modules/storage.md#n-1e55971a7ab3ff58)：完整内容按哈希复用
- [采集记录与文件清单](modules/records.md#n-1d8f2faf261d23a6)：小采集记录加可复用清单
- [压缩与存放表示](modules/storage.md#n-d00cf82ac296b520)：逐内容对象使用标准无损编码
- [导出与恢复](modules/storage.md#n-da66683208852205)：导出完整记录与内容闭包
- [多处都有一份](modules/availability.md#n-ae9194edde188509)：本地保存加外部有版本备份
- [第一阶段怎样验收](modules/delivery.md#n-ae87acd40d922614)：用可重复的小来源验证留下与取回

## 后续阶段：先让 Agent 找到原文

收集能独立使用后，逐项增加确定性读出、检索和引用。遇到实际答不好的问题，再决定模型理解形态。

依赖相关原件及定位能力已经可用。重跑已有基准，不能沿用旧导洞的结果当新实现验收。

- [整理何时计算](modules/understanding.md#n-922a57e91c6742aa)：需要时解析并缓存
- [程序和模型怎样分工](modules/understanding.md#n-9b5d1c252325594d)：先只用确定性读法
- [基本读出怎样重写](modules/understanding.md#n-d34b5c062cf781c5)：逐个重写最小读出
- [投影库怎样选择](modules/understanding.md#n-929aa0a735a703a6)：第一次读出先用单一本地投影
- [索引怎样增长](modules/understanding.md#n-f3ee89d07892466b)：先结构和全文，再按缺题加索引
- [Agent入口怎样提供](modules/access.md#n-da5b4169050995d4)：先命令行，按需加MCP
- [查询输出怎样组织](modules/access.md#n-83096b85d1cbe387)：结构化结果加简洁文本呈现
- [后续读取怎样验收](modules/delivery.md#n-9e39e29694bbe145)：用新实现重跑既有题并抽查证据、先验证确定性行为

## 需要多设备时：中心汇聚

已有外部备份不能满足实际跨设备读取或交换时，再建设一个明确保存点与采集端之间的交换。

仅在真实多设备需求出现时启用；传感器缓冲能力、保存地信任和分享权限分别决定。

- [多处都有一份](modules/availability.md#n-ae9194edde188509)：Orisumi内建副本交换
- [网状拓扑和完整节点](modules/availability.md#n-561838d4e083830d)：有明确中心的汇聚
- [设备角色与离线工作](modules/availability.md#n-e56d1b0735fb0084)：长期完整保存点加本地采集/缓存
- [采集设备怎样缓冲](modules/availability.md#n-358b28d323bdbb8b)：设备上先保存原件，再交长期保存点
- [同步交换与提交](modules/availability.md#n-da173cb6e04759f8)：清单对比加缺失对象复制
- [连接信任与访问边界](modules/availability.md#n-ca03d6309f6b9fd6)：自己的可信设备先经私有网络连接
- [多副本删除与离线节点](modules/availability.md#n-19ed3ea2239e62f9)：保存删除事实并阻止旧记录复活
- [长期恢复怎样验证](modules/delivery.md#n-254f30d6e194be24)：定期在干净目录读回一份保存集、提供通用导出作为恢复入口

## 需要理解与分享时：可追溯产物

保留模型调用与理解版本；先用一个真实问题验收，再以可审阅的静态范围分享。

属于后续条件路线。静态导出交给别人后不能保证收回；持续撤权需要另选受控查询方案。

- [程序和模型怎样分工](modules/understanding.md#n-9b5d1c252325594d)：结构由程序读，解释按需用模型
- [模型调用留下什么](modules/understanding.md#n-97745ff5030f65f3)：保留实际可取得的调用包
- [理解版本怎样使用](modules/understanding.md#n-be7944dd96e1f491)：版本并存，显式采用一版
- [新旧理解怎样评测](modules/delivery.md#n-cbfb97254da427c8)：展示实质差异和对应证据
- [共享怎样授权](modules/access.md#n-4b3c6032607d1b5c)：先按明确范围生成共享副本
- [多人怎样使用](modules/access.md#n-533f290602f41d06)：每个人独立资料库
- [派生结果遇到删除或撤权](modules/access.md#n-183194894919be77)：按引用依赖处理可见性与重建
