using System.CommandLine;
using Orisumi.Capture.Hosting;
using Orisumi.Collectors.Claude;
using Orisumi.Platform;

var store = CollectorCommand.StoreOption();
var capture = new Command("capture", "持续监听 Claude Code 会话日志并增量导入。");
capture.SetAction((result, token) =>
    CaptureLifetime.Run(stop => ClaudeCollector.Watch(result.GetValue(store) ?? Instance.DefaultRoot(), stop), token));
var root = CollectorCommand.Create(ClaudeCollector.Collector, "Orisumi · Claude Code 会话日志采集", store, ["capture"]);
root.Add(capture);
return await CollectorCommand.Run(root, args);
