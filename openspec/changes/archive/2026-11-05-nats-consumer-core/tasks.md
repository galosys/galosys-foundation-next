# NATS 消费者核心能力 Implementation Plan

> **REQUIRED SUB-SKILL:** 使用 `opsx-apply-x` 或 `executing-plans` 逐任务执行本计划。
> 本仓库虽有 git，但按约定不自动提交；步骤中的"验证"统一用构建 + 端到端运行，不执行 git commit。

**Goal:** 把 `Galosys.Foundation.Nats.Net` 从半成品原型补成可用的 Core NATS 消费者框架：`[NatsConsumer]` 特性 → 扫描 → 按 subject 订阅 → 反序列化 → 派发给 `IMessageHandler<T>`。

**Architecture:** `NatsConsumerAttribute`（`NATS.Net`，与上游 `NatsClient` 共享命名空间，对齐 `AmqpHandlerAttribute`/`RabbitMQ.Client` 约定）声明 subject/queue group；`ServiceProviderExtensions.RegisterHandlerAsync` 扫描程序集上下文，订阅 `byte[]` 负载，用 `IObjectSerializer` 按运行时类型反序列化，通过缓存的泛型 `MethodInfo` 反射派发给 handler；`NatsBootstrapper`（`Microsoft.Extensions.Hosting`，`IHostedLifecycleService`）在 `StartingAsync` 建订阅、在 `StoppingAsync` 取消消费循环；`NatsServiceCollectionExtensions.AddNats` 绑定 `Nats` 配置节；`InternalModule` 让模块化宿主自动接线。

**Tech Stack:** .NET 10、`nats.net` 3.3.1（`NATS.Client.Core`）、`galosys.foundation.core` 26.10.29.1（`IObjectSerializer`、`IMessageHandler`、`MessageBase`、`ConcurrentRunner`、`IModule`）、`Microsoft.Extensions.Hosting` / `Options` / `DependencyInjection`。

**规格依据:** `openspec/changes/nats-consumer-core/specs/nats-consumer/spec.md`（新能力）、`.../specs/foundation-namespace-conventions/spec.md`、`.../specs/repository-layout/spec.md`。
**设计依据:** `openspec/changes/nats-consumer-core/design.md`（决策 1~8）。

---

## 1. 公共表面：特性与选项类型

- [x] 1.1 把 `NatsConsumerAttribute` 移到 `NATS.Net/` 目录并补 XML 文档（命名空间保持 `NATS.Net`）

**Files:**
- Create: `src/Galosys.Foundation.Nats.Net/NATS.Net/NatsConsumerAttribute.cs`
- Delete: `src/Galosys.Foundation.Nats.Net/NatsConsumerAttribute.cs`

**Step 1: 建立 `NATS.Net/` 目录并写入特性类型**

命名空间保持 `NATS.Net` 不变 —— 上游 `NATS.Client.Simplified` 已占用该命名空间（`NATS.Net.NatsClient` 等），
C# 允许跨程序集合并同一命名空间，且这与 `galosys.foundation.amqp` 的 `AmqpHandlerAttribute`
声明在 `RabbitMQ.Client` 完全同构。文件位置改为 `NATS.Net/` 目录以满足“目录与命名空间一致”。

```csharp
using System;

namespace NATS.Net
{
    /// <summary>
    /// 标记一个消息处理方法为 NATS 消费者，声明其订阅的 subject 与可选的队列组。
    /// </summary>
    /// <remarks>
    /// 可重复标注在同一方法上以声明多个订阅；每个标记各自建立独立订阅。
    /// </remarks>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
    public sealed class NatsConsumerAttribute : Attribute
    {
        /// <summary>
        /// 初始化消费者标记。
        /// </summary>
        /// <param name="subject">要订阅的 NATS subject。</param>
        /// <param name="queueGroup">可选的队列组；为 null 时表示不加入队列组（广播语义）。</param>
        public NatsConsumerAttribute(string subject, string? queueGroup = null)
        {
            Subject = subject;
            QueueGroup = queueGroup;
        }

        /// <summary>
        /// 要订阅的 NATS subject。
        /// </summary>
        public string Subject { get; }

        /// <summary>
        /// 队列组名称；为 null 时表示不加入队列组。
        /// </summary>
        public string? QueueGroup { get; }
    }
}
```

**Step 2: 删除旧文件**

删除 `src/Galosys.Foundation.Nats.Net/NatsConsumerAttribute.cs`（项目根目录下的旧位置）。
命名空间与公共 API 均不变，因此无需改动任何调用方；仓库内除
`samples/NatsSample.ConsoleApp/Adapter/Handlers/SampleGreatHandler.cs` 的注释外无其它引用，
示例的引用在任务 4.2 启用。

**Step 3: 验证命名空间与警告**

Run: `dotnet build src/Galosys.Foundation.Nats.Net/Galosys.Foundation.Nats.Net.csproj`
Expected: 0 errors；输出中不再出现 `NatsConsumerAttribute.cs` 的 CS1591 警告。

Run: `Select-String -Path src/Galosys.Foundation.Nats.Net/NATS.Net/NatsConsumerAttribute.cs -Pattern "namespace NATS.Net"`
Expected: 有匹配（命名空间保持 `NATS.Net`，与上游 `NatsClient` 共享，对齐 AMQP 约定）。

- [x] 1.2 实现 `NatsOptions` 与 `NatsHandlerProperty`

**Files:**
- Modify: `src/Galosys.Foundation.Nats.Net/Microsoft/Extensions/Options/NatsOptions.cs`

**Step 1: 写入选项类型**

文件内容替换为：

