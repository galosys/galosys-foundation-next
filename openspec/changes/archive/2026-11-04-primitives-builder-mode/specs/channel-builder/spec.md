# Spec Delta

## Purpose

定义 `Galosys.Foundation.Channel` 的 builder 构建契约：server、client 与 client pool 的唯一公开构造入口，覆盖配置快照、中间件注册顺序、终端处理器唯一性、TLS 与参数校验时机，以及重复构建的实例独立性。

## ADDED Requirements

### Requirement: Server 构建器契约

The implementation SHALL provide a public sealed `ChannelServerBuilder` in `Galosys.Foundation.Channel` with static `Create(ChannelServerOptions)` and `Create(Action<ChannelServerOptions>)` entry points, chainable `Use(ChannelMiddleware)`, `Run(ChannelHandlerDelegate)`, `WithLoggerFactory(ILoggerFactory?)`, `WithServiceScopeFactory(IServiceScopeFactory?)` methods, and a `Build()` method producing a `ChannelServer`.

#### Scenario: 构建的服务器可启动并回显

- **WHEN** 调用方用 `Create` 配置端点、`Run` 配置回显处理器后调用 `Build()` 并启动结果
- **THEN** 服务器绑定成功且 `LocalEndPoint` 非 null
- **AND** 客户端连接后收到完整回显
- **AND** 停止后不再接受新连接

### Requirement: Server 构建器的配置快照

The server builder MUST clone the supplied options so later caller mutations MUST NOT affect a built server.

#### Scenario: 配置快照隔离调用方修改

- **WHEN** 调用方构建服务器后修改原始 `ChannelServerOptions`
- **THEN** 运行中的服务器继续使用构建时的配置
- **AND** 服务器行为不受该修改影响

### Requirement: Server 构造函数不再是公开入口

The `ChannelServer` constructor MUST NOT be public. The builder MUST be the only public construction path for a server.

#### Scenario: 构造函数不再是公开入口

- **WHEN** 通过反射检查 `ChannelServer` 的构造函数
- **THEN** 不存在 public 构造函数
- **AND** `ChannelServerBuilder.Build()` 是唯一公开的服务器构造路径

### Requirement: 中间件与终端处理器的注册顺序

The implementation SHALL preserve middleware registration order when materializing the pipeline, and MUST allow exactly one terminal handler. Registering a second terminal handler MUST throw `InvalidOperationException` at registration time. Building without a terminal handler MUST throw `InvalidOperationException` before any listener is bound. The materialized pipeline MUST be applied on every server start attempt.

#### Scenario: 中间件按注册顺序包裹处理器

- **WHEN** 调用方依次注册两个中间件后 `Run` 一个处理器
- **THEN** 请求先经过第一个中间件，再经过第二个中间件，最后到达处理器
- **AND** 中间件的响应按相反顺序返回

#### Scenario: 重复注册终端处理器被拒绝

- **WHEN** 调用方对同一个构建器调用两次 `Run`
- **THEN** 第二次调用抛出 `InvalidOperationException`
- **AND** 构建器保留第一次注册的处理器

#### Scenario: 缺少终端处理器时构建失败

- **WHEN** 调用方只注册中间件而没有 `Run`，然后调用 `Build()`
- **THEN** `Build()` 抛出 `InvalidOperationException`
- **AND** 不创建监听 socket，`LocalEndPoint` 保持 null

### Requirement: TLS 配置的即时校验

The implementation SHALL validate server-side TLS configuration when `UseTls` is called on the builder, not deferred until the server starts. A TLS configuration missing a server certificate or carrying an invalid handshake timeout MUST throw `ArgumentException` at the `UseTls` call. The builder MUST register the TLS middleware in the same position as the other middleware.

#### Scenario: 缺少服务端证书时立即失败

- **WHEN** 调用方用一个没有 `ServerCertificate` 的 TLS 配置调用 `UseTls`
- **THEN** `UseTls` 抛出 `ArgumentException`
- **AND** 该 TLS 中间件不被注册到构建器

#### Scenario: 非法握手超时立即失败

- **WHEN** 调用方用一个握手超时非法的 TLS 配置调用 `UseTls`
- **THEN** `UseTls` 抛出 `ArgumentException`

#### Scenario: 有效 TLS 配置正常注册

- **WHEN** 调用方用一个有效的服务端 TLS 配置调用 `UseTls`，然后 `Run` 处理器并 `Build()`
- **THEN** 构建成功
- **AND** 服务器在建立连接时完成握手后才进入处理器

