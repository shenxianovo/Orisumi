# Orisumi.Dev

Orisumi.Dev 是人和 Agent 共用的开发工具，负责启动文档站、构建、测试、lint、质量扫描、模型检查和验证证据。项目开发需要的功能都加在这里。

## 准备

需要 `global.json` 指定的 .NET SDK，以及 Node.js、npm 和 pnpm。文档站的依赖由 `docs dev` 自动安装；lint 和质量扫描用到的 node 工具在 `tools/Orisumi.Dev/node-tools/`，首次用到时按锁文件自动安装。各语言用自己的工具链：C# 用 Roslyn 和 .NET 分析器，TypeScript 用 TypeScript 编译器 API，复杂度和未使用代码另用 ESLint 和 Knip。

模型检查需要 OpenAI API key，设置一次，这台机器上的所有检出目录共用：

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

其余信息来自各项目自己的清单：csproj 的 `ProjectReference` 决定测试项目覆盖哪些项目，`package.json` 的 scripts 决定 `verify` 运行哪些脚本，`pnpm-lock.yaml` 决定用 pnpm 还是 npm，knip 读项目目录里的 `knip.json`。验证、质量扫描和行数统计都从这份清单推导。新增项目时把它加进 `Orisumi.slnx`；不属于任何项目的代码文件在 `analyze loc` 里列为未分类。

## 命令

| 命令 | 作用 |
| --- | --- |
| `docs` | 运行文档站 |
| `verify` | 按改动运行构建、测试、lint、文档检查和模型检查 |
| `lint` | 代码里的注释 |
| `review` | 模型检查：按规则判断写出的文字 |
| `analyze` | 质量扫描和行数统计 |
| `runs` | 验证、质量扫描和模型检查的运行记录 |
| `config` | 用户级设置 |
| `tool` | 安装和更新 `orisumi` 自身 |

只输入命令组时显示该组的帮助。

### 输出

命令默认输出给人读的文字，带 `--json` 选项的命令加上它输出 JSON。文字的第一行是“主题 · 范围 · 结论”，例如 `Quality · against HEAD (cd2bedb) · nothing new`。状态记号：

| 记号 | 含义 |
| --- | --- |
| ✓ | 成功 |
| ✗ | 失败 |
| ● | 运行中 |
| ○ | 等待或已停止 |
| ⊘ | 跳过或取消 |

仓库内的路径写成相对仓库根目录的形式，主目录写成 `~`。

在支持 ANSI 的交互终端里，输出带颜色，长时间的步骤原地刷新转圈和已用时间，每秒一次。输出被重定向、`TERM=dumb`、设置了 `CI` 时，输出纯文本，不刷新；设置了 `NO_COLOR` 时不着色。Agent 在伪终端里运行命令时，设置 `TERM=dumb` 得到纯文本。

## 文档站

```sh
orisumi docs dev
```

先按锁文件安装文档站的依赖，已经一致时 pnpm 跳过，再在前台运行开发服务器，输出直接显示在终端，按 Ctrl+C 停止。地址是 <http://localhost:3000/>，端口被占用时 Next.js 换用下一个空闲端口，以服务器输出的地址为准。

## 验证

```sh
orisumi verify [--scope changed|all]
```

`--scope` 选择范围，默认 `changed`：

| 范围 | 检查 |
| --- | --- |
| `changed` | 根据相对基点的改动选择检查。项目之外的非 Markdown 文件触发全部检查 |
| `all` | lint，构建解决方案，运行所有测试项目，运行所有 Node 项目的检查脚本 |

