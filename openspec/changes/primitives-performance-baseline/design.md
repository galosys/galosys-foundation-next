## Context

P1/P2 已经有完整的正确性测试，但没有任何性能基准：

- 测试只断言消息内容、顺序、异常和停机语义，不记录吞吐、延迟或分配；
- 一次 4 KiB 消息往返的耗时中，transport、framing、JSON codec 和 session 各占多少没有数据；
- 后续若做零拷贝、缓冲池或批处理优化，无法证明收益，也无法发现回归；
- 仓库没有 benchmark 项目，且 `.slnx` 因 `Galosys.Foundation.Nats.Net` 不可编译而不能作为统一入口。

P5 已经包含 NamedPipe 与 Diagnostics。NamedPipe 会新增一条 transport 路径，Diagnostics 会给热路径增加守卫；如果没有基线，无法判断这些改动的开销是否可接受。因此本 change 只建立可复现的测量设施和决策门，不在同一 change 里做优化。

## Goals / Non-Goals

**Goals:**

- 提供独立、可手动运行的 BenchmarkDotNet 工程，目标 `net10.0`，Release 构建。
- 覆盖四类核心场景：原始字节回显往返、`LengthPrefixedFramer` 解析/写入、`JsonMessageCodec<T>` 编码/解码、JSON session 往返。
- 每个场景输出 ns/op 与 B/op，payload 大小可参数化，便于观察随消息增大的趋势。
- 复用连接、接收缓冲和 `ArrayBufferWriter`，让分配数据主要反映库本身而不是测试设施。
- 在 benchmark README 中记录环境要求、命令、结果解读、局限和优化决策门。
- 在 `results/README.md` 中定义基线记录格式，真实数字由维护者在参考机器上手动生成。
- 在根 README 中提供入口，不把项目加入不可构建的 `.slnx`。

**Non-Goals:**

- 不修改 `src/`、`tests/`、`samples/` 的任何生产或测试代码。
- 不设置 CI 性能门禁，不把基准结果作为 PR 合并或发布阻断条件。
- 不承诺绝对延迟/吞吐 SLA；不同机器、电源模式和后台负载都会改变结果。
- 不做 Kestrel、IOQueue、`SocketAsyncEventArgs` 或其它 transport 的对照实现；这些会引入额外依赖且不是同一抽象层。
- 不测量连接建立、TLS、NamedPipe、Diagnostics 开关或 P4 客户端；这些留给各自 change 的独立基准。
- 不在本 change 内实施任何优化，也不根据一次运行直接改生产代码。
- 不把 BenchmarkDotNet 引入测试项目或生产程序集。

## Decisions

### 1. 独立 `benchmarks/` 项目，不加入 `.slnx`

项目放在 `benchmarks/Galosys.Foundation.Primitives.Benchmarks/`，与 `src/`、`tests/`、`samples/` 平级：

```text
benchmarks/Galosys.Foundation.Primitives.Benchmarks/
  Galosys.Foundation.Primitives.Benchmarks.csproj
  Program.cs
  Benchmarks/
    EchoRoundTripBenchmarks.cs
    FramingBenchmarks.cs
    JsonCodecBenchmarks.cs
    SessionRoundTripBenchmarks.cs
  Infrastructure/
    EchoHandlers.cs
    LoopbackServer.cs
    RawEchoClient.cs
    SessionEchoClient.cs
    BenchmarkPayload.cs
  results/
    README.md
  README.md
```

项目使用 `OutputType=Exe`、`net10.0`、`IsPackable=false`，只引用 Primitives 与 `BenchmarkDotNet 0.15.8`。`0.15.8` 是当前支持 .NET 10 的稳定版本，固定版本避免不同机器还原到不同 BDN 行为。

不修改 `Galosys.Foundation.Next.slnx`：该文件包含不可编译的 `Galosys.Foundation.Nats.Net`，把 benchmark 加进去不会让整体构建变得可用。README 给出单项目命令。

**备选方案：** 放进 `tests/`。否决，因为基准运行时间长、依赖 Release 与稳定机器，和快速正确性测试的生命周期不同。

