# channel-client-pool Specification

## Purpose
定义有界客户端连接池的工厂、配置、租借、归还、健康替换与释放语义，让并发调用方在固定连接上限内复用已建立的客户端连接。

## Requirements

### Requirement: 客户端工厂契约

The implementation SHALL provide a public `IChannelClientFactory` in `Microsoft.Extensions.Hosting.Net` with `IChannelClient Create(ChannelClientOptions options)` and a public sealed `ChannelClientFactory`. `Create` MUST reject null options with `ArgumentNullException` and MUST return a new client instance on every call; option validation remains the client constructor's responsibility.

#### Scenario: 默认工厂每次创建新客户端

- **WHEN** 连续两次调用 `ChannelClientFactory.Create(options)`
- **THEN** 返回两个不同的 `IChannelClient` 实例
- **AND** 每个实例都使用传入的选项校验并克隆配置

#### Scenario: 工厂拒绝空选项

- **WHEN** 调用 `Create(null)`
- **THEN** 抛出 `ArgumentNullException`
- **AND** 不创建客户端

### Requirement: 连接池工厂注入

`ChannelClientPool` MUST accept an optional `IChannelClientFactory` and MUST use `ChannelClientFactory` when none is supplied. Every client created by the pool MUST come from the configured factory.

#### Scenario: 连接池使用默认工厂

- **WHEN** 构造 `ChannelClientPool` 时未传入工厂
- **THEN** 池使用 `ChannelClientFactory` 创建客户端

#### Scenario: 连接池使用注入的工厂

- **WHEN** 构造 `ChannelClientPool` 时传入自定义 `IChannelClientFactory`
- **THEN** 池创建的每个客户端都来自该工厂

### Requirement: 连接池配置

The implementation SHALL provide a public sealed `ChannelClientPoolOptions` in `Microsoft.Extensions.Options` with `ChannelClientOptions ClientOptions`, `int MaxSize` defaulting to 8, and `TimeSpan RentTimeout` defaulting to 30 seconds. `ClientOptions` MUST be non-null and valid; `MaxSize` MUST be greater than zero; `RentTimeout` MUST be greater than zero and at most one day. The pool MUST validate and clone the options at construction so later caller mutations do not affect a running pool.

#### Scenario: 默认配置

- **WHEN** 创建 `ChannelClientPoolOptions`
- **THEN** `MaxSize` 为 8
- **AND** `RentTimeout` 为 30 秒
- **AND** `ClientOptions` 非 null

#### Scenario: 非法池配置

- **WHEN** `ClientOptions` 为 null 或无效、`MaxSize` 小于等于 0，或 `RentTimeout` 非正或超过 1 天
- **THEN** 构造 `ChannelClientPool` 时抛出 `ArgumentException`
- **AND** 不创建任何客户端

#### Scenario: 选项克隆隔离

- **WHEN** 构造连接池后修改原 `ChannelClientPoolOptions` 或其中的 `ClientOptions`
- **THEN** 运行中的连接池继续使用构造时的配置
- **AND** 后续创建的客户端不受修改影响

### Requirement: 有界租借与复用

The implementation SHALL provide `ChannelClientPool.RentAsync(CancellationToken ct)` returning a connected `IChannelClient`. The pool MUST reuse a healthy idle client when available; otherwise, while the total created-or-rented count is below `MaxSize`, it MUST create and connect a new client. Creation failures MUST release the reserved slot and dispose the partial client. Caller cancellation during creation MUST throw `OperationCanceledException`.

#### Scenario: 低于上限时创建新连接

- **WHEN** 没有空闲客户端且当前总数小于 `MaxSize`
- **THEN** 池通过工厂创建一个客户端并调用 `ConnectAsync`
- **AND** 返回已连接的客户端

#### Scenario: 复用健康空闲连接

- **WHEN** 租借一个客户端、归还后再租借
- **THEN** 第二次租借返回同一个客户端实例
- **AND** 不创建新连接

#### Scenario: 创建失败释放槽位

- **WHEN** 工厂或 `ConnectAsync` 在创建新客户端时失败
- **THEN** 失败客户端被释放
- **AND** 槽位被归还，后续 `RentAsync` 仍可创建客户端
- **AND** 原始异常传播给调用方

#### Scenario: 并发租借不超过上限

