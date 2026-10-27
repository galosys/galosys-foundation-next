# P5-2 Diagnostics 可观测性 Implementation Plan

> **REQUIRED SUB-SKILL:** 使用 `executing-plans` 或 `opsx-apply-x` 逐任务执行本计划。
> 本仓库虽有 git，但按约定不自动提交；步骤中的“验证”统一用构建 + 测试，不执行 git commit。

**Goal:** 为服务端连接生命周期、消息会话与 framing 增加稳定的 `ActivitySource` / `Meter`，默认只使用低基数标签，并在没有监听器时不分配 tag 集合、不改变协议行为。

**Architecture:** `ChannelDiagnostics` 持有 static `ActivitySource`/`Meter` 与公共名称常量；`ChannelServerCore` 产生连接活动和服务端指标，`ChannelSession<T>` 产生 session 活动、消息计数/大小直方图与 framing 时长；所有入口先检查 `HasListeners`/`Instrument.Enabled`。`ChannelDiagnosticsOptions` 控制 `connection.id`、`remote.endpoint`、`message.size` 等高基数标签，默认关闭。P4 客户端指标不在本 change 内。

**Tech Stack:** .NET 10、`System.Diagnostics.DiagnosticSource`、`System.Diagnostics.Metrics`、xunit 2.9.3、Microsoft.Extensions.Options。

---

## 1. 公共诊断表面

- [ ] 1.1 增加 `ChannelDiagnosticsOptions` 与 `ChannelDiagnostics` 名称常量

**Files:**
- Create: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Options/ChannelDiagnosticsOptions.cs`
- Create: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Diagnostics/ChannelDiagnostics.cs`

**Step 1: 选项类型**

```csharp
namespace Microsoft.Extensions.Options
{
    /// <summary>诊断采集选项。高基数标签默认关闭。</summary>
    public sealed class ChannelDiagnosticsOptions
    {
        /// <summary>是否在 activity 上附加 connection.id，默认 false。</summary>
        public bool CaptureConnectionId { get; set; }

        /// <summary>是否在 activity 上附加 remote.endpoint，默认 false。</summary>
        public bool CaptureRemoteEndPoint { get; set; }

        /// <summary>是否在 activity 上附加 message.size 标签，默认 false。</summary>
        public bool CaptureMessageSize { get; set; }

        /// <summary>是否为每条消息创建 session activity，默认 true。</summary>
        public bool EnableSessionActivities { get; set; } = true;

        /// <summary>复制选项。</summary>
        public ChannelDiagnosticsOptions Clone() => (ChannelDiagnosticsOptions)MemberwiseClone();
    }
}
```

**Step 2: 公共名称常量**

```csharp
namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>Primitives 的 ActivitySource/Meter 名称、活动和标签常量。</summary>
    public static class ChannelDiagnostics
    {
        public const string SourceName = "Galosys.Foundation.Primitives";
        public const string MeterName = "Galosys.Foundation.Primitives";

        public const string ServerConnectionActivity = "ChannelServer.Connection";
        public const string SessionReadActivity = "ChannelSession.Read";
        public const string SessionWriteActivity = "ChannelSession.Write";

        public const string ConnectionsAcceptedMetric = "channel.server.connections.accepted";
        public const string ConnectionsRejectedMetric = "channel.server.connections.rejected";
        public const string ConnectionsActiveMetric = "channel.server.connections.active";
        public const string ConnectionDurationMetric = "channel.server.connection.duration";
        public const string AcceptErrorsMetric = "channel.server.accept.errors";
        public const string SessionMessagesMetric = "channel.session.messages";
        public const string SessionMessageSizeMetric = "channel.session.message.size";
        public const string SessionFramingDurationMetric = "channel.session.framing.duration";
        public const string SessionErrorsMetric = "channel.session.errors";

        public const string ConnectionIdTag = "connection.id";
        public const string RemoteEndPointTag = "remote.endpoint";
        public const string TransportTag = "transport";
        public const string ProtocolTag = "protocol";
        public const string DirectionTag = "direction";
        public const string OutcomeTag = "outcome";
        public const string ReasonTag = "reason";
        public const string MessageSizeTag = "message.size";
        public const string ErrorTypeTag = "error.type";
    }
}
```

Run: `dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`
Expected: 0 errors。

- [ ] 1.2 增加 internal ActivitySource/Meter、instrument 与防空分配 helper

