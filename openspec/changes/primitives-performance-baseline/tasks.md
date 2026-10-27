# Primitives 性能基线 Implementation Plan

> **REQUIRED SUB-SKILL:** 使用 `executing-plans` 或 `opsx-apply-x` 逐任务执行本计划。
> 本仓库虽有 git，但按约定不自动提交；步骤中的“验证”统一用构建、冒烟运行与 OpenSpec 校验，不执行 git commit。

**Goal:** 新建独立的 BenchmarkDotNet 工程，覆盖回显传输、framing、JSON codec 与 session 往返四类场景，提供可复现的手动基线、结果记录模板和优化决策门；不修改任何生产代码，不设 CI 门禁。

**Architecture:** `benchmarks/Galosys.Foundation.Primitives.Benchmarks/` 引用 Primitives，用 `LoopbackServer` 在进程内启动绑定 `127.0.0.1:0` 的 `ChannelServer`，`RawEchoClient`/`SessionEchoClient` 建立持久连接；benchmark 类只负责参数化与调用，缓冲在 `[GlobalSetup]` 创建并复用。服务端 session 场景使用 `AsJsonSession<EchoMessage>()`，客户端使用公共 `LengthPrefixedFramer` 与 `JsonMessageCodec<EchoMessage>`。

**Tech Stack:** .NET 10、BenchmarkDotNet 0.15.8、`System.Net.Sockets`、`System.IO.Pipelines`、`System.Text.Json`、`Galosys.Foundation.Primitives`（P1.5a + P2）。

---

## 1. 项目骨架

- [ ] 1.1 创建 benchmark 项目文件

**Files:**
- Create: `benchmarks/Galosys.Foundation.Primitives.Benchmarks/Galosys.Foundation.Primitives.Benchmarks.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
    <RootNamespace>Galosys.Foundation.Primitives.Benchmarks</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="BenchmarkDotNet" Version="0.15.8" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Galosys.Foundation.Primitives\Galosys.Foundation.Primitives.csproj" />
  </ItemGroup>

</Project>
```

不要把项目加入 `Galosys.Foundation.Next.slnx`；该解决方案当前不可构建。

Run: `dotnet restore benchmarks/Galosys.Foundation.Primitives.Benchmarks/Galosys.Foundation.Primitives.Benchmarks.csproj`
Expected: 还原成功，解析到 `BenchmarkDotNet 0.15.8`。

- [ ] 1.2 增加程序入口与 payload 设施

**Files:**
- Create: `benchmarks/Galosys.Foundation.Primitives.Benchmarks/Program.cs`
- Create: `benchmarks/Galosys.Foundation.Primitives.Benchmarks/Infrastructure/BenchmarkPayload.cs`

```csharp
// Program.cs
using BenchmarkDotNet.Running;

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
```

```csharp
// Infrastructure/BenchmarkPayload.cs
namespace Galosys.Foundation.Primitives.Benchmarks.Infrastructure;

internal sealed record EchoMessage(int Id, string Text);

internal static class BenchmarkPayload
{
    public static byte[] CreateBytes(int size)
    {
        var payload = new byte[size];
        for (var i = 0; i < payload.Length; i++)
            payload[i] = (byte)('a' + (i % 26));

        return payload;
    }

    public static EchoMessage CreateMessage(int textLength)
        => new(1, new string('x', textLength));
}
```

Run: `dotnet build benchmarks/Galosys.Foundation.Primitives.Benchmarks/Galosys.Foundation.Primitives.Benchmarks.csproj -c Release`
Expected: 0 errors（此时还没有基准类，BenchmarkSwitcher 可正常启动并提示没有基准）。

## 2. 回显基础设施

- [ ] 2.1 增加 loopback server 与两种 handler

**Files:**
- Create: `benchmarks/Galosys.Foundation.Primitives.Benchmarks/Infrastructure/LoopbackServer.cs`
- Create: `benchmarks/Galosys.Foundation.Primitives.Benchmarks/Infrastructure/EchoHandlers.cs`

