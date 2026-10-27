# Primitives P2：Message Frame 与 Message Codec 设计

> 一句话：**Frame 负责“消息边界”，Codec 负责“一帧 payload ↔ T”**。两者必须拆开；P2 首批只做 `System.Text.Json` + 长度前缀/换行/透传，不引入 protobuf/MessagePack。

## 简易路线图

| 阶段 | 状态 | 内容 | 验收标准 | 粗估 |
|---|---|---|---|---|
| **P1 服务端内核** | ✅ 已完成 | 自包含 socket transport、输入/输出 Pipe、管线、Options、每连接 scope、优雅停机 | 60 个 loopback/单元测试通过；核心程序集无 `Microsoft.AspNetCore.*` | — |
| **P1.5a 入口组合化** | 设计完成 | 抽 `ChannelServerCore`；sealed `ChannelServer`；删除 `ChannelServerBase`；移除泛型 DI 入口 | 单 server 行为等价；旧测试迁移后全绿 | 2–3 天 |
| **P1.5b 多服务器** | 待做 | `ChannelServerGroup`；一个 host 多个 server；独立 options；统一启停/失败回滚 | 两个 server 同时监听、独立配置、统一停机 | 1–2 天 |
| **P2 消息层** | **本设计** | `IMessageFramer` + `IMessageCodec<T>` + `IChannelSession<T>`；长度前缀、换行、透传 framer；JSON、UTF-8、Raw codec | 半包/粘包/超限/EOF/取消测试通过；示例迁移到 `LineFramer` | 3–5 天 |
| **P2.5 协议分发** | 按需 | 同端口 `IChannelProtocolSelector` + `ProtocolDispatcher`；首字节分流，ALPN 预留 | 同端口两种协议可连接；TLS ALPN 在 P3 接入 | 2–4 天 |
| **P3 TLS** | 待做 | `ChannelTlsOptions` + `UseTls`（服务端/客户端），`SslStream` over `IDuplexPipe` 适配器 | 握手成功、证书校验失败、TLS 下关闭语义 | 2–3 天 |
| **P4 客户端** | 待做 | `IChannelClient` + 状态机 + 重连 + 连接池 + 批处理；复用 P2 的 session/codec | 断线重连、退避、取消、池耗尽、pipeline 顺序 | 5–8 天 |
| **P5 收尾** | 待做 | NamedPipe transport、`ActivitySource`/`Meter`、示例与文档 | metrics/trace 可见；示例迁移 | 3–5 天 |
| **P6 可选** | 按需 | Kestrel 适配包、IOQueue/SAEA、BenchmarkDotNet | 仅在压测证明瓶颈后做 | 按需 |

**建议顺序**：P1.5a → P1.5b → P2 → P2.5（按需）→ P3 → P4 → P5。P2 设计与 P1.5 可并行，但示例迁移依赖 P1.5a 的 `ChannelServer`。若对外服务、安全要求高，把 P3 提到 P2 之前。

---

| 项 | 内容 |
|---|---|
| 状态 | 已定稿（待拆 OpenSpec 任务） |
| 日期 | 2026-10-22 |
| 范围 | `src/Galosys.Foundation.Primitives` 的 P2 消息层 |
| 关联 | `docs/superpowers/specs/2026-10-20-primitives-socket-transport-design.md` §5.6（本文替代其 `IMessageFramer<T>` 方案）；服务器入口见 [2026-10-22-primitives-server-composition-design.md](2026-10-22-primitives-server-composition-design.md) |
| 结论 | 拆成 `IMessageFramer` + `IMessageCodec<T>`；首批只做 `System.Text.Json`；长度前缀优先 |

---

## 1. 背景

`Galosys.Foundation.Primitives` 的 P1 已交付：

- 基于 `System.Net.Sockets` + `System.IO.Pipelines` 的自包含服务端传输；
- `ChannelContext`（连接身份、端点、`Transport`、`ConnectionClosed`、每连接 DI scope）；
- `Configure(IChannelPipeline)` 中间件缝；
- 优雅停机、背压、半关闭、RST 处理；
- 60 个真实 loopback/单元测试，核心程序集零 ASP.NET Core 依赖。

