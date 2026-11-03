# NetServer.ConsoleApp

`Galosys.Foundation.Channel` 的**双向**示例：同一个进程里跑服务端与客户端，演示框架客户端侧的
`ChannelClient`、`ChannelClientPool`、重连退避与批量写。

- 服务端监听 `0.0.0.0:8000`，通过 `context.AsLineTextSession()` 按行读取并回包。
- 客户端每秒发一条请求并读回响应，形成 request/response 往返。
- 前 3 轮用 `WriteBatchAsync` 批量发两条，之后退化为单条发送。

## 运行

```powershell
dotnet run --project samples/NetServer.ConsoleApp/NetServer.ConsoleApp.csproj
```

预期输出（客户端与服务端日志交错出现）：

```text
info: NetServer.ConsoleApp.SenderBootstrapper[0]
      客户端状态: Disconnected -> Connecting
info: NetServer.ConsoleApp.SenderBootstrapper[0]
      客户端状态: Connecting -> Connected
info: NetServer.ConsoleApp.ChannelSender[0]
      批量发送 2 条: batch-0-a, batch-0-b
info: NetServer.ConsoleApp.SampleChannelHandler[0]
      >>>>> batch-0-a <<<<<
info: NetServer.ConsoleApp.SampleChannelHandler[0]
      >>>>> batch-0-b <<<<<
info: NetServer.ConsoleApp.ChannelSender[0]
      收到: echo: batch-0-a
info: NetServer.ConsoleApp.ChannelSender[0]
      收到: echo: batch-0-b
info: NetServer.ConsoleApp.ChannelSender[0]
      发送: hello-3
info: NetServer.ConsoleApp.SampleChannelHandler[0]
      >>>>> hello-3 <<<<<
info: NetServer.ConsoleApp.ChannelSender[0]
      收到: echo: hello-3
```

按 Ctrl+C 触发宿主优雅停机。

## 协议

| 方向 | 线格式 | 说明 |
|---|---|---|
| 客户端 → 服务端 | `hello-3\n` | 换行由 `LineFramer` 自动追加，**不要**自己在字符串里加 `\n` |
| 服务端 → 客户端 | `echo: hello-3\n` | 服务端给每行加 `echo: ` 前缀 |

协议是「换行分隔 + 严格 UTF-8 文本」：`AsLineTextSession()` = `LineFramer` + `Utf8StringCodec`。
`Utf8StringCodec` 拒绝空帧，所以协议里不包含空行。

## 两种发送模式

`Program.cs` 里的 `usePool` 变量切换：

| `usePool` | 使用的类型 | 演示内容 |
|---|---|---|
| `false`（默认） | `ChannelClient` | 基础收发 + 重连与指数退避 |
| `true` | `ChannelClientPool` | 租借 / 归还、连接复用 |

连接池模式下客户端**不配置** `Reconnect`：池自己做健康检查与断线替换，客户端自带重连会与之竞争。

## 观察重连

把两端拆成两个进程，才能在客户端存活时单独重启服务端：

```powershell
# 终端 1：只起服务端
dotnet run --project samples/NetServer.ConsoleApp/NetServer.ConsoleApp.csproj -- server

# 终端 2：只起客户端
dotnet run --project samples/NetServer.ConsoleApp/NetServer.ConsoleApp.csproj -- client
```

然后在终端 1 按 Ctrl+C 停掉服务端，客户端日志会出现：

```text
客户端状态: Connected -> Reconnecting（远程主机强制关闭了一个现有的连接）
客户端状态: Reconnecting -> Connected
```

退避间隔按 1 秒 → 2 秒 → 4 秒递增（带抖动，上限 30 秒）。再启动服务端即可看到收发恢复。

`MaxAttempts = 0` 表示无限重试，此时 `ConnectAsync` 可能一直等待，调用方必须传可取消的 token；
示例用的是有限值 5，避免服务端长期不可用时示例永不退出。

## 多字节 UTF-8 验证

保持示例运行，另开一个 PowerShell 终端，用框架客户端把一个多字节字符拆成两次写入：

```powershell
dotnet run --project samples/NetServer.ConsoleApp/NetServer.ConsoleApp.csproj -- client
```

或者用任意 TCP 客户端手动验证（服务端接受任意 TCP 客户端）：

```powershell
$client = [System.Net.Sockets.TcpClient]::new()
$client.Connect('127.0.0.1', 8000)
$stream = $client.GetStream()
$bytes = [System.Text.Encoding]::UTF8.GetBytes("你好`n")
$stream.Write($bytes, 0, 1)
Start-Sleep -Milliseconds 100
$stream.Write($bytes, 1, $bytes.Length - 1)
$stream.Flush()
Start-Sleep -Milliseconds 500
$client.Close()
```

预期在示例终端看到：

```text
>>>>> 你好 <<<<<
```

同时仍会看到每秒的 `hello-N`；不应出现替换字符 `�`。

## 结构

| 文件 | 作用 |
|---|---|
| `Program.cs` | 解析运行角色（`server`/`client`/`both`）与发送模式，注册服务端与发送端 |
| `SampleChannelHandler.cs` | 服务端处理器：用 `AsLineTextSession()` 读取并回包 |
| `ChannelServerRunner.cs` | 把 `ChannelServer` 启停映射到宿主 |
| `ChannelSender.cs` | 单个连接上的收发辅助：建会话、发消息、读响应 |
| `SenderBootstrapper.cs` | 单连接客户端：`ChannelClient` + 重连退避 + 批量写 |
| `PoolSenderBootstrapper.cs` | 连接池客户端：`RentAsync`/`ReturnAsync` 租借归还 |

## 关键约束

这些是框架的硬性约束，示例里必须遵守：

- **一个连接只能创建一个会话，且会话释放后槽位依然被占用**（`ChannelContext` 的会话槽位在连接
  生命周期内只允许被占用一次）。所以同一个连接上不能反复 `AsLineTextSession()`；要换连接才能换会话。
- **重连会产生新的 `ChannelContext` 与新的 `ConnectionId`**，旧会话绑定在旧连接上，不能跨重连复用。
  示例用 `ReferenceEquals` 比较连接引用，连接变了就重建 `ChannelSender`。
- **断线期间不缓冲、不重放消息**。
- **归还即失去所有权**：`ChannelClientPool.ReturnAsync` 之后不得再访问该客户端。
- **批量写不是原子的**：接收方可能只看到前缀，也不做请求-响应配对。示例按到达顺序读取响应。
- **session 只在终端 handler 创建**，不在 pipeline 中间件中创建。
- **客户端半关闭不会触发 `ConnectionClosed`**，读端靠 `EndOfStreamException` 判断对端结束。
- 端口 8000 被占用时，`host.Run()` 在启动阶段失败并输出异常。