```csharp
namespace Microsoft.Extensions.Options
{
    /// <summary>
    /// NATS 消费者选项。仅承载本框架自身的关注点；连接参数（地址、认证、TLS）由上游
    /// <c>NATS.Client.Core.NatsOpts</c> 经 <c>AddNatsClient</c> 管理，不在此重复。
    /// </summary>
    public sealed class NatsOptions
    {
        /// <summary>
        /// 每个 subject 的并发派发数，默认 1。
        /// </summary>
        public int Concurrency { get; set; } = 1;

        /// <summary>
        /// 处理器抛出异常后的最大重试次数，默认 0（不重试）。
        /// </summary>
        public int MaxRetryCount { get; set; }

        /// <summary>
        /// 重试基础延迟（毫秒），默认 100；第 n 次重试的延迟为该值乘以 2 的 n 次方。
        /// </summary>
        public int RetryBaseDelayMs { get; set; } = 100;

        /// <summary>
        /// 按 subject 覆盖的处理器设置。键为 subject。
        /// </summary>
        public IDictionary<string, NatsHandlerProperty> Handlers { get; set; }
            = new Dictionary<string, NatsHandlerProperty>(StringComparer.Ordinal);
    }

    /// <summary>
    /// 单个 subject 的消费者设置，用于覆盖 <see cref="NatsOptions"/> 的全局值。
    /// </summary>
    public sealed class NatsHandlerProperty
    {
        /// <summary>
        /// 覆盖该 subject 的并发派发数。
        /// </summary>
        public int? Concurrency { get; set; }

        /// <summary>
        /// 覆盖该 subject 的最大重试次数。
        /// </summary>
        public int? MaxRetryCount { get; set; }

        /// <summary>
        /// 覆盖该 subject 的重试基础延迟（毫秒）。
        /// </summary>
        public int? RetryBaseDelayMs { get; set; }
    }
}
```

**Step 2: 补 `using`**

文件顶部加 `using System;` 与 `using System.Collections.Generic;`。

**Step 3: 验证**

Run: `dotnet build src/Galosys.Foundation.Nats.Net/Galosys.Foundation.Nats.Net.csproj`
Expected: 0 errors。

Run: `Select-String -Path src/Galosys.Foundation.Nats.Net/Microsoft/Extensions/Options/NatsOptions.cs -Pattern "internal class NatsOptions"`
Expected: 无匹配（必须是 `public`，外部宿主才能绑定配置节）。

## 2. 扫描、订阅、反序列化与派发

- [x] 2.1 实现消费者注册扫描与订阅描述

**Files:**
- Modify: `src/Galosys.Foundation.Nats.Net/Microsoft/Extensions/DependencyInjection/ServiceProviderExtensions.cs`

**Step 1: 定义订阅描述类型**

`NatsSubscription` 放在 `Galosys.Foundation.Nats.Net` 命名空间（与 `NatsConsumerDispatcher`、`InternalModule` 同处），
以便派发器与启动器共用。它是 `internal` 类型，因此**不能**出现在 `public` 扩展方法的签名里 ——
下面 Step 2 的 `RegisterHandlers` 必须同时是 `internal`，否则会触发 CS0050（不一致的可访问性）。

文件顶部需同时保留 `using NATS.Net;`（取 `NatsConsumerAttribute`）
与 `using Galosys.Foundation.Nats.Net;`（取 `NatsSubscription`）。

```csharp
namespace Galosys.Foundation.Nats.Net
{
    /// <summary>
    /// 一个已建立的 NATS 订阅描述。供启动器持有并驱动其消费循环。
    /// </summary>
    internal sealed record NatsSubscription(
        string Subject,
        string? QueueGroup,
        MethodInfo HandlerMethod,
        Type MessageType,
        MethodInfo InvokableHandleAsync,
        int Concurrency,
        int MaxRetryCount,
        int RetryBaseDelayMs);
}
```

**Step 2: 重写扫描入口**

替换现有的 `RegisterAllHandlersAsync` / `RegisterHandlerAsync<T>` / `RegisterHandlerAsync(params Type[])` 三个方法，改为返回订阅描述集合：

```csharp
/// <summary>
/// 扫描并注册所有被 <see cref="NatsConsumerAttribute"/> 标记的处理器，返回待建立的订阅描述。
/// </summary>
/// <param name="root">根服务提供者。</param>
/// <param name="handlerTypes">限定扫描的类型；为空表示扫描全部。</param>
/// <returns>待建立的订阅描述集合。</returns>
internal static IReadOnlyList<NatsSubscription> RegisterHandlers(
    this IServiceProvider root,
    params Type[] handlerTypes)
{
    var context = DependencyContext.Default;
    if (context is null)
    {
        root.GetService<ILogger<INatsConnection>>()
            ?.LogWarning("DependencyContext 不可用，跳过 NATS 消费者扫描。");
        return Array.Empty<NatsSubscription>();
    }

    var candidates = context.GetImplementTypes();
    if (handlerTypes is { Length: > 0 })
        candidates = candidates.Where(t => handlerTypes.Contains(t));

    var marked = candidates
        .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => (Method: m, Attributes: m.GetCustomAttributes<NatsConsumerAttribute>(false).ToArray())))
        .Where(x => x.Attributes.Length > 0)
        .ToArray();

    if (marked.Length == 0)
        return Array.Empty<NatsSubscription>();

    var options = root.GetRequiredService<IOptionsMonitor<NatsOptions>>().CurrentValue;
    var logger = root.GetService<ILogger<NatsBootstrapper>>()
        ?? (ILogger?)root.GetService<ILogger<INatsConnection>>();

    var subscriptions = new List<NatsSubscription>();

    foreach (var (method, attributes) in marked)
    {
        var parameters = method.GetParameters();
        if (parameters.Length == 0)
        {
            logger?.LogWarning(
                "NATS 消费者方法 {Method} 没有形参，跳过。", method.DeclaringType?.FullName);
            continue;
        }

        var messageType = parameters[0].ParameterType;

        foreach (var attribute in attributes)
        {
            if (string.IsNullOrWhiteSpace(attribute.Subject))
            {
                logger?.LogWarning(
                    "NATS 消费者方法 {Method} 的 subject 为空，跳过。", method.DeclaringType?.FullName);
                continue;
            }

            var key = (method, attribute.Subject, attribute.QueueGroup);
            if (_handlersRegistered.ContainsKey(key))
                continue;

            var handleAsync = ResolveHandleAsync(method, messageType, logger);
            if (handleAsync is null)
                continue;

            var property = ResolveHandlerProperty(options, attribute.Subject);

            subscriptions.Add(new NatsSubscription(
                attribute.Subject,
                attribute.QueueGroup,
                method,
                messageType,
                handleAsync,
                property.Concurrency,
                property.MaxRetryCount,
                property.RetryBaseDelayMs));

            _handlersRegistered.TryAdd(key, DateTime.UtcNow);
        }
    }

    return subscriptions;
}
```