P1 之上缺的是 **L3 消息层**：调用方只能拿到原始字节，必须自己处理半包、粘包、编码和最大消息限制。现有设计文档 §5.6 提出的 `IMessageFramer<T>` 把“找边界”和“序列化”压进一个接口，短期能用，长期会限制复用和测试。

本设计的升级原则延续前述参考比较：

- **Kestrel 给骨架**：传输抽象、生命周期、限额、心跳；
- **DotNetty 给编解码**：frame decoder/encoder 与 message codec 分层；
- **BeetleX 给协议手感**：协议适配、会话状态、统计；
- **FreeRedis 给客户端形态**：连接池、pipeline、超时重试。

P2 只解决消息层，不碰 TLS、客户端、连接池和具体业务协议。

---

## 2. Framing 与 Message Codec：两个粒度

| 维度 | 字节级 codec / Framing | 消息级 codec / Message Codec |
|---|---|---|
| 回答的问题 | 一条消息**到哪结束** | 这段字节**是什么对象** |
| 输入 | 连续字节流 `ReadOnlySequence<byte>` | **恰好一帧**的 payload |
| 输出 | 一帧字节 + 消费位置 | `T` |
| 典型实现 | 长度前缀、分隔符、固定长度、HTTP/2 frame、WebSocket frame | JSON、protobuf、MessagePack、自定义二进制、RESP 类型解析 |
| 关心 | 半包/粘包、最大帧、缓冲区、背压 | schema、版本、类型、字段、编码 |
| 不关心 | 对象是什么 | 流里哪一段属于这一帧 |

一句话：**Frame 是数据单元/边界；framing 是字节级 codec（stream ↔ frame），message codec 是帧级 codec（frame ↔ T）。** DotNetty 把 framing 实现为 decoder，所以 framer 也属于 codec；本文拆的是编解码的**粒度**，不是把 frame 排除在 codec 之外。

举例：

- 长度前缀 + JSON = `LengthPrefixedFramer` + `JsonMessageCodec<T>`
- 换行 + UTF-8 = `LineFramer` + `Utf8StringCodec`
- 长度前缀 + protobuf = `LengthPrefixedFramer` + `ProtobufCodec<T>`

> 术语说明：广义上，framing 也是 codec（stream ↔ frame）；本文把 `IMessageCodec<T>` 限定为 **frame payload ↔ T** 的消息级 codec，把 `IMessageFramer` 称为字节级 codec。二者是粒度差异，不是“frame vs codec”的类别对立。

---

## 3. 为什么必须拆

### 3.1 组合爆炸

假设有 3 种分帧、3 种序列化：

- 合并接口：需要 3 × 3 = 9 个实现；
- 拆开后：3 个 framer + 3 个 codec = 6 个实现。

而且新增一种序列化时，合并方案要为每种分帧各写一遍。

### 3.2 写侧尤其别扭

长度前缀需要先知道 payload 长度，才能写 4 字节头。合并接口的 `WriteMessage(IBufferWriter<byte>, T)` 只有两种做法：

1. 序列化两遍（先算长度再写）；
2. 内部偷偷缓冲（隐藏分配和失败点）。

拆开后，codec 只负责写 payload，framer 只负责加头，长度问题在一个地方解决。

### 3.3 测试耦合

- 分帧测试不应该依赖 JSON；
- JSON 测试不应该起 socket；
- 协议错误和序列化错误应该能分别定位。

### 3.4 参考实现也是分开的

- **DotNetty**：`LengthFieldBasedFrameDecoder` / `LengthFieldPrepender` 负责 stream ↔ frame，`MessageToByteEncoder<T>` / `MessageToMessageDecoder<IByteBuffer>` 负责 frame ↔ T。前者也继承 `ByteToMessageDecoder` / `MessageToMessageEncoder<IByteBuffer>`，所以两段都是 codec/decoder；区别是转换类型和粒度。这是我们要对齐的分层。
- **BeetleX**：`IPacket.Decode` 先读 size，再调 `OnRead` 反序列化；`Encode` 先写 size，再调 `OnWrite`。用起来方便，但换序列化就要重写分帧。它适合做“协议适配层”，不适合做 foundation 的底层契约。

