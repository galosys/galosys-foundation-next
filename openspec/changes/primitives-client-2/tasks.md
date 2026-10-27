# P4-2 重连与指数退避 Implementation Plan

> **REQUIRED SUB-SKILL:** 使用 `executing-plans` 或 `opsx-apply-x` 逐任务执行本计划。
> 本仓库虽有 git，但按约定不自动提交；步骤中的“验证”统一用构建 + 测试，不执行 git commit。

**Goal:** 新增 `ChannelReconnectPolicy` 与 `ChannelClientOptions.Reconnect`，让首次连接失败和已连接断线都按同一套可取消的指数退避策略重连；测试使用 `FakeTimeProvider`，不等待真实退避时间。

**Architecture:** `ChannelClient` 把单次连接抽成 `ConnectOnceAsync`，外层重试循环负责状态迁移、延迟和尝试计数；已连接后的 watcher 在释放旧连接后进入 `Reconnecting` 并启动后台重试。所有退避调用 `Task.Delay(TimeSpan, TimeProvider, CancellationToken)`，`CloseAsync` 通过取消 token 终止循环。

**Tech Stack:** .NET 10、`System.Net.Sockets`、`System.Threading.Tasks`、`Microsoft.Extensions.Options`、`Microsoft.Extensions.TimeProvider.Testing`、xunit 2.9.3。

---

## 1. 重连策略与选项

- [ ] 1.1 增加测试包并写 `ChannelReconnectPolicy` 失败测试

**Files:**
- Modify: `tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj`
- Create: `tests/Galosys.Foundation.Primitives.Tests/ChannelReconnectPolicyTests.cs`

在测试项目 `ItemGroup` 中加入：

```xml
<PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" Version="10.0.0" />
```

测试：

```csharp
using Microsoft.Extensions.Options;

namespace Galosys.Foundation.Primitives.Tests;

public sealed class ChannelReconnectPolicyTests
{
    [Fact]
    public void Defaults_MatchDesign()
    {
        var policy = new ChannelReconnectPolicy();

        Assert.Equal(0, policy.MaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(1), policy.InitialDelay);
        Assert.Equal(TimeSpan.FromSeconds(30), policy.MaxDelay);
        Assert.Equal(2.0, policy.BackoffMultiplier);
        Assert.Equal(0.2, policy.JitterFactor);
        Assert.Null(policy.ShouldRetry);
    }

    [Fact]
    public void Validate_Throws_WhenMaxDelaySmallerThanInitialDelay()
    {
        var policy = new ChannelReconnectPolicy
        {
            InitialDelay = TimeSpan.FromSeconds(5),
            MaxDelay = TimeSpan.FromSeconds(1),
        };

        Assert.Throws<ArgumentException>(() => policy.Validate());
    }

    [Fact]
    public void Validate_Throws_WhenJitterOutOfRange()
    {
        var policy = new ChannelReconnectPolicy { JitterFactor = 1.5 };

        Assert.Throws<ArgumentException>(() => policy.Validate());
    }
}
```

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter "FullyQualifiedName~ChannelReconnectPolicyTests"`
Expected: 编译失败，提示 `ChannelReconnectPolicy` 不存在（这是本任务的“红”）。

- [ ] 1.2 新增 `ChannelReconnectPolicy`

**Files:**
- Create: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Options/ChannelReconnectPolicy.cs`