**Step 3: 补辅助方法**

```csharp
private static readonly ConcurrentDictionary<(MethodInfo Method, string Subject, string? QueueGroup), DateTime>
    _handlersRegistered = new();

private static MethodInfo? ResolveHandleAsync(
    MethodInfo method,
    Type messageType,
    ILogger? logger)
{
    var handleAsync = typeof(IMessageHandler<>).MakeGenericType(messageType)
        .GetMethod(nameof(IMessageHandler<object>.HandleAsync));

    if (handleAsync is not null)
        return handleAsync;

    logger?.LogWarning(
        "NATS 消费者方法 {Method} 的形参类型 {MessageType} 未实现 IMessageHandler<T>，跳过。",
        method.DeclaringType?.FullName,
        messageType.FullName);

    return null;
}

private static (int Concurrency, int MaxRetryCount, int RetryBaseDelayMs) ResolveHandlerProperty(
    NatsOptions options,
    string subject)
{
    var concurrency = options.Concurrency;
    var maxRetry = options.MaxRetryCount;
    var retryDelay = options.RetryBaseDelayMs;

    if (options.Handlers.TryGetValue(subject, out var property) && property is not null)
    {
        concurrency = property.Concurrency ?? concurrency;
        maxRetry = property.MaxRetryCount ?? maxRetry;
        retryDelay = property.RetryBaseDelayMs ?? retryDelay;
    }

    return (Math.Max(concurrency, 1), Math.Max(maxRetry, 0), Math.Max(retryDelay, 0));
}
```

**Step 4: 清理旧代码**

删除原文件中硬编码 `"sample.greet"` / `"nats-sample-console-app"` 的订阅块、`await Task.CompletedTask` 占位、`ex` 未使用变量、以及注释掉的 `logger?.LogError` 调用。

**Step 5: 补 `using`**

文件顶部需要：`System`、`System.Collections.Concurrent`、`System.Collections.Generic`、`System.Linq`、`System.Reflection`、`System.Threading.Tasks`、`Galosys.Foundation.Core`、`Galosys.Foundation.Nats.Net`、`Microsoft.Extensions.DependencyModel`、`Microsoft.Extensions.Logging`、`Microsoft.Extensions.Messaging`、`Microsoft.Extensions.Options`、`NATS.Client.Core`、`NATS.Net`。

其中 `NATS.Net` 提供 `NatsConsumerAttribute`（原文件第 7 行已有，保留即可），
`Galosys.Foundation.Nats.Net` 提供 `NatsSubscription`。

**注意：`NatsBootstrapper` 在 `Microsoft.Extensions.Hosting` 命名空间**，因此上面的
`ILogger<NatsBootstrapper>` 需要 `using Microsoft.Extensions.Hosting;`。若不想引入该 using，
改用 `ILogger<INatsConnection>`（`NATS.Client.Core` 已引入）即可。

**Step 6: 验证**

Run: `dotnet build src/Galosys.Foundation.Nats.Net/Galosys.Foundation.Nats.Net.csproj`
Expected: 0 errors；不再出现 CS8604（`DependencyContext` 可空）与 CS0168（`ex` 未使用）警告。

Run: `Select-String -Path src/Galosys.Foundation.Nats.Net/Microsoft/Extensions/DependencyInjection/ServiceProviderExtensions.cs -Pattern "sample.greet"`
Expected: 无匹配（硬编码 subject 已移除）。

- [x] 2.2 实现消费循环、负载反序列化与 handler 派发

**Files:**
- Create: `src/Galosys.Foundation.Nats.Net/Galosys/Foundation/Nats/Net/NatsConsumerDispatcher.cs`
- Modify: `src/Galosys.Foundation.Nats.Net/Microsoft/Extensions/DependencyInjection/ServiceProviderExtensions.cs`

**Step 1: 实现派发器**

```csharp
using System.Reflection;
using System.Text;
using Galosys.Foundation.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Messaging;
using NATS.Client.Core;

namespace Galosys.Foundation.Nats.Net
{
    /// <summary>
    /// NATS 消费者派发器：把收到的消息负载反序列化为处理器形参类型并调用处理器。
    /// </summary>
    internal sealed class NatsConsumerDispatcher(
        IServiceProvider root,
        IObjectSerializer serializer,
        ILogger logger)
    {
        private readonly IServiceProvider _root = root;
        private readonly IObjectSerializer _serializer = serializer;
        private readonly ILogger _logger = logger;

        /// <summary>
        /// 处理一条消息：反序列化、解析处理器实例、调用处理器方法。
        /// </summary>
        /// <param name="subscription">订阅描述。</param>
        /// <param name="payload">消息原始负载。</param>
        /// <returns>处理是否成功。</returns>
        public async Task<bool> DispatchAsync(NatsSubscription subscription, byte[] payload)
        {
            object? message;

            try
            {
                message = _serializer.Deserialize(
                    Encoding.UTF8.GetString(payload),
                    subscription.MessageType);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "NATS 消息反序列化失败: subject={Subject}, type={Type}",
                    subscription.Subject,
                    subscription.MessageType.FullName);
                return false;
            }

            if (message is null)
            {
                _logger.LogWarning(
                    "NATS 消息反序列化结果为 null: subject={Subject}, type={Type}",
                    subscription.Subject,
                    subscription.MessageType.FullName);
                return false;
            }

            using var scope = _root.CreateScope();

            object handler;
            try
            {
                handler = scope.ServiceProvider.GetRequiredService(
                    subscription.HandlerMethod.DeclaringType
                    ?? throw new InvalidOperationException("消费者方法没有声明类型。"));
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "NATS 消费者类型解析失败: subject={Subject}, type={Type}",
                    subscription.Subject,
                    subscription.HandlerMethod.DeclaringType?.FullName);
                throw;
            }

            var arguments = new[] { message };

            try
            {
                // InvokableHandleAsync 是 IMessageHandler<T>.HandleAsync 的泛型 MethodInfo，
                // 由 ResolveHandleAsync 用 MakeGenericType 预先构造并缓存，不在每条消息上计算。
                var result = subscription.InvokableHandleAsync.Invoke(handler, arguments);
                if (result is Task<bool> task)
                    return await task.ConfigureAwait(false);

                return true;
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                _logger.LogError(
                    ex.InnerException,
                    "NATS 消费者处理失败: subject={Subject}, type={Type}",
                    subscription.Subject,
                    subscription.HandlerMethod.DeclaringType?.FullName);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "NATS 消费者处理失败: subject={Subject}, type={Type}",
                    subscription.Subject,
                    subscription.HandlerMethod.DeclaringType?.FullName);
                return false;
            }
        }
    }
}
```