### 3.5 结论

**拆成 `IMessageFramer`（字节级 codec）+ `IMessageCodec<T>`（消息级 codec），不保留合并版 `IMessageFramer<T>`。** 便捷性用 `AsJsonSession<T>()` / `AsLineTextSession()` 这类工厂方法补，不需要第三个接口。

---

## 4. 推荐契约

### 4.1 分帧（字节级 codec）

```csharp
public interface IMessageFramer
{
    FrameParseResult Parse(in ReadOnlySequence<byte> buffer);
    void WriteFrame(IBufferWriter<byte> writer, ReadOnlySequence<byte> payload);
}

public readonly struct FrameParseResult
{
    public bool IsComplete { get; }
    public ReadOnlySequence<byte> Frame { get; }
    public SequencePosition Consumed { get; }
    public SequencePosition Examined { get; }

    public static FrameParseResult NeedMoreData(in ReadOnlySequence<byte> buffer);
    public static FrameParseResult NeedMoreData(SequencePosition consumed, SequencePosition examined);
    public static FrameParseResult Complete(ReadOnlySequence<byte> frame);
}
```

`IMessageFramer` 是字节级 codec：输入是连续字节流，输出是一帧；它不负责把 payload 反序列化成 `T`。

`Consumed` / `Examined` 的语义：

- **帧不完整**：`Consumed = buffer.Start`，`Examined = buffer.End`，告诉 `PipeReader`“我看过了，但一个字节都没消费”。
- **帧完整**：`Consumed = frame.End`，`Examined = frame.End`，消费这一帧。
- **有状态 framer**：可以先用 `NeedMoreData(headerEnd, buffer.End)` 消费帧头、保留 payload 长度，下一次读再取 payload。
- **格式错误**：直接抛 `InvalidDataException`，由 session 中止连接。

不使用 `bool TryParse(ref buffer, out frame)`：它无法表达“看完但没消费”的位置，容易把 `PipeReader.AdvanceTo` 用错。

### 4.2 编解码（消息级 codec）

```csharp
public interface IMessageCodec<T>
{
    T Decode(ReadOnlySequence<byte> frame);
    void Encode(IBufferWriter<byte> writer, T message);
}
```

它只处理**一帧内部**的字节，不关心长度头、分隔符、半包/粘包。codec 必须同步完成：序列化是内存操作；需要异步压缩/加密时，包一层 session，不要让 codec 变成 async。

### 4.3 消息会话

```csharp
public interface IChannelSession<T> : IAsyncDisposable
{
    ValueTask<T> ReadAsync(CancellationToken ct);
    ValueTask WriteAsync(T message, CancellationToken ct);
}

public static class ChannelContextSessionExtensions
{
    public static IChannelSession<T> AsSession<T>(
        this ChannelContext context,
        IMessageFramer framer,
        IMessageCodec<T> codec,
        MessageChannelOptions? options = null);
}
```

- `IAsyncDisposable` 只释放 session 自己的写锁和状态，**不关闭连接**；连接由 `ChannelContext` / `ChannelServer` 负责。
- 不暴露 `Completion`；需要等待连接关闭时使用 `context.ConnectionClosed`。
- 一个连接只能创建一个 session；`AsSession` 要防重复调用，否则两个 session 会抢同一个 `PipeReader`。

组合用法：

```csharp
var json = context.AsSession(
    new LengthPrefixedFramer(),
    new JsonMessageCodec<MyMessage>());

var text = context.AsSession(
    new LineFramer(),
    new Utf8StringCodec());
```

便捷入口只做组合，不作为底层契约：

```csharp
public static IChannelSession<T> AsJsonSession<T>(
    this ChannelContext context,
    LengthFieldFramerOptions? framer = null,
    JsonSerializerOptions? json = null,
    MessageChannelOptions? options = null);

public static IChannelSession<string> AsLineTextSession(
    this ChannelContext context,
    LineFramerOptions? framer = null,
    MessageChannelOptions? options = null);
```

