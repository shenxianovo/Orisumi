# 来源事实与假设

现实证据支持哪些判断，证据覆盖到哪里。

[返回全景](../README.md) · [交互浏览](../index.html)

```mermaid
flowchart TD
  n_8cda5b6eb0b65f2e["既有决定·本轮回看｜模型越来越强"]
  n_3de1e9f135b38d16["既有决定·本轮回看｜数据大多会流失"]
  n_199bd2fb464f74ce["已有观察｜有的数据错过就没了"]
  n_5de3cb593b5de6a7["已有观察｜采集会丢数据"]
  n_4c18fe2430707f02["已有观察｜格式随版本变化"]
  n_f8edb5f34401fec3["已有观察｜一个东西有多个名字"]
  n_c7f6551688fc3ac5["已有观察｜一件事散在多处"]
  n_eec8ee5eac1a2112["已有观察｜存下的也会消失"]
  click n_8cda5b6eb0b65f2e href "#n-8cda5b6eb0b65f2e"
  click n_3de1e9f135b38d16 href "#n-3de1e9f135b38d16"
  click n_199bd2fb464f74ce href "#n-199bd2fb464f74ce"
  click n_5de3cb593b5de6a7 href "#n-5de3cb593b5de6a7"
  click n_4c18fe2430707f02 href "#n-4c18fe2430707f02"
  click n_f8edb5f34401fec3 href "#n-f8edb5f34401fec3"
  click n_c7f6551688fc3ac5 href "#n-c7f6551688fc3ac5"
  click n_eec8ee5eac1a2112 href "#n-eec8ee5eac1a2112"
```

<a id="n-8cda5b6eb0b65f2e"></a>

## 模型越来越强

**既有决定·本轮回看** · 未来模型能力和价格趋势是既有假设；原样保存的价值也可来自换读法和重新查询。

