# P1.5b-2 多服务器宿主注册 Implementation Plan

> **REQUIRED SUB-SKILL:** 使用 `executing-plans` 或 `opsx-apply-x` 逐任务执行本计划。
> 本仓库虽有 git，但按约定不自动提交；步骤中的“验证”统一用构建 + 测试，不执行 git commit。

**Goal:** 在 `ChannelServerGroup` 之上增加 `ChannelServerDefinition`、`ChannelServerGroupHostedService` 和 `AddChannelServer(definition)` / `AddChannelServerGroup()`，让一个宿主安全托管多个独立配置的 server。

**Architecture:** 定义只描述 server（名称、options 快照、pipeline 回调）；`AddChannelServer` 以单例追加定义；`AddChannelServerGroup` 用容器 `ILoggerFactory` / `IServiceScopeFactory` 创建一个组和一个 hosted service。注册顺序即启动顺序，重复组注册幂等，缺少组注册时定义保持惰性。组内核语义不变。

**Tech Stack:** .NET 10、Microsoft.Extensions.Hosting/DependencyInjection/Logging/Options、xunit 2.9.3。

---

## 1. 测试先行：双定义宿主启停

- [ ] 1.1 写 `ServerGroupHostingTests` 双定义宿主启动测试

**Files:**
- Create: `tests/Galosys.Foundation.Primitives.Tests/ServerGroupHostingTests.cs`

**Step 1: 写入最小测试**

```csharp
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Net;
using Microsoft.Extensions.Options;

namespace Galosys.Foundation.Primitives.Tests;

public sealed class ServerGroupHostingTests
{
    [Fact]
    public async Task Host_StartAsync_BindsBothDefinitions_AndStopsThem()
    {
        using var host = BuildHost(
            EchoDefinition("first"),
            EchoDefinition("second"));

        await host.StartAsync();

        var group = host.Services.GetRequiredService<ChannelServerGroup>();
        Assert.Equal(2, group.Servers.Count);
        Assert.All(group.Servers, server => Assert.NotNull(server.LocalEndPoint));

        var firstEndPoint = (IPEndPoint)group.Servers[0].LocalEndPoint!;
        using (var client = await TestClient.ConnectAsync(firstEndPoint))
        {
            var stream = client.GetStream();
            await stream.WriteAsync("ping"u8.ToArray());
            var echoed = await TestClient.ReadExactlyAsync(stream, 4);
            Assert.Equal("ping", System.Text.Encoding.UTF8.GetString(echoed));
        }

        await host.StopAsync();
        Assert.All(group.Servers, server => Assert.Null(server.LocalEndPoint));
    }

    private static IHost BuildHost(params ChannelServerDefinition[] definitions)
    {
        var builder = Host.CreateApplicationBuilder();

        foreach (var definition in definitions)
            builder.Services.AddChannelServer(definition);

        builder.Services.AddChannelServerGroup();
        return builder.Build();
    }

    private static ChannelServerDefinition EchoDefinition(
        string name,
        int port = 0,
        TimeSpan? shutdownTimeout = null,
        int maxConnections = 0)
        => new(
            name,
            new ChannelServerOptions
            {
                EndPoint = new IPEndPoint(IPAddress.Loopback, port),
                ShutdownTimeout = shutdownTimeout ?? TimeSpan.FromSeconds(2),
                MaxConnections = maxConnections,
            },
            pipeline => pipeline.Run(TestHandlers.EchoAsync));
}
```

**Step 2: 运行测试，确认编译失败**

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter FullyQualifiedName~ServerGroupHostingTests`
Expected: 编译失败，提示 `ChannelServerDefinition` 或 `AddChannelServerGroup` 不存在（这是本任务的“红”）。

## 2. 实现定义、hosted service 与 DI 扩展

- [ ] 2.1 创建 `ChannelServerDefinition`

**Files:**
- Create: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/ChannelServerDefinition.cs`

```csharp
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>描述一个由宿主注册的服务器。</summary>
    public sealed class ChannelServerDefinition
    {
        /// <summary>创建服务器定义；options 会被快照。</summary>
        public ChannelServerDefinition(
            string name,
            ChannelServerOptions options,
            Action<IChannelPipeline> configurePipeline)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("名称不能为空。", nameof(name));

            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(configurePipeline);

            Name = name;
            Options = options.Clone();
            ConfigurePipeline = configurePipeline;
        }

        /// <summary>诊断名称，不要求全局唯一。</summary>
        public string Name { get; }

        /// <summary>定义构造时的 options 快照。</summary>
        public ChannelServerOptions Options { get; }

        /// <summary>每连接的 pipeline 配置回调。</summary>
        public Action<IChannelPipeline> ConfigurePipeline { get; }
    }
}
```

- [ ] 2.2 创建 `ChannelServerGroupHostedService`

**Files:**
- Create: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Hosting/ChannelServerGroupHostedService.cs`

```csharp
using Microsoft.Extensions.Hosting;

namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>把服务器组映射到宿主生命周期。</summary>
    public sealed class ChannelServerGroupHostedService : IHostedService, IAsyncDisposable
    {
        private readonly ChannelServerGroup _group;

        public ChannelServerGroupHostedService(ChannelServerGroup group)
        {
            ArgumentNullException.ThrowIfNull(group);
            _group = group;
        }

        public Task StartAsync(CancellationToken cancellationToken)
            => _group.StartAsync(cancellationToken);

        public Task StopAsync(CancellationToken cancellationToken)
            => _group.StopAsync(CancellationToken.None);

        public ValueTask DisposeAsync() => _group.DisposeAsync();
    }
}
```

- [ ] 2.3 创建 `AddChannelServer` 与 `AddChannelServerGroup`

**Files:**
- Create: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/DependencyInjection/ChannelServerGroupServiceCollectionExtensions.cs`

```csharp
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Net;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>多服务器宿主注册扩展。</summary>
    public static class ChannelServerGroupServiceCollectionExtensions
    {
        /// <summary>追加一个服务器定义；注册顺序即启动顺序。</summary>
        public static IServiceCollection AddChannelServer(
            this IServiceCollection services,
            ChannelServerDefinition definition)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(definition);

            services.AddSingleton(definition);
            return services;
        }

        /// <summary>注册一个由所有定义构建的服务器组及其 hosted service。</summary>
        public static IServiceCollection AddChannelServerGroup(this IServiceCollection services)
        {
            ArgumentNullException.ThrowIfNull(services);

            services.TryAddSingleton(sp =>
            {
                var definitions = sp.GetServices<ChannelServerDefinition>();
                var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
                var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

                var servers = definitions
                    .Select(definition => new ChannelServer(
                        definition.Options,
                        definition.ConfigurePipeline,
                        loggerFactory,
                        scopeFactory))
                    .ToArray();

                return new ChannelServerGroup(servers);
            });

            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IHostedService, ChannelServerGroupHostedService>());

            return services;
        }
    }
}
```

- [ ] 2.4 构建并跑首个宿主测试

Run: `dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`
Expected: 0 errors。

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter FullyQualifiedName~ServerGroupHostingTests`
Expected: `Host_StartAsync_BindsBothDefinitions_AndStopsThem` 通过。

## 3. 补齐注册与失败语义测试

- [ ] 3.1 增加注册顺序与独立 options 测试

**Files:**
- Modify: `tests/Galosys.Foundation.Primitives.Tests/ServerGroupHostingTests.cs`

```csharp
[Fact]
public async Task Host_UsesDefinitionOrder_AndIndependentOptions()
{
    var firstPort = GetFreePort();
    var secondPort = GetFreePort();

    using var host = BuildHost(
        EchoDefinition("first", firstPort, TimeSpan.FromSeconds(1), maxConnections: 1),
        EchoDefinition("second", secondPort, TimeSpan.FromSeconds(3), maxConnections: 0));

    await host.StartAsync();

    var group = host.Services.GetRequiredService<ChannelServerGroup>();
    Assert.Equal(firstPort, ((IPEndPoint)group.Servers[0].LocalEndPoint!).Port);
    Assert.Equal(secondPort, ((IPEndPoint)group.Servers[1].LocalEndPoint!).Port);

    await host.StopAsync();
}

private static int GetFreePort()
{
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    return ((IPEndPoint)listener.LocalEndpoint).Port;
}
```

- [ ] 3.2 增加缺少组注册与重复组注册测试

**Files:**
- Modify: `tests/Galosys.Foundation.Primitives.Tests/ServerGroupHostingTests.cs`

```csharp
[Fact]
public async Task Host_WithoutAddChannelServerGroup_DoesNotStartDefinitions()
{
    var builder = Host.CreateApplicationBuilder();
    builder.Services.AddChannelServer(EchoDefinition("orphan"));

    using var host = builder.Build();
    await host.StartAsync();

    Assert.Null(host.Services.GetService<ChannelServerGroup>());
    await host.StopAsync();
}