```csharp
namespace Microsoft.Extensions.Options
{
    /// <summary>客户端重连与退避策略。</summary>
    public sealed class ChannelReconnectPolicy
    {
        /// <summary>首次失败后的最大重试次数；0 表示无限。</summary>
        public int MaxAttempts { get; set; }

        /// <summary>第一次重试前的等待，默认 1 秒。</summary>
        public TimeSpan InitialDelay { get; set; } = TimeSpan.FromSeconds(1);

        /// <summary>退避上限，默认 30 秒。</summary>
        public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>每次失败后的退避倍数，默认 2。</summary>
        public double BackoffMultiplier { get; set; } = 2.0;

        /// <summary>抖动比例，默认 0.2；范围 [0, 1]。</summary>
        public double JitterFactor { get; set; } = 0.2;

        /// <summary>自定义重试判定；null 时使用 <see cref="ChannelConnectionFailure.IsRetryable"/>。</summary>
        public Func<Exception, bool>? ShouldRetry { get; set; }

        internal Func<double> JitterSource { get; set; } = () => Random.Shared.NextDouble();

        internal void Validate()
        {
            if (MaxAttempts < 0)
                throw new ArgumentException("MaxAttempts 不能为负数。", nameof(MaxAttempts));

            if (InitialDelay <= TimeSpan.Zero || InitialDelay > TimeSpan.FromDays(1))
                throw new ArgumentException("InitialDelay 必须大于 0 且不超过 1 天。", nameof(InitialDelay));

            if (MaxDelay < InitialDelay || MaxDelay > TimeSpan.FromDays(1))
                throw new ArgumentException("MaxDelay 必须不小于 InitialDelay 且不超过 1 天。", nameof(MaxDelay));

            if (BackoffMultiplier < 1 || double.IsNaN(BackoffMultiplier) || double.IsInfinity(BackoffMultiplier))
                throw new ArgumentException("BackoffMultiplier 必须是大于等于 1 的有限数。", nameof(BackoffMultiplier));

            if (JitterFactor is < 0 or > 1 || double.IsNaN(JitterFactor))
                throw new ArgumentException("JitterFactor 必须在 [0, 1] 内。", nameof(JitterFactor));
        }

        internal ChannelReconnectPolicy Clone()
        {
            var clone = (ChannelReconnectPolicy)MemberwiseClone();
            clone.JitterSource = JitterSource;
            return clone;
        }
    }
}
```

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter "FullyQualifiedName~ChannelReconnectPolicyTests"`
Expected: 三个测试通过。

- [ ] 1.3 给 `ChannelClientOptions` 增加 `Reconnect`

**Files:**
- Modify: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Options/ChannelClientOptions.cs`
- Modify: `tests/Galosys.Foundation.Primitives.Tests/ChannelClientOptionsTests.cs`

在 `Tls` 之后增加：

```csharp
/// <summary>重连策略；null 表示失败后不重试。</summary>
public ChannelReconnectPolicy? Reconnect { get; set; }
```

在 `Tls?.ValidateClient();` 之后增加：

```csharp
Reconnect?.Validate();
```

在 `Clone()` 的 `return clone;` 之前增加：

```csharp
if (Reconnect is not null)
    clone.Reconnect = Reconnect.Clone();
```

在 `ChannelClientOptionsTests` 增加：

```csharp
[Fact]
public void Validate_Throws_WhenReconnectInvalid()
{
    var options = new ChannelClientOptions
    {
        EndPoint = new IPEndPoint(IPAddress.Loopback, 12345),
        Reconnect = new ChannelReconnectPolicy { InitialDelay = TimeSpan.Zero },
    };

    Assert.Throws<ArgumentException>(() => options.Validate());
}

[Fact]
public void Clone_CopiesReconnectPolicy()
{
    var options = new ChannelClientOptions
    {
        EndPoint = new IPEndPoint(IPAddress.Loopback, 12345),
        Reconnect = new ChannelReconnectPolicy { MaxAttempts = 3 },
    };

    var clone = options.Clone();
    options.Reconnect!.MaxAttempts = 9;

    Assert.NotSame(options.Reconnect, clone.Reconnect);
    Assert.Equal(3, clone.Reconnect!.MaxAttempts);
}
```

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter "FullyQualifiedName~ChannelReconnectPolicyTests|FullyQualifiedName~ChannelClientOptionsTests"`
Expected: 全部通过。

## 2. 单次尝试与首次连接重试

- [ ] 2.1 写首次连接重试失败测试

**Files:**
- Create: `tests/Galosys.Foundation.Primitives.Tests/ChannelReconnectTests.cs`

```csharp
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting.Net;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Galosys.Foundation.Primitives.Tests;

