## ADDED Requirements

### Requirement: 示例迁移

The `NetServer.ConsoleApp` sample SHALL create its message session with `context.AsLineTextSession()` inside the terminal handler only, using `LineFramer` and `Utf8StringCodec`. It MUST keep listening on `0.0.0.0:8000`, print each received line as `>>>>> {line} <<<<<`, send `hello\n` once per second from `SenderBootstrapper`, and shut down gracefully on Ctrl+C. The sample README MUST document a manual verification that splits a multibyte UTF-8 character across two writes and expects the complete character in the log. This requirement MUST NOT change the `channel-hosting` capability.

#### Scenario: 行文本会话替换原始字节读取

- **WHEN** 客户端向示例发送 `hello\n`
- **THEN** `SampleChannelHandler` 通过 `AsLineTextSession` 读到 `hello`
- **AND** 日志输出 `>>>>> hello <<<<<`
- **AND** handler 不再按读取块直接调用 `Encoding.UTF8.GetString`

#### Scenario: 发送端每秒发送带换行的 hello

- **WHEN** 示例启动并连接本机 8000 端口
- **THEN** `SenderBootstrapper` 每秒发送一次 `hello\n`
- **AND** 示例每秒打印一条 `>>>>> hello <<<<<`
- **AND** 逻辑消息仍然是 `hello`，换行只作为行协议边界

#### Scenario: 多字节 UTF-8 跨两次写入

- **WHEN** 客户端把 `"你好\n"` 的 UTF-8 字节拆成两次写入，第一次只写第一个字节，第二次写剩余字节
- **THEN** 示例打印 `>>>>> 你好 <<<<<`
- **AND** 不出现替换字符 `�`
- **AND** 字符不会因为 TCP 分段而被截断

#### Scenario: 会话只在终端处理器创建

- **WHEN** 检查示例的 `Program.cs`、`ChannelServerRunner` 和 pipeline 配置
- **THEN** 这些位置都不调用 `AsSession`/`AsLineTextSession`
- **AND** session 只在 `SampleChannelHandler.HandleAsync` 内创建

#### Scenario: Ctrl+C 优雅停机

- **WHEN** 对运行中的示例按 Ctrl+C
- **THEN** 进程在宿主与服务器的 `ShutdownTimeout` 内退出
- **AND** 不出现未处理异常

#### Scenario: README 提供手工验证

- **WHEN** 阅读 `samples/NetServer.ConsoleApp/README.md`
- **THEN** 可以找到启动示例、另开客户端、分两次写入 `"你好\n"` 的 PowerShell 命令
- **AND** 文档给出预期日志 `>>>>> 你好 <<<<<`
- **AND** 文档说明 sender 的 `hello` 日志会同时出现