```csharp
var json = context.AsJsonSession<MyMessage>(
    new LengthFieldFramerOptions { MaxFrameSize = 1024 * 1024 });

var text = context.AsLineTextSession();
```

### 4.4 会话选项

```csharp
public sealed class MessageChannelOptions
{
    /// <summary>单条消息 payload 的硬上限，默认 1 MiB。</summary>
    public int MaxMessageSize { get; set; } = 1024 * 1024;
}
```

framer 自己的协议上限（如 `LengthFieldFramerOptions.MaxFrameSize`、`LineFramerOptions.MaxFrameSize`）负责在等待 payload 之前拒绝明显非法的帧；`MessageChannelOptions.MaxMessageSize` 是 session 层的统一兜底。

### 4.5 为什么不保留合并接口

- 合并接口会让“换序列化”或“换分帧”互相牵连；
- 调用方只需要一行组合，不需要额外抽象；
- 工厂方法 `AsJsonSession<T>()` 已经覆盖了 90% 的便捷场景；
- P2 尚未实现，现在拆分没有兼容负担。

### 4.6 入口与生命周期

P1.5 之后服务器入口是组合式的：

```csharp
var server = new ChannelServer(
    options,
    pipeline => pipeline.Run(HandleAsync));
```

session 必须在**终端 handler** 里创建，不能放在 `IChannelPipeline.Use` 里：

```csharp
static async Task HandleAsync(ChannelContext context)
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

        // 处理一行
    }
}
```

原因：`ChannelMiddleware` 的契约是“只包装 Transport，不读取业务数据”；session 要读 `context.Reader`，只能由终端 handler 持有。TLS/压缩等中间件先包装 transport，session 在 handler 里看到的是包装后的 `Reader/Writer`。

生命周期规则：

- 一个连接一个 session；`AsSession` 用 `ChannelContext` 内部槽位防重复，第二次调用抛 `InvalidOperationException`。
- session 借用 `ChannelContext`，不拥有连接；`DisposeAsync` 只等待在途写并释放 session 自己的锁，不完成/关闭 transport。
- handler 返回后，`ChannelServer` 负责关闭连接；需要提前中止用 `context.Abort(reason)`。
- P2 不提供 `Completion`；连接关闭信号统一用 `context.ConnectionClosed`。
- 同端口多协议时，由 P2.5 的 dispatcher 选中协议，再由该协议的 handler 创建 session；仍然遵守“一个连接一个 session”。

---

## 5. 数据流

```text
读：
socket bytes
  → PipeReader.ReadAsync
  → IMessageFramer.Parse        找边界、去掉长度头/分隔符
  → IMessageCodec<T>.Decode     一帧 payload → T
  → ReadAsync 返回 T

写：
T
  → IMessageCodec<T>.Encode      T → 一帧 payload
  → IMessageFramer.WriteFrame    加长度头/分隔符
  → PipeWriter.FlushAsync
```

`ChannelSession<T>` 是**拉模型**：handler 主动调用 `ReadAsync`，不在后台起读循环，也不维护 `Channel<T>` 队列。这样异常、背压和 `ConnectionClosed` 的生命周期都保持简单。

---

## 6. P2 范围

### 6.1 内置 framer

以下 framer 都是字节级 codec：输入字节流，输出一帧；它们不负责 payload ↔ T。

| 类型 | 用途 | 关键选项 |
|---|---|---|
| `LengthPrefixedFramer` | 二进制 RPC/消息总线默认形态 | 长度字段偏移/长度/调整/剥离、大小端、`MaxFrameSize` |
| `LineFramer` | 文本协议、示例、命令行风格 | 分隔符（默认 `\n`）、是否裁剪 `\r`、`MaxFrameSize` |
| `PassThroughFramer` | 兼容现有原始字节用法 | 无；每次 `ReadAsync` 的结果作为一帧 |

`LengthPrefixedFramer` 首版默认 **4 字节大端长度头**；`LengthFieldOffset` / `LengthAdjustment` / `InitialBytesToStrip` 等字段可以预留，首版只实现默认路径，后续按 DotNetty `LengthFieldBasedFrameDecoder` 扩展。

