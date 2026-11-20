using System.CommandLine;
using Orisumi.Collectors.Browser;
using Orisumi.Collectors.Hosting;
using Orisumi.Platform;

var store = CollectorCommand.StoreOption();
var root = CollectorCommand.Create("browser", "Orisumi · 浏览器采集", store, ["capture"]);
var capture = new Command("capture", "常驻接收 Chrome 扩展的 WebSocket 消息。");
capture.SetAction((result, token) => CaptureLifetime.Run(
    stop => BrowserServer.Run(Instance.Open(result.GetValue(store) ?? Instance.DefaultRoot()), stop), token));
root.Add(capture);
return await CollectorCommand.Run(root, args);
