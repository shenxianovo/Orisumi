using System.CommandLine;
using Orisumi.Capture.Hosting;
using Orisumi.Collectors.Codex;
using Orisumi.Core;

var store = CollectorCommand.StoreOption();
var capture = new Command("capture", "持续监听 Codex 会话并增量导入。");
capture.SetAction((result, token) =>
    CaptureLifetime.Run(stop => CodexCollector.Watch(result.GetValue(store) ?? Instance.DefaultRoot(), stop), token));
var root = CollectorCommand.Create(CodexCollector.Collector, "Orisumi · Codex 会话采集", store, ["capture"]);
root.Add(capture);
return await CollectorCommand.Run(root, args);