**Step 2: 确认订阅描述的命名空间**

`NatsSubscription` 已在任务 2.1 定义于 `Galosys.Foundation.Nats.Net` 命名空间，
派发器与其同命名空间，可直接引用，无需额外 `using`。
`ServiceProviderExtensions.cs` 顶部需保留 `using Galosys.Foundation.Nats.Net;`（取 `NatsSubscription`）
与 `using NATS.Net;`（取 `NatsConsumerAttribute`）。

Run: `dotnet build src/Galosys.Foundation.Nats.Net/Galosys.Foundation.Nats.Net.csproj`
Expected: 0 errors。

**Step 3: 实现重试**

在 `NatsConsumerDispatcher` 中新增：

```csharp
/// <summary>
/// 按配置的次数重试执行处理器。
/// </summary>
/// <param name="subscription">订阅描述。</param>
/// <param name="payload">消息原始负载。</param>
/// <returns>处理是否成功。</returns>
public async Task<bool> DispatchWithRetryAsync(NatsSubscription subscription, byte[] payload)
{
    var attempt = 0;

    while (true)
    {
        if (await DispatchAsync(subscription, payload).ConfigureAwait(false))
            return true;

        if (attempt >= subscription.MaxRetryCount)
            return false;

        var delay = subscription.RetryBaseDelayMs * (int)Math.Pow(2, attempt);
        attempt++;

        _logger.LogWarning(
            "NATS 消费者重试: subject={Subject}, attempt={Attempt}, delayMs={Delay}",
            subscription.Subject,
            attempt,
            delay);

        await Task.Delay(delay).ConfigureAwait(false);
    }
}
```

**Step 4: 验证**

Run: `dotnet build src/Galosys.Foundation.Nats.Net/Galosys.Foundation.Nats.Net.csproj`
Expected: 0 errors。

## 3. 宿主生命周期与 DI 接线

- [x] 3.1 实现 `NatsBootstrapper`

**Files:**
- Create: `src/Galosys.Foundation.Nats.Net/Microsoft/Extensions/Hosting/NatsBootstrapper.cs`
- Delete: `src/Galosys.Foundation.Nats.Net/NatsConsumerBootstrapper.cs`

**Step 1: 删除旧文件**

删除 `src/Galosys.Foundation.Nats.Net/NatsConsumerBootstrapper.cs`（整个文件被注释掉的原型）。

**Step 2: 写入启动器**

```csharp
using System.Threading;
using System.Threading.Tasks;
using Galosys.Foundation.Core;
using Galosys.Foundation.Nats.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NATS.Client.Core;

namespace Microsoft.Extensions.Hosting
{
    /// <summary>
    /// NATS 消费者启动器：负责扫描消费者标记、建立订阅，并在宿主停机时取消所有消费循环。
    /// </summary>
    public sealed class NatsBootstrapper : IHostedLifecycleService
    {
        private readonly IServiceProvider _root;
        private readonly ILogger<NatsBootstrapper> _logger;
        private readonly CancellationTokenSource _stopping = new();
        private readonly List<IConcurrentRunner> _runners = new();

        public NatsBootstrapper(IServiceProvider root, ILogger<NatsBootstrapper> logger)
        {
            _root = root;
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task StartingAsync(CancellationToken cancellationToken)
        {
            var connection = _root.GetService<INatsConnection>();
            if (connection is null)
            {
                throw new InvalidOperationException(
                    "未注册 NATS 客户端（INatsConnection）。请先调用 services.AddNatsClient(...)。");
            }

            var subscriptions = _root.RegisterHandlers();
            if (subscriptions.Count == 0)
            {
                _logger.LogInformation("未发现带 [NatsConsumer] 标记的处理器。");
                return;
            }

            // IObjectSerializer 由 AddCore() 内的 AddObjectSerializer() 注册（JSON 实现）。
            // 若宿主未注册 core 服务，GetRequiredService 会抛异常 —— 这是明确的失败，
            // 好过静默用错序列化器。

            var serializer = _root.GetRequiredService<IObjectSerializer>();
            var dispatcher = new NatsConsumerDispatcher(_root, serializer, _logger);

            foreach (var subscription in subscriptions)
            {
                StartSubscription(connection, subscription, dispatcher);
            }

            _logger.LogInformation(
                "NATS 消费者启动完成，共 {Count} 个订阅。", subscriptions.Count);
        }

        /// <inheritdoc />
        public async Task StoppingAsync(CancellationToken cancellationToken)
        {
            if (!_stopping.IsCancellationRequested)
                _stopping.Cancel();

            foreach (var runner in _runners)
            {
                await runner.StopAsync(cancellationToken).ConfigureAwait(false);
            }

            _runners.Clear();
        }

        /// <inheritdoc />
        public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        /// <inheritdoc />
        public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        /// <inheritdoc />
        public Task StopAsync(CancellationToken cancellationToken) => StoppingAsync(cancellationToken);

        private void StartSubscription(
            INatsConnection connection,
            NatsSubscription subscription,
            NatsConsumerDispatcher dispatcher)
        {
            // Concurrency = N 时建立 N 个独立 runner（每个一个 worker），各自订阅同一 subject 与
            // queue group。不能用单个 N worker 的 runner：RunnerSlot 会反复调用 task，而
            // await foreach 只在取消时结束，单 runner 只会真正消费第一个 worker。
            for (var i = 0; i < subscription.Concurrency; i++)
            {
                var runner = ConcurrentRunner.CreateTaskRunner(1);
                _runners.Add(runner);

                runner.Start(ct => ConsumeAsync(connection, subscription, dispatcher, ct));
            }

            _logger.LogInformation(
                "NATS 订阅已建立: subject={Subject}, queueGroup={QueueGroup}, concurrency={Concurrency}",
                subscription.Subject,
                subscription.QueueGroup,
                subscription.Concurrency);
        }

        private async Task ConsumeAsync(
            INatsConnection connection,
            NatsSubscription subscription,
            NatsConsumerDispatcher dispatcher,
            CancellationToken ct)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token, ct);

            await foreach (var msg in connection.SubscribeAsync<byte[]>(
                subscription.Subject,
                queueGroup: subscription.QueueGroup,
                cancellationToken: linked.Token).ConfigureAwait(false))
            {
                var payload = msg.Data;
                if (payload is null)
                    continue;

                await dispatcher.DispatchWithRetryAsync(subscription, payload).ConfigureAwait(false);
            }
        }
    }
}
```