public sealed class ChannelReconnectTests
{
    [Fact]
    public async Task ConnectAsync_RetriesInitialConnection_WithFakeClock()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var endpoint = (IPEndPoint)probe.LocalEndpoint;
        probe.Stop();

        var time = new FakeTimeProvider();

        await using var client = new ChannelClient(new ChannelClientOptions
        {
            EndPoint = endpoint,
            ConnectTimeout = TimeSpan.FromSeconds(2),
            TimeProvider = time,
            Reconnect = new ChannelReconnectPolicy
            {
                MaxAttempts = 3,
                InitialDelay = TimeSpan.FromMilliseconds(100),
                MaxDelay = TimeSpan.FromSeconds(1),
                JitterFactor = 0,
            },
        });

        var connect = client.ConnectAsync(CancellationToken.None);

        await TestClient.WaitUntilAsync(
            () => Task.FromResult(client.State == ChannelConnectionState.Reconnecting),
            "客户端未进入 Reconnecting。");

        await using var server = await TestServer.StartAsync(
            TestHandlers.EchoAsync,
            options => options.EndPoint = endpoint);

        time.Advance(TimeSpan.FromMilliseconds(100));
        await connect.WaitAsync(TestWaits.DefaultTimeout);

        Assert.Equal(ChannelConnectionState.Connected, client.State);
        Assert.NotNull(client.Connection);
    }
}
```

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter "FullyQualifiedName~ChannelReconnectTests"`
Expected: 测试失败，`ConnectAsync` 在首次拒绝后直接抛出（这是本任务的“红”）。

- [ ] 2.2 抽出 `ConnectOnceAsync` 并保留单次行为

**Files:**
- Modify: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Client/ChannelClient.cs`

把 `ConnectAsync` 中的任务创建改为：

```csharp
_connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _closed.Token);
_connectTask = RunConnectOperationAsync(_connectCts, ct);
```

把原 `ConnectCoreAsync` 的方法体拆成 `ConnectOnceAsync`：它只负责一次 socket/TLS/context 建立，成功时赋值 `_connection`、`_transportConnection`、`_tls`，失败时清理并抛出，不发布 `Connecting` / `Connected` / `Disconnected`。关键签名：

```csharp
private async Task ConnectOnceAsync(
    CancellationToken operationToken,
    CancellationToken callerToken)
{
    using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(operationToken);
    attemptCts.CancelAfter(_options.ConnectTimeout);

    Socket? socket = null;
    SocketChannelConnection? connection = null;
    TlsDuplexPipe? tls = null;
    ChannelContext? context = null;

    try
    {
        socket = CreateSocket(_options.EndPoint);
        ApplySocketOptions(socket, _options);
        await socket.ConnectAsync(_options.EndPoint, attemptCts.Token).ConfigureAwait(false);

        connection = new SocketChannelConnection(
            socket,
            new ChannelServerOptions { EndPoint = _options.EndPoint },
            logger: null);
        socket = null;

        if (_options.Tls is not null)
        {
            tls = await TlsDuplexPipe.CreateAsync(
                connection.Transport,
                _options.Tls,
                isServer: false,
                attemptCts.Token).ConfigureAwait(false);
        }

        context = new ChannelContext(
            Guid.NewGuid().ToString("N"),
            connection,
            EmptyServiceProvider.Instance);

        if (tls is not null)
            context.UseTransport(tls);

        lock (_gate)
        {
            if (_state == ChannelConnectionState.Closed)
                throw new OperationCanceledException(_closed.Token);

            _connection = context;
            _transportConnection = connection;
            _tls = tls;
        }

        connection = null;
        tls = null;
        context = null;
    }
    catch (OperationCanceledException) when (attemptCts.IsCancellationRequested)
    {
        await CleanupAttemptAsync(socket, connection, tls, context).ConfigureAwait(false);

        if (_closed.IsCancellationRequested || callerToken.IsCancellationRequested)
            throw new OperationCanceledException(operationToken);

        throw new TimeoutException($"连接在 {_options.ConnectTimeout} 内未完成。");
    }
    catch
    {
        await CleanupAttemptAsync(socket, connection, tls, context).ConfigureAwait(false);
        throw;
    }
}
```

`RunConnectOperationAsync` 暂时只做一次尝试：

```csharp
private async Task RunConnectOperationAsync(
    CancellationTokenSource operationCts,
    CancellationToken callerToken)
{
    await Task.Yield();

    try
    {
        SetState(ChannelConnectionState.Connecting);
        await ConnectOnceAsync(operationCts.Token, callerToken).ConfigureAwait(false);
        SetState(ChannelConnectionState.Connected);
        WatchCompletion();
    }
    catch (OperationCanceledException)
    {
        if (!_closed.IsCancellationRequested)
            SetState(ChannelConnectionState.Disconnected, CanceledFailure(operationCts.Token));
        throw;
    }
    catch (Exception ex)
    {
        SetState(ChannelConnectionState.Disconnected, ChannelConnectionFailure.FromException(ex));
        throw;
    }
    finally
    {
        lock (_gate)
        {
            if (ReferenceEquals(_connectCts, operationCts))
            {
                _connectCts = null;
                _connectTask = null;
            }
        }

        operationCts.Dispose();
    }
}