```csharp
// Infrastructure/LoopbackServer.cs
using System.Net;
using Microsoft.Extensions.Hosting.Net;
using Microsoft.Extensions.Options;

namespace Galosys.Foundation.Primitives.Benchmarks.Infrastructure;

internal sealed class LoopbackServer : IAsyncDisposable
{
    private readonly ChannelServer _server;

    private LoopbackServer(ChannelServer server, IPEndPoint endPoint)
    {
        _server = server;
        EndPoint = endPoint;
    }

    public IPEndPoint EndPoint { get; }

    public static async Task<LoopbackServer> StartAsync(
        Func<ChannelContext, Task> handler,
        CancellationToken ct = default)
    {
        var server = new ChannelServer(
            new ChannelServerOptions
            {
                EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                NoDelay = true,
                ShutdownTimeout = TimeSpan.FromSeconds(5),
            },
            pipeline => pipeline.Run(handler));

        await server.StartAsync(ct);
        return new LoopbackServer(server, (IPEndPoint)server.LocalEndPoint!);
    }

    public ValueTask DisposeAsync() => _server.DisposeAsync();
}
```

```csharp
// Infrastructure/EchoHandlers.cs
using System.IO;
using Microsoft.Extensions.Hosting.Net;

namespace Galosys.Foundation.Primitives.Benchmarks.Infrastructure;

internal static class EchoHandlers
{
    public static async Task RawAsync(ChannelContext context)
    {
        while (true)
        {
            var result = await context.Reader.ReadAsync(context.ConnectionClosed);
            if (result.IsCompleted && result.Buffer.IsEmpty)
                break;

            foreach (var segment in result.Buffer)
                await context.Writer.WriteAsync(segment, context.ConnectionClosed);

            context.Reader.AdvanceTo(result.Buffer.End);
            await context.Writer.FlushAsync(context.ConnectionClosed);
        }
    }

    public static async Task JsonSessionAsync(ChannelContext context)
    {
        await using var session = context.AsJsonSession<EchoMessage>();

        while (true)
        {
            EchoMessage message;
            try
            {
                message = await session.ReadAsync(context.ConnectionClosed);
            }
            catch (EndOfStreamException)
            {
                break;
            }

            await session.WriteAsync(message, context.ConnectionClosed);
        }
    }
}
```

`RawAsync` 与现有 Primitives README 的回显循环一致；`JsonSessionAsync` 依赖 P2 的 `AsJsonSession<T>()` 与 `IChannelSession<T>.ReadAsync/WriteAsync`。

Run: `dotnet build benchmarks/Galosys.Foundation.Primitives.Benchmarks/Galosys.Foundation.Primitives.Benchmarks.csproj -c Release`
Expected: 0 errors。

- [ ] 2.2 增加原始 socket client 与 session client

**Files:**
- Create: `benchmarks/Galosys.Foundation.Primitives.Benchmarks/Infrastructure/RawEchoClient.cs`
- Create: `benchmarks/Galosys.Foundation.Primitives.Benchmarks/Infrastructure/SessionEchoClient.cs`

```csharp
// Infrastructure/RawEchoClient.cs
using System.Net;
using System.Net.Sockets;

namespace Galosys.Foundation.Primitives.Benchmarks.Infrastructure;

internal sealed class RawEchoClient : IDisposable
{
    private readonly NetworkStream _stream;

    private RawEchoClient(NetworkStream stream) => _stream = stream;

    public static async Task<RawEchoClient> ConnectAsync(
        IPEndPoint endPoint,
        CancellationToken ct = default)
    {
        var socket = new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
        };

        await socket.ConnectAsync(endPoint, ct);
        return new RawEchoClient(new NetworkStream(socket, ownsSocket: true));
    }

    public async Task RoundTripAsync(
        ReadOnlyMemory<byte> payload,
        Memory<byte> receiveBuffer,
        CancellationToken ct = default)
    {
        await _stream.WriteAsync(payload, ct);
        await _stream.ReadExactlyAsync(receiveBuffer[..payload.Length], ct);
    }

    public void Dispose() => _stream.Dispose();
}
```

`SessionEchoClient` 复用 writer 与接收缓冲，客户端组帧与 P2 默认协议一致（4 字节大端长度头）：