`LineFramer` 是**字节级**的：只认 `\n` / `\r\n`，UTF-8 解码属于 codec。`\r\n` 被拆到两个 buffer 时要正确处理。

### 6.2 内置 codec

| 类型 | 用途 | 说明 |
|---|---|---|
| `JsonMessageCodec<T>` | JSON 消息 | 使用 `Utf8JsonReader` / `Utf8JsonWriter`，原生支持 `ReadOnlySequence<byte>` |
| `Utf8StringCodec` | UTF-8 文本 | 因为 frame 已完整，不会出现跨 chunk 的多字节字符截断 |
| `RawMessageCodec` | 原始字节 | 返回 `ReadOnlyMemory<byte>` 的拷贝；文档说明生命周期 |

`Galosys.Foundation.Core` 的 `IObjectSerializer` 如果可用，后续写一个 `ObjectSerializerCodec<T>` 适配器即可；核心接口不绑定它。

### 6.3 目录结构

```text
src/Galosys.Foundation.Primitives/
  Framing/
    IMessageFramer.cs
    FrameParseResult.cs
    LengthPrefixedFramer.cs
    LineFramer.cs
    PassThroughFramer.cs
  Codecs/
    IMessageCodec.cs
    JsonMessageCodec.cs
    Utf8StringCodec.cs
    RawMessageCodec.cs
  Session/
    IChannelSession.cs
    ChannelSession.cs
    ChannelContextSessionExtensions.cs
  Microsoft/Extensions/Options/
    MessageChannelOptions.cs
    LengthFieldFramerOptions.cs
    LineFramerOptions.cs
```

### 6.4 P2 明确不做

- protobuf / MessagePack（后续独立适配包）；
- 压缩、加密（TLS 属于 P3，压缩以后作为 session 装饰器）；
- 请求/响应关联、pipeline/batch（属于 P4）；
- 客户端、连接池、重连（P4）；
- Redis/HTTP/MQTT 等具体协议实现（独立包或示例）；
- 把 codec 做成 `IChannelPipeline` 中间件（见 §7 第 8 条）。

---

## 7. 实现细节与坑

1. **长度前缀的写顺序**：`IBufferWriter` 不支持回头改长度。v1 先把 payload 编码到池化的 buffer writer（基于 `ArrayPool<byte>`），再 `WriteFrame` 写“长度 + payload”。多一次拷贝，但简单、安全、稳态零分配。不要用“先写 4 字节占位、后面回填”的方案，`PipeWriter` 的段可能已经切换。
2. **`ReadOnlySequence<byte>` 的生命周期**：codec 必须在 `AdvanceTo` 之前完成解码；不能把 slice 存到 `T` 里长期持有，Pipe 会复用内存。需要保留就 copy。
3. **EOF 语义**：干净 EOF（buffer 为空且 `IsCompleted`）→ `EndOfStreamException`；帧中途 EOF → `InvalidDataException`。
4. **读取消是安全的**：`ReadAsync(ct)` 取消时不要 `AdvanceTo`，下次可以继续读。**写取消是不安全的**：可能已写半个帧，应中止连接。
5. **最大帧/最大消息**：长度头声明超过上限时，在等待 payload 之前就拒绝；分隔符 framer 在 buffer 超过上限且找不到分隔符时拒绝。防止 OOM/慢速攻击。
6. **单读者/单写者**：`ReadAsync` 并发调用应直接抛 `InvalidOperationException`，而不是排队（排队会打乱消息顺序）；`WriteAsync` 用 `SemaphoreSlim` 串行化。
7. **分隔符是字节级的**：`LineFramer` 只认 `\n`/`\r\n`，UTF-8 解码属于 codec。不要把 `Encoding` 塞进 framer。
8. **语义头属于 codec**：消息类型、flags、correlation id 如果属于协议 payload，就由 codec 解析；framer 只剥掉自己的长度/分隔符。未来如果要支持“帧头元数据”，再扩展 `FrameParseResult`，不要现在把 `T` 塞进 framer。
9. **TLS/压缩不是 codec**：TLS 是 transport middleware；压缩可以作为 session 装饰器或 codec 包装，但不要混进分帧。
10. **codec 保持同步**：序列化是内存操作；需要异步压缩/加密时包一层 session，不要让 `IMessageCodec<T>` 变成 async。
11. **刷新与背压**：`WriteFrame` 之后必须 `FlushAsync`，并检查 `FlushResult.IsCompleted`；忽略它会向已关闭连接写数据。批量写入的“只 flush 一次”留给 P4 的 batch，不在 P2 提前复杂化。
12. **空帧**：长度 0 的帧默认允许 framer 通过，由 codec 决定是否合法（例如 JSON codec 应拒绝空 payload）。不要在校验层替 codec 做业务判断。
13. **整数溢出**：长度字段 + adjustment + header 的加法用 checked 运算，避免负长度或回绕。