**Step 3: 修正 `using` 与命名空间引用**

`ConcurrentRunner` / `IConcurrentRunner` 位于 `System.Threading.Tasks` 命名空间（`galosys.foundation.core`），
需加 `using System.Threading.Tasks;`；`CancellationTokenSource` 需加 `using System.Threading;`。
`NatsSubscription` 位于 `Galosys.Foundation.Nats.Net`，已加。

**注意 `ConcurrentRunner` 的调用语义**（已核对 `galosys.foundation.core` 实现）：`RunnerSlot.RunAsync`
的循环体是 `while (!cancelled) { await _task(token); }` —— **task 会被反复调用直到取消**。

因此：

- task 体内的 `await foreach` 只能在取消时结束。订阅被取消会抛 `OperationCanceledException`，
  被 `RunnerSlot` 捕获并置为 `Stopped`，不会进入下一次调用。若订阅因其它原因正常结束，
  slot 会再次调用 task → 再次订阅，形成无限重订阅。
- task 体内抛出的非取消异常会使 slot 变为 `Faulted` 并停止。派发器已吸收 handler 异常
  （见任务 2.2），不会逃逸到 slot。
- `Concurrency = N` 用 **N 个独立 runner**（每个 1 worker），而不是单个 N worker 的 runner。

**Step 3.1: 确认 `IHostedLifecycleService` 可用**

`IHostedLifecycleService` 定义在 `Microsoft.Extensions.Hosting`（`Microsoft.Extensions.Hosting.Abstractions`），
同时继承 `IHostedService`，因此必须实现 `StartAsync` / `StopAsync`。上面的实现已包含。
若 `Galosys.Foundation.Nats.Net.csproj` 编译报找不到该接口，在 `.csproj` 增加
`<PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" />`
并在根 `Directory.Packages.props` 中按现有版本风格补 `PackageVersion`。

**Step 4: 验证**

Run: `dotnet build src/Galosys.Foundation.Nats.Net/Galosys.Foundation.Nats.Net.csproj`
Expected: 0 errors。

Run: `Select-String -Path src/Galosys.Foundation.Nats.Net -Pattern "NatsSample.ConsoleApp"`
Expected: 无匹配（旧原型文件的错误命名空间 `NatsSample.ConsoleApp.Infrastructure` 已删除）。

- [x] 3.2 实现 `AddNats` 注册扩展

**Files:**
- Create: `src/Galosys.Foundation.Nats.Net/Microsoft/Extensions/DependencyInjection/NatsServiceCollectionExtensions.cs`

**Step 1: 写入扩展方法**

```csharp
using Galosys.Foundation.Nats.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>
    /// NATS 消费者服务注册扩展方法。
    /// </summary>
    public static class NatsServiceCollectionExtensions
    {
        /// <summary>
        /// 绑定 NATS 消费者选项并注册消费者生命周期。
        /// </summary>
        /// <param name="services">服务集合。</param>
        /// <param name="configuration">配置源；消费者选项绑定到 <c>Nats</c> 配置节。</param>
        /// <returns>同一个服务集合，便于链式调用。</returns>
        public static IServiceCollection AddNats(
            this IServiceCollection services,
            IConfiguration configuration)
        {
            services.AddOptions<NatsOptions>()
                .Bind(configuration.GetSection("Nats"));
            services.AddNats();
            return services;
        }

        /// <summary>
        /// 注册消费者生命周期，不绑定配置。
        /// </summary>
        /// <param name="services">服务集合。</param>
        /// <returns>同一个服务集合，便于链式调用。</returns>
        public static IServiceCollection AddNats(this IServiceCollection services)
        {
            services.AddOptions<NatsOptions>();
            services.AddHostedService<NatsBootstrapper>();
            return services;
        }
    }
}
```

**Step 2: 验证幂等性**

`AddHostedService<T>` 使用 `TryAddEnumerable`，同一 `T` 重复调用不会产生重复注册。
`AddOptions<NatsOptions>().Bind(...)` 在重复调用时也会重复绑定同一配置节 —— 由于绑定的是同一节、值相同，
实际效果一致；`IOptionsMonitor<NatsOptions>.CurrentValue` 得到的仍是同一份配置。

`Bind` 扩展来自 `Microsoft.Extensions.Options.ConfigurationExtensions`，需确认它在依赖图中
（`Microsoft.Extensions.Hosting` 已传递引入 `Microsoft.Extensions.Options` 与
`Microsoft.Extensions.Options.ConfigurationExtensions`）。若编译报找不到 `Bind`，在
`Galosys.Foundation.Nats.Net.csproj` 增加
`<PackageReference Include="Microsoft.Extensions.Options.ConfigurationExtensions" />`
并在根 `Directory.Packages.props` 中按现有版本风格补 `PackageVersion`。

