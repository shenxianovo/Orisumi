# Orisumi.Docs

Orisumi.Docs 是 Orisumi 的文档站，使用 Fumadocs 和 Next.js。文档写在 [content/docs](content/docs)。公开站点是 <https://docs.orisumi.dev>。

## 运行

前置条件：Node.js 24 LTS 和 pnpm 12.3.4。

在仓库任何目录运行：

```sh
orisumi docs dev
```

它安装依赖并启动开发服务器，打开 <http://localhost:3000/>。用法见 [Orisumi.Dev README · 文档站](../../../tools/Orisumi.Dev/README.md#文档站)。

在本目录还可以运行：

| 命令 | 作用 |
| --- | --- |
| `pnpm docs:check` | 检查项目文档的一致性，见[文档检查](#文档检查) |
| `pnpm test` | 单元测试 |
| `pnpm types:check` | 生成路由类型并做类型检查 |
| `pnpm build` | 静态导出到 `out/`，并检查导出的页面、链接和资源 |

开发服务器不按静态导出运行，导出不支持的写法到 `pnpm build` 时才报错。

## 发布

公开站点由 `.github/workflows/docs.yml` 发布到 GitHub Pages，域名是 `docs.orisumi.dev`。工作流在 main 上本站目录或工作流本身有改动时运行，也可以在 Actions 页面手动运行：

1. 用完整的 Git 历史检出，供页面历史使用。
2. 运行 `pnpm docs:check`、`pnpm test` 和 `pnpm types:check`。
3. 运行 `pnpm build`，上传 `out/`，再由 GitHub Pages 发布。

任何一步失败都不发布，线上保留上一次的站点。工作流只使用 GitHub 提供的令牌，不需要 PAT 或 Secrets。

仓库设置和 DNS：

- Pages 的发布来源是 GitHub Actions，自定义域名是 `docs.orisumi.dev`，开启 Enforce HTTPS。
- `docs.orisumi.dev` 有四条 A 记录，指向 GitHub Pages 的 `185.199.108.153` 到 `185.199.111.153`。域名所有权验证的 TXT 记录保留。
- GA4 的衡量 ID 是 `G-WKHX52M7JW`，只在公开站点采集。Search Console 通过 Cloudflare 中的 TXT 记录验证，这条记录也保留。

## 文档检查

`pnpm docs:check` 检查项目文档：文档站内容、仓库中每个 `README.md` 和 `AGENTS.md`，以及 `.agents/` 下的 skill。

| 检查 | 内容 |
| --- | --- |
| `link` | 相对链接和站内绝对链接指向的文件存在 |
| `anchor` | 锚点对应显式的 `[#id]`，或者标题按 github-slugger 生成的 slug |
| `frontmatter` | 文档站页面都有 `title` 和 `description` |
| `json` | ` ```json ` 代码块可以解析，带标题或行高亮的代码块也检查 |
| `sidebar` | `meta.json` 中的条目存在，每个页面都被某个 `meta.json` 收录 |