---

## 8. 参考库对照

| 库 | 字节级 codec（framing） | 消息级 codec | 结论 |
|---|---|---|---|
| **DotNetty** | `LengthFieldBasedFrameDecoder`、`DelimiterBasedFrameDecoder`、`LengthFieldPrepender` | `MessageToByteEncoder<T>`、`MessageToMessageDecoder<IByteBuffer>` | 最接近我们的目标；注意 frame decoder 本身也是 decoder，两段都是 codec handler |
| **BeetleX** | `IPacket.Decode` 里读 size | `OnRead/OnWrite` 反序列化 | 合并了，方便但复用差；只借 `IPacket` 的协议适配思路 |
| **Kestrel** | HTTP/2 frame 层、HTTP/1.1 parser | HPACK、内容解码 | 证明分帧和语义编解码是两层，不直接复用 |
| **FreeRedis** | RESP 类型/长度解析 | RESP → `RedisResult`/类型化值 | 协议自描述，天然合并；不要把这个耦合带进通用库 |
| **我们 P2** | `IMessageFramer` | `IMessageCodec<T>` | 拆开，组合使用 |

> DotNetty 的 frame decoder 继承 `ByteToMessageDecoder`，frame prepender 继承 `MessageToMessageEncoder<IByteBuffer>`；因此“framing 属于 codec”与“我们拆两个接口”并不矛盾：前者是机制分类，后者是转换粒度。

---

## 9. 任务拆分与验收

| # | 任务 | 交付 | 验收 |
|---|---|---|---|
| 1 | 字节级 codec 契约与内置 framer | `IMessageFramer`、`FrameParseResult`、`LengthPrefixedFramer`、`LineFramer`、`PassThroughFramer` | 纯内存单测：1 字节一喂、一次多帧、零长度、超限、CRLF 拆分 |
| 2 | 消息级 codec 契约与内置 codec | `IMessageCodec<T>`、`JsonMessageCodec<T>`、`Utf8StringCodec`、`RawMessageCodec` | JSON 往返、无效 JSON、UTF-8 完整帧、Raw 拷贝语义 |
| 3 | `ChannelSession<T>` | `IChannelSession<T>`、`ChannelSession<T>`、`MessageChannelOptions`、`AsSession`/`AsJsonSession`；单 session 约束、`DisposeAsync` 语义 | loopback：半包/粘包、EOF、取消、并发读抛错、并发写保序、1 MiB 背压；重复 `AsSession` 抛错；Dispose 不关闭连接 |
| 4 | 示例与文档 | 基于 P1.5 的 `ChannelServer`；`NetServer.ConsoleApp` 迁到 `LineFramer` + `Utf8StringCodec`；README/AGENTS 更新 | 示例不再出现 UTF-8 跨 chunk 截断；文档说明 frame/codec 分工与 session 入口 |

### 测试清单

- Framer：半包、粘包、零长度、最大帧、长度字段跨段、分隔符跨段、CRLF 拆分、非法长度。
- Codec：JSON 往返、空 payload、无效 JSON、UTF-8 多字节完整帧、Raw 生命周期。
- Session：干净 EOF、帧中途 EOF、读取消后继续、写取消中止连接、并发读抛错、并发写保序、`MaxMessageSize` 拒绝。
- 集成：1 MiB 消息、背压、连接重置、优雅停机期间的消息收尾。

---

## 10. 风险与取舍