**Files:**
- Modify: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Diagnostics/ChannelDiagnostics.cs`

**Step 1: 增加静态字段**

```csharp
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;

namespace Microsoft.Extensions.Hosting.Net
{
    public static partial class ChannelDiagnostics
    {
        internal static readonly ActivitySource ActivitySource = new(SourceName, "1.0.0");
        internal static readonly Meter Meter = new(MeterName, "1.0.0");

        internal static readonly Counter<long> ConnectionsAccepted =
            Meter.CreateCounter<long>(ConnectionsAcceptedMetric);

        internal static readonly Counter<long> ConnectionsRejected =
            Meter.CreateCounter<long>(ConnectionsRejectedMetric);

        internal static readonly UpDownCounter<long> ConnectionsActive =
            Meter.CreateUpDownCounter<long>(ConnectionsActiveMetric);

        internal static readonly Histogram<double> ConnectionDuration =
            Meter.CreateHistogram<double>(ConnectionDurationMetric, unit: "ms");

        internal static readonly Counter<long> AcceptErrors =
            Meter.CreateCounter<long>(AcceptErrorsMetric);

        internal static readonly Counter<long> SessionMessages =
            Meter.CreateCounter<long>(SessionMessagesMetric);

        internal static readonly Histogram<long> SessionMessageSize =
            Meter.CreateHistogram<long>(SessionMessageSizeMetric, unit: "By");

        internal static readonly Histogram<double> SessionFramingDuration =
            Meter.CreateHistogram<double>(SessionFramingDurationMetric, unit: "ms");

        internal static readonly Counter<long> SessionErrors =
            Meter.CreateCounter<long>(SessionErrorsMetric);
    }
}
```

**Step 2: 增加守卫与标签 helper**

```csharp
internal static bool ShouldMeasureConnection()
    => ActivitySource.HasListeners() || ConnectionDuration.Enabled;

internal static string GetTransportName(EndPoint? endPoint)
    => endPoint switch
    {
        IPEndPoint => "tcp",
        null => "unknown",
        { } value when value.GetType().FullName
            == "Microsoft.Extensions.Hosting.Net.NamedPipeEndPoint" => "namedpipe",
        _ => "other",
    };

internal static Activity? StartServerConnection(
    string connectionId,
    string transport,
    EndPoint? remoteEndPoint,
    ChannelDiagnosticsOptions options)
{
    if (!ActivitySource.HasListeners())
        return null;

    var activity = ActivitySource.StartActivity(
        ServerConnectionActivity,
        ActivityKind.Server);

    if (activity is null)
        return null;

    activity.SetTag(TransportTag, transport);
    activity.SetTag(ProtocolTag, "stream");

    if (options.CaptureConnectionId)
        activity.SetTag(ConnectionIdTag, connectionId);

    if (options.CaptureRemoteEndPoint && remoteEndPoint is not null)
        activity.SetTag(RemoteEndPointTag, remoteEndPoint.ToString());

    return activity;
}

internal static Activity? StartSessionActivity(
    string name,
    string protocol,
    string direction,
    ChannelDiagnosticsOptions options)
{
    if (!options.EnableSessionActivities || !ActivitySource.HasListeners())
        return null;

    var activity = ActivitySource.StartActivity(name, ActivityKind.Internal);
    if (activity is null)
        return null;

    activity.SetTag(ProtocolTag, protocol);
    activity.SetTag(DirectionTag, direction);
    return activity;
}

internal static void SetOutcome(Activity? activity, string outcome)
    => activity?.SetTag(OutcomeTag, outcome);

internal static void SetError(Activity? activity, Exception exception)
{
    if (activity is null)
        return;

    activity.SetTag(OutcomeTag, "error");
    activity.SetTag(ErrorTypeTag, exception.GetType().FullName);
    activity.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
}
```

文件顶部补 `using Microsoft.Extensions.Options;`。`ChannelDiagnostics` 是 static class，使用 `partial` 便于后续任务追加 helper。

Run: `dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`
Expected: 0 errors。

- [ ] 1.3 把诊断选项接入 `ChannelServerOptions` 与 `MessageChannelOptions`

**Files:**
- Modify: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Options/ChannelServerOptions.cs`
- Modify: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Options/MessageChannelOptions.cs`

**Step 1: 服务端选项**

```csharp
/// <summary>诊断采集选项。</summary>
public ChannelDiagnosticsOptions Diagnostics { get; set; } = new();
```

在 `Clone()` 中复制：

```csharp
clone.Diagnostics = Diagnostics.Clone();
```

**Step 2: 消息选项**

```csharp
/// <summary>协议名称，用于 protocol 标签；自定义协议应设置为固定值。</summary>
public string ProtocolName { get; set; } = "custom";

