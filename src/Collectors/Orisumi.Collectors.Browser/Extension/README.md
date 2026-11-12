# Orisumi Chrome 采集扩展

从仓库根目录构建本机采集器和扩展。

```sh
dotnet build Orisumi.slnx
npm --prefix src/Collectors/Orisumi.Collectors.Browser/Extension ci
npm --prefix src/Collectors/Orisumi.Collectors.Browser/Extension run build
```

在 Chrome 扩展页启用开发者模式，加载构建产物目录 `src/Collectors/Orisumi.Collectors.Browser/ChromeExtension`。复制扩展 ID，然后在仓库根目录注册本机采集器。

```sh
dotnet run --no-build --project src/Orisumi.Cli -- browser register-chrome EXTENSION_ID
```

扩展安装和 Chrome 启动时会自动同步，之后每分钟继续采集。注册本机采集器后，下一轮会自动重试。数据写入 `.orisumi/raw/browser.sqlite`。

修改扩展源码后，重新运行构建命令并在 Chrome 扩展页重载。运行 `npm test` 可验证历史同步的断点重试和数据库重建行为。
