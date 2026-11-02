# Orisumi.Dev

Orisumi.Dev 是人和 Agent 共用的开发工具，负责启动文档站、构建、测试、质量扫描和验证证据。

## 准备

需要 `global.json` 指定的 .NET SDK。文档站和质量扫描还需要 Node.js、npm 和 pnpm。文档站的依赖由 `docs dev` 自动安装；质量扫描的依赖在首次使用时安装：

```sh
npm --prefix tools/Orisumi.Dev/jscpd ci --ignore-scripts
```

## 安装

Orisumi.Dev 是 .NET 全局工具，命令名是 `orisumi`。首次安装时，在仓库根目录运行：

```sh
dotnet run --project tools/Orisumi.Dev -- tool install
```

这条命令打包当前检出目录中的 Orisumi.Dev，然后安装或更新 `orisumi`。每次打包都生成新的版本号，所以更新后运行的总是刚打包的版本。Orisumi.Dev 改动后，运行 `orisumi tool install` 更新。

`orisumi` 在仓库中的任何目录都可以运行，它通过 Git 找到当前检出目录的根目录。`orisumi <命令>` 与 `dotnet run --project tools/Orisumi.Dev -- <命令>` 等价；某个分支修改了 Orisumi.Dev 时，在那个分支上用后一种写法。

## 命令

| 命令 | 作用 |
| --- | --- |
| `docs` | 运行文档站 |
| `verify` | 构建与自动化检查 |
| `analyze` | 质量扫描和行数统计 |
| `runs` | 验证与质量扫描的运行记录 |
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
orisumi verify checks [--scope changed|all]
```

运行构建和自动化检查，`--scope` 选择范围，默认 `changed`：

| 范围 | 检查 |
| --- | --- |
| `changed` | 根据相对基点的改动选择检查。无法识别的路径触发全部检查 |
| `all` | 构建 .NET 解决方案，运行开发工具测试，检查并构建文档站 |

改动路径和检查的对应：

| 改动 | 检查 |
| --- | --- |
| `src/Docs/Orisumi.Docs/` | 文档检查、单元测试、类型检查和构建 |
| `tools/Orisumi.Dev/`、`tests/Orisumi.Dev.Tests/` | .NET 构建和开发工具测试 |
| 任何 `README.md`、`AGENTS.md`，`.agents/` 下的 Markdown | 文档检查 |
| `.scratch/` | 无 |

- `--base` 指定基点。有未提交的改动时可以省略，默认使用 `HEAD`；没有改动时必须指定。
- `--quality` 在同一次运行中执行检查，再以同一基点运行质量扫描，两者共用一份运行记录。
- `--base` 和 `--quality` 只用于 `changed` 范围，与 `--scope all` 一起使用时返回参数错误。
- 构建失败、测试失败或扫描未完成时，命令返回失败。质量扫描的发现不使命令失败，供评审参考。

### 计划与 JSON 输出

加 `--plan` 只输出计划，不执行。加 `--json` 以 JSON 输出：

| 命令 | stdout |
| --- | --- |
| `verify checks --plan --json` | 计划：范围、`quality`、基点、改动路径和步骤 |
| `verify checks --json` | 一个结果对象：`exitCode`、`evidence`、`verification`、`quality` |

`verify checks --json` 的 `evidence` 是证据目录，`verification` 和 `quality` 是这次运行保存的报告，没有生成的报告为 `null`。执行进度写到 stderr，stdout 可以整体解析。取消时同样输出结果，内容是已经完成的部分。

## 质量与行数

```sh
orisumi analyze quality --base HEAD
orisumi analyze loc
```

两个命令都可以加 `--json`。

`analyze quality` 扫描业务代码、文档站和开发工具的复杂度、重复代码和未使用代码，并与基点比较。发现供审查，业务需要的复杂代码可以保留。基点必须能完成分析构建；历史代码无法编译时，命令报告比较未完成并返回失败，当前代码的扫描结果仍然保存。

终端列出相对基点新增的发现：重复代码、新增或变复杂的函数、未使用的代码，以及未完成的扫描。现有的发现只给数量，完整列表在证据目录的 `quality.json`。`verify checks --quality` 的质量部分写法相同。

`analyze loc` 按模块和语言统计工作区中已跟踪的文件，不计空行和纯注释行。开头的总数只算代码，也就是实现加测试，实现是生产代码和开发工具。文档、构建描述和生成文件（锁文件）单列一行，不进总数。文件先按语言分类，所以测试目录里的 csproj 算构建描述，README 算文档。

`src/` 下的测试归它所在的模块。`tests/` 下的测试项目按 csproj 的 `ProjectReference` 归属：只引用一个模块的归那个模块，引用多个模块或不引用生产项目的归 `Cross-module`。

## 证据

每次验证和质量扫描的证据保存在 `.artifacts/verification/<run-id>/`：

| 文件 | 内容 |
| --- | --- |
| `manifest.json` | 执行清单 |
| `verification.json` | 验证结果 |
| `quality.json` | 质量报告，加 `--quality` 或运行 `analyze quality` 时生成 |
| 步骤日志 | 每个步骤的输出 |

成功、失败和取消的运行都保存。

```sh
orisumi runs list
orisumi runs prune           # 只预览
orisumi runs prune --apply   # 删除
```

`runs list` 每次运行一行：开始的时间（本地时区）、运行的类型、结果、耗时和失败的步骤，由此可以看出哪次运行失败、失败在哪一步。需要证据目录的路径时加 `--json`。

每次运行结束时自动清理，保留最近 10 次、最近 5 次没有成功的，以及 2 天内的全部运行。`runs prune` 使用同样的条件。质量扫描的基线工作树缓存在 `.artifacts/quality-baselines/<commit>/`，不参与清理，需要回收空间时移除对应的 Git worktree。

## 退出码

| 退出码 | 含义 |
| --- | --- |
| 0 | 成功 |
| 2 | 参数错误 |
| 130 | 已取消 |
| 其他非零值 | 执行失败 |