Run: `dotnet build src/Galosys.Foundation.Nats.Net/Galosys.Foundation.Nats.Net.csproj`
Expected: 0 errors。

- [x] 3.3 实现 `InternalModule` 供模块化宿主自动接线

**Files:**
- Create: `src/Galosys.Foundation.Nats.Net/Galosys/Foundation/Nats/Net/InternalModule.cs`

**Step 1: 写入模块**

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Galosys.Foundation.Nats.Net
{
    /// <summary>
    /// NATS 消费者模块。启用模块化启动的宿主无需手工调用 <c>AddNats</c> 即可获得消费者功能。
    /// </summary>
    internal sealed class InternalModule : IModule
    {
        /// <inheritdoc />
        public void ConfigureServices(IServiceCollection services, HostBuilderContext context)
        {
            services.AddNats(context.Configuration);
        }
    }
}
```

**Step 2: 验证**

Run: `dotnet build src/Galosys.Foundation.Nats.Net/Galosys.Foundation.Nats.Net.csproj`
Expected: 0 errors。

Run: `Select-String -Path src/Galosys.Foundation.Nats.Net/Galosys/Foundation/Nats/Net/InternalModule.cs -Pattern "using Microsoft.Extensions.DependencyInjection"`
Expected: 有匹配（`IModule` 位于该命名空间，不在 `Galosys.Foundation.Core`）。

## 4. 示例接线

- [x] 4.1 把 NATS 连接参数迁到 `appsettings.json` 的 `Nats` 节

**Files:**
- Modify: `samples/NatsSample.ConsoleApp/appsettings.json`
- Modify: `samples/NatsSample.ConsoleApp/Program.cs`

**Step 1: 写配置节**

`appsettings.json` 内容改为：

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.Hosting.Lifetime": "Information"
    }
  },
  "Nats": {
    "Concurrency": 1,
    "MaxRetryCount": 0,
    "RetryBaseDelayMs": 100,
    "Url": "nats://127.0.0.1:4222",
    "Username": "appuser",
    "Password": "AppPass123"
  }
}
```

**说明：** 只用 `Nats` 一个节。`AddNats` 绑定的是 `GetSection("Nats")`，因此本框架的
`NatsOptions`（`Concurrency` / `MaxRetryCount` / `RetryBaseDelayMs`）从该节读取。
`NatsOptions` 不含 `Url`/`Username`/`Password`，绑定时这三个键会被忽略；它们由 `Program.cs`
读取后传给上游的 `AddNatsClient`（连接参数由上游 `NatsOpts` 管理，不在本框架的选项中重复）。

**Step 2: 改 `Program.cs`**

```csharp
using NATS.Extensions.Microsoft.DependencyInjection;

var builder = Host.CreateDefaultBuilder(args);
builder.UseModularization();
builder.ConfigureServices(services =>
{
    var nats = builder.Configuration.GetSection("Nats");

    services.AddNatsClient(natsOpts =>
    {
        natsOpts.ConfigureOptions(optsBuilder => optsBuilder.Configure(o => o.Opts = o.Opts with
        {
            Url = nats["Url"] ?? "nats://127.0.0.1:4222",
            AuthOpts = new NATS.Client.Core.NatsAuthOpts
            {
                Username = nats["Username"],
                Password = nats["Password"]
            }
        }));
    });
});

var host = builder.Build();
host.Run();
```

**说明：** `AddNats`（消费者注册）由 `UseModularization()` 发现的 `InternalModule` 自动完成，无需在此重复调用。

**Step 3: 验证**

Run: `dotnet build samples/NatsSample.ConsoleApp/NatsSample.ConsoleApp.csproj`
Expected: 0 errors。

- [x] 4.2 启用消费者特性并实现处理器

**Files:**
- Modify: `samples/NatsSample.ConsoleApp/Adapter/Handlers/SampleGreatHandler.cs`

**Step 1: 写入处理器**

```csharp
using Microsoft.Extensions.Messaging;
using NATS.Net;

namespace NatsSample.ConsoleApp.Adapter.Handlers
{
    [Handler]
    internal class SampleGreatHandler
        : IMessageHandler<SampleGreatEvent>
    {
        private readonly ILogger<SampleGreatHandler> _logger;

        public SampleGreatHandler(ILogger<SampleGreatHandler> logger)
        {
            _logger = logger;
        }

        [NatsConsumer("sample.greet", queueGroup: "nats-sample-console-app")]
        public Task<bool> HandleAsync(SampleGreatEvent msg)
        {
            _logger.LogInformation("收到 NATS 消息: {@msg}", msg);
            return Task.FromResult(true);
        }
    }

    internal class SampleGreatEvent : MessageBase
    {
        public string? Text { get; set; }
    }
}
```

**说明：** `SampleGreatEvent` 增加 `Text` 属性，使反序列化结果可观察。

**Step 2: 验证特性被扫描到**

Run: `dotnet build samples/NatsSample.ConsoleApp/NatsSample.ConsoleApp.csproj`
Expected: 0 errors；无 CS0246（`NATS.Net` 命名空间可解析）。

**说明：** 处理器只需 `using NATS.Net;` 即可拿到 `NatsConsumerAttribute`（与上游 `NatsClient` 同一命名空间）。

- [x] 4.3 移除示例中重复的手写订阅器并改造生产者

**Files:**
- Delete: `samples/NatsSample.ConsoleApp/NatsBootstrapper.cs`
- Modify: `samples/NatsSample.ConsoleApp/Worker.cs`

**Step 1: 删除手写订阅器**

删除 `samples/NatsSample.ConsoleApp/NatsBootstrapper.cs`。订阅由框架的 `NatsBootstrapper` 接管；示例中重复的手写订阅会与框架订阅争抢同一 queue group，导致消息被随机分发到两个消费者。

**Step 2: 改造生产者**