/// <summary>诊断采集选项。</summary>
public ChannelDiagnosticsOptions Diagnostics { get; set; } = new();
```

`ChannelServerOptions.Clone()` 必须复制 `Diagnostics`，否则两个 server 会共享同一个可变选项。`MessageChannelOptions` 由 session 在创建时读取；文档要求创建 session 后不要再修改。

Run: `dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`
Expected: 0 errors（`MessageChannelOptions` 依赖 P2 已落地）。

## 2. 服务端埋点

- [ ] 2.1 在 accept loop 增加接受/拒绝/错误指标

**Files:**
- Modify: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/ChannelServerCore.cs`

**Step 1: 缓存 transport 名称**

```csharp
private readonly string _transportName;

// 构造函数中，_options 克隆之后：
_transportName = ChannelDiagnostics.GetTransportName(_options.EndPoint);
```

**Step 2: 在 accept 成功、拒绝和异常处记录**

```csharp
// listener.AcceptAsync 返回非 null 后：
ChannelDiagnostics.RecordConnectionAccepted(_transportName);

// MaxConnections 拒绝分支：
ChannelDiagnostics.RecordConnectionRejected(_transportName, "max_connections");

// 连接初始化失败分支：
ChannelDiagnostics.RecordConnectionRejected(_transportName, "initialization_error");

// accept 异常且非 ct 取消时：
ChannelDiagnostics.RecordAcceptError(_transportName);
```

在同文件的 `ChannelDiagnostics` partial 或 helper 区域补：

```csharp
internal static void RecordConnectionAccepted(string transport)
{
    if (!ConnectionsAccepted.Enabled)
        return;

    ConnectionsAccepted.Add(
        1,
        new KeyValuePair<string, object?>(TransportTag, transport));
}

internal static void RecordConnectionRejected(string transport, string reason)
{
    if (!ConnectionsRejected.Enabled)
        return;

    ConnectionsRejected.Add(1, new[]
    {
        new KeyValuePair<string, object?>(TransportTag, transport),
        new KeyValuePair<string, object?>(ReasonTag, reason),
    });
}

internal static void RecordAcceptError(string transport)
{
    if (!AcceptErrors.Enabled)
        return;

    AcceptErrors.Add(
        1,
        new KeyValuePair<string, object?>(TransportTag, transport));
}
```

`RecordConnectionAccepted` 放在 `connection is null` 检查和 `MaxConnections` 检查之后，确保被拒绝的连接不会同时计入 accepted。

Run: `dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`
Expected: 0 errors。

- [ ] 2.2 为每个连接创建 `ChannelServer.Connection` 活动与时长/活动连接数

**Files:**
- Modify: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/ChannelServerCore.cs`

**Step 1: 在 `ProcessConnectionAsync` 开头启动**

```csharp
var started = ChannelDiagnostics.ShouldMeasureConnection()
    ? Stopwatch.GetTimestamp()
    : 0;

using var activity = ChannelDiagnostics.StartServerConnection(
    id,
    _transportName,
    context.RemoteEndPoint,
    _options.Diagnostics);

Interlocked.Increment(ref _connectionCount);
ChannelDiagnostics.RecordConnectionActive(_transportName, 1);

var outcome = "ok";
Exception? error = null;
```

**Step 2: 在现有 catch/finally 中标记结果**

```csharp
catch (OperationCanceledException) when (context.ConnectionClosed.IsCancellationRequested)
{
    outcome = "cancelled";
}
catch (Exception ex)
{
    error = ex;
    outcome = "error";
    ChannelDiagnostics.SetError(activity, ex);
    // 保留现有 logger
}

finally
{
    ChannelDiagnostics.RecordConnectionActive(_transportName, -1);

    if (started != 0)
    {
        ChannelDiagnostics.RecordConnectionDuration(
            Stopwatch.GetElapsedTime(started),
            _transportName,
            outcome);
    }

    if (error is null)
        ChannelDiagnostics.SetOutcome(activity, outcome);

    // 保留现有 BeginCloseAsync / DisposeScopeAsync / DisposeConnectionAsync
}
```

补 helper：

```csharp
internal static void RecordConnectionActive(string transport, int delta)
{
    if (!ConnectionsActive.Enabled)
        return;

    ConnectionsActive.Add(
        delta,
        new KeyValuePair<string, object?>(TransportTag, transport));
}