```csharp
// Infrastructure/SessionEchoClient.cs（核心成员）
using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting.Net;

namespace Galosys.Foundation.Primitives.Benchmarks.Infrastructure;

internal sealed class SessionEchoClient : IDisposable
{
    private const int HeaderSize = 4;
    private const int MaxMessageSize = 1024 * 1024;

    private readonly NetworkStream _stream;
    private readonly LengthPrefixedFramer _framer = new();
    private readonly JsonMessageCodec<EchoMessage> _codec = new();
    private readonly ArrayBufferWriter<byte> _payloadWriter = new(MaxMessageSize);
    private readonly ArrayBufferWriter<byte> _frameWriter = new(MaxMessageSize);
    private readonly byte[] _header = new byte[HeaderSize];
    private readonly byte[] _receive = new byte[MaxMessageSize];

    private SessionEchoClient(NetworkStream stream) => _stream = stream;

    public static async Task<SessionEchoClient> ConnectAsync(
        IPEndPoint endPoint,
        CancellationToken ct = default)
    {
        var socket = new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
        };

        await socket.ConnectAsync(endPoint, ct);
        return new SessionEchoClient(new NetworkStream(socket, ownsSocket: true));
    }

    public async Task<EchoMessage> RoundTripAsync(
        EchoMessage message,
        CancellationToken ct = default)
    {
        _payloadWriter.Clear();
        _codec.Encode(_payloadWriter, message);

        _frameWriter.Clear();
        _framer.WriteFrame(
            _frameWriter,
            new ReadOnlySequence<byte>(_payloadWriter.WrittenMemory));

        await _stream.WriteAsync(_frameWriter.WrittenMemory, ct);

        await _stream.ReadExactlyAsync(_header.AsMemory(), ct);
        var length = BinaryPrimitives.ReadInt32BigEndian(_header);
        if (length < 0 || length > MaxMessageSize)
            throw new InvalidDataException($"Invalid frame length: {length}.");

        await _stream.ReadExactlyAsync(_receive.AsMemory(0, length), ct);
        return _codec.Decode(new ReadOnlySequence<byte>(_receive.AsMemory(0, length)));
    }

    public void Dispose() => _stream.Dispose();
}
```

文件顶部补 `using System.IO;`。`MaxMessageSize` 与 `MessageChannelOptions` 默认 1 MiB 对齐，避免 4 KiB 场景之外的缓冲扩容。

Run: `dotnet build benchmarks/Galosys.Foundation.Primitives.Benchmarks/Galosys.Foundation.Primitives.Benchmarks.csproj -c Release`
Expected: 0 errors。

## 3. 基准场景

- [ ] 3.1 增加原始回显往返基准

**Files:**
- Create: `benchmarks/Galosys.Foundation.Primitives.Benchmarks/Benchmarks/EchoRoundTripBenchmarks.cs`

```csharp
using BenchmarkDotNet.Attributes;
using Galosys.Foundation.Primitives.Benchmarks.Infrastructure;

namespace Galosys.Foundation.Primitives.Benchmarks.Cases;

[MemoryDiagnoser]
public class EchoRoundTripBenchmarks
{
    [Params(64, 4096, 65536)]
    public int PayloadSize { get; set; }

    private LoopbackServer _server = null!;
    private RawEchoClient _client = null!;
    private byte[] _payload = null!;
    private byte[] _receive = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _server = await LoopbackServer.StartAsync(EchoHandlers.RawAsync);
        _client = await RawEchoClient.ConnectAsync(_server.EndPoint);
        _payload = BenchmarkPayload.CreateBytes(PayloadSize);
        _receive = new byte[PayloadSize];
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        _client.Dispose();
        await _server.DisposeAsync();
    }

    [Benchmark]
    public Task RoundTrip() => _client.RoundTripAsync(_payload, _receive);
}
```

Run: `dotnet build benchmarks/Galosys.Foundation.Primitives.Benchmarks/Galosys.Foundation.Primitives.Benchmarks.csproj -c Release`
Expected: 0 errors。

- [ ] 3.2 增加 framing 解析与写入基准

**Files:**
- Create: `benchmarks/Galosys.Foundation.Primitives.Benchmarks/Benchmarks/FramingBenchmarks.cs`