```csharp
using NATS.Client.Core;
using NatsSample.ConsoleApp.Adapter.Handlers;

namespace NatsSample.ConsoleApp
{
    [HostedService]
    public class Worker(ILogger<Worker> logger, INatsConnection nats) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                // 等待消费者订阅建立，避免消息在订阅前发布而丢失。
                await Task.Delay(3000, stoppingToken);

                await nats.PublishAsync(
                    "sample.greet",
                    new SampleGreatEvent { Text = "hello" });
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // 正常停机。
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "发布 NATS 消息失败。");
            }
        }
    }
}
```

**说明：** 发布类型化消息会走上游默认的 ad-hoc JSON 序列化器，而消费侧用 `IObjectSerializer` 反序列化 —— 两者对 `SampleGreatEvent`（继承 `MessageBase`，含 `Id`/`Key`/`OccurredOn`）的 JSON 字段名可能不一致（上游用 PascalCase 默认策略，`IObjectSerializer` 用 camelCase + 大小写不敏感）。`PropertyNameCaseInsensitive = true` 保证 camelCase 负载能被正确读入 PascalCase 模型。`Text` 是简单字符串属性，最稳妥。

**注意：** `PublishAsync<T>` 的泛型参数在编译期已知，因此直接传 `SampleGreatEvent` 即可，不需要
`IObjectSerializer`；这也是本 change 不提供发布端封装（`NatsTemplate` 之类）的原因 ——
发布侧直接用 `INatsConnection.PublishAsync` 已足够。

**Step 3: 验证**

Run: `dotnet build samples/NatsSample.ConsoleApp/NatsSample.ConsoleApp.csproj`
Expected: 0 errors；CS9113（`logger` 未使用）警告消失。

## 5. 文档

- [x] 5.1 编写 `src/Galosys.Foundation.Nats.Net/README.md`

**Files:**
- Create: `src/Galosys.Foundation.Nats.Net/README.md`

**Step 1: 写入 README**

内容至少包含以下章节：

```markdown
# Galosys.Foundation.Nats.Net

Core NATS 消费者框架：特性驱动的处理器扫描、订阅、反序列化与派发。

## 能力范围

- **本库提供**：消费者侧（订阅 → 反序列化 → 派发）。
- **本库不提供**：发布端封装、JetStream、事务性 outbox。发布请直接使用 `INatsConnection.PublishAsync`。

## 快速开始

```csharp
var builder = Host.CreateDefaultBuilder(args);
builder.UseModularization();   // InternalModule 自动接线
builder.ConfigureServices(services =>
{
    services.AddNatsClient(opts => opts.ConfigureOptions(b => b.Configure(o => o.Opts = o.Opts with
    {
        Url = "nats://127.0.0.1:4222"
    })));
});
```

处理器（`NatsConsumerAttribute` 位于 `NATS.Net` 命名空间，与上游 `NatsClient` 共享，
对齐 `AmqpHandlerAttribute`/`RabbitMQ.Client` 的既有约定）：

```csharp
using NATS.Net;

[Handler]
internal class OrderCreatedHandler : IMessageHandler<OrderCreated>
{
    [NatsConsumer("orders.created", queueGroup: "billing")]
    public Task<bool> HandleAsync(OrderCreated msg)
    {
        // ...
        return Task.FromResult(true);
    }
}
```

`UseModularization()` 会自动发现本库的 `InternalModule` 并调用 `services.AddNats(configuration)`；
不使用模块化启动时，手工调用 `services.AddNats(builder.Configuration)`。

## 配置

消费者选项绑定到 `Nats` 配置节：

```json
{
  "Nats": {
    "Concurrency": 1,
    "MaxRetryCount": 0,
    "RetryBaseDelayMs": 100,
    "Handlers": {
      "orders.created": { "Concurrency": 4 }
    }
  }
}
```

| 配置项 | 默认 | 含义 |
|---|---|---|
| `Concurrency` | 1 | 每个 subject 的并发派发数 |
| `MaxRetryCount` | 0 | 处理器异常后的重试次数 |
| `RetryBaseDelayMs` | 100 | 重试基础延迟，第 n 次重试为该值 × 2ⁿ |
| `Handlers` | 空 | 按 subject 覆盖上述设置 |

连接参数（地址、认证、TLS）由上游 `NatsOpts` 经 `AddNatsClient` 管理，不在本库的配置节中。

## 语义说明

- **异常不会终止订阅**：Core NATS 无 broker 侧 redelivery，处理器异常若逃逸会永久终止该 subject 的消费。
  框架内部吸收异常、按 `MaxRetryCount` 重试、耗尽后记录日志并放弃该条消息。
- **反序列化失败跳过该条消息**：记录错误日志后继续消费。
- **每条消息一个 DI 作用域**：handler 的 scoped 依赖在单条消息处理后即释放。
- **队列组语义**：指定 `queueGroup` 时同一队列组内的多个实例负载均衡；不指定则为广播语义。

## 与 AMQP 的差异

本库与 `galosys.foundation.amqp` 心智模型一致（特性标记 → 扫描 → 派发），但 NATS Core 没有
exchange/queue/DLX 概念，`NatsConsumerAttribute` 只有 `Subject` 与 `QueueGroup`。
```

**Step 2: 验证**

Run: `Select-String -Path src/Galosys.Foundation.Nats.Net/README.md -Pattern "## 能力范围"`
Expected: 有匹配。

- [x] 5.2 修正根 `README.md` 与 `AGENTS.md` 的"不可编译"表述

**Files:**
- Modify: `README.md`
- Modify: `AGENTS.md`

**Step 1: 改 `README.md`**

把项目一览表中 `Galosys.Foundation.Nats.Net` 一行：

```
| `Galosys.Foundation.Nats.Net` | `src/Galosys.Foundation.Nats.Net/` | 本地类库 | NATS 消费者扫描原型，当前不可编译。 |
```

改为：

```
| `Galosys.Foundation.Nats.Net` | `src/Galosys.Foundation.Nats.Net/` | 本地类库 | Core NATS 消费者框架：特性驱动的扫描、订阅、反序列化与派发；见 [README](src/Galosys.Foundation.Nats.Net/README.md)。 |
```