internal static void RecordConnectionDuration(
    TimeSpan elapsed,
    string transport,
    string outcome)
{
    if (!ConnectionDuration.Enabled)
        return;

    ConnectionDuration.Record(elapsed.TotalMilliseconds, new[]
    {
        new KeyValuePair<string, object?>(TransportTag, transport),
        new KeyValuePair<string, object?>(OutcomeTag, outcome),
    });
}
```

`using var activity` 必须在方法作用域内，确保 handler 结束、`Dispose` 触发 activity 停止。不要在 `ProcessConnectionAsync` 的早期 return 之前漏掉 `RecordConnectionActive(-1)`；现有方法只有一个 finally，保持在该 finally 中。

Run: `dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`
Expected: 0 errors。

## 3. 消息会话与 framing 埋点

- [ ] 3.1 在 `ChannelSession<T>.ReadAsync` / `WriteAsync` 增加活动和消息指标

**Files:**
- Modify: `src/Galosys.Foundation.Primitives/Session/ChannelSession.cs`
- Modify: `src/Galosys.Foundation.Primitives/Session/ChannelContextSessionExtensions.cs`

**Step 1: session 保存协议名与诊断选项**

```csharp
private readonly string _protocolName;
private readonly ChannelDiagnosticsOptions _diagnostics;

// 构造函数参数增加 MessageChannelOptions options：
_protocolName = string.IsNullOrWhiteSpace(options.ProtocolName)
    ? "custom"
    : options.ProtocolName;
_diagnostics = options.Diagnostics;
```

**Step 2: 读取路径**

```csharp
using var activity = ChannelDiagnostics.StartSessionActivity(
    ChannelDiagnostics.SessionReadActivity,
    _protocolName,
    "read",
    _diagnostics);

try
{
    // 保留现有 framer.Parse / codec.Decode
    ChannelDiagnostics.RecordSessionMessage(
        _protocolName,
        "read",
        frame.Length,
        "ok",
        _diagnostics);
    return message;
}
catch (OperationCanceledException)
{
    ChannelDiagnostics.SetOutcome(activity, "cancelled");
    ChannelDiagnostics.RecordSessionError(_protocolName, "read", "cancelled");
    throw;
}
catch (Exception ex)
{
    ChannelDiagnostics.SetError(activity, ex);
    ChannelDiagnostics.RecordSessionError(_protocolName, "read", "error");
    throw;
}
```

**Step 3: 写入路径**

```csharp
using var activity = ChannelDiagnostics.StartSessionActivity(
    ChannelDiagnostics.SessionWriteActivity,
    _protocolName,
    "write",
    _diagnostics);

try
{
    // 保留现有 codec.Encode / framer.WriteFrame / FlushAsync
    ChannelDiagnostics.RecordSessionMessage(
        _protocolName,
        "write",
        payloadLength,
        "ok",
        _diagnostics);
}
catch (OperationCanceledException)
{
    ChannelDiagnostics.SetOutcome(activity, "cancelled");
    ChannelDiagnostics.RecordSessionError(_protocolName, "write", "cancelled");
    throw;
}
catch (Exception ex)
{
    ChannelDiagnostics.SetError(activity, ex);
    ChannelDiagnostics.RecordSessionError(_protocolName, "write", "error");
    throw;
}
```

**Step 4: helper**

```csharp
internal static void RecordSessionMessage(
    string protocol,
    string direction,
    long size,
    string outcome,
    ChannelDiagnosticsOptions options)
{
    if (SessionMessages.Enabled)
    {
        SessionMessages.Add(1, new[]
        {
            new KeyValuePair<string, object?>(ProtocolTag, protocol),
            new KeyValuePair<string, object?>(DirectionTag, direction),
            new KeyValuePair<string, object?>(OutcomeTag, outcome),
        });
    }

    if (SessionMessageSize.Enabled)
    {
        SessionMessageSize.Record(size, new[]
        {
            new KeyValuePair<string, object?>(ProtocolTag, protocol),
            new KeyValuePair<string, object?>(DirectionTag, direction),
        });
    }

    // message.size 只作为 histogram 值；只有 CaptureMessageSize 时才写入 activity tag。
    _ = options;
}

