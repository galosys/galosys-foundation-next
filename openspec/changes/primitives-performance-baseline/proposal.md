## Why

P1/P2 的 transport、framing、JSON codec 与 session 只有正确性测试，没有可重复的性能基线：无法回答“一次 4 KiB 消息往返花多少时间、分配多少字节”，也无法判断后续零拷贝、批处理或缓冲池优化是否真的有效。没有基线时做优化只能靠猜测，回归也只能等到线上暴露。

仓库当前没有任何 benchmark 项目，且根目录 `Galosys.Foundation.Next.slnx` 因包含不可编译的 `Galosys.Foundation.Nats.Net` 而无法整体构建。本 change 新建一个独立、手动运行的 BenchmarkDotNet 工程，覆盖回显传输、framing、JSON codec 与 session 往返四类场景，并记录环境、命令和决策门；它不修改任何生产代码，也不设置 CI 性能门禁。

## What Changes

- 新建 `benchmarks/Galosys.Foundation.Primitives.Benchmarks/`，目标 `net10.0`，引用 `Galosys.Foundation.Primitives`，使用固定版本 `BenchmarkDotNet 0.15.8`。
- 新增共享基准设施：`LoopbackServer`（启动绑定 `127.0.0.1:0` 的 `ChannelServer`，暴露实际端口）、`RawEchoClient`、`SessionEchoClient` 和固定 payload 生成器；连接在 `GlobalSetup` 建立、`GlobalCleanup` 关闭，避免把连接建立计入每次操作。
- 新增四组基准：
  - `EchoRoundTripBenchmarks`：原始字节回显，payload 64 B / 4 KiB / 64 KiB。
  - `FramingBenchmarks`：`LengthPrefixedFramer.Parse` 与 `WriteFrame`，payload 64 B / 4 KiB / 64 KiB。
  - `JsonCodecBenchmarks`：`JsonMessageCodec<T>.Encode` 与 `Decode`，文本 64 / 4096 字符。
  - `SessionRoundTripBenchmarks`：服务端 `IChannelSession<T>` 回显 + 客户端使用同一组 framer/codec，文本 64 / 4096 字符。
- 所有基准启用 `[MemoryDiagnoser]`；基准方法复用接收缓冲与 `ArrayBufferWriter`，避免测量设施自身成为主要分配来源。
- 新增 `benchmarks/.../README.md`：环境要求、构建/运行命令、场景说明、结果解读、同进程 client/server 的局限，以及“何时才开优化 change”的决策门。
- 新增 `benchmarks/.../results/README.md`：基准结果模板与记录规则（机器、OS、SDK、commit、命令、日期），真实基线由维护者在参考机器上手动运行后写入。
- 根 `README.md` 增加 benchmarks 项目行和“性能基准”命令段；不改 `.slnx`，因为该解决方案当前不可构建。
- **明确不在本 change 内**：不修改 `src/`、`tests/`、`samples/` 的任何代码；不引入 Kestrel、IOQueue、`SocketAsyncEventArgs` 对照实现；不新增 P4 客户端；不设 CI 门禁；不把基准结果作为发布阻断条件。
- 本 change 是可选的、可延后执行的；前置为 P1（服务端内核）、P1.5a（`ChannelServer`）与 P2（framer/codec/session）。

## Capabilities

### New Capabilities

- `channel-performance`: 提供可复现的 Primitives 性能基准工程、四类核心场景、结果记录规范和优化决策门。

### Modified Capabilities

无。生产代码、公共 API 与现有 `channel-server-transport`、`channel-diagnostics` 等要求均不变。

## Impact

- 代码：仅新增 `benchmarks/Galosys.Foundation.Primitives.Benchmarks/`（项目文件、`Program.cs`、`Benchmarks/`、`Infrastructure/`、`README.md`、`results/README.md`）；修改根 `README.md` 增加入口。
- 公共 API：无变更；benchmark 项目不是可打包产物（`IsPackable=false`）。
- 依赖：仅 benchmark 项目新增 `BenchmarkDotNet 0.15.8`；`src/Galosys.Foundation.Primitives` 不新增包。
- 构建：`dotnet build benchmarks/Galosys.Foundation.Primitives.Benchmarks/Galosys.Foundation.Primitives.Benchmarks.csproj -c Release` 独立验证；不加入不可构建的 `.slnx`。
- 前置：需要 P1.5a 的 `ChannelServer` 与 P2 的 `LengthPrefixedFramer`、`JsonMessageCodec<T>`、`AsJsonSession<T>()` 已落地；否则 benchmark 项目无法编译。
- 验证：Release 构建、`--job dry` 冒烟、`openspec validate "primitives-performance-baseline" --strict --no-interactive`；完整基准运行是维护者手动步骤，不纳入任务完成条件。