```csharp
using System.Buffers;
using BenchmarkDotNet.Attributes;
using Galosys.Foundation.Primitives.Benchmarks.Infrastructure;
using Microsoft.Extensions.Hosting.Net;

namespace Galosys.Foundation.Primitives.Benchmarks.Cases;

[MemoryDiagnoser]
public class FramingBenchmarks
{
    [Params(64, 4096, 65536)]
    public int PayloadSize { get; set; }

    private readonly LengthPrefixedFramer _framer = new();
    private readonly ArrayBufferWriter<byte> _writer = new();
    private ReadOnlySequence<byte> _frame;

    [GlobalSetup]
    public void Setup()
    {
        var payload = BenchmarkPayload.CreateBytes(PayloadSize);
        _framer.WriteFrame(_writer, new ReadOnlySequence<byte>(payload));
        _frame = new ReadOnlySequence<byte>(_writer.WrittenMemory.ToArray());
    }

    [Benchmark]
    public long Parse() => _framer.Parse(_frame).Frame.Length;

    [Benchmark]
    public int WriteFrame()
    {
        _writer.Clear();
        _framer.WriteFrame(_writer, _frame.Slice(4));
        return _writer.WrittenCount;
    }
}
```

`_frame.Slice(4)` 去掉 `LengthPrefixedFramer` 默认的 4 字节大端长度头，只把 payload 交给 `WriteFrame`。

Run: `dotnet build benchmarks/Galosys.Foundation.Primitives.Benchmarks/Galosys.Foundation.Primitives.Benchmarks.csproj -c Release`
Expected: 0 errors。

- [ ] 3.3 增加 JSON codec 编解码基准

**Files:**
- Create: `benchmarks/Galosys.Foundation.Primitives.Benchmarks/Benchmarks/JsonCodecBenchmarks.cs`

```csharp
using System.Buffers;
using BenchmarkDotNet.Attributes;
using Galosys.Foundation.Primitives.Benchmarks.Infrastructure;
using Microsoft.Extensions.Hosting.Net;

namespace Galosys.Foundation.Primitives.Benchmarks.Cases;

[MemoryDiagnoser]
public class JsonCodecBenchmarks
{
    [Params(64, 4096)]
    public int TextLength { get; set; }

    private readonly JsonMessageCodec<EchoMessage> _codec = new();
    private readonly ArrayBufferWriter<byte> _writer = new();
    private EchoMessage _message = null!;
    private ReadOnlySequence<byte> _frame;

    [GlobalSetup]
    public void Setup()
    {
        _message = BenchmarkPayload.CreateMessage(TextLength);
        _codec.Encode(_writer, _message);
        _frame = new ReadOnlySequence<byte>(_writer.WrittenMemory.ToArray());
    }

    [Benchmark]
    public int Encode()
    {
        _writer.Clear();
        _codec.Encode(_writer, _message);
        return _writer.WrittenCount;
    }

    [Benchmark]
    public EchoMessage Decode() => _codec.Decode(_frame);
}
```

`Decode` 使用 setup 阶段预编码的帧，不把编码时间混入解码测量；返回消息对象避免 JIT 消除调用。

Run: `dotnet build benchmarks/Galosys.Foundation.Primitives.Benchmarks/Galosys.Foundation.Primitives.Benchmarks.csproj -c Release`
Expected: 0 errors。

- [ ] 3.4 增加 session 往返基准

**Files:**
- Create: `benchmarks/Galosys.Foundation.Primitives.Benchmarks/Benchmarks/SessionRoundTripBenchmarks.cs`

```csharp
using BenchmarkDotNet.Attributes;
using Galosys.Foundation.Primitives.Benchmarks.Infrastructure;

namespace Galosys.Foundation.Primitives.Benchmarks.Cases;

[MemoryDiagnoser]
public class SessionRoundTripBenchmarks
{
    [Params(64, 4096)]
    public int TextLength { get; set; }

    private LoopbackServer _server = null!;
    private SessionEchoClient _client = null!;
    private EchoMessage _message = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _server = await LoopbackServer.StartAsync(EchoHandlers.JsonSessionAsync);
        _client = await SessionEchoClient.ConnectAsync(_server.EndPoint);
        _message = BenchmarkPayload.CreateMessage(TextLength);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        _client.Dispose();
        await _server.DisposeAsync();
    }

    [Benchmark]
    public Task<EchoMessage> RoundTrip() => _client.RoundTripAsync(_message);
}
```

