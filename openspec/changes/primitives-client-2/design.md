## Context

`primitives-client-1` 已经交付 `ChannelClient`：连接成功暴露 `ChannelContext`，失败按 `ChannelConnectionFailure` 分类后回到 `Disconnected`，`Reconnecting` 只作为枚举值存在。设计文档 §5.7 要求客户端在断线后按指数退避重连，并明确“重连 = 新连接 + 新 `ConnectionId`，断开期间不缓冲、不重放”。

本 change 在现有状态机上叠加一条可取消的重试循环。它复用一个 policy 同时服务“首次连接失败后的重试”和“已连接后断线的重连”，避免两套退避逻辑。连接池与 batch 仍由 `primitives-client-3` 负责。

## Goals / Non-Goals

**Goals:**

- 提供 public `ChannelReconnectPolicy`，默认值与设计文档一致（1 秒初始、30 秒上限、2 倍退避、0.2 抖动、0 次表示无限）。
- `ChannelClientOptions.Reconnect` 可选；null 时保持 P4-1 的单次连接行为。
- 可重试失败进入 `Reconnecting`，按策略退避后重试；成功后 `Connected`，失败分类不可重试或次数耗尽后 `Disconnected`。
- 显式 `ConnectAsync` 在配置了策略时等待整个重试过程；已连接后的意外断开由后台重连驱动。
- 退避只使用 `TimeProvider`，测试可在不等待真实时间的情况下推进。
- `CloseAsync` / `DisposeAsync` 立即取消退避与重连，终态不可逆。

**Non-Goals:**

- 不缓冲或重放断开期间的消息；重连后旧 `ChannelContext` 不复活。
- 不做多 `EndPoint` 轮询/故障转移。
- 不做连接池、租借、健康检查或 batch。
- 不引入 Polly、`IResiliencePipeline` 或宿主 `BackgroundService`。
- 不改变 `ChannelConnectionFailure` 的分类规则，只在策略层决定是否重试。
- 不做心跳；TCP keep-alive 仍由 `ChannelClientOptions.KeepAlive` 控制。

## Decisions

### 1. `ChannelReconnectPolicy` 的字段与默认值

```csharp
namespace Microsoft.Extensions.Options;

public sealed class ChannelReconnectPolicy
{
    public int MaxAttempts { get; set; }                 // 0 = 无限
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(30);
    public double BackoffMultiplier { get; set; } = 2.0;
    public double JitterFactor { get; set; } = 0.2;
    public Func<Exception, bool>? ShouldRetry { get; set; }

    internal Func<double> JitterSource { get; set; } = () => Random.Shared.NextDouble();
    internal void Validate();
    internal ChannelReconnectPolicy Clone();
}
```

- `MaxAttempts` 只统计“首次尝试之后的重试次数”。`0` 表示无限；`1` 表示失败后再试一次。
- `InitialDelay` 必须是正数且不超过 1 天；`MaxDelay` 必须不小于 `InitialDelay` 且不超过 1 天；`BackoffMultiplier` 必须大于等于 1；`JitterFactor` 必须在 `[0, 1]`。
- `ShouldRetry` 为 null 时使用 `ChannelConnectionFailure.IsRetryable`；非 null 时完全覆盖默认分类，便于业务把某些网络错误视为致命。
- `JitterSource` 是 internal，测试通过 `InternalsVisibleTo` 注入固定值；公开 API 不暴露随机源。
- `Clone()` 复制所有值并保留 `ShouldRetry` 与 `JitterSource` 委托；`ChannelClientOptions.Clone()` 调用它，避免构造后修改原 policy 影响运行中的客户端。

### 2. 首次连接与断线重连共用一条重试循环

`ConnectAsync` 的语义从“一次尝试”改为：

1. 尝试一次连接。
2. 失败且 `Reconnect` 非空、失败可重试、尝试次数未耗尽 → 进入 `Reconnecting`，退避后重试。
3. 成功 → `Connected` 并返回。
4. 不可重试或次数耗尽 → `Disconnected`，抛出最后一次异常。

已连接后由 watcher 观察到 `Completion`，先释放旧连接，再调用同一套重试循环（后台运行，不向调用方抛异常）。这样：

- 首次连接与断线重连的退避、分类、状态迁移完全一致。
- `ConnectAsync` 在配置了无限重试且服务端一直不可用时可能永不返回；调用方必须用取消 token 控制。这是显式的策略选择，不静默吞掉失败。
- 重连期间 `Connection` 为 null；只有新连接成功后才重新非 null。
- 每次成功都创建新的 `ChannelContext` 与 `ConnectionId`；旧 context 已释放，不能复用。

### 3. 状态迁移

```text
Disconnected --ConnectAsync--> Connecting --成功--> Connected
                                  |                    |
                                  | 可重试失败          | 意外断开
                                  v                    v
                              Reconnecting <------------+
                                  |
                          成功 ---+--- 不可重试/耗尽 --> Disconnected
                                  |
                          CloseAsync/Dispose --> Closed
```

- `Reconnecting` 事件在进入退避前触发一次，携带触发重连的 `ChannelConnectionFailure`。
- 断线后直接 `Connected → Reconnecting`，不额外插入 `Disconnected`；调用方从 `Connection` 变 null 就能知道旧连接已失效。
- 重连尝试失败不重复触发 `Reconnecting`（状态未变），只更新内部最后的失败；最终 `Disconnected` 时携带最后一次失败。
- `Closed` 仍然终态；关闭后 watcher 与退避都检查 `_closed`，不再迁移。

### 4. 退避、抖动与 `TimeProvider`