改动路径按[项目清单](#项目清单)找到所属的项目，再选择检查：

| 改动 | 检查 |
| --- | --- |
| `.cs`、`.ts`、`.tsx`、`.mjs` 等源文件 | lint，再加所属项目的检查 |
| C# 项目里的文件 | 构建解决方案，运行覆盖这个项目的测试项目 |
| Node 项目里的文件 | 项目 scripts 中的 `docs:check`、`test`、`types:check`、`build`，按这个顺序 |
| 任何 Markdown | 各项目的 `docs:check` |
| `.scratch/` | 无 |

改动涉及模型检查规则覆盖的文件或规则本身时，其余检查之后再运行模型检查，见[模型检查](#模型检查)。它的发现不使命令失败；没有设置 key 时跳过并提示设置命令。

- `--base` 指定基点。有未提交的改动时可以省略，默认使用 `HEAD`；没有改动时必须指定。
- `--quality` 在同一次运行中执行检查，再以同一基点运行质量扫描，两者共用一份运行记录。
- `--base` 和 `--quality` 只用于 `changed` 范围，与 `--scope all` 一起使用时返回参数错误。
- 每项检查都运行到底，前面的失败不跳过后面的检查；取消时停止。
- 构建失败、测试失败、lint 有发现、质量扫描或模型检查未完成时，命令返回失败。质量扫描和模型检查的发现不使命令失败，供评审参考。

### 计划与 JSON 输出

加 `--plan` 只输出计划，不执行。加 `--json` 以 JSON 输出：

| 命令 | stdout |
| --- | --- |
| `verify --plan --json` | 计划：`scope`、`fallback`、`base`、`changedPaths` 和每项检查的 `name`、`description` |
| `verify --json` | 验证结果，和证据目录里的 `verification.json` 相同 |

验证结果有 `evidence`（证据目录）、`scope`、`fallback`、`base`、`changedPaths`、`exitCode` 和 `checks`。`checks` 里每项检查有 `name`、`exitCode`、`log`（进程的输出），lint、质量扫描和模型检查另有 `report`。执行进度写到 stderr，stdout 可以整体解析。取消时同样输出结果，内容是已经完成的部分。

### 文档检查

文档检查查链接、锚点、JSON 示例、frontmatter 和侧边栏，并检查需求页的依赖图：

- `## 公理`、`## 假设`、`## 推论` 下的每个 `###` 是一条陈述，名字在所有需求页中唯一。
- 推论有「依据：」一行，链接到已有的陈述；公理和假设是出发点，没有依据。依据不能成环。
- 页面上的 mermaid 图画出这一页的每条陈述和每条依据，假设带 `class hypothesis`，每个节点有 `click` 链接到自己的说明。

## Lint

```sh
orisumi lint [--json]
```

检查工作区所有 C#、TypeScript 和 JavaScript 文件，代码里不写注释：需要解释的，改成更好的名字或一个测试。C# 用 Roslyn 解析，TypeScript 和 JavaScript 用 TypeScript 编译器 API，字符串和正则里的 `//` 不算。三斜线 `<reference>`、`@ts-` 指令和 shebang 是给工具的指令，不算注释。有发现时返回 1。

## 模型检查

有些写作规则靠代码判断不出来，例如「用正面陈述写」。这类规则交给模型判断：每条规则是一道是非题，回答「是」表示违反。

```sh
orisumi review check                # 相对基点新出现的段落
orisumi review check <文件>...       # 整个文件
orisumi review check --all          # 规则覆盖的所有文件
orisumi review rules                # 列出规则
orisumi review calibrate [<规则>...] # 用规则自带的例子校准
```

`review check` 默认的基点和 `verify` 相同，有未提交的改动时用 `HEAD`，否则用 `--base` 指定。三个命令都可以加 `--json`。

规则在 `tools/Orisumi.Dev/Review/Rules/`，一条规则一个 JSON 文件，文件名是规则的 ID：

| 字段 | 内容 |
| --- | --- |
| `title` | 规则名，输出里显示 |
| `paths` | 覆盖的文件，`**` 跨目录，`*` 不跨目录 |
| `question` | 问模型的是非题，「是」表示违反 |
| `violations` | 违反规则的例子 |
| `conforming` | 符合规则的例子 |

文件按空行切成段落，跳过 frontmatter、代码块、只有标题的段落和 MDX 的 import/export。`.scratch/` 不参与检查。模型用的是 OpenAI Decisions API，一次请求问一段文字的所有适用规则，返回每条违反的概率，不返回理由，所以发现按段落定位，写成「规则 · 文件:行号 · 概率 · 段落开头」。概率达到 0.5 算发现。

规则的例子是规则自己的测试。`review calibrate` 让模型判断每个例子，列出判错的：该判违反的没判出来，或者把符合的判成了违反。改了规则，`verify` 会校准改过的那几条。纠正一类写法的问题时，在这里加一条规则，并写上被纠正的原句作为违反的例子。

同一模型、同一问题、同一段文字的回答缓存在 `.artifacts/review-cache/`，没改的内容不重复调用，结果也保持一致。每次调用的请求和响应保存在证据目录。

## 配置

```sh
orisumi config list
orisumi config set <名称> [<值>]
orisumi config unset <名称>
```

设置保存在 `~/.config/orisumi/config.json`（设置了 `XDG_CONFIG_HOME` 时在它下面），只有当前用户能读写。`set` 省略值时，在终端里隐藏输入，stdin 被重定向时读取全部输入，这样 key 不会留在 shell 历史里。`list` 只显示 key 的首尾几位。

| 名称 | 用途 |
| --- | --- |
| `openai.api-key` | 模型检查调用 OpenAI |

## 质量与行数

```sh
orisumi analyze quality [--base <ref>]
orisumi analyze loc
```

两个命令都可以加 `--json`。`analyze quality` 的基点和 `verify` 相同。

`analyze quality` 扫描业务代码和开发工具的复杂度和未使用代码，并与基点比较。两边按当前的项目清单分类，C# 构建一次，同时得到复杂度和未使用的私有成员；TypeScript 和 JavaScript 用 ESLint 量复杂度，Node 项目用 Knip 找未使用的代码。发现供审查，业务需要的复杂代码可以保留。基点必须能完成分析构建；历史代码无法编译时，命令报告比较未完成并返回失败，当前代码的扫描结果仍然保存。

终端列出相对基点新增的发现：新增或变复杂的函数、未使用的代码，以及未完成的扫描。现有的发现只给数量，完整列表在证据目录的 `quality.json`。`verify --quality` 的质量部分写法相同。

`analyze loc` 按项目和语言统计工作区中已跟踪的文件，不计空行和纯注释行。开头的总数只算代码，也就是实现加测试，实现是业务代码和开发工具。文档、构建描述和生成文件（锁文件）单列一行，不进总数。文件先按语言分类，所以测试项目里的 csproj 算构建描述，README 算文档。

项目里的测试归这个项目。测试项目按 csproj 的 `ProjectReference` 归属：只引用一个非测试项目的归那个项目，引用多个或一个都不引用的归 `Cross-module`。

## 证据

每次验证、质量扫描和模型检查的证据保存在 `.artifacts/runs/<run-id>/`：

| 文件 | 内容 |
| --- | --- |
| `manifest.json` | 执行清单：启动的命令、开始和结束时间、退出码、执行过的外部命令和产物。运行开始时写入，结束时更新 |
| `verification.json` | 验证结果，见[计划与 JSON 输出](#计划与-json-输出) |
| `quality.json` | 质量报告，加 `--quality` 或运行 `analyze quality` 时生成 |
| `review.json` | 模型检查的报告：发现、校准判错的例子和每段的判断 |
| `review-calls.jsonl` | 模型检查每次调用的请求、响应和耗时 |
| 步骤日志 | 每个步骤的输出 |

成功、失败和取消的运行都保存。

```sh
orisumi runs list
orisumi runs prune           # 只预览
orisumi runs prune --apply   # 删除
```

`runs list` 每次运行一行：开始的时间（本地时区）、启动它的命令、结果、耗时和失败的检查，由此可以看出哪次运行失败、失败在哪一步。需要证据目录的路径时加 `--json`。

每次运行结束时自动清理，保留最近 10 次、最近 5 次没有成功的，以及 2 天内的全部运行。`runs prune` 使用同样的条件。质量扫描的基线工作树缓存在 `.artifacts/quality-baselines/<commit>/`，不参与清理，需要回收空间时移除对应的 Git worktree。

## 退出码

| 退出码 | 含义 |
| --- | --- |
| 0 | 成功 |
| 2 | 参数错误 |
| 130 | 已取消 |
| 其他非零值 | 执行失败 |