依据材料：[src/Docs/Orisumi.Docs/content/docs/requirements/belief.mdx#模型越来越强](../../../../../src/Docs/Orisumi.Docs/content/docs/requirements/belief.mdx#%E6%A8%A1%E5%9E%8B%E8%B6%8A%E6%9D%A5%E8%B6%8A%E5%BC%BA)。来源样本的观察与范围见原记录；不扩成对所有来源的保证。

<details><summary>可能需要回看：41 个下游节点（含所有候选路线）</summary>

[不绑定某种存储](storage.md#n-9e024cb39c60031e)、[位置定位到哪里](records.md#n-d75a21e927a755bb)、[位置用采集记录的名字](records.md#n-17f75f43a19b2bcb)、[保存地是否需要加密封装](availability.md#n-5d0b7394b458d05a)、[共享怎样授权](access.md#n-4b3c6032607d1b5c)、[内容索引与事实边界](storage.md#n-52a4e60764c152e9)、[分块策略与版本](storage.md#n-044919fef0bfc086)、[删除记录与回收](storage.md#n-0350116e746cba2c)、[压缩与存放表示](storage.md#n-d00cf82ac296b520)、[原件分单元和块两层](records.md#n-c2507b9e78d79f34)、[原件存成块](storage.md#n-1e55971a7ab3ff58)、[同步交换与提交](availability.md#n-da173cb6e04759f8)、[后续读取怎样验收](delivery.md#n-9e39e29694bbe145)、[基本读出怎样重写](understanding.md#n-d34b5c062cf781c5)、[多副本删除与离线节点](availability.md#n-19ed3ea2239e62f9)、[多处都有一份](availability.md#n-ae9194edde188509)、[导出与恢复](storage.md#n-da66683208852205)、[投影库只放投影](understanding.md#n-0a185e82d7e59da2)、[投影库怎样选择](understanding.md#n-929aa0a735a703a6)、[数据比代码长寿](storage.md#n-de48cd5fab039fea)、[新旧理解怎样评测](delivery.md#n-cbfb97254da427c8)、[时间含义怎样保留](understanding.md#n-e0bf0a291b892dad)、[来源原生增量怎样保存](storage.md#n-95be81155ce3d6b5)、[查询输出怎样组织](access.md#n-83096b85d1cbe387)、[模型读出存成不可变记录](understanding.md#n-ceb36910cb6e88ef)、[模型调用留下什么](understanding.md#n-97745ff5030f65f3)、[派生结果遇到删除或撤权](access.md#n-183194894919be77)、[理解版本怎样使用](understanding.md#n-be7944dd96e1f491)、[理解能追溯来源](understanding.md#n-299861624af1def3)、[留下观测重算理解](understanding.md#n-77f980be4039dd0a)、[程序和模型怎样分工](understanding.md#n-9b5d1c252325594d)、[索引怎样增长](understanding.md#n-f3ee89d07892466b)、[缺口怎样呈现](understanding.md#n-99d2d4d1188e8e92)、[网状拓扑和完整节点](availability.md#n-561838d4e083830d)、[设备角色与离线工作](availability.md#n-e56d1b0735fb0084)、[读出引用到哪里](understanding.md#n-fe26af63a38ee976)、[身份对应怎样形成](understanding.md#n-3dd56ca36a38bda1)、[连接信任与访问边界](availability.md#n-ca03d6309f6b9fd6)、[采集设备怎样缓冲](availability.md#n-358b28d323bdbb8b)、[重复位置怎样归并](records.md#n-9034dc9f63983316)、[长期恢复怎样验证](delivery.md#n-254f30d6e194be24)

</details>

<a id="n-3de1e9f135b38d16"></a>

## 数据大多会流失

**既有决定·本轮回看** · 既有总体判断；当前来源样本证明存在损失，“大多”的总体比例没有测量。

依据材料：[src/Docs/Orisumi.Docs/content/docs/requirements/belief.mdx#数据大多会流失](../../../../../src/Docs/Orisumi.Docs/content/docs/requirements/belief.mdx#%E6%95%B0%E6%8D%AE%E5%A4%A7%E5%A4%9A%E4%BC%9A%E6%B5%81%E5%A4%B1)。来源样本的观察与范围见原记录；不扩成对所有来源的保证。

<a id="n-199bd2fb464f74ce"></a>

## 有的数据错过就没了

**已有观察** · 应用活动等瞬时来源在当时未记录，事后通常无法从该来源补齐；需按来源核对可回放窗口。

依据材料：[.scratch/requirements/survey-heartbeat.md](../../../../requirements/survey-heartbeat.md)。来源样本的观察与范围见原记录；不扩成对所有来源的保证。

<details><summary>可能需要回看：47 个下游节点（含所有候选路线）</summary>

[事件时间与顺序](capture.md#n-0843f43438c1c4a1)、[位置定位到哪里](records.md#n-d75a21e927a755bb)、[先采错过就没的](sources.md#n-e4cd3dc0553aa56f)、[共享怎样授权](access.md#n-4b3c6032607d1b5c)、[分块策略与版本](storage.md#n-044919fef0bfc086)、[删除记录与回收](storage.md#n-0350116e746cba2c)、[压缩与存放表示](storage.md#n-d00cf82ac296b520)、[原件分单元和块两层](records.md#n-c2507b9e78d79f34)、[原件存成块](storage.md#n-1e55971a7ab3ff58)、[原样保存与一致性](records.md#n-49d755be3c14fe60)、[取得方式按来源形态选择](capture.md#n-6d6180cd980550cc)、[取得时间怎样记录](records.md#n-746612aae6ea6fde)、[取得权限与资源条件](capture.md#n-55c7589ea4cd33f0)、[各处原样都存](records.md#n-8e446efeb629f847)、[同步交换与提交](availability.md#n-da173cb6e04759f8)、[多副本删除与离线节点](availability.md#n-19ed3ea2239e62f9)、[完整内容的边界](records.md#n-26854bffbe13f18e)、[导出与恢复](storage.md#n-da66683208852205)、[屏幕观测粒度](sources-live.md#n-e5a27649574fb74b)、[接口与导出取得方式](capture.md#n-c68c6175d29abc5e)、[接口全量与增量](capture.md#n-76b9927e1c8393d5)、[数据库取得一致性](capture.md#n-cba810bb05a18834)、[文件变化取得策略](capture.md#n-6de65500ebbea77b)、[文件读取一致性](capture.md#n-a6463ad095322f24)、[新旧理解怎样评测](delivery.md#n-cbfb97254da427c8)、[时间含义怎样保留](understanding.md#n-e0bf0a291b892dad)、[来源原生增量怎样保存](storage.md#n-95be81155ce3d6b5)、[来源接入顺序](sources.md#n-821855273f39bc59)、[模型调用留下什么](understanding.md#n-97745ff5030f65f3)、[派生结果遇到删除或撤权](access.md#n-183194894919be77)、[浏览器事件传输](capture.md#n-b75cd5e6c6494099)、[浏览器页面内容范围](sources-live.md#n-a7ffdec9fe8bb5c3)、[理解版本怎样使用](understanding.md#n-be7944dd96e1f491)、[瞬时事件接收方式](capture.md#n-359653aed5ab9c59)、[离线与历史补取](capture.md#n-f116b26a22aa88eb)、[设备角色与离线工作](availability.md#n-e56d1b0735fb0084)、[读出引用到哪里](understanding.md#n-fe26af63a38ee976)、[调度频率与触发](capture.md#n-4804bedf60d73204)、[跨材料的取得时刻](capture.md#n-cc2fd44e41899000)、[连续媒体分段与编码](capture.md#n-84f109ff78f86572)、[连续录制怎样分段](capture.md#n-fd5e4f2373611f18)、[采集单次结果与保障边界](capture.md#n-5c39e84c8ee42507)、[采集启动与执行位置](capture.md#n-f47170f9f44e3fc5)、[采集幂等](storage.md#n-cbc00cff523393ab)、[采集设备怎样缓冲](availability.md#n-358b28d323bdbb8b)、[重复位置怎样归并](records.md#n-9034dc9f63983316)、[附件与依赖的收集边界](sources.md#n-128cbd6f1a6dfa45)

</details>

<a id="n-5de3cb593b5de6a7"></a>

## 采集会丢数据

**已有观察** · 旧 Heartbeat 的样本记录过积压和溢出导致长时间缺口；这支持评估运行边界，当前保障范围按用户选择从简。

依据材料：[.scratch/requirements/survey-heartbeat.md](../../../../requirements/survey-heartbeat.md)。来源样本的观察与范围见原记录；不扩成对所有来源的保证。

<details><summary>可能需要回看：3 个下游节点（含所有候选路线）</summary>

[缺口怎样呈现](understanding.md#n-99d2d4d1188e8e92)、[采集单次结果与保障边界](capture.md#n-5c39e84c8ee42507)、[采集失败要通知](capture.md#n-3077b006413df0eb)

</details>

<a id="n-4c18fe2430707f02"></a>

## 格式随版本变化

**已有观察** · 勘探看到旧来源标识、编码等形态变化；保留实际内容与版本信息支持后续解释。

依据材料：[.scratch/requirements/survey-heartbeat.md](../../../../requirements/survey-heartbeat.md)。来源样本的观察与范围见原记录；不扩成对所有来源的保证。

<details><summary>可能需要回看：1 个下游节点（含所有候选路线）</summary>

[记下采集器版本](records.md#n-b260468ee1981b5f)

</details>

<a id="n-f8edb5f34401fec3"></a>

## 一个东西有多个名字

**已有观察** · 同一应用、会话或路径在不同平台和来源使用不同标识；对应关系的可靠程度要保留依据。

依据材料：[.scratch/requirements/survey-assistant-sessions.md](../../../../requirements/survey-assistant-sessions.md)。来源样本的观察与范围见原记录；不扩成对所有来源的保证。

<details><summary>可能需要回看：7 个下游节点（含所有候选路线）</summary>

[新旧理解怎样评测](delivery.md#n-cbfb97254da427c8)、[来源标识怎样保留](records.md#n-35c210bea72e9af7)、[模型调用留下什么](understanding.md#n-97745ff5030f65f3)、[理解版本怎样使用](understanding.md#n-be7944dd96e1f491)、[身份事后读出](understanding.md#n-5261a78fdee82c0f)、[身份对应怎样形成](understanding.md#n-3dd56ca36a38bda1)、[阅读视图怎样组织](access.md#n-45c1a653f0eb56ac)

</details>

<a id="n-c7f6551688fc3ac5"></a>

## 一件事散在多处

**已有观察** · 会话信息散在日志、索引、数据库和附件，复制、分叉、续接使内容重合。

依据材料：[.scratch/requirements/survey-assistant-sessions.md](../../../../requirements/survey-assistant-sessions.md)。来源样本的观察与范围见原记录；不扩成对所有来源的保证。

<details><summary>可能需要回看：27 个下游节点（含所有候选路线）</summary>

[Agent入口怎样提供](access.md#n-da5b4169050995d4)、[主动讲述怎样进入](access.md#n-4bf3066db8e45d47)、[人端先做什么](access.md#n-9b3ff99aea342b48)、[共享怎样授权](access.md#n-4b3c6032607d1b5c)、[分块策略与版本](storage.md#n-044919fef0bfc086)、[删除记录与回收](storage.md#n-0350116e746cba2c)、[压缩与存放表示](storage.md#n-d00cf82ac296b520)、[原件存成块](storage.md#n-1e55971a7ab3ff58)、[各处原样都存](records.md#n-8e446efeb629f847)、[同一份数据多种视图](access.md#n-309f702d88cb0e1d)、[后续读取怎样验收](delivery.md#n-9e39e29694bbe145)、[基本读出怎样重写](understanding.md#n-d34b5c062cf781c5)、[多副本删除与离线节点](availability.md#n-19ed3ea2239e62f9)、[导出与恢复](storage.md#n-da66683208852205)、[新旧理解怎样评测](delivery.md#n-cbfb97254da427c8)、[时间含义怎样保留](understanding.md#n-e0bf0a291b892dad)、[来源原生增量怎样保存](storage.md#n-95be81155ce3d6b5)、[查询输出怎样组织](access.md#n-83096b85d1cbe387)、[模型调用留下什么](understanding.md#n-97745ff5030f65f3)、[派生结果遇到删除或撤权](access.md#n-183194894919be77)、[理解版本怎样使用](understanding.md#n-be7944dd96e1f491)、[缺口怎样呈现](understanding.md#n-99d2d4d1188e8e92)、[资料中的指令怎样处理](access.md#n-61280889c3818aa0)、[采集幂等](storage.md#n-cbc00cff523393ab)、[重复位置怎样归并](records.md#n-9034dc9f63983316)、[重复材料怎样呈现](understanding.md#n-68f660987b59d242)、[阅读视图怎样组织](access.md#n-45c1a653f0eb56ac)

</details>

<a id="n-eec8ee5eac1a2112"></a>

## 存下的也会消失

**已有观察** · 来源已有记录可能被删除、移动或压缩；移动与压缩是否造成实际信息损失需要分别判断。

依据材料：[.scratch/requirements/survey-assistant-sessions.md](../../../../requirements/survey-assistant-sessions.md)。来源样本的观察与范围见原记录；不扩成对所有来源的保证。

<details><summary>可能需要回看：15 个下游节点（含所有候选路线）</summary>

[保存地是否需要加密封装](availability.md#n-5d0b7394b458d05a)、[共享怎样授权](access.md#n-4b3c6032607d1b5c)、[同步交换与提交](availability.md#n-da173cb6e04759f8)、[多副本删除与离线节点](availability.md#n-19ed3ea2239e62f9)、[多处都有一份](availability.md#n-ae9194edde188509)、[导出与恢复](storage.md#n-da66683208852205)、[离线与历史补取](capture.md#n-f116b26a22aa88eb)、[网状拓扑和完整节点](availability.md#n-561838d4e083830d)、[能补的也定期采](capture.md#n-a6470ed7ace260f0)、[设备角色与离线工作](availability.md#n-e56d1b0735fb0084)、[调度频率与触发](capture.md#n-4804bedf60d73204)、[连接信任与访问边界](availability.md#n-ca03d6309f6b9fd6)、[采集启动与执行位置](capture.md#n-f47170f9f44e3fc5)、[采集设备怎样缓冲](availability.md#n-358b28d323bdbb8b)、[长期恢复怎样验证](delivery.md#n-254f30d6e194be24)

</details>