private static ChannelConnectionFailure CanceledFailure(CancellationToken ct)
    => new(ChannelConnectionFailureKind.Canceled, new OperationCanceledException(ct));
```

`CleanupAttemptAsync` 增加 TLS 参数并在 context/connection 之前释放。此任务完成后：

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter "FullyQualifiedName~ChannelClientTests"`
Expected: 现有客户端测试通过；重连测试仍失败。

- [ ] 2.3 加入重试循环与退避

**Files:**
- Modify: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Client/ChannelClient.cs`

把 `RunConnectOperationAsync` 的 `try` 主体替换为循环：

```csharp
var policy = _options.Reconnect;
var delay = policy?.InitialDelay ?? TimeSpan.Zero;
var retries = 0;

SetState(ChannelConnectionState.Connecting);

while (true)
{
    try
    {
        await ConnectOnceAsync(operationCts.Token, callerToken).ConfigureAwait(false);
        SetState(ChannelConnectionState.Connected);
        WatchCompletion();
        return;
    }
    catch (OperationCanceledException)
    {
        throw;
    }
    catch (Exception ex)
    {
        var failure = ChannelConnectionFailure.FromException(ex);

        if (!CanRetry(policy, failure)
            || (policy!.MaxAttempts != 0 && retries >= policy.MaxAttempts))
        {
            SetState(ChannelConnectionState.Disconnected, failure);
            throw;
        }

        SetState(ChannelConnectionState.Reconnecting, failure);
        await DelayBeforeRetryAsync(delay, operationCts.Token).ConfigureAwait(false);

        retries++;
        delay = NextDelay(delay, policy);
    }
}
```

增加辅助方法：

```csharp
private static bool CanRetry(
    ChannelReconnectPolicy? policy,
    ChannelConnectionFailure failure)
{
    if (policy is null)
        return false;

    return policy.ShouldRetry?.Invoke(failure.Exception) ?? failure.IsRetryable;
}

private static TimeSpan NextDelay(TimeSpan current, ChannelReconnectPolicy policy)
{
    var next = TimeSpan.FromTicks((long)(current.Ticks * policy.BackoffMultiplier));
    return next > policy.MaxDelay ? policy.MaxDelay : next;
}

private async Task DelayBeforeRetryAsync(TimeSpan delay, CancellationToken ct)
{
    var policy = _options.Reconnect!;
    var random = policy.JitterSource();
    var factor = 1 + ((random * 2) - 1) * policy.JitterFactor;
    var jittered = TimeSpan.FromTicks(Math.Max(1, (long)(delay.Ticks * factor)));

    await Task.Delay(jittered, _options.TimeProvider, ct).ConfigureAwait(false);
}
```

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter "FullyQualifiedName~ChannelReconnectTests"`
Expected: `ConnectAsync_RetriesInitialConnection_WithFakeClock` 通过。