### 2. 四类场景分别回答不同问题

| 基准 | 测量对象 | 参数 | 回答的问题 |
|---|---|---|---|
| `EchoRoundTripBenchmarks` | 原始 socket + `ChannelServer` 回显 | 64 B / 4 KiB / 64 KiB | transport 与 pipeline 的每往返成本 |
| `FramingBenchmarks` | `LengthPrefixedFramer.Parse` / `WriteFrame` | 64 B / 4 KiB / 64 KiB | 分帧的 CPU 与分配 |
| `JsonCodecBenchmarks` | `JsonMessageCodec<T>.Encode` / `Decode` | 64 / 4096 字符 | 序列化的 CPU 与分配 |
| `SessionRoundTripBenchmarks` | 服务端 session 回显 + 客户端 framer/codec | 64 / 4096 字符 | 组合路径的每消息成本 |

Echo 与 session 都使用持久连接，在 `[GlobalSetup]` 建立、`[GlobalCleanup]` 关闭。一次 benchmark 调用只发送并接收一条消息，因此结果表示稳态每消息成本，不包含 TCP 握手、连接关闭或服务器 accept。

JSON 不使用 64 KiB 文本，是因为 JSON 转义与字符串分配会把 session 场景的绝对时间拉得远高于其它场景，参数扫描的边际信息较少；64 B 与 4 KiB 已能区分小消息与常见业务消息。

### 3. client 与 server 同进程，结果为相对基线

benchmark 进程内启动一个 `ChannelServer`，再用原始 `Socket`/`NetworkStream` 作为 client：

```csharp
_server = await LoopbackServer.StartAsync(EchoHandlers.RawAsync);
_client = await RawEchoClient.ConnectAsync(_server.EndPoint);
```

优点是不需要额外进程编排、端口发现和清理逻辑；缺点是 ns/op 同时包含 client 与 server 两端，不能当作纯 transport 指标。README 明确要求只比较同一机器、同一 SDK、同一命令下的相对变化，不把绝对值写进 SLA。

**备选方案：** 两个进程 + 端口文件。否决，增加生命周期与失败清理复杂度，对“发现回归”这个目标收益有限。

### 4. 服务端 session 路径使用真实公共 API

Session 场景的 server handler 直接使用 P2 公共 API：

```csharp
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
```

client 侧没有 P4 `ChannelClient`，因此使用同一组公共 `LengthPrefixedFramer` 与 `JsonMessageCodec<EchoMessage>` 手工组帧：

```csharp
_payloadWriter.Clear();
_codec.Encode(_payloadWriter, message);

_frameWriter.Clear();
_framer.WriteFrame(
    _frameWriter,
    new ReadOnlySequence<byte>(_payloadWriter.WrittenMemory));

await _stream.WriteAsync(_frameWriter.WrittenMemory, ct);
```

这样测到的是真实 framer、codec 与 session 组合，而不是另写一套简化协议。client 自己组帧的成本也计入结果，README 说明这一点。

### 5. 复用缓冲，避免测量设施主导分配

每个 benchmark 实例在 setup 中分配固定大小缓冲，benchmark 方法内只 `Clear()`，不 `new`：

- `RawEchoClient` 接收缓冲由调用方传入并复用；
- `SessionEchoClient` 复用 payload writer、frame writer、4 字节长度头和接收缓冲；
- `ArrayBufferWriter.Clear()` 保留底层数组，避免每次操作重新分配；
- payload 与消息对象在 `[GlobalSetup]` 创建一次。

BDN 的 `[MemoryDiagnoser]` 仍会统计 framer/codec/session 真实产生的分配；README 提醒 B/op 包含协议 payload 拷贝，不应期望为 0。

### 6. 默认 Job + `--job dry` 冒烟

基准类只加 `[MemoryDiagnoser]`，不强行指定 iteration 数，使用 BDN 默认 Job 以获得带统计置信区间的结果。完整运行可能需要十几分钟；开发时用：

