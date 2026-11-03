# Orisumi.Docs

Orisumi.Docs 是 Orisumi 的文档站，使用 Fumadocs 和 Next.js。文档写在 [content/docs](content/docs)。

## 运行

前置条件：Node.js 24 LTS 和 pnpm 12.3.4。

在仓库任何目录运行：

```sh
orisumi docs dev
```

它安装依赖并启动开发服务器，打开 <http://localhost:3000/>。用法见 [Orisumi.Dev README · 文档站](../../../tools/Orisumi.Dev/README.md#文档站)。

本地预览会在正文中用红绿标记显示相对最近一次提交的修改，包括已暂存和未暂存的内容。标记每三秒及窗口重新获得焦点时刷新，提交或撤销修改后自动清除。读取修改的接口使用 `route.dev.ts`，只由开发服务器加载；静态构建导出正常正文和页面历史。

在本目录还可以运行：

| 命令 | 作用 |
| --- | --- |
| `pnpm types:check` | 生成路由类型并做类型检查 |
| `pnpm build` | 静态导出到 `out/`，并检查导出的页面、链接和资源 |

开发服务器不按静态导出运行，导出不支持的写法到 `pnpm build` 时才报错。

## 写文档

- 链接写成指向文件的相对路径，例如 `[发心](./belief.mdx#用户)`，页面上和 GitHub 上都能跳转。链接、锚点、JSON 示例和需求页的依赖图由 `orisumi` 的 lint 检查，见 [Orisumi.Dev README · Lint](../../../tools/Orisumi.Dev/README.md#lint)。
- 每个文件夹的 `meta.json` 是 Fumadocs 的侧边栏配置。`pages` 先列出需要排在前面的页面，最后写 `"..."`，其余页面按文件名排在后面，新页面自动出现在侧边栏里。
