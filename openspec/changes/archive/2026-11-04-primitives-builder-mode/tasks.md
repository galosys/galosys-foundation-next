# Tasks

> **REQUIRED SUB-SKILL:** 使用 `executing-plans` 或 `opsx-apply-x` 逐任务执行本计划。
> 本仓库虽有 git，但按约定不自动提交；步骤中的"验证"统一用构建 + 测试，不执行 git commit。

**Goal:** 删除 `Galosys.Foundation.Channel` 的 IoC/Hosting 集成，改用 `ChannelServerBuilder` / `ChannelClientBuilder` / `ChannelClientPoolBuilder` 作为 server、client 与连接池的唯一公开构造入口。

**Architecture:** 三个 builder 各自持有配置副本与累积项，`Build()` 时物化成 `ChannelServer` / `ChannelClient` / `ChannelClientPool`。`ChannelServerBuilder` 在 `Build()` 时把中间件列表与终端处理器闭包成 `Action<IChannelPipeline>`，复用现有 internal `ChannelPipeline` 语义。`UseTls` 在注册时调用 `ChannelTlsOptions.ValidateServer()` 做即时校验。

**Tech Stack:** .NET 10、xunit 2.9.3、Microsoft.Extensions.DependencyInjection/Logging Abstractions。

---

## 1. Server 构建器

- [x] 1.1 创建 `ChannelServerBuilder` 骨架（配置快照 + 中间件列表 + 终端处理器）—— 已创建 `Builders/ChannelServerBuilder.cs`，Create 立即校验配置
- [x] 1.2 给 `ChannelServerBuilder` 增加 `Use` / `Run` / `WithLoggerFactory` / `WithServiceScopeFactory` —— Use/Run/WithLoggerFactory/WithServiceScopeFactory 已加，Run 重复调用抛 InvalidOperationException
- [x] 1.3 给 `ChannelServerBuilder` 增加 `UseTls`（注册时即时校验）—— UseTls 注册时调用 ValidateServer() 即时校验
- [x] 1.4 给 `ChannelServerBuilder` 增加 `Build`（物化管线）—— Build 物化 Action<IChannelPipeline>，TLS 在管线最前
- [x] 1.5 新增 `ChannelServerBuilderTests` 并覆盖构建器的核心行为 —— 新增 `ChannelServerBuilderTests.cs`，6 个测试
- [x] 1.6 补充中间件顺序与多次构建隔离性的测试 —— 补中间件顺序与配置快照测试，共 8 个测试
- [x] 1.7 更新 Channel README 的 server 构建章节 —— 最小用法改为 builder；新增架构分层、职责边界、关键抽象章节

## 2. Client 与连接池构建器

- [x] 2.1 创建 `ChannelClientBuilder` 骨架 —— 已创建 `Builders/ChannelClientBuilder.cs`
- [x] 2.2 给 `ChannelClientBuilder` 增加配置方法与 `Build` —— WithReconnect/WithTimeProvider/WithKeepAlive/Build 已加
- [x] 2.3 创建 `ChannelClientPoolBuilder` —— 已创建 `Builders/ChannelClientPoolBuilder.cs`，MaxSize/RentTimeout 即时校验
- [x] 2.4 给 `ChannelClientBuilder` 增加 `AsPool` —— AsPool 继承客户端配置
- [x] 2.5 新增 `ChannelClientBuilderTests` —— 新增 `ChannelClientBuilderTests.cs`，6 个测试
- [x] 2.6 更新 Channel README 的 client 与 pool 章节 —— client / 重连 / 连接池章节改为 builder 写法

## 3. 删除 IoC 与 Hosting 集成

- [x] 3.1 把三个类型的构造函数改为 internal —— 三个构造函数改 internal，XML doc 指向对应 builder
- [x] 3.2 删除 IoC 扩展、hosted service 与服务器定义 —— 已删除三个文件及两个空目录
- [x] 3.3 移除 hosting 包引用 —— csproj 移除 microsoft.extensions.hosting.abstractions
- [x] 3.4 给 `NamespaceConventionTests` 增加「已删除类型不存在」的回归断言 —— 断言已删除类型不存在 + 构造函数非 public

## 4. 迁移测试

- [x] 4.1 删除 `ServerGroupHostingTests` —— 文件已删除
- [x] 4.2 迁移 `HostingTests` 到 builder —— 改走 builder；缺 EndPoint 场景改为在 Create 处断言 ArgumentException
- [x] 4.3 迁移 `ScopedHostingTests` 到 builder —— 用 ServiceCollection 提供 IServiceScopeFactory
- [x] 4.4 迁移 `ChannelClientTests` 到 builder —— 9 处改走 ChannelClientBuilder
- [x] 4.5 迁移 `ChannelClientPoolTests` 到 builder —— 2 处改走 AsPool；非法配置场景改测 WithMaxSize/WithRentTimeout
- [x] 4.6 跑全量测试 —— 271/271 通过

## 5. 迁移示例

- [x] 5.1 迁移 `NetServer.ConsoleApp` 的 server 构造 —— server 构造改走 ChannelServerBuilder
- [x] 5.2 迁移 `NetServer.ConsoleApp` 的连接池构造 —— 池构造改走 AsPool()
- [x] 5.3 迁移 `SenderBootstrapper` 的客户端构造 —— 客户端改走 ChannelClientBuilder，移除 IChannelClientFactory 参数
- [x] 5.4 更新示例 README —— 发送模式表补构建方式，结构表说明改为构建器构建

## 6. 更新规范与仓库文档

- [x] 6.1 更新 `openspec/specs/` 下的主 spec —— 6 个主 spec 已合并 delta；额外拆分 2 条超长 requirement 以过 --strict
- [x] 6.2 新增 `channel-builder` 主 spec —— 新增 openspec/specs/channel-builder/spec.md，10 requirement / 20 scenario
- [x] 6.3 更新 `AGENTS.md` —— Channel 库条目改为 builder 描述，补充不提供 DI 扩展与 hosted service
- [x] 6.4 更新 Channel README 的宿主接入与多服务器章节 —— 删除「多服务器宿主注册」整节；宿主接入改为调用方自包 runner；协议分发/前缀路由/TLS 示例改 builder
- [x] 6.5 全量验证与 OpenSpec 校验 —— change 校验通过；spec 基线 8 passed/5 failed 变为 9 passed/5 failed（5 项为改动前既有失败）

## 7. README 片段回归保护

- [x] 7.1 新增 `ReadmeSnippetCompileTests`，把 README 关键代码片段搬成测试 —— 9 个测试，README 改了就编译失败

## Workflow follow-up

- 归档本 change：完成 review 后执行 `openspec archive primitives-builder-mode`，把 delta 同步进主 spec 并清理 change 目录。
- 归档后重新运行 `openspec validate --specs --strict --no-interactive` 确认主 spec 仍然有效。
- README 任务（1.7、2.6、5.4、6.4）在归档阶段处理。