| 风险/取舍 | 说明 | 缓解 |
|---|---|---|
| 写侧多一次拷贝 | codec 先编码到池化 buffer，再写 frame | 稳态零分配；后续可为长度前缀做专用 writer 优化 |
| 分隔符 framer 可能重复扫描 | 无状态实现每次从 buffer 起点找分隔符 | `MaxFrameSize` 兜底；必要时后续加扫描状态 |
| codec 解码失败即中止连接 | 不尝试跳过坏帧继续 | 协议错误通常意味着流已不可信，abort 更安全；如需容错，业务层自行处理 |
| 不提供合并接口 | 调用方要组合两个对象 | `AsJsonSession<T>()` 等工厂方法覆盖常见场景 |
| `IObjectSerializer` 适配延后 | 核心不硬依赖 `Galosys.Foundation.Core` | P2 先用 `System.Text.Json`；后续加适配器 |
| 公共 API 过早冻结 | session/framer 还可能演进 | P2 只冻结最小集；P4 再决定 batch/pool 契约 |
| P1.5 入口重构与 P2 并行 | 示例迁移依赖新 `ChannelServer` | 先做 P1.5a；P2 的 framer/codec/session 不依赖服务器继承 |

---

## 11. 与后续阶段的关系

- **P1.5a**：移除 `ChannelServerBase`，改为 sealed `ChannelServer` + `ChannelServerCore`；P2 的 session 仍在终端 handler 创建（`context.AsSession`），不依赖服务器继承。P2 设计可并行，示例迁移依赖 P1.5a。
- **P1.5b**：`ChannelServerGroup` 支持一个 host 多个 server；`IChannelSession<T>` 每连接独立，天然适配多 server。
- **P2.5**：同端口多协议分发；每个协议在自己的 handler 里创建 session，不改变 P2 契约。
- **P3 TLS**：TLS 是 transport middleware，P2 的 session/framer/codec 在 TLS 之上透明工作，不需要改接口。
- **P4 客户端**：客户端复用 `IChannelSession<T>`、`IMessageFramer`、`IMessageCodec<T>`；连接池、重连、batch 在客户端层实现。
- **P5 Diagnostics**：可为 `ChannelSession<T>` 增加 `ActivitySource` 标签（framer/codec 名称、消息大小），但不改变契约。
- **P6 可选**：Kestrel 适配包、IOQueue/SAEA、BenchmarkDotNet 只在压测证明瓶颈后做。

---

## 12. 决议

1. **拆分**：`IMessageFramer`（字节级 codec，stream ↔ frame）+ `IMessageCodec<T>`（消息级 codec，frame ↔ T），不保留合并版。
2. **首批 codec**：`System.Text.Json` + `Utf8StringCodec` + `RawMessageCodec`；protobuf/MessagePack 不进核心。
3. **首批 framer**：`LengthPrefixedFramer` 主推，`LineFramer` 保留，`PassThroughFramer` 仅兼容。
4. **第一个真实协议**：长度前缀优先；如果现有对端已定换行协议，则先做 `LineFramer`。
5. **会话模型**：拉模型 `ReadAsync`/`WriteAsync`，不做后台读循环 + 队列。
6. **写侧**：池化 buffer 编码后写 frame，不回填长度头。
7. **位置语义**：`FrameParseResult` 必须携带 `Consumed`/`Examined`。
8. **便捷性**：用 `AsJsonSession<T>()` / `AsLineTextSession()` 工厂方法，不引入第三个合并接口。
9. **服务器入口**：移除 `ChannelServerBase`；sealed `ChannelServer` + internal `ChannelServerCore`；组合优先，继承退出。
10. **多服务器**：`ChannelServerGroup` 负责统一启停；IoC/`AddChannelServer` 后置，不阻塞 P2。
11. **同端口多协议**：P2.5 的 `IChannelProtocolSelector` + `ProtocolDispatcher`，不塞进 P2。
12. **Session 入口**：只在终端 handler 创建；一个连接一个 session；`DisposeAsync` 不关闭连接；不暴露 `Completion`。
13. **Builder**：单 server 不引入 `ChannelServerBuilder`；`Action<IChannelPipeline>` 已经足够，host 级 builder 按需再加。