- [ ] 2.4 让取消与关闭打断退避

**Files:**
- Modify: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Client/ChannelClient.cs`

确认 `DelayBeforeRetryAsync` 使用 `operationCts.Token`；`CloseAsync` 在把状态置为 `Closed` 后调用 `_connectCts?.Cancel()`。在 `RunConnectOperationAsync` 的 `OperationCanceledException` 分支中，若 `_closed.IsCancellationRequested` 则不发布 `Disconnected`，否则发布 `Canceled` 失败。补测试：

```csharp
[Fact]
public async Task ConnectAsync_CallerCancellation_DuringBackoff_EndsDisconnected()
{
    var probe = new TcpListener(IPAddress.Loopback, 0);
    probe.Start();
    var endpoint = (IPEndPoint)probe.LocalEndpoint;
    probe.Stop();

    var time = new FakeTimeProvider();
    await using var client = new ChannelClient(new ChannelClientOptions
    {
        EndPoint = endpoint,
        ConnectTimeout = TimeSpan.FromSeconds(2),
        TimeProvider = time,
        Reconnect = new ChannelReconnectPolicy
        {
            MaxAttempts = 0,
            InitialDelay = TimeSpan.FromSeconds(1),
            MaxDelay = TimeSpan.FromSeconds(1),
            JitterFactor = 0,
        },
    });

    using var cts = new CancellationTokenSource();
    var connect = client.ConnectAsync(cts.Token);

    await TestClient.WaitUntilAsync(
        () => Task.FromResult(client.State == ChannelConnectionState.Reconnecting),
        "客户端未进入 Reconnecting。");

    cts.Cancel();
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connect);
    Assert.Equal(ChannelConnectionState.Disconnected, client.State);
}
```

Run: 上面命令的 filter 加上 `ConnectAsync_CallerCancellation_DuringBackoff_EndsDisconnected`。
Expected: 测试通过。

## 3. 断线后台重连

- [ ] 3.1 写已连接断线重连测试

**Files:**
- Modify: `tests/Galosys.Foundation.Primitives.Tests/ChannelReconnectTests.cs`

```csharp
[Fact]
public async Task ConnectionDropped_ReconnectsOnNextAttempt()
{
    var connections = 0;

    await using var server = await TestServer.StartAsync(async context =>
    {
        if (Interlocked.Increment(ref connections) == 1)
        {
            context.Abort(new IOException("drop-first"));
            return;
        }

        await TestHandlers.EchoAsync(context);
    });

    var time = new FakeTimeProvider();
    await using var client = new ChannelClient(new ChannelClientOptions
    {
        EndPoint = server.EndPoint,
        ConnectTimeout = TimeSpan.FromSeconds(2),
        TimeProvider = time,
        Reconnect = new ChannelReconnectPolicy
        {
            MaxAttempts = 3,
            InitialDelay = TimeSpan.FromMilliseconds(100),
            MaxDelay = TimeSpan.FromSeconds(1),
            JitterFactor = 0,
        },
    });

    await client.ConnectAsync(CancellationToken.None);
    var firstId = client.Connection!.ConnectionId;

    await TestClient.WaitUntilAsync(
        () => Task.FromResult(client.State == ChannelConnectionState.Reconnecting),
        "客户端未在断线后进入 Reconnecting。");

    time.Advance(TimeSpan.FromMilliseconds(100));

    await TestClient.WaitUntilAsync(
        () => Task.FromResult(client.State == ChannelConnectionState.Connected),
        "客户端未在退避后重连成功。");

    Assert.NotNull(client.Connection);
    Assert.NotEqual(firstId, client.Connection!.ConnectionId);
    Assert.True(Volatile.Read(ref connections) >= 2);
}
```

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter "FullyQualifiedName~ConnectionDropped_ReconnectsOnNextAttempt"`
Expected: 失败，状态停在 `Disconnected`（这是本任务的“红”）。

