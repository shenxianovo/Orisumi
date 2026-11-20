项目正在重写。不用兼容。

# 文档

- 新增项目文档统一放在 `src/Orisumi.Docs/content/docs`；文档站维护方式见其中的 `development/contributing.mdx`。
- 文档简短、准确；保留必要语义、操作和限制，同一规则只在一处维护，其余引用。

# 开发

- 本机管理命令和集成检查使用 C#/.NET；平台原生与扩展代码沿用各自工具链。
- 新增或修改采集器时，先读 `src/Orisumi.Docs/content/docs/capture/index.mdx`。采集原则和数据库约定只在那里维护。