[Fact]
public async Task Host_DuplicateAddChannelServerGroup_RegistersOneHostedService()
{
    var builder = Host.CreateApplicationBuilder();
    builder.Services.AddChannelServer(EchoDefinition("first"));
    builder.Services.AddChannelServerGroup();
    builder.Services.AddChannelServerGroup();

    Assert.Equal(1, builder.Services.Count(descriptor =>
        descriptor.ServiceType == typeof(IHostedService) &&
        descriptor.ImplementationType == typeof(ChannelServerGroupHostedService)));

    using var host = builder.Build();
    await host.StartAsync();

    try
    {
        var group = host.Services.GetRequiredService<ChannelServerGroup>();
        Assert.Single(group.Servers);
    }
    finally
    {
        await host.StopAsync();
    }
}
```

- [ ] 3.3 增加 scope factory 传播测试

**Files:**
- Modify: `tests/Galosys.Foundation.Primitives.Tests/ServerGroupHostingTests.cs`

在文件顶部补 `using System.Collections.Concurrent;`，然后加入：

```csharp
[Fact]
public async Task Host_PropagatesScopeFactory_ToEveryDefinition()
{
    GroupScopedProbe.Reset();

    var builder = Host.CreateApplicationBuilder();
    builder.Services.AddScoped<GroupScopedProbe>();
    builder.Services.AddChannelServer(ScopedDefinition("first"));
    builder.Services.AddChannelServer(ScopedDefinition("second"));
    builder.Services.AddChannelServerGroup();

    using var host = builder.Build();
    await host.StartAsync();

    try
    {
        var group = host.Services.GetRequiredService<ChannelServerGroup>();
        using var first = await TestClient.ConnectAsync(
            (IPEndPoint)group.Servers[0].LocalEndPoint!);
        using var second = await TestClient.ConnectAsync(
            (IPEndPoint)group.Servers[1].LocalEndPoint!);

        await TestClient.WaitUntilAsync(
            () => Task.FromResult(
                GroupScopedProbe.Instances.Count == 2 &&
                GroupScopedProbe.DisposedCount == 2),
            "两个连接未创建并释放独立的 scoped 实例。");

        Assert.Equal(2, GroupScopedProbe.Instances.Distinct().Count());
    }
    finally
    {
        await host.StopAsync();
    }
}

private static ChannelServerDefinition ScopedDefinition(string name)
    => new(
        name,
        new ChannelServerOptions { EndPoint = new IPEndPoint(IPAddress.Loopback, 0) },
        pipeline => pipeline.Run(context =>
        {
            GroupScopedProbe.Instances.Add(
                context.Services.GetRequiredService<GroupScopedProbe>());
            return Task.CompletedTask;
        }));

private sealed class GroupScopedProbe : IDisposable
{
    public static readonly ConcurrentBag<GroupScopedProbe> Instances = new();

    private static int _disposed;

    public static int DisposedCount => Volatile.Read(ref _disposed);

    public static void Reset()
    {
        Instances.Clear();
        Interlocked.Exchange(ref _disposed, 0);
    }

    public void Dispose() => Interlocked.Increment(ref _disposed);
}
```

- [ ] 3.4 增加启动失败清理测试

**Files:**
- Modify: `tests/Galosys.Foundation.Primitives.Tests/ServerGroupHostingTests.cs`

```csharp
[Fact]
public async Task Host_StartupFailure_RollsBackPreviouslyStartedServers()
{
    using var blocker = new TcpListener(IPAddress.Loopback, 0);
    blocker.Start();
    var busyPort = ((IPEndPoint)blocker.LocalEndpoint).Port;

    var first = EchoDefinition("first");
    var second = new ChannelServerDefinition(
        "second",
        new ChannelServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, busyPort),
        },
        pipeline => pipeline.Run(TestHandlers.EchoAsync));

    using var host = BuildHost(first, second);

    await Assert.ThrowsAsync<SocketException>(() => host.StartAsync());

    var group = host.Services.GetRequiredService<ChannelServerGroup>();
    Assert.Null(group.Servers[0].LocalEndPoint);
    Assert.Null(group.Servers[1].LocalEndPoint);
}
```

- [ ] 3.5 跑宿主测试全集

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter FullyQualifiedName~ServerGroupHostingTests`
Expected: 全部 `ServerGroupHostingTests` 通过。

## 4. README 与最终验证

- [ ] 4.1 更新 Primitives README 的多服务器宿主注册章节

**Files:**
- Modify: `src/Galosys.Foundation.Primitives/README.md`

在“多服务器组合”之后增加：

    ## 多服务器宿主注册

    ```csharp
    var builder = Host.CreateApplicationBuilder(args);

    builder.Services.AddChannelServer(new ChannelServerDefinition(
        "http",
        new ChannelServerOptions { EndPoint = new IPEndPoint(IPAddress.Any, 8080) },
        pipeline => pipeline.Run(HttpHandler)));

    builder.Services.AddChannelServer(new ChannelServerDefinition(
        "mqtt",
        new ChannelServerOptions { EndPoint = new IPEndPoint(IPAddress.Any, 1883) },
        pipeline => pipeline.Run(MqttHandler)));

    builder.Services.AddChannelServerGroup();

    var host = builder.Build();
    host.Run();
    ```

    注册顺序就是启动顺序；`ChannelServerGroupHostedService` 在宿主启动时按该顺序启动组，停机时并行停止。
    重复调用 `AddChannelServerGroup()` 是幂等的；只注册定义而不调用它时，定义不会监听。
    每个定义保留自己的 `ChannelServerOptions`，不存在全局 `ChannelServerOptions` 注册。
    每连接作用域来自容器的 `IServiceScopeFactory`；宿主 `ShutdownTimeout` 应大于最大的 server `ShutdownTimeout`。

- [ ] 4.2 构建并跑全量测试

Run: `dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`
Expected: 0 errors。

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj`
Expected: 现有测试与新增 `ServerGroupHostingTests` 全部通过。

- [ ] 4.3 校验 OpenSpec artifact

Run: `openspec validate "primitives-server-group-2" --strict --no-interactive`
Expected: `Change 'primitives-server-group-2' is valid`。