删除"已知限制"下的这一行：

```
- `Galosys.Foundation.Nats.Net` 当前不可编译，导致整个解决方案无法构建。
```

把"构建、测试与运行"章节下的已知限制段落：

```
> **已知限制**：`dotnet build Galosys.Foundation.Next.slnx` 会失败。解决方案包含
> `Galosys.Foundation.Nats.Net`，该项目是代码不完整的原型、当前无法编译，而 `NatsSample.ConsoleApp`
> 又引用了它。请改用上面的单项目构建。
```

改为：

```
> **已知限制**：`dotnet build Galosys.Foundation.Next.slnx` 仍会失败。请改用上面的单项目构建。
```

（若在任务 6.2 验证后整解决方案可构建成功，则删除该段落并保留单项目构建说明。）

在"相关文档"章节增加：

```markdown
- [Galosys.Foundation.Nats.Net](src/Galosys.Foundation.Nats.Net/README.md)
```

**Step 2: 改 `AGENTS.md`**

把"本地类库"段落中的：

```
`Galosys.Foundation.Nats.Net`（不可编译的 NATS 消费者扫描原型）、
```

改为：

```
`Galosys.Foundation.Nats.Net`（Core NATS 消费者框架：`[NatsConsumer]` 特性标记位于 `NATS.Net`
  命名空间（与上游 `NatsClient` 共享，对齐 `AmqpHandlerAttribute`/`RabbitMQ.Client` 约定）、
  内部核心类型在 `Galosys.Foundation.Nats.Net`、Options 在 `Microsoft.Extensions.Options`、
  DI 扩展在 `Microsoft.Extensions.DependencyInjection`；`AddNats(configuration)` 绑定 `Nats` 配置节，
  `UseModularization()` 通过 `InternalModule` 自动接线）、
```

把"坑点"之前的构建说明中的：

```
- **只构建改动的单个项目**：示例项目用 `dotnet build samples/<Project>/<Project>.csproj`。
  foundation 类库用 `dotnet build src/<Project>/<Project>.csproj`。
  `dotnet build Galosys.Foundation.Next.slnx` 会失败：`Galosys.Foundation.Nats.Net` 是无法编译的原型，
  而 `NatsSample.ConsoleApp` 引用了它。
```

改为：

```
- **只构建改动的单个项目**：示例项目用 `dotnet build samples/<Project>/<Project>.csproj`。
  foundation 类库用 `dotnet build src/<Project>/<Project>.csproj`。
```

**Step 3: 验证**

Run: `Select-String -Path README.md, AGENTS.md -Pattern "当前不可编译"`
Expected: 无匹配。

Run: `Select-String -Path README.md, AGENTS.md -Pattern "不可编译的 NATS"`
Expected: 无匹配。

## 6. 验证

- [x] 6.1 构建库与示例

**Files:**
- 无（仅验证）

Run: `dotnet build src/Galosys.Foundation.Nats.Net/Galosys.Foundation.Nats.Net.csproj`
Expected: 0 errors，0 warnings（除 NU1506 重复 PackageVersion 警告外，该警告来自根 `Directory.Packages.props` 的历史重复项，与本 change 无关）。

Run: `dotnet build samples/NatsSample.ConsoleApp/NatsSample.ConsoleApp.csproj`
Expected: 0 errors。

Run: `Select-String -Path src/Galosys.Foundation.Nats.Net/NATS.Net/NatsConsumerAttribute.cs -Pattern "namespace NATS.Net"`
Expected: 有匹配（命名空间保持 `NATS.Net`）。

Run: `Test-Path src/Galosys.Foundation.Nats.Net/NatsConsumerAttribute.cs`
Expected: `False`（旧位置的文件已删除）。

- [x] 6.2 端到端验证

**Files:**
- 无（仅验证）

**前置**：本地运行 NATS 服务器，监听 `127.0.0.1:4222`，账号 `appuser` / 密码 `AppPass123`。
若无服务器，可用 `docker run -p 4222:4222 nats:2 -c 'authorization { user: appuser, password: AppPass123 }'` 启动。

Run: `dotnet run --project samples/NatsSample.ConsoleApp/NatsSample.ConsoleApp.csproj`
Expected:
- 日志出现 `NATS 订阅已建立: subject=sample.greet, queueGroup=nats-sample-console-app, concurrency=1`
- 日志出现 `NATS 消费者启动完成，共 1 个订阅。`
- 约 3 秒后出现 `收到 NATS 消息: {@msg}`，且 `Text` 为 `hello`
- `Ctrl+C` 停机后进程正常退出，无残留异常

**反例验证**：把 `SampleGreatHandler.HandleAsync` 临时改为 `throw new InvalidOperationException("boom")`，
重新运行。Expected: 日志出现 `NATS 消费者处理失败: subject=sample.greet`，且订阅保持存活（进程不退出、后续消息仍被消费）。

- [x] 6.3 校验 OpenSpec

**Files:**
- 无（仅验证）

Run: `openspec validate "nats-consumer-core" --strict --no-interactive`
Expected: `Change 'nats-consumer-core' is valid`。

Run: `openspec status --change "nats-consumer-core"`
Expected: `proposal`、`specs`、`design`、`tasks` 四个 artifact 均为 `done`。

Run: `openspec status --change "nats-consumer-core" --json`
Expected: `isPlanningComplete` 为 `true`。

## Workflow follow-up

- 发布端封装（`NatsTemplate` 之类）、JetStream 支持、`IOutboxTransport` 三项作为独立 change 推进，
  不在本 change 范围内。
- 后续 change 建立 `tests/Galosys.Foundation.Nats.Net.Tests` 测试项目，补齐反序列化与派发的单元测试
  （需引入可替换的 NATS 连接测试替身，避免 CI 依赖真实服务器）。
- 本 change 应用并归档后，`openspec/specs/nats-consumer/spec.md` 需由归档流程生成，
  `foundation-namespace-conventions` 与 `repository-layout` 的 delta 同步合入主 spec。