- [ ] 3.2 实现后台重连与 watcher 分支

**Files:**
- Modify: `src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Client/ChannelClient.cs`

把 `WatchCompletionAsync` 末尾改为：

```csharp
var failure = error is not null
    ? ChannelConnectionFailure.FromException(error)
    : new ChannelConnectionFailure(
        ChannelConnectionFailureKind.Network,
        new IOException("连接已由对端关闭。"));

if (_closed.IsCancellationRequested)
    return;

if (CanRetry(_options.Reconnect, failure))
    StartBackgroundReconnect(failure);
else
    SetState(ChannelConnectionState.Disconnected, failure);
```

增加：

```csharp
private void StartBackgroundReconnect(ChannelConnectionFailure failure)
{
    ChannelStateChangedEventArgs? args = null;
    Task task;

    lock (_gate)
    {
        if (_state == ChannelConnectionState.Closed || _connectTask is not null)
            return;

        args = new ChannelStateChangedEventArgs(
            _state,
            ChannelConnectionState.Reconnecting,
            failure);
        _state = ChannelConnectionState.Reconnecting;
        task = _connectTask = RunBackgroundReconnectAsync();
    }

    RaiseStateChanged(args);
    _ = task.ContinueWith(
        static t => _ = t.Exception,
        CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted,
        TaskScheduler.Default);
}

private async Task RunBackgroundReconnectAsync()
{
    await Task.Yield();

    var policy = _options.Reconnect!;
    var delay = policy.InitialDelay;
    var retries = 0;
    var failure = new ChannelConnectionFailure(
        ChannelConnectionFailureKind.Network,
        new IOException("连接已由对端关闭。"));

    try
    {
        while (!_closed.IsCancellationRequested)
        {
            await DelayBeforeRetryAsync(delay, _closed.Token).ConfigureAwait(false);
            retries++;

            try
            {
                await ConnectOnceAsync(_closed.Token, CancellationToken.None).ConfigureAwait(false);
                SetState(ChannelConnectionState.Connected);
                WatchCompletion();
                return;
            }
            catch (OperationCanceledException) when (_closed.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failure = ChannelConnectionFailure.FromException(ex);

                if (!CanRetry(policy, failure)
                    || (policy.MaxAttempts != 0 && retries >= policy.MaxAttempts))
                {
                    SetState(ChannelConnectionState.Disconnected, failure);
                    throw;
                }

                delay = NextDelay(delay, policy);
            }
        }
    }
    catch (OperationCanceledException) when (_closed.IsCancellationRequested)
    {
    }
    finally
    {
        lock (_gate)
            _connectTask = null;
    }
}
```

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter "FullyQualifiedName~ChannelReconnectTests"`
Expected: 断线重连测试通过。

- [ ] 3.3 补非可重试与尝试耗尽测试

**Files:**
- Modify: `tests/Galosys.Foundation.Primitives.Tests/ChannelReconnectTests.cs`

```csharp
[Fact]
public async Task TlsCertificateFailure_DoesNotEnterReconnecting()
{
    using var certificate = TlsTestCertificates.CreateSelfSigned();

    await using var server = new ChannelServer(
        new ChannelServerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            ShutdownTimeout = TimeSpan.FromSeconds(2),
        },
        pipeline =>
        {
            pipeline.UseTls(new ChannelTlsOptions
            {
                ServerOptions = new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                },
            });
            pipeline.Run(TestHandlers.EchoAsync);
        });

    await server.StartAsync(CancellationToken.None);

    var time = new FakeTimeProvider();
    await using var client = new ChannelClient(new ChannelClientOptions
    {
        EndPoint = server.LocalEndPoint!,
        ConnectTimeout = TimeSpan.FromSeconds(2),
        TimeProvider = time,
        Reconnect = new ChannelReconnectPolicy
        {
            MaxAttempts = 3,
            InitialDelay = TimeSpan.FromMilliseconds(100),
            MaxDelay = TimeSpan.FromSeconds(1),
            JitterFactor = 0,
        },
        Tls = new ChannelTlsOptions
        {
            ClientOptions = new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                RemoteCertificateValidationCallback = (_, _, _, _) => false,
            },
        },
    });

    var states = new List<ChannelConnectionState>();
    client.StateChanged += (_, args) => states.Add(args.NewState);

    await Assert.ThrowsAsync<AuthenticationException>(
        () => client.ConnectAsync(CancellationToken.None));

    Assert.DoesNotContain(ChannelConnectionState.Reconnecting, states);
    Assert.Equal(ChannelConnectionState.Disconnected, client.State);
}

