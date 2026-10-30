## Context

`primitives-protocol-dispatch-1` 已经定义 `IChannelProtocolSelector` 和 `ProtocolDispatcher`，但没有内置选择器。同一端口上的协议通常用一个短前缀区分：HTTP/2 preface、MQTT 固定头、TLS record、私有协议的 magic。选择器必须在协议 handler 之前运行，又必须把前缀完整留给 handler；这要求它只窥探 `PipeReader`，不能消费。

本 change 增加 `PrefixProtocolSelector`，作为 P2.5 的首个内置选择器。它不依赖 P2 的 framer/codec/session：选择器本身只使用 `System.IO.Pipelines`；协议 handler 可以选择使用 P2 session，也可以直接读原始 Pipe。

## Goals / Non-Goals

**Goals:**

- 提供 `PrefixProtocolRule`、`PrefixProtocolSelectorOptions`、`PrefixProtocolSelector`。
- 按注册顺序做 first-match，规则重叠时先注册者优先。
- 正确处理半包前缀、EOF、无匹配、超过 `MaxPrefixSize` 和取消。
- 严格只窥探：返回结果时 `AdvanceTo(buffer.Start, buffer.Start)`，等待半包时 `AdvanceTo(buffer.Start, buffer.End)`；选中协议后前缀仍可读。
- 用单元测试和同端口 loopback 集成测试锁定行为。

**Non-Goals:**

- 不做最长前缀匹配；重叠规则由注册顺序决定。
- 不做 ALPN、TLS、`SslStream` 或握手后二次选择。
- 不做正则、掩码、偏移或可变长度前缀；规则只支持从位置 0 开始的固定字节前缀。
- 不做协议注册表、DI、配置绑定或 `ProtocolDispatcher` 改造。
- 不要求协议 handler 使用 P2 session；集成测试使用原始 Pipe。

## Decisions

### 1. 规则类型保存防御性副本

```csharp
public sealed class PrefixProtocolRule
{
    public PrefixProtocolRule(ReadOnlyMemory<byte> prefix, IChannelProtocol protocol);

    public ReadOnlyMemory<byte> Prefix { get; }
    public IChannelProtocol Protocol { get; }
}
```

- 空前缀抛 `ArgumentException`；null protocol 抛 `ArgumentNullException`。
- 构造时 `prefix.ToArray()` 复制，避免调用方修改原始数组影响匹配。
- `Prefix` 和 `Protocol` 只读；规则是不可变描述，可安全跨连接共享。
- 规则不包含 `Name`；协议自己的 `Name` 用于诊断。

### 2. 选 first-match，而不是 longest-prefix

规则按构造函数传入的顺序保存，选择器**按注册顺序**检查：

1. 如果当前缓冲区以规则前缀开头，立即返回该规则协议；
2. 如果当前缓冲区是规则前缀的严格前缀（只收到半包），停止检查后续规则并等待更多数据；
3. 如果当前缓冲区与规则前缀无关，继续检查下一条规则；
4. 所有规则都不匹配且没有半包可能时返回 null。

**为什么不用 longest-prefix：** 流式读取时，“最长”要求在选择一个已经完整匹配的短前缀后继续等待，看是否还会出现更长规则。例如规则 `AB` 和 `ABCD`，客户端只发送 `AB` 并等待响应时，longest-prefix 会永久等待；如果提前返回 `AB`，又可能错过 `ABCD`，结果依赖 TCP 分段。first-match 把优先级交给注册顺序，行为确定，调用方可以用 `AB` 规则显式覆盖 `ABCD`。

**重叠规则的约束：** 因为 first-match，重叠规则不是错误，但顺序有意义。README 和 XML 文档必须说明“先注册者优先”；需要更长前缀优先时，把更长规则放在前面。

### 3. 半包等待与 EOF/无匹配

```csharp
var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
var buffer = result.Buffer;
IChannelProtocol? selected = null;
var needsMore = false;
try
{
    foreach (var rule in _rules)
    {
        if (StartsWith(buffer, rule.Prefix.Span))
        {
            selected = rule.Protocol;
            break;
        }

        if (IsPartialPrefix(buffer, rule.Prefix.Span))
        {
            needsMore = true;
            break;
        }
    }
}
finally
{
    if (selected is null && needsMore)
        reader.AdvanceTo(buffer.Start, buffer.End);
    else
        reader.AdvanceTo(buffer.Start, buffer.Start);
}
```