internal static void RecordSessionError(string protocol, string direction, string outcome)
{
    if (!SessionErrors.Enabled)
        return;

    SessionErrors.Add(1, new[]
    {
        new KeyValuePair<string, object?>(ProtocolTag, protocol),
        new KeyValuePair<string, object?>(DirectionTag, direction),
        new KeyValuePair<string, object?>(OutcomeTag, outcome),
    });
}
```

`AsJsonSession<T>()` 在创建 options 时设置 `ProtocolName = "json"`，`AsLineTextSession()` 设置 `"line"`；`AsSession()` 使用调用方提供的 `MessageChannelOptions`，为空时新建默认 `"custom"`。不要把 codec 类型名作为 protocol 标签。

Run: `dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`
Expected: 0 errors。

- [ ] 3.2 在 `IMessageFramer.Parse` 周围记录 framing 时长

**Files:**
- Modify: `src/Galosys.Foundation.Primitives/Session/ChannelSession.cs`

**Step 1: 只在有监听器时计时**

```csharp
var parseStarted = ChannelDiagnostics.ShouldMeasureFraming()
    ? Stopwatch.GetTimestamp()
    : 0;

var parseResult = _framer.Parse(result.Buffer);

if (parseStarted != 0)
{
    ChannelDiagnostics.RecordFramingDuration(
        _protocolName,
        Stopwatch.GetElapsedTime(parseStarted));
}
```

**Step 2: helper**

```csharp
internal static bool ShouldMeasureFraming()
    => SessionFramingDuration.Enabled;

internal static void RecordFramingDuration(string protocol, TimeSpan elapsed)
{
    if (!SessionFramingDuration.Enabled)
        return;

    SessionFramingDuration.Record(
        elapsed.TotalMilliseconds,
        new KeyValuePair<string, object?>(ProtocolTag, protocol));
}
```

如果 `Parse` 抛异常，不要记录成功时长；让 3.1 的 catch 记录 `session.errors` 并原样抛出。

Run: `dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`
Expected: 0 errors。

## 4. 诊断测试

- [ ] 4.1 增加 ActivityListener/MeterListener 测试辅助并禁用并行

**Files:**
- Create: `tests/Galosys.Foundation.Primitives.Tests/DiagnosticsTestSupport.cs`

**Step 1: 写入收集器**

```csharp
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Galosys.Foundation.Primitives.Tests;

[CollectionDefinition("Diagnostics", DisableParallelization = true)]
public sealed class DiagnosticsCollection
{
}

internal sealed class ActivityCollector : IDisposable
{
    private readonly ActivityListener _listener;

    public ActivityCollector()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ChannelDiagnostics.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => Activities.Enqueue(activity),
        };

        ActivitySource.AddActivityListener(_listener);
    }

    public ConcurrentQueue<Activity> Activities { get; } = new();

    public void Dispose() => _listener.Dispose();
}

internal readonly record struct MetricMeasurement(
    string Name,
    double Value,
    KeyValuePair<string, object?>[] Tags)
{
    public object? GetTag(string name)
    {
        foreach (var tag in Tags)
        {
            if (tag.Key == name)
                return tag.Value;
        }

        return null;
    }
}

internal sealed class MetricCollector : IDisposable
{
    private readonly MeterListener _listener = new();

    public MetricCollector()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == ChannelDiagnostics.MeterName)
                listener.EnableMeasurementEvents(instrument);
        };

        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            Measurements.Enqueue(new MetricMeasurement(
                instrument.Name,
                value,
                tags.ToArray())));

        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            Measurements.Enqueue(new MetricMeasurement(
                instrument.Name,
                value,
                tags.ToArray())));

        _listener.Start();
    }

    public ConcurrentQueue<MetricMeasurement> Measurements { get; } = new();

    public void Dispose() => _listener.Dispose();
}
```

`ActivityCollector` 与 `MetricCollector` 必须在测试类构造/`using` 中创建；测试类加 `[Collection("Diagnostics")]`，避免全局 listener 捕获并行测试的活动。

Run: `dotnet build tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj`
Expected: 0 errors。

- [ ] 4.2 增加服务端连接活动测试

**Files:**
- Create: `tests/Galosys.Foundation.Primitives.Tests/ServerDiagnosticsTests.cs`

**Step 1: 测试正常连接**

```csharp
using System.Net;
using System.Text;
using Microsoft.Extensions.Hosting.Net;
using Microsoft.Extensions.Options;

