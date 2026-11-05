# Orisumi.Dev

Orisumi.Dev 是开发的唯一入口，人、Agent 和以后的 CI 都通过它的命令 `orisumi` 格式化、lint、构建、测试、评审和查看证据。开发需要的功能都加在这里，见 [ADR 0001](../../src/Docs/Orisumi.Docs/content/docs/dev/adr/0001-single-entry.mdx)。用词见[开发术语](../../src/Docs/Orisumi.Docs/content/docs/dev/glossary.mdx)。

## 准备

需要 `global.json` 指定的 .NET SDK，以及 Node.js、npm 和 pnpm。各语言用自己的工具链：C# 用 .NET 分析器和 `dotnet format`，TypeScript 和 JavaScript 用 TypeScript 编译器 API，未使用的代码用 Knip。这些 node 工具在 `tools/Orisumi.Dev/node-tools/`，首次用到时按锁文件自动安装；Node 项目自己的依赖在 lint 需要时按它的锁文件安装。

评审需要 OpenAI API key，设置一次，这台机器上的所有检出目录共用：

```sh
orisumi config set openai.api-key
```

## 安装

Orisumi.Dev 是 .NET 全局工具，命令名是 `orisumi`。首次安装时，在仓库根目录运行：

```sh
dotnet run --project tools/Orisumi.Dev -- tool install
```

这条命令打包当前检出目录中的 Orisumi.Dev，然后安装或更新 `orisumi`。每次打包都生成新的版本号，所以更新后运行的总是刚打包的版本。Orisumi.Dev 改动后，运行 `orisumi tool install` 更新。

`orisumi` 在仓库中的任何目录都可以运行，它通过 Git 找到当前检出目录的根目录。

## 项目清单

根目录的 `Orisumi.slnx` 是仓库唯一的项目清单。C# 项目写成 `<Project>`，Node 项目把它的 `package.json` 写成 `<File>`。项目所在的文件夹决定它的角色：

| 文件夹 | 角色 |
| --- | --- |
| `/src/` | 业务代码 |
| `/tests/` | 测试 |
| `/tools/` | 开发工具 |

其余信息来自各项目自己的文件：