```powershell
dotnet run --project benchmarks/Galosys.Foundation.Primitives.Benchmarks/Galosys.Foundation.Primitives.Benchmarks.csproj -c Release -- --filter "*Framing*" --job dry
```

`--job dry` 只验证基准能启动、连接能建立、方法能执行，不产生可用于比较的基线数据。README 区分“冒烟”和“基线”两种运行方式。

### 7. 决策门：先有数据，再开优化 change

本 change 只交付测量设施。是否优化由以下任一条件触发：

1. 4 KiB session 往返的 B/op 超过同 payload 原始 echo 的 2 倍；
2. 4 KiB session 往返的平均延迟超过同 payload 原始 echo 的 2 倍；
3. framing + JSON codec 的合计时间超过 session 往返时间的 50%；
4. 与已记录基线相比，同一场景平均延迟回归超过 10%，且 99.9% 置信区间不重叠。

触发后只允许两种动作：重新测量确认，或另开专门的优化 change 并附上基线对比；不在本 change 中直接改 `src/`。未触发则记录“无需优化”，不为了数字好看做微优化。

基线文件命名 `results/baseline-<yyyy-MM-dd>-<machine>.md`，至少记录：机器/CPU、内存、OS、`dotnet --version`、commit、构建配置、完整命令、BDN 摘要表。超过 90 天、换机器或换 SDK 后必须重新记录，不能拿旧基线直接比较。

### 8. 不设 CI 门禁

仓库没有稳定的自托管性能机器，共享 CI runner 的 CPU 频率、邻居负载和电源策略都会造成 2–5 倍抖动。BDN 的统计只能缓解、不能消除这种噪声。因此：

- 不新增 workflow 或 pipeline 步骤；
- 不设置“超过阈值即失败”的测试；
- 构建正确性仍由现有 `dotnet build` / `dotnet test` 保证；
- 性能结论必须由维护者在参考机器上手动运行并记录。

## Risks / Trade-offs

- **同进程测量包含两端** → ns/op 不是纯 transport 数字；README 限定只做相对比较，绝对数字不作为 SLA。
- **BDN 与 .NET 10 的兼容性** → 固定 `0.15.8` 并在 README 记录 SDK；升级 BDN 需单独验证生成项目能编译。
- **机器噪声与电源策略** → 基线文件记录机器和 OS；比较前确认同一环境，必要时重复运行取稳定结果。
- **持久连接掩盖连接成本** → 明确不测 accept/握手/关闭；连接建立成本由后续独立基准覆盖。
- **payload 拷贝计入 B/op** → framer/codec 的协议拷贝无法避免；决策门用相对倍数而不是“零分配”判断。
- **session 场景依赖 P2** → 若 P2 未落地，项目不能编译；本 change 明确前置，任务顺序不跨越 P1.5a/P2。
- **结果文件可能过期** → `results/README.md` 规定 90 天与换机重录规则；不把历史数字当作长期承诺。
- **可选 change 被误当发布阻断** → 提案、README 与任务均标注 optional/deferred，不加入 CI。

## Migration Plan

1. 新建 benchmark 项目文件与 `Program.cs`，确认 `dotnet restore` 能解析 `BenchmarkDotNet 0.15.8`。
2. 增加 payload、回显 handler、`LoopbackServer`、`RawEchoClient` 基础设施。
3. 增加 echo、framing、JSON codec、session 四组基准。
4. 写 benchmark README（环境、命令、场景、局限、决策门）与 `results/README.md` 模板。
5. 根 README 增加项目行与运行命令；不改 `.slnx`。
6. 执行 Release 构建与 `--job dry` 冒烟；完整基准由维护者按需运行。
7. 运行 `openspec validate` 并确认 `src/`、`tests/`、`samples/` 无改动。

回滚策略：删除 `benchmarks/` 目录并移除根 README 的两处新增内容即可；没有生产代码、公共 API 或数据格式变更，回滚不影响运行时行为。

## Open Questions

无。基准场景、运行方式、结果记录和决策门均已确定；真实基线数字由应用后的手动运行产生，不作为本 change 的完成条件。