namespace Galosys.Foundation.Primitives.Tests;

[Collection("Diagnostics")]
public sealed class ServerDiagnosticsTests
{
    [Fact]
    public async Task Connection_EmitsActivity_WithLowCardinalityTags()
    {
        using var activities = new ActivityCollector();

        var server = new ChannelServer(
            new ChannelServerOptions
            {
                EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                ShutdownTimeout = TimeSpan.FromSeconds(2),
            },
            pipeline => pipeline.Run(TestHandlers.EchoAsync));

        await server.StartAsync(CancellationToken.None);

        try
        {
            using (var client = await TestClient.ConnectAsync((IPEndPoint)server.LocalEndPoint!))
            {
                var stream = client.GetStream();
                await stream.WriteAsync("ping"u8.ToArray());
                await TestClient.ReadExactlyAsync(stream, 4);
            }

            await TestClient.WaitUntilAsync(
                () => Task.FromResult(activities.Activities.Any(
                    a => a.OperationName == ChannelDiagnostics.ServerConnectionActivity)),
                "未收到服务端连接 activity。");

            var activity = Assert.Single(activities.Activities,
                a => a.OperationName == ChannelDiagnostics.ServerConnectionActivity);

            Assert.Equal("tcp", activity.GetTagItem(ChannelDiagnostics.TransportTag));
            Assert.Equal("stream", activity.GetTagItem(ChannelDiagnostics.ProtocolTag));
            Assert.Equal("ok", activity.GetTagItem(ChannelDiagnostics.OutcomeTag));
            Assert.Null(activity.GetTagItem(ChannelDiagnostics.ConnectionIdTag));
        }
        finally
        {
            await server.DisposeAsync();
        }
    }
}
```

`Assert.Single(collection, predicate)` 返回匹配项；如果 xUnit 版本没有该重载，用 `Assert.Single(activities.Activities.Where(predicate))`。不要依赖 activity 的父子关系；这里只验证名称和标签。

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter FullyQualifiedName~ServerDiagnosticsTests`
Expected: 测试通过；activity 包含 `transport=tcp`、`protocol=stream`、`outcome=ok`。

- [ ] 4.3 增加错误活动与服务端指标测试

**Files:**
- Modify: `tests/Galosys.Foundation.Primitives.Tests/ServerDiagnosticsTests.cs`

**Step 1: 错误活动**

```csharp
[Fact]
public async Task HandlerError_SetsErrorOutcomeAndType()
{
    using var activities = new ActivityCollector();

    var server = new ChannelServer(
        new ChannelServerOptions { EndPoint = new IPEndPoint(IPAddress.Loopback, 0) },
        pipeline => pipeline.Run(_ => throw new InvalidOperationException("boom")));

    await server.StartAsync(CancellationToken.None);

    try
    {
        using (var client = await TestClient.ConnectAsync((IPEndPoint)server.LocalEndPoint!))
        {
            await TestClient.AssertConnectionClosedAsync(client.GetStream());
        }

        await TestClient.WaitUntilAsync(
            () => Task.FromResult(activities.Activities.Any(
                a => a.GetTagItem(ChannelDiagnostics.OutcomeTag) as string == "error")),
            "未收到错误 outcome 的连接 activity。");

        var activity = Assert.Single(activities.Activities,
            a => a.OperationName == ChannelDiagnostics.ServerConnectionActivity);

        Assert.Equal("error", activity.GetTagItem(ChannelDiagnostics.OutcomeTag));
        Assert.Equal(
            "System.InvalidOperationException",
            activity.GetTagItem(ChannelDiagnostics.ErrorTypeTag));
    }
    finally
    {
        await server.DisposeAsync();
    }
}
```

**Step 2: 指标**