### Requirement: Client 构建器契约

The implementation SHALL provide a public sealed `ChannelClientBuilder` in `Galosys.Foundation.Channel` with static `Create(ChannelClientOptions)` and `Create(Action<ChannelClientOptions>)` entry points, chainable `WithReconnect(ChannelReconnectPolicy)`, `WithTimeProvider(TimeProvider)`, `WithKeepAlive(ChannelKeepAliveOptions)` methods, and a `Build()` method producing an `IChannelClient`.

#### Scenario: 构建的客户端可建立连接

- **WHEN** 调用方用 `Create` 配置目标端点后 `Build()` 并调用 `ConnectAsync`
- **THEN** 连接建立成功且 `State` 为 `Connected`
- **AND** `Connection` 非 null

### Requirement: Client 构建器的配置校验

The client builder MUST validate and clone the supplied options at creation, and MUST reject invalid configuration with `ArgumentException` before any socket is created.

#### Scenario: 非法客户端配置在构建时失败

- **WHEN** 调用方用缺失端点或超时非法的配置调用 `Create`
- **THEN** 抛出 `ArgumentException`
- **AND** 不创建任何 socket

#### Scenario: 重连策略经构建器生效

- **WHEN** 调用方用 `WithReconnect` 配置重连策略后 `Build()` 并连接到不可用端点
- **THEN** 客户端按该策略的退避节奏重试
- **AND** 未配置重连策略时失败不重试

### Requirement: Client 构造函数不再是公开入口

The `ChannelClient` constructor MUST NOT be public. The builder MUST be the only public construction path for a single-connection client.

#### Scenario: 客户端构造函数不再是公开入口

- **WHEN** 通过反射检查 `ChannelClient` 的构造函数
- **THEN** 不存在 public 构造函数
- **AND** `ChannelClientBuilder.Build()` 是唯一公开的客户端构造路径

### Requirement: 连接池作为客户端构建的选项

The implementation SHALL provide a public sealed `ChannelClientPoolBuilder` in `Galosys.Foundation.Channel` reached through a `AsPool()` method on the client builder, with chainable `WithMaxSize(int)`, `WithRentTimeout(TimeSpan)`, `WithFactory(IChannelClientFactory)` methods, and a `Build()` method producing a `ChannelClientPool`. Entering pool mode MUST carry over the client options already configured on the client builder. The `ChannelClientPool` constructor MUST be non-public.

#### Scenario: 构建的连接池可租借客户端

- **WHEN** 调用方用 `Create` 配置端点、`AsPool()` 进入池模式、`Build()` 后租借客户端
- **THEN** 池返回一个已连接的客户端
- **AND** 归还后池可以再次租借该客户端

#### Scenario: 池模式继承客户端配置

- **WHEN** 调用方在 `Create` 中配置端点后调用 `AsPool()` 并 `Build()`
- **THEN** 池创建的客户端使用该端点
- **AND** 池的 `MaxSize` 与 `RentTimeout` 取其默认值

#### Scenario: 非法池参数立即失败

- **WHEN** 调用方用非正数调用 `WithMaxSize`，或用非正数调用 `WithRentTimeout`
- **THEN** 该调用抛出 `ArgumentException`
- **AND** 不构建池

#### Scenario: 注入自定义工厂

- **WHEN** 调用方用 `WithFactory` 注入自定义 `IChannelClientFactory` 并 `Build()`
- **THEN** 池创建的每个客户端都来自该工厂

#### Scenario: 池构造函数不再是公开入口

- **WHEN** 通过反射检查 `ChannelClientPool` 的构造函数
- **THEN** 不存在 public 构造函数
- **AND** `ChannelClientPoolBuilder.Build()` 是唯一公开的连接池构造路径

### Requirement: 多次构建的实例独立性

The implementation SHALL allow a builder to be built more than once. Each build MUST produce a distinct instance, MUST snapshot the configuration at build time, and MUST NOT share mutable configuration between the produced instances. A later mutation of the builder's configuration MUST NOT affect an already built instance.

#### Scenario: 重复构建产出不同实例

- **WHEN** 调用方对同一个构建器连续调用两次 `Build()`
- **THEN** 两次调用返回两个不同的实例
- **AND** 两个实例各自可以独立启动或连接

#### Scenario: 构建后的修改不影响既有实例

- **WHEN** 调用方构建实例后再修改构建器的配置
- **THEN** 已构建的实例继续使用构建时的配置