```csharp
private static TimeSpan NextDelay(TimeSpan current, ChannelReconnectPolicy policy)
{
    var next = TimeSpan.FromTicks((long)(current.Ticks * policy.BackoffMultiplier));
    return next > policy.MaxDelay ? policy.MaxDelay : next;
}

private async Task DelayBeforeRetryAsync(TimeSpan delay, CancellationToken ct)
{
    var random = _options.Reconnect!.JitterSource();
    var factor = 1 + ((random * 2) - 1) * _options.Reconnect.JitterFactor;
    var jittered = TimeSpan.FromTicks(Math.Max(1, (long)(delay.Ticks * factor)));

    await Task.Delay(jittered, _options.TimeProvider, ct).ConfigureAwait(false);
}
```

- 只调用三参数 `Task.Delay(TimeSpan, TimeProvider, CancellationToken)`；库代码中不得再出现裸 `Task.Delay(TimeSpan, CancellationToken)` 用于退避。
- 抖动在 `[1 - JitterFactor, 1 + JitterFactor]` 内；`JitterFactor = 0` 时可精确断言延迟。
- `FakeTimeProvider.Advance` 触发延迟完成；测试不等待真实退避时间。
- `ConnectTimeout` 仍按每次尝试计算，覆盖该次 TCP + TLS；退避时间不计入单次 `ConnectTimeout`。

### 5. 失败分类与 `ShouldRetry`

- 默认重试：`ConnectionRefused`、`TimedOut`、`HostUnreachable`、`Network`。
- 默认不重试：`Authentication`（证书/协议配置错误）、`Canceled`（调用方关闭）、`Configuration`（选项错误）、`Unknown`。
- `ShouldRetry` 非 null 时，以回调结果为准；回调抛出的异常不捕获，按当前重连操作的失败处理，避免策略异常被误判为可重试。
- 远端正常 EOF 没有异常原因时，构造一个 `IOException("连接已由对端关闭。")` 的 `Network` 失败参与判断，使“正常关闭”也能按策略重连。
- TLS 证书失败不会重连；测试锁定 `StateChanged` 不出现 `Reconnecting`。

### 6. 取消与关闭

- `ConnectAsync` 首次调用者的 token 继续控制整个操作（包括退避）；并发调用者只取消自己的等待。
- `CloseAsync` 取消 `_connectCts` 与退避 token，把状态置为 `Closed`，等待重连任务退出后释放当前连接；重连任务看到 `Closed` 不再迁移。
- 后台重连任务必须观察所有异常，不能产生未观察的 `Task`；失败通过状态事件暴露。
- 关闭后即使退避计时器到期，也不能再创建 socket；每次重连尝试前检查 `_closed`。

### 7. 测试策略

- 使用 `Microsoft.Extensions.TimeProvider.Testing` 的 `FakeTimeProvider`，版本与测试项目其他 Microsoft.Extensions 包保持 10.x。
- 服务端先关闭再启动，锁定“首次连接失败后重试成功”。
- 服务端第一条连接主动 `Abort`，锁定“已连接后后台重连”。
- TLS 证书拒绝、`ShouldRetry = _ => false`、`MaxAttempts` 耗尽锁定“不重试/停止”。
- `CloseAsync` 在 `Reconnecting` 期间调用，推进假时钟后确认没有新连接。
- 测试中的 `JitterFactor` 设为 0，避免随机延迟；另用一个 policy 单测验证抖动范围。

## Risks / Trade-offs

- **无限重试会让 `ConnectAsync` 永不返回** → 默认 `MaxAttempts = 0` 是设计文档的选择；调用方必须传入可取消 token。README 明确说明。
- **首次调用者取消会取消整个共享重试** → 与 P4-1 的共享尝试语义一致；并发调用者可用自己的 token 只取消等待。需要独立生命周期的场景由连接池/manager 层解决。
- **断线后直接进入 `Reconnecting` 而不经过 `Disconnected`** → 减少状态噪音，但订阅方必须把 `Reconnecting` 当作“当前无连接”。`Connection` 为 null 已表达这一点。
- **`ShouldRetry` 回调可能阻塞或抛异常** → 回调在重试循环内同步执行；文档要求快速、无阻塞。异常按当前尝试失败处理，不额外包装。
- **假时钟与真实 socket 超时混用** → 退避完全用假时钟，TCP/TLS 超时仍用真实 `CancelAfter`；测试把 `ConnectTimeout` 设短，避免长时间等待。
- **后台重连与 `CloseAsync` 竞争** → 所有字段由 `_gate` 保护，关闭先置 `Closed` 再取消；重连每次尝试前检查状态。测试覆盖关闭后退避到期。
- **重连不重放消息** → 调用方必须自行判断哪些请求需要重试；框架不提供“至少一次”语义，避免隐瞒重复发送风险。

## Migration Plan

1. 增加 `ChannelReconnectPolicy` 与校验测试；确认默认值、`MaxAttempts = 0` 和 `ShouldRetry` 克隆。
2. 给 `ChannelClientOptions` 增加 `Reconnect`，补选项校验与克隆隔离测试。
3. 把 `ChannelClient` 的单次连接拆成“单次尝试 + 重试循环”，先让首次连接重试测试通过。
4. 把 watcher 的 `Disconnected` 分支改为“按策略进入 `Reconnecting` 并后台重连”，跑通断线重连。
5. 补非可重试、耗尽、关闭、取消测试；用假时钟断言退避。
6. 更新 README，跑构建、全量测试与 OpenSpec 校验。
7. 回滚时删除 policy 与 `Reconnect` 属性，恢复 `ConnectAsync` 单次尝试与 watcher 的 `Disconnected` 分支即可；P4-1 行为不受影响。

## Open Questions

无。连接池、batch、多 EndPoint 与心跳明确留给后续 change。