[Fact]
public async Task MaxAttemptsExhausted_EndsDisconnected()
{
    var probe = new TcpListener(IPAddress.Loopback, 0);
    probe.Start();
    var endpoint = (IPEndPoint)probe.LocalEndpoint;
    probe.Stop();

    var time = new FakeTimeProvider();
    await using var client = new ChannelClient(new ChannelClientOptions
    {
        EndPoint = endpoint,
        ConnectTimeout = TimeSpan.FromSeconds(2),
        TimeProvider = time,
        Reconnect = new ChannelReconnectPolicy
        {
            MaxAttempts = 2,
            InitialDelay = TimeSpan.FromMilliseconds(100),
            MaxDelay = TimeSpan.FromMilliseconds(100),
            JitterFactor = 0,
        },
    });

    var connect = client.ConnectAsync(CancellationToken.None);

    await TestClient.WaitUntilAsync(
        () => Task.FromResult(client.State == ChannelConnectionState.Reconnecting),
        "客户端未进入 Reconnecting。");

    time.Advance(TimeSpan.FromMilliseconds(100));
    await Task.Delay(50);
    time.Advance(TimeSpan.FromMilliseconds(100));

    await Assert.ThrowsAnyAsync<SocketException>(() => connect);
    Assert.Equal(ChannelConnectionState.Disconnected, client.State);
}
```

在文件顶部补充 `System.Net.Security`、`System.Security.Authentication`、`System.Security.Cryptography`。

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter "FullyQualifiedName~TlsCertificateFailure_DoesNotEnterReconnecting|FullyQualifiedName~MaxAttemptsExhausted_EndsDisconnected"`
Expected: 两个测试通过。

- [ ] 3.4 补关闭打断退避测试

**Files:**
- Modify: `tests/Galosys.Foundation.Primitives.Tests/ChannelReconnectTests.cs`

```csharp
[Fact]
public async Task CloseAsync_DuringReconnecting_StopsFurtherAttempts()
{
    var connections = 0;
    await using var server = await TestServer.StartAsync(async context =>
    {
        Interlocked.Increment(ref connections);
        context.Abort(new IOException("drop"));
        await Task.CompletedTask;
    });

    var time = new FakeTimeProvider();
    await using var client = new ChannelClient(new ChannelClientOptions
    {
        EndPoint = server.EndPoint,
        ConnectTimeout = TimeSpan.FromSeconds(2),
        TimeProvider = time,
        Reconnect = new ChannelReconnectPolicy
        {
            MaxAttempts = 0,
            InitialDelay = TimeSpan.FromSeconds(1),
            MaxDelay = TimeSpan.FromSeconds(1),
            JitterFactor = 0,
        },
    });

    await client.ConnectAsync(CancellationToken.None);

    await TestClient.WaitUntilAsync(
        () => Task.FromResult(client.State == ChannelConnectionState.Reconnecting),
        "客户端未进入 Reconnecting。");

    var before = Volatile.Read(ref connections);
    await client.CloseAsync(CancellationToken.None);
    time.Advance(TimeSpan.FromSeconds(5));
    await Task.Delay(100);

    Assert.Equal(ChannelConnectionState.Closed, client.State);
    Assert.Equal(before, Volatile.Read(ref connections));
}
```

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter "FullyQualifiedName~CloseAsync_DuringReconnecting_StopsFurtherAttempts"`
Expected: 测试通过。

## 4. 策略覆盖、文档与验证

- [ ] 4.1 补 `ShouldRetry` 覆盖与抖动范围测试

**Files:**
- Modify: `tests/Galosys.Foundation.Primitives.Tests/ChannelReconnectPolicyTests.cs`
- Modify: `tests/Galosys.Foundation.Primitives.Tests/ChannelReconnectTests.cs`

```csharp
[Fact]
public void Clone_PreservesShouldRetryAndJitterSource()
{
    Func<Exception, bool> shouldRetry = _ => false;
    var policy = new ChannelReconnectPolicy
    {
        ShouldRetry = shouldRetry,
        JitterSource = () => 0.5,
    };

    var clone = policy.Clone();

    Assert.Same(shouldRetry, clone.ShouldRetry);
    Assert.Equal(0.5, clone.JitterSource());
}