服务端走 `AsJsonSession<EchoMessage>()`，客户端走同一组 framer/codec；持久连接在 setup 建立，因此结果不含 TCP 握手与 accept。

Run: `dotnet build benchmarks/Galosys.Foundation.Primitives.Benchmarks/Galosys.Foundation.Primitives.Benchmarks.csproj -c Release`
Expected: 0 errors。

## 4. 文档与结果模板

- [ ] 4.1 编写 benchmark README

**Files:**
- Create: `benchmarks/Galosys.Foundation.Primitives.Benchmarks/README.md`

内容至少包含：

````markdown
# Galosys.Foundation.Primitives Benchmarks

可选的手动基准工程，不参与 CI，不作为发布门禁。

## 环境

- Windows
- .NET SDK 10.0.401（由根 `global.json` 固定）
- Release 配置
- 建议接通电源、关闭后台负载；记录 CPU/内存/OS

## 命令

```powershell
dotnet build benchmarks/Galosys.Foundation.Primitives.Benchmarks/Galosys.Foundation.Primitives.Benchmarks.csproj -c Release

# 冒烟：只验证能启动与执行
dotnet run --project benchmarks/Galosys.Foundation.Primitives.Benchmarks/Galosys.Foundation.Primitives.Benchmarks.csproj -c Release -- --filter "*Framing*" --job dry

# 完整基线（时间较长）
dotnet run --project benchmarks/Galosys.Foundation.Primitives.Benchmarks/Galosys.Foundation.Primitives.Benchmarks.csproj -c Release -- --filter "*"
```

## 场景

| 基准 | 参数 | 说明 |
|---|---|---|
| `EchoRoundTripBenchmarks` | 64 / 4096 / 65536 B | 原始 socket + `ChannelServer` 回显 |
| `FramingBenchmarks` | 64 / 4096 / 65536 B | `LengthPrefixedFramer.Parse` / `WriteFrame` |
| `JsonCodecBenchmarks` | 64 / 4096 字符 | `JsonMessageCodec<T>.Encode` / `Decode` |
| `SessionRoundTripBenchmarks` | 64 / 4096 字符 | 服务端 session 回显 + 客户端 framer/codec |

## 解读与局限

- client 与 server 在同一个 benchmark 进程内，ns/op 包含两端成本，不是纯 transport 指标。
- 只比较同一机器、同一 SDK、同一命令下的相对变化；绝对数字不是 SLA。
- 持久连接，不测量 accept、TCP 握手、TLS、NamedPipe 或 Diagnostics 开销。
- B/op 包含协议 payload 拷贝，不应期望为 0。
- `--job dry` 只用于冒烟，不能作为基线。

## 优化决策门

以下任一条件触发时才考虑优化：

1. 4 KiB session 往返 B/op > 同 payload 原始 echo 的 2 倍；
2. 4 KiB session 往返平均延迟 > 同 payload 原始 echo 的 2 倍；
3. framing + JSON codec 合计时间 > session 往返的 50%；
4. 与已记录基线相比平均延迟回归 > 10%，且 99.9% 置信区间不重叠。

触发后先复测；确认后另开优化 change 并附基线对比。不在本 change 中修改 `src/`。

## 基线记录

按 [`results/README.md`](results/README.md) 的模板记录到 `results/baseline-<yyyy-MM-dd>-<machine>.md`。
超过 90 天、换机器或换 SDK 后必须重新记录。
````

Run: `Test-Path benchmarks/Galosys.Foundation.Primitives.Benchmarks/README.md`
Expected: `True`。

- [ ] 4.2 编写结果记录模板

**Files:**
- Create: `benchmarks/Galosys.Foundation.Primitives.Benchmarks/results/README.md`

