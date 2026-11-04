using Galosys.Foundation.Channel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetServer.ConsoleApp;
using System.Net;

// 运行角色：
//   server —— 只启动服务端，监听 0.0.0.0:8000
//   client —— 只启动客户端，连接 127.0.0.1:8000
//   both   —— 同时启动两端（默认）
//
// 分成两个进程可以观察客户端的重连过程：先起 server，再起 client，
// 然后 Ctrl+C 掉 server，client 会按退避策略重连；再重启 server 即可看到连接恢复。
var role = args.Length > 0 && args[0].Equals("client", StringComparison.OrdinalIgnoreCase)
    ? "client"
    : args.Length > 0 && args[0].Equals("server", StringComparison.OrdinalIgnoreCase)
        ? "server"
        : "both";

// 发送端模式：true 用连接池（ChannelClientPool），false 用单连接客户端（ChannelClient）。
// 切换这个变量即可对比两种模式；连接池模式同时演示租借与归还。
var usePool = false;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<SampleChannelHandler>();

if (role is "both" or "client")
{
    if (usePool)
    {
        builder.Services.AddSingleton(ChannelClientBuilder
            .Create(o =>
            {
                o.EndPoint = new IPEndPoint(IPAddress.Loopback, 8000);
                o.ConnectTimeout = TimeSpan.FromSeconds(5);
            })
            .AsPool()
            .WithMaxSize(4)
            .WithRentTimeout(TimeSpan.FromSeconds(30))
            .Build());
    }
    // else 分支不需要注册：客户端由 SenderBootstrapper 自己创建，
    // 以便订阅 StateChanged 观察重连过程。
}

if (role is "both" or "server")
{
    builder.Services.AddSingleton(sp =>
    {
        var handler = sp.GetRequiredService<SampleChannelHandler>();

        return ChannelServerBuilder
            .Create(o => o.EndPoint = new IPEndPoint(IPAddress.Any, 8000))
            .Run(handler.HandleAsync)
            .WithLoggerFactory(sp.GetRequiredService<ILoggerFactory>())
            .WithServiceScopeFactory(sp.GetRequiredService<IServiceScopeFactory>())
            .Build();
    });

    // ChannelServerRunner 必须先于发送端注册，保证监听就绪后客户端才连接。
    builder.Services.AddHostedService<ChannelServerRunner>();
}

if (role is "both" or "client")
{
    if (usePool)
        builder.Services.AddHostedService<PoolSenderBootstrapper>();
    else
        builder.Services.AddHostedService<SenderBootstrapper>();
}

var host = builder.Build();
host.Run();
