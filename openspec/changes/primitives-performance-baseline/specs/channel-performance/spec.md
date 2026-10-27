## ADDED Requirements

### Requirement: 独立可复现的基准项目

The repository SHALL provide a standalone BenchmarkDotNet project at `benchmarks/Galosys.Foundation.Primitives.Benchmarks/`, targeting `net10.0` and referencing `src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`. The project MUST build and run independently in `Release` without building `Galosys.Foundation.Next.slnx`. The BenchmarkDotNet package version MUST be pinned. The project MUST NOT be added to the broken solution file, and generated `BenchmarkDotNet.Artifacts/` output MUST NOT be committed.

#### Scenario: Release 独立构建

- **WHEN** 执行 `dotnet build benchmarks/Galosys.Foundation.Primitives.Benchmarks/Galosys.Foundation.Primitives.Benchmarks.csproj -c Release`
- **THEN** 命令以 0 errors 结束
- **AND** 不要求先构建 `Galosys.Foundation.Nats.Net` 或整个 `.slnx`

#### Scenario: 冒烟运行

- **WHEN** 执行 `dotnet run --project benchmarks/Galosys.Foundation.Primitives.Benchmarks/Galosys.Foundation.Primitives.Benchmarks.csproj -c Release -- --filter "*Framing*" --job dry`
- **THEN** BenchmarkDotNet 启动并执行选中的基准
- **AND** 不产生可用于基线比较的统计结论

#### Scenario: 不污染解决方案

- **WHEN** 检查 `Galosys.Foundation.Next.slnx`
- **THEN** 其中没有新增 benchmark 项目条目
- **AND** `BenchmarkDotNet.Artifacts/` 仍被根 `.gitignore` 忽略

### Requirement: 覆盖传输、framing、codec 与会话的基准场景

The benchmark project SHALL include scenarios for raw echo round-trip, `LengthPrefixedFramer` parse/write, `JsonMessageCodec<T>` encode/decode, and JSON session round-trip. Echo and framing scenarios MUST parameterize payload sizes `64`, `4096` and `65536` bytes. JSON codec and session scenarios MUST parameterize text lengths `64` and `4096` characters. Every benchmark class MUST use `[MemoryDiagnoser]`. Echo and session scenarios MUST establish one connection in `[GlobalSetup]` and close it in `[GlobalCleanup]`, so connection setup is not counted per operation. Receive buffers and `ArrayBufferWriter` instances MUST be reused across operations.

#### Scenario: 原始回显往返

- **WHEN** 运行 `EchoRoundTripBenchmarks`
- **THEN** 每个 payload 大小产生一行包含 `Mean`、`Allocated` 的结果
- **AND** client 与 server 使用持久连接，每次操作只发送并接收一条消息

#### Scenario: framing 解析与写入

- **WHEN** 运行 `FramingBenchmarks`
- **THEN** `Parse` 返回完整帧的 payload 长度
- **AND** `WriteFrame` 写入的长度随 payload 大小增长
- **AND** 结果包含解析与写入两个独立基准

#### Scenario: JSON 编解码

- **WHEN** 运行 `JsonCodecBenchmarks`
- **THEN** `Encode` 与 `Decode` 分别报告 ns/op 与 B/op
- **AND** 解码使用 setup 阶段预先编码的完整帧，不把编码时间计入解码

#### Scenario: session 往返

- **WHEN** 运行 `SessionRoundTripBenchmarks`
- **THEN** 服务端使用 `context.AsJsonSession<EchoMessage>()` 回显消息
- **AND** 客户端使用公共 `LengthPrefixedFramer` 与 `JsonMessageCodec<EchoMessage>` 组帧和解析
- **AND** 每条消息的往返结果被返回，避免 JIT 消除基准调用

#### Scenario: 缓冲复用

- **WHEN** 检查 benchmark 方法体
- **THEN** 接收缓冲、payload writer 与 frame writer 在 setup 中创建并在每次操作前 `Clear()`
- **AND** benchmark 方法内不分配与 payload 大小相同的临时接收数组

### Requirement: 决策门、范围边界与手动运行

The benchmark README MUST document environment requirements, build/run commands, scenario meanings, interpretation limits and the optimization decision gate. The change MUST NOT add a CI performance gate, MUST NOT modify production code under `src/`, `tests/` or `samples/`, and MUST NOT add Kestrel, IOQueue, `SocketAsyncEventArgs` comparison implementations or P4 client code. Baseline results MUST be recorded only by a manual run on a reference machine, using the template in `results/README.md`; a baseline older than 90 days, from a different machine or from a different .NET SDK MUST be re-recorded before comparison. The change is optional and MAY be applied after the P1.5a/P2 prerequisites are available.

#### Scenario: 无 CI 门禁

- **WHEN** 检查仓库的 CI/workflow 配置与测试项目
- **THEN** 没有新增 benchmark 步骤或性能阈值失败条件
- **AND** 现有构建与正确性测试行为不变

#### Scenario: 决策门触发

- **WHEN** 4 KiB session 往返的 B/op 超过同 payload 原始 echo 的 2 倍，或平均延迟回归超过 10% 且置信区间不重叠
- **THEN** 维护者先复测确认
- **AND** 需要优化时另开专门 change 并附基线对比，不在本 change 中修改 `src/`

#### Scenario: 明确排除的对照实现

- **WHEN** 检查 benchmark 项目的引用与源码
- **THEN** 不存在 Kestrel、IOQueue、`SocketAsyncEventArgs` 对照实现
- **AND** 不存在 `ChannelClient` 或 `IChannelClient` 基准

#### Scenario: 过期基线

- **WHEN** 基线记录超过 90 天、机器不同或 `dotnet --version` 不同
- **THEN** 该基线不得直接用于回归判断
- **AND** 按 `results/README.md` 的模板重新记录后再比较

#### Scenario: 生产代码零改动

- **WHEN** 对比本 change 的改动范围
- **THEN** 只有 `benchmarks/` 与根 `README.md` 发生变化
- **AND** `src/`、`tests/`、`samples/` 的文件内容保持不变