- **WHEN** 多个任务同时调用 `RentAsync`，`MaxSize` 为 M
- **THEN** 池创建并持有的客户端总数不超过 M
- **AND** 每个成功调用都获得已连接的客户端

### Requirement: 池满等待与超时

When the pool is full, `RentAsync` MUST wait up to `RentTimeout` for a return. A return MUST wake one waiter. Rent timeout MUST throw `TimeoutException`; caller cancellation while waiting MUST throw `OperationCanceledException` and MUST NOT consume a slot.

#### Scenario: 达到上限后等待归还

- **WHEN** `MaxSize` 为 1，第一个客户端已租出，第二个 `RentAsync` 正在等待
- **THEN** 第二个调用不创建新客户端
- **AND** 第一个客户端归还后，第二个调用获得该客户端

#### Scenario: 等待归还超时

- **WHEN** 池已满且 `RentTimeout` 到期仍没有客户端归还
- **THEN** `RentAsync` 抛出 `TimeoutException`
- **AND** 已租出的客户端保持可用

#### Scenario: 调用方取消等待

- **WHEN** `RentAsync` 在等待归还期间调用方取消
- **THEN** 抛出 `OperationCanceledException`
- **AND** 不占用槽位、不创建客户端

### Requirement: 健康检查与替换

The pool MUST treat a client as healthy only when `State` is `Connected` and `Connection` is non-null. An unhealthy idle client discovered during rent MUST be disposed and its slot released before another client is created or awaited. `ReturnAsync` MUST dispose an unhealthy client instead of placing it in the idle queue. The pool MUST NOT proactively ping or scan rented clients.

#### Scenario: 空闲客户端断线后被替换

- **WHEN** 空闲客户端在池中因远端关闭变为不健康，随后调用 `RentAsync`
- **THEN** 该客户端被释放
- **AND** 池创建并返回新的已连接客户端

#### Scenario: 归还不健康客户端

- **WHEN** 租出的客户端断线后调用 `ReturnAsync`
- **THEN** 客户端被释放而不是放回空闲队列
- **AND** 槽位被归还，下一次租借可以创建新客户端

#### Scenario: 已租出客户端不被后台替换

- **WHEN** 客户端在租借期间断线
- **THEN** 池不会主动创建替换连接
- **AND** 调用方归还后，池在下次租借时再替换

### Requirement: 归还与所有权

`ReturnAsync` MUST accept only a client currently rented from the same pool. Returning null MUST throw `ArgumentNullException`; returning an unknown, foreign, or already-returned client MUST throw `InvalidOperationException`. A successful return MUST transfer ownership back to the pool, after which the caller MUST NOT use the client. Returning after pool disposal MUST still release the client and its slot.

#### Scenario: 重复归还抛错

- **WHEN** 同一个客户端连续两次调用 `ReturnAsync`
- **THEN** 第二次抛出 `InvalidOperationException`
- **AND** 池状态不受影响

#### Scenario: 归还其他池的客户端

- **WHEN** 把连接池 A 租出的客户端归还给连接池 B
- **THEN** 连接池 B 抛出 `InvalidOperationException`
- **AND** 两个池都不释放该客户端

#### Scenario: 池释放后归还

- **WHEN** 连接池已释放，调用方归还之前租出的客户端
- **THEN** 归还操作正常完成
- **AND** 客户端被释放、槽位被清理

### Requirement: 连接池释放

`ChannelClientPool.DisposeAsync` MUST be idempotent. It MUST stop new rentals, complete the idle queue, dispose all idle clients, and cause pending and future `RentAsync` calls to throw `ObjectDisposedException`. Rented clients MUST NOT be force-aborted; they are disposed when returned.

#### Scenario: 释放空闲连接

- **WHEN** 池中有空闲客户端时调用 `DisposeAsync`
- **THEN** 所有空闲客户端被释放并进入 `Closed`
- **AND** 后续 `RentAsync` 抛出 `ObjectDisposedException`

#### Scenario: 已租出连接不被强制中断

- **WHEN** 池释放时仍有客户端处于租出状态
- **THEN** 该客户端保持 `Connected`
- **AND** 调用方归还时才被释放

#### Scenario: 重复释放

- **WHEN** 连续调用两次 `DisposeAsync`
- **THEN** 第二次正常返回
- **AND** 不重复释放客户端、不抛异常