[Fact]
public async Task ShouldRetryFalse_StopsImmediately()
{
    var probe = new TcpListener(IPAddress.Loopback, 0);
    probe.Start();
    var endpoint = (IPEndPoint)probe.LocalEndpoint;
    probe.Stop();

    await using var client = new ChannelClient(new ChannelClientOptions
    {
        EndPoint = endpoint,
        ConnectTimeout = TimeSpan.FromSeconds(2),
        TimeProvider = new FakeTimeProvider(),
        Reconnect = new ChannelReconnectPolicy
        {
            MaxAttempts = 3,
            InitialDelay = TimeSpan.FromMilliseconds(100),
            MaxDelay = TimeSpan.FromSeconds(1),
            JitterFactor = 0,
            ShouldRetry = _ => false,
        },
    });

    var states = new List<ChannelConnectionState>();
    client.StateChanged += (_, args) => states.Add(args.NewState);

    await Assert.ThrowsAnyAsync<SocketException>(
        () => client.ConnectAsync(CancellationToken.None));

    Assert.DoesNotContain(ChannelConnectionState.Reconnecting, states);
    Assert.Equal(ChannelConnectionState.Disconnected, client.State);
}
```

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter "FullyQualifiedName~ChannelReconnect"`
Expected: 全部重连测试通过。

- [ ] 4.2 审计退避没有裸 `Task.Delay`

**Files:**
- 只读检查：`src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Client/ChannelClient.cs`

Run: `rg -n "Task\.Delay\(" src/Galosys.Foundation.Primitives/Microsoft/Extensions/Hosting/Net/Client/ChannelClient.cs`
Expected: 只有 `Task.Delay(jittered, _options.TimeProvider, ct)` 一行；没有两参数或无 `TimeProvider` 的退避调用。

- [ ] 4.3 更新 Primitives README 的重连章节

**Files:**
- Modify: `src/Galosys.Foundation.Primitives/README.md`

增加：

```markdown
## 重连与退避（P4-2）

```csharp
var options = new ChannelClientOptions
{
    EndPoint = endPoint,
    Reconnect = new ChannelReconnectPolicy
    {
        MaxAttempts = 0,                 // 0 = 无限
        InitialDelay = TimeSpan.FromSeconds(1),
        MaxDelay = TimeSpan.FromSeconds(30),
        BackoffMultiplier = 2.0,
        JitterFactor = 0.2,
    },
};
```

- 首次连接失败与已连接断线共用同一套退避。
- 重连成功产生新的 `ChannelContext` 与新 `ConnectionId`；断开期间不缓冲、不重放消息。
- `MaxAttempts = 0` 时 `ConnectAsync` 可能一直等待，调用方必须传入可取消 token。
- TLS 证书失败、调用方取消与配置错误不会重试。
- 连接池与 batch 不在本阶段。
```

- [ ] 4.4 跑构建、全量测试与 OpenSpec 校验

Run: `dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`
Expected: 0 errors。

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj`
Expected: 现有测试与新增 `ChannelReconnectPolicyTests` / `ChannelReconnectTests` 全部通过。

Run: `openspec validate "primitives-client-2" --strict --no-interactive`
Expected: `Change 'primitives-client-2' is valid`。