| 文件 | 决定 |
| --- | --- |
| csproj 的 `ProjectReference` | 测试项目覆盖哪些项目 |
| `package.json` 的 scripts | `verify` 运行其中的 `test`、`types:check`、`build` |
| `pnpm-lock.yaml` | 用 pnpm 还是 npm |
| `knip.json` | Knip 的入口和范围 |
| `exceptions.json` | 未使用代码的[例外](#未使用的代码和例外) |
| `Questions/*.json` | 这个项目的[题](#题) |

验证、评审和行数统计都从这份清单推导。新增项目时把它加进 `Orisumi.slnx`；不属于任何项目的代码文件在 `loc` 里列为未分类。

## 命令

改动按时机经过三层：

| 层 | 命令 | 内容 |
| --- | --- | --- |
| 整理 | `tidy` | 改完文件后顺手运行：格式化改过的代码，lint 改过的文件 |
| 验证 | `verify` | 交付前运行，全部是门禁：整理是否做完、lint、构建、测试 |
| 评审 | `review` | 改完之后给评审者看，全部是报告：相对基点新写的文字、例外的理由 |

其他命令：

| 命令 | 作用 |
| --- | --- |
| `run` | 运行本检出的 Orisumi，`run` 后面的参数原样交给它，数据放在 `.local/orisumi/` |
| `docs` | 运行文档站 |
| `loc` | 行数统计 |
| `runs` | 运行记录 |
| `config` | 用户级设置 |
| `tool` | 安装和更新 `orisumi` 自身 |

只输入命令组时显示该组的帮助。

### 输出

命令默认输出给人读的文字，带 `--json` 选项的命令加上它输出 JSON。文字的第一行是“主题 · 范围 · 结论”，例如 `Review · against HEAD (cd2bedb) · nothing new`。状态记号：

| 记号 | 含义 |
| --- | --- |
| ✓ | 成功 |
| ✗ | 失败 |
| ● | 运行中，或者报告有发现 |
| ○ | 等待或已停止 |
| ⊘ | 跳过或取消 |

仓库内的路径写成相对仓库根目录的形式，主目录写成 `~`。

在支持 ANSI 的交互终端里，输出带颜色，长时间的步骤原地刷新转圈和已用时间，每秒一次。输出被重定向、`TERM=dumb`、设置了 `CI` 时，输出纯文本，不刷新；设置了 `NO_COLOR` 时不着色。Agent 在伪终端里运行命令时，设置 `TERM=dumb` 得到纯文本。

## 文档站

```sh
orisumi docs dev
```

先按锁文件安装文档站的依赖，已经一致时 pnpm 跳过，再在前台运行开发服务器，输出直接显示在终端，按 Ctrl+C 停止。地址是 <http://localhost:3000/>，端口被占用时 Next.js 换用下一个空闲端口，以服务器输出的地址为准。

## 整理

```sh
orisumi tidy [--json]
```

改动是相对 `HEAD` 的已跟踪改动和未跟踪文件。`tidy` 依次：

1. 格式化改过的代码：C# 用 `dotnet format whitespace`，按根目录的 `.editorconfig`；TypeScript 和 JavaScript 用 TypeScript 语言服务的格式化。Markdown 和 JSON 保持原样。
2. 对改过的文件运行 [lint](#lint)，有发现时返回 1。

## 验证

```sh
orisumi verify [--scope changed|all] [--base <ref>] [--plan] [--json]
```

`--scope` 选择范围，默认 `changed`：

| 范围 | 检查 |
| --- | --- |
| `changed` | 根据相对基点的改动选择检查。项目之外的非 Markdown 文件触发全部检查 |
| `all` | 全部代码文件的整理、整个仓库的 lint、构建解决方案、所有测试项目、所有 Node 项目的脚本 |

改动路径按[项目清单](#项目清单)找到所属的项目，再选择检查：

| 改动 | 检查 |
| --- | --- |
| C#、TypeScript、JavaScript 文件 | 整理是否做完，lint，再加所属项目的检查 |
| Markdown | lint；属于 Node 项目时再加那个项目的脚本 |
| C# 项目里的其他文件 | 构建解决方案，运行覆盖这个项目的测试项目 |
| Node 项目里的其他文件 | 项目 scripts 中的 `test`、`types:check`、`build`，按这个顺序 |
| `.scratch/` | 无 |

- `--base` 指定基点。有未提交的改动时可以省略，默认使用 `HEAD`；没有改动时必须指定。只用于 `changed` 范围。
- 每项检查都运行到底，前面的失败不跳过后面的检查；取消时停止。
- 任何一项检查失败，命令返回失败。

### 测试结果

测试按用例汇总。C# 测试项目输出 TRX，Node 项目的 `test` 脚本通过 `NODE_OPTIONS` 加上 Node 自带的 JUnit reporter。每项测试检查显示用例数，失败时列出失败的用例和消息，完整结果在证据目录的 `.trx` 和 `.junit.xml` 文件里。

### 计划与 JSON 输出

加 `--plan` 只输出计划，不执行。加 `--json` 以 JSON 输出：

| 命令 | stdout |
| --- | --- |
| `verify --plan --json` | 计划：`scope`、`fallback`、`base`、`changedPaths` 和每项检查的 `name`、`description` |
| `verify --json` | 验证结果，和证据目录里的 `verification.json` 相同 |

验证结果有 `evidence`（证据目录）、`scope`、`fallback`、`base`、`changedPaths`、`exitCode` 和 `checks`。`checks` 里每项检查有 `name`、`exitCode`、`log`（进程的输出），整理、lint 和测试另有 `report`。执行进度写到 stderr，stdout 可以整体解析。取消时同样输出结果，内容是已经完成的部分。

## Lint

lint 是确定性的规则，在 `tidy` 里查改过的文件，在 `verify` 里查整个仓库。每条发现写成「规则 · 文件:行号 · 说明」。

### 文档

`.scratch/` 之外的所有 Markdown 和 MDX：

| 规则 | 内容 |
| --- | --- |
| `link` | 链接写成指向文件的相对路径，目标存在；以 `/` 开头的站点路径是发现。mermaid 的 `click` 链接也检查 |
| `anchor` | 锚点对应显式的 `[#id]`，或者标题按 github-slugger 生成的 slug |
| `json` | ` ```json ` 代码块可以解析，带标题或行高亮的代码块也检查 |
| `requirements` | 需求页的依赖图，见下文 |

一页里有 `## 立场`、`## 假设` 或 `## 推论` 时，它是需求页：

- 这三节下的每个 `###` 是一条陈述，名字在所有需求页中唯一。
- 推论有「依据：」一行，链接到已有的陈述；立场和假设是出发点，没有依据。依据不能成环。
- 页面上的 mermaid 图画出这一页的每条陈述和每条依据，假设带 `class hypothesis`，每个节点有 `click` 链接到自己的说明。

### 未使用的代码和例外

C# 的未使用私有成员（IDE0051）由 `.editorconfig` 设为警告，构建时作为错误报告。Node 业务项目用 Knip 查未使用的文件、导出和依赖，每条发现都是 lint 发现。

有些发现不需要改代码，例如只在 CSS 里 `@import` 的包，Knip 看不到这种引用。这样的发现由所属项目在自己目录下的 `exceptions.json` 声明为例外：

```json
[
  { "rule": "knip/devDependencies", "path": "package.json", "symbol": "tailwindcss", "reason": "referenced-outside-code" }
]
```

`path` 相对项目目录，`reason` 是题 `unused-reason` 的选项。声明了但 Knip 不再报告的例外、理由不在选项里的例外也是 lint 发现。例外的理由是否仍然成立由[评审](#评审)复核。

## 题

有些判断靠代码做不出来，交给 OpenAI Decisions API。一道题是一个 JSON 文件，放在所属项目的 `Questions/` 目录，文件名是题的 ID，ID 在仓库中唯一。

| 字段 | 内容 |
| --- | --- |
| `title` | 题名，输出里显示 |
| `type` | `predicate`（判断题，默认）或 `choice`（选择题） |
| `question` | 交给模型的题目 |
| `paths` | 评审时审读哪些文件的新段落，`**` 跨目录，`*` 不跨目录 |
| `threshold` | 通过条件，默认 0.5 |
| `options` | 选择题的选项：`value`、`description` |

判断题的概率表示模型认为陈述成立的把握，只和通过条件比较。

## 评审

```sh
orisumi review [--base <ref>] [--json]
```

基点和 `verify` 相同。评审全部是报告，发现不使命令失败，没有完成时返回 1。

| 内容 | 来源 |
| --- | --- |
| 写作 | 改动文件中新出现的段落，按 `paths` 覆盖它的题审读，概率达到通过条件的是发现 |
| 例外 | 每条例外附上项目内的引用和锁文件中的位置，模型回答 `unused-reason`，声明的理由不再成立时列出 |

评审通过 Git 读取基点的文件内容。文件按空行切成段落，跳过 frontmatter、代码块、只有标题的段落和 MDX 的 import/export。设置 key 后运行写作和例外理由的模型评审；缺少 key 时输出跳过说明。评审的回答缓存在 `.artifacts/review-cache/`，不提交。

## 配置

```sh
orisumi config list
orisumi config set <名称> [<值>]
orisumi config unset <名称>
```

设置保存在 `~/.config/orisumi/config.json`（设置了 `XDG_CONFIG_HOME` 时在它下面），只有当前用户能读写。`set` 省略值时，在终端里隐藏输入，stdin 被重定向时读取全部输入，这样 key 不会留在 shell 历史里。`list` 只显示 key 的首尾几位。

| 名称 | 用途 |
| --- | --- |
| `openai.api-key` | 评审调用 OpenAI |

## 行数

```sh
orisumi loc [--json]
```

按项目和语言统计工作区中已跟踪的文件，不计空行和纯注释行。开头的总数只算代码，也就是实现加测试，实现是业务代码和开发工具。文档、构建描述和生成文件（锁文件）单列一行，不进总数。文件先按语言分类，所以测试项目里的 csproj 算构建描述，README 算文档。

项目里的测试归这个项目。测试项目按 csproj 的 `ProjectReference` 归属：只引用一个非测试项目的归那个项目，引用多个或一个都不引用的归 `Cross-module`。

## 证据

每次验证和评审的证据保存在 `.artifacts/runs/<run-id>/`：

| 文件 | 内容 |
| --- | --- |
| `manifest.json` | 执行清单：启动的命令、开始和结束时间、退出码、执行过的外部命令和产物。运行开始时写入，结束时更新 |
| `verification.json` | 验证结果，见[计划与 JSON 输出](#计划与-json-输出) |
| `*.trx`、`*.junit.xml` | 每个测试检查的用例结果 |
| `review.json` | 评审报告：写作的发现、例外的复核和每条例外的证据 |
| `review-calls.jsonl` | 评审调用模型的请求、响应和耗时 |
| 步骤日志 | 每个步骤的输出 |

成功、失败和取消的运行都保存。

```sh
orisumi runs list
orisumi runs prune           # 只预览
orisumi runs prune --apply   # 删除
```

`runs list` 每次运行一行：开始的时间（本地时区）、启动它的命令、结果、耗时和失败的检查，由此可以看出哪次运行失败、失败在哪一步。需要证据目录的路径时加 `--json`。

每次运行结束时自动清理，保留最近 10 次、最近 5 次没有成功的，以及 2 天内的全部运行。`runs prune` 使用同样的条件。

## 退出码

| 退出码 | 含义 |
| --- | --- |
| 0 | 成功 |
| 2 | 参数错误 |
| 130 | 已取消 |
| 其他非零值 | 执行失败 |