```csharp
[Fact]
public async Task Connection_RecordsAcceptedActiveAndDurationMetrics()
{
    using var metrics = new MetricCollector();

    var server = new ChannelServer(
        new ChannelServerOptions { EndPoint = new IPEndPoint(IPAddress.Loopback, 0) },
        pipeline => pipeline.Run(_ => Task.CompletedTask));

    await server.StartAsync(CancellationToken.None);

    try
    {
        using (var client = await TestClient.ConnectAsync((IPEndPoint)server.LocalEndPoint!))
        {
            await TestClient.AssertConnectionClosedAsync(client.GetStream());
        }

        await TestClient.WaitUntilAsync(
            () => Task.FromResult(metrics.Measurements.Any(
                m => m.Name == ChannelDiagnostics.ConnectionDurationMetric)),
            "未收到连接时长直方图。");

        Assert.Contains(metrics.Measurements, m =>
            m.Name == ChannelDiagnostics.ConnectionsAcceptedMetric
            && Equals(m.GetTag(ChannelDiagnostics.TransportTag), "tcp"));

        Assert.Contains(metrics.Measurements, m =>
            m.Name == ChannelDiagnostics.ConnectionDurationMetric
            && Equals(m.GetTag(ChannelDiagnostics.OutcomeTag), "ok"));
    }
    finally
    {
        await server.DisposeAsync();
    }
}
```

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter FullyQualifiedName~ServerDiagnosticsTests`
Expected: 三个服务端诊断测试通过；服务器在错误后仍能继续接受连接。

- [ ] 4.4 增加 session 消息计数、大小直方图与 framing 时长测试

**Files:**
- Create: `tests/Galosys.Foundation.Primitives.Tests/SessionDiagnosticsTests.cs`

**Step 1: 用 `AsLineTextSession` 做回显**

```csharp
using System.Net;
using Microsoft.Extensions.Hosting.Net;
using Microsoft.Extensions.Options;

namespace Galosys.Foundation.Primitives.Tests;

