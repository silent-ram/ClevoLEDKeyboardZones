using ColorfulLedKeyboard.Service;

// 单实例守卫：多个服务实例会同时写 EC、抢占 IPC 端口与设置文件（演示脚本多次运行/
// 残留进程时出现），表现为模式切换"不生效"——第二个实例直接退出。Global\ 前缀跨提权边界。
using var singleInstance = new Mutex(initiallyOwned: true, @"Global\ClevoLEDKeyboardControlZones.Service", out var serviceCreatedNew);
if (!serviceCreatedNew)
{
    Console.WriteLine("Another ColorfulLedKeyboard.Service instance is already running; exiting.");
    return 1;
}

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = ColorfulLedKeyboard.Core.AppPaths.ServiceName;
});

builder.Services.AddSingleton<ServiceIpcServer>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
return 0;