- 半包：当前字节是某条规则前缀的严格前缀，且长度小于 `MaxPrefixSize`，循环继续 `ReadAsync`。
- EOF：`result.IsCompleted` 为 true 时，如果有完整匹配已在上面返回；否则返回 null。
- 无匹配：当前字节不是任何规则前缀，也不是任何规则的前缀，返回 null。
- 超过上限：缓冲区长度达到 `MaxPrefixSize` 仍未匹配，返回 null，防止无限缓冲。
- 每次成功读取后：返回协议或返回 null 时调用 `AdvanceTo(buffer.Start, buffer.Start)`，保证后续协议立即读到同一批字节；等待半包时调用 `AdvanceTo(buffer.Start, buffer.End)`，让下一次 `ReadAsync` 等待新数据。两种情况下都不消费字节。
- 读取被取消时不调用 `AdvanceTo`，直接把 `OperationCanceledException` 交给分发器；分发器在 `ConnectionClosed` 已取消时安静返回。

**为什么由选择器返回 null，而不是直接 `Abort`：** 选择器只负责“匹配/不匹配”，中止策略属于分发器。这样选择器可以单独测试，也能被其他调用方复用。

### 4. `MaxPrefixSize` 是选择器的扫描上限

```csharp
public sealed class PrefixProtocolSelectorOptions
{
    public int MaxPrefixSize { get; set; } = 64;
}
```

- `MaxPrefixSize` 必须大于 0；否则构造函数抛 `ArgumentOutOfRangeException`。
- 每条规则的 `Prefix.Length` 必须小于等于 `MaxPrefixSize`；否则构造时抛 `ArgumentException`。
- 缓冲区长度大于等于 `MaxPrefixSize` 且没有完整匹配时，选择器返回 null；不再等待。
- 这个上限同时约束单条规则长度和“未知协议”扫描量，避免慢速客户端用无限前缀拖住连接。
- 默认 64 足以覆盖常见 magic/preface；需要更长前缀的调用方显式设置。

### 5. 重复前缀在构造时拒绝

first-match 下重复前缀虽然可以用“先注册者胜出”解释，但配置几乎总是错误，且会让规则顺序的意图不清晰。构造函数用 `SequenceEqual` 检查重复前缀并抛 `ArgumentException`。重叠但不相等的规则允许，按 first-match 处理。

### 6. 只窥探不消费，协议 handler 负责消费自己的前缀

选择器返回协议后，输入位置仍在 `buffer.Start`。协议 handler 看到的是完整字节流，包括自己的前缀。协议可以选择：

- 先读取并消费前缀，再解析 payload；
- 把前缀当作协议头的一部分；
- 使用 P2 的 framer/session（如果该协议选择这样做）。

集成测试中的 handler 显式读取前缀字节，证明选择器没有替它消费。

### 7. 不依赖 P2

`PrefixProtocolSelector` 只引用 `ChannelContext.Reader`、`System.IO.Pipelines` 和 `IChannelProtocol`。它不引用 `IMessageFramer`、`IMessageCodec<T>` 或 `IChannelSession<T>`；因此 P2 未实现时也能编译和测试。集成测试的 handler 用原始 `PipeReader`/`PipeWriter`，不创建 session。

## Risks / Trade-offs

- **first-match 依赖注册顺序** → README、XML 文档和测试都明确“先注册者优先”；需要更长前缀优先时调整顺序。
- **半包等待可能长期占用连接** → `MaxPrefixSize` 限制扫描量；客户端一直不发送足够字节时，连接会保持到超时/关闭，这是协议选择的固有成本。
- **每次读取复制缓冲区做比较** → 只在连接建立/协议选择阶段发生，不在消息热路径；后续可在保持契约的前提下优化。
- **选择器返回 null 后由分发器中止** → 调用方如果直接使用选择器而不接分发器，需要自己处理 null；README 说明推荐组合。
- **不支持 ALPN** → TLS 同端口需要 P3；本 change 只处理明文前缀。
- **协议 handler 忘记消费前缀** → 前缀仍在输入中，handler 必须按自己的协议处理；集成测试和 README 明确这一点。

## Migration Plan

1. 新增 `PrefixProtocolRule.cs` 与 `PrefixProtocolSelectorOptions.cs`。
2. 新增 `PrefixProtocolSelector.cs`，先实现构造校验、规则复制和空规则返回 null。
3. 实现 `SelectAsync` 的读取、first-match、半包等待、EOF/上限和 `AdvanceTo`。
4. 新增 `PrefixProtocolSelectorTests.cs`，覆盖 first-match、重叠、半包、EOF、未知、取消、不消费、超限和构造校验。
5. 新增 `PrefixProtocolIntegrationTests.cs`，用一个 `ChannelServer` + `ProtocolDispatcher` + 两个前缀协议覆盖同端口分发、半包和未知前缀。
6. 更新 Primitives README 的同端口前缀路由章节。
7. 运行 Primitives 构建、全量测试和 `openspec validate`。

回滚策略：删除三个新源文件、两个测试文件和 README 段落；`ProtocolDispatcher` 与现有 server 不受影响。

## Open Questions

无。最长前缀、ALPN、TLS 和 P2 session 集成明确不在本 change。