[Collection("Diagnostics")]
public sealed class SessionDiagnosticsTests
{
    [Fact]
    public async Task SessionReadWrite_RecordsMessagesSizesAndFraming()
    {
        using var metrics = new MetricCollector();

        var server = new ChannelServer(
            new ChannelServerOptions { EndPoint = new IPEndPoint(IPAddress.Loopback, 0) },
            pipeline => pipeline.Run(LineEchoAsync));

        await server.StartAsync(CancellationToken.None);

        try
        {
            using var client = await TestClient.ConnectAsync((IPEndPoint)server.LocalEndPoint!);
            var stream = client.GetStream();

            await stream.WriteAsync("hello\n"u8.ToArray());
            var reply = await TestClient.ReadExactlyAsync(stream, 6);

            Assert.Equal("hello\n", System.Text.Encoding.UTF8.GetString(reply));

            await TestClient.WaitUntilAsync(
                () => Task.FromResult(metrics.Measurements.Any(
                    m => m.Name == ChannelDiagnostics.SessionMessagesMetric
                         && Equals(m.GetTag(ChannelDiagnostics.DirectionTag), "read"))),
                "未收到 session 读取计数。");

            Assert.Contains(metrics.Measurements, m =>
                m.Name == ChannelDiagnostics.SessionMessagesMetric
                && Equals(m.GetTag(ChannelDiagnostics.ProtocolTag), "line")
                && Equals(m.GetTag(ChannelDiagnostics.DirectionTag), "write"));

            Assert.Contains(metrics.Measurements, m =>
                m.Name == ChannelDiagnostics.SessionMessageSizeMetric
                && m.Value > 0);

            Assert.Contains(metrics.Measurements, m =>
                m.Name == ChannelDiagnostics.SessionFramingDurationMetric
                && Equals(m.GetTag(ChannelDiagnostics.ProtocolTag), "line"));
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    private static async Task LineEchoAsync(ChannelContext context)
    {
        await using var session = context.AsLineTextSession();

        while (true)
        {
            string line;
            try
            {
                line = await session.ReadAsync(context.ConnectionClosed);
            }
            catch (EndOfStreamException)
            {
                break;
            }

            await session.WriteAsync(line, context.ConnectionClosed);
        }
    }
}
```

`AsLineTextSession()` 的 `ProtocolName` 应为 `"line"`；如果 P2 工厂没有设置，3.1 的 Step 4 负责补上。测试在连接关闭前读取回复，避免 session 在写完后立即结束导致指标来不及刷新。

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter FullyQualifiedName~SessionDiagnosticsTests`
Expected: 测试通过；`protocol=line`，读写方向都有计数。

- [ ] 4.5 增加无监听器时的 tag 零分配测试

**Files:**
- Create: `tests/Galosys.Foundation.Primitives.Tests/DiagnosticsOverheadTests.cs`

**Step 1: 测试守卫**

```csharp
using Microsoft.Extensions.Hosting.Net;
using Microsoft.Extensions.Options;

namespace Galosys.Foundation.Primitives.Tests;

[Collection("Diagnostics")]
public sealed class DiagnosticsOverheadTests
{
    [Fact]
    public void NoListener_DoesNotAllocateTags()
    {
        Assert.False(ChannelDiagnostics.ActivitySource.HasListeners());

        var options = new ChannelDiagnosticsOptions();

        // warmup，排除 JIT 首次调用分配。
        for (var i = 0; i < 10; i++)
            Assert.Null(ChannelDiagnostics.StartServerConnection("id", "tcp", null, options));

        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var i = 0; i < 1_000; i++)
            Assert.Null(ChannelDiagnostics.StartServerConnection("id", "tcp", null, options));

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 16 * 1024, $"无监听器路径分配了 {allocated} 字节。");
    }

    [Fact]
    public void NoListener_DoesNotCreateSessionActivity()
    {
        Assert.False(ChannelDiagnostics.ActivitySource.HasListeners());

        var options = new ChannelDiagnosticsOptions();
        var activity = ChannelDiagnostics.StartSessionActivity(
            ChannelDiagnostics.SessionReadActivity,
            "json",
            "read",
            options);

        Assert.Null(activity);
    }
}
```

如果 `ActivitySource.HasListeners()` 受其它并行测试影响，确认 `DiagnosticsCollection` 的 `DisableParallelization = true` 且所有诊断测试类都带 `[Collection("Diagnostics")]`。不要把分配上限设为 0；JIT 和运行时可能在循环中产生极少量分配。

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter FullyQualifiedName~DiagnosticsOverheadTests`
Expected: 两个测试通过；无监听器时不创建 activity/tag。

## 5. 文档与验证

- [ ] 5.1 更新 Primitives README 的可观测性章节

**Files:**
- Modify: `src/Galosys.Foundation.Primitives/README.md`

在“配置”章节后增加：

```markdown
## 可观测性

ActivitySource 与 Meter 名称都是 `Galosys.Foundation.Primitives`。

```csharp
using var activityListener = new ActivityListener
{
    ShouldListenTo = source => source.Name == ChannelDiagnostics.SourceName,
    Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
        ActivitySamplingResult.AllDataAndRecorded,
};
ActivitySource.AddActivityListener(activityListener);

using var meterListener = new MeterListener();
meterListener.InstrumentPublished = (instrument, listener) =>
{
    if (instrument.Meter.Name == ChannelDiagnostics.MeterName)
        listener.EnableMeasurementEvents(instrument);
};
meterListener.Start();
```

主要指标：

| 指标 | 类型 | 标签 |
|---|---|---|
| `channel.server.connections.accepted` | Counter | `transport` |
| `channel.server.connections.rejected` | Counter | `transport`、`reason` |
| `channel.server.connections.active` | UpDownCounter | `transport` |
| `channel.server.connection.duration` | Histogram (ms) | `transport`、`outcome` |
| `channel.server.accept.errors` | Counter | `transport` |
| `channel.session.messages` | Counter | `protocol`、`direction`、`outcome` |
| `channel.session.message.size` | Histogram (By) | `protocol`、`direction` |
| `channel.session.framing.duration` | Histogram (ms) | `protocol` |
| `channel.session.errors` | Counter | `protocol`、`direction`、`outcome` |

- `connection.id`、`remote.endpoint`、`message.size` 默认不作为标签；通过 `ChannelDiagnosticsOptions` 仅在 activity 上开启。
- `EnableSessionActivities` 默认开启；高吞吐场景可关闭每条消息的 activity，计数器和直方图仍保留。
- 客户端重连指标留给 P4 客户端落地后的独立 change；本版本不提供客户端 instrument。
```

Run: `Select-String -Path src/Galosys.Foundation.Primitives/README.md -Pattern "可观测性"`
Expected: 能匹配到新增章节标题。

- [ ] 5.2 构建、跑诊断/全量测试并校验 OpenSpec

Run: `dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`
Expected: 0 errors。

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter FullyQualifiedName~Diagnostics`
Expected: `ServerDiagnosticsTests`、`SessionDiagnosticsTests`、`DiagnosticsOverheadTests` 全部通过。

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj`
Expected: 既有测试全部通过，协议与异常行为无变化。

Run: `openspec validate "primitives-diagnostics" --strict --no-interactive`
Expected: `Change 'primitives-diagnostics' is valid`。

Run: `openspec status --change "primitives-diagnostics" --json`
Expected: `proposal`、`design`、`specs`、`tasks` 四个 artifact 均为 `done`。