```markdown
# 基准结果记录

真实基线由维护者在参考机器上手动运行后写入，文件名为
`baseline-<yyyy-MM-dd>-<machine>.md`。不要把 `BenchmarkDotNet.Artifacts/` 提交到仓库。

## 必填信息

- 日期：
- 机器 / CPU：
- 内存：
- OS：
- `dotnet --version`：
- commit：
- 配置：Release
- 命令：
- BDN 版本：0.15.8

## 结果

| 基准 | 参数 | Mean | Error | StdDev | Allocated |
|---|---:|---:|---:|---:|---:|
| EchoRoundTripBenchmarks.RoundTrip | 64 B | | | | |
| EchoRoundTripBenchmarks.RoundTrip | 4 KiB | | | | |
| EchoRoundTripBenchmarks.RoundTrip | 64 KiB | | | | |
| FramingBenchmarks.Parse | 4 KiB | | | | |
| FramingBenchmarks.WriteFrame | 4 KiB | | | | |
| JsonCodecBenchmarks.Encode | 4096 字符 | | | | |
| JsonCodecBenchmarks.Decode | 4096 字符 | | | | |
| SessionRoundTripBenchmarks.RoundTrip | 4096 字符 | | | | |

## 有效性

- 基线超过 90 天、机器不同或 SDK 不同：重新记录后再比较。
- 只使用同一次运行的相对结果做决策门判断。
- 不把本文件中的数字写成产品 SLA。
```

Run: `Test-Path benchmarks/Galosys.Foundation.Primitives.Benchmarks/results/README.md`
Expected: `True`。

## 5. 入口与验证

- [ ] 5.1 在根 README 增加 benchmarks 入口

**Files:**
- Modify: `README.md`

在“项目一览”表格的 `tests/Galosys.Foundation.Primitives.Tests` 行之后增加：

```markdown
| `benchmarks/Galosys.Foundation.Primitives.Benchmarks` | `benchmarks/Galosys.Foundation.Primitives.Benchmarks/` | 基准（BenchmarkDotNet） | 可选、手动运行；覆盖回显、framing、JSON codec 与 session；不参与 CI。 |
```

在“构建、测试与运行”章节之后增加：

````markdown
### 性能基准（可选）

```powershell
dotnet build benchmarks/Galosys.Foundation.Primitives.Benchmarks/Galosys.Foundation.Primitives.Benchmarks.csproj -c Release
dotnet run --project benchmarks/Galosys.Foundation.Primitives.Benchmarks/Galosys.Foundation.Primitives.Benchmarks.csproj -c Release -- --filter "*Framing*" --job dry
```

完整运行时间较长，且结果依赖机器；见 [benchmark README](benchmarks/Galosys.Foundation.Primitives.Benchmarks/README.md)。
````

同时把“目录结构”表格补一行：

```markdown
| `benchmarks/` | 可选的性能基准（手动运行） |
```

Run: `Select-String -Path README.md -Pattern "性能基准（可选）"`
Expected: 能匹配到新增标题。

- [ ] 5.2 Release 构建 benchmark 项目

Run: `dotnet build benchmarks/Galosys.Foundation.Primitives.Benchmarks/Galosys.Foundation.Primitives.Benchmarks.csproj -c Release`
Expected: 0 errors；输出中的程序集目标为 `net10.0`。

- [ ] 5.3 冒烟运行选中基准

Run: `dotnet run --project benchmarks/Galosys.Foundation.Primitives.Benchmarks/Galosys.Foundation.Primitives.Benchmarks.csproj -c Release -- --filter "*EchoRoundTrip*" --job dry`
Expected: BDN 执行 3 个 payload 参数各一次，输出 Dry 结果表；连接建立与清理无异常。

- [ ] 5.4 OpenSpec 校验与改动范围检查

Run: `openspec validate "primitives-performance-baseline" --strict --no-interactive`
Expected: `Change 'primitives-performance-baseline' is valid`。

Run: `openspec status --change "primitives-performance-baseline" --json`
Expected: `proposal`、`design`、`specs`、`tasks` 四个 artifact 均为 `done`。

Run: `git diff --stat -- src tests samples`
Expected: 无输出，确认本 change 没有修改生产代码、测试或示例。

Run: `git status --short -- benchmarks README.md`
Expected: 只列出 `benchmarks/` 新文件与 `README.md` 修改。
