using System.CommandLine;
using Orisumi.Collectors.Browser;
using Orisumi.Collectors.Hosting;
using Orisumi.Platform;

var store = CollectorCommand.StoreOption();
var root = CollectorCommand.Create("browser", "Orisumi · 浏览器采集", store, "chrome", []);
var registered = new Option<bool>("--registered") { Hidden = true };
var nativeHost = new Command("native-host", "接收 Chrome Native Messaging 消息。") { Hidden = true, Options = { registered } };
nativeHost.SetAction((result, token) =>
    CaptureLifetime.Run(stop => ChromeNativeHost.Run(result.GetValue(store) ?? Instance.DefaultRoot(), result.GetValue(registered), stop), token));
var extensionId = new Argument<string>("extension-id") { Description = "Chrome 扩展页显示的扩展 ID。" };
var register = new Command("register-chrome", "注册当前版本的 Chrome 本机接收端。") { extensionId };
register.SetAction(result => Console.WriteLine(ChromeNativeHost.Register(
    result.GetValue(store) ?? Instance.DefaultRoot(), result.GetRequiredValue(extensionId))));
root.Add(nativeHost);
root.Add(register);
return await CollectorCommand.Run(root, args);
