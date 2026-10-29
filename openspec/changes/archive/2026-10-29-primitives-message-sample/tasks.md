# P2-4 示例迁移与文档 Implementation Plan

> **REQUIRED SUB-SKILL:** 使用 `executing-plans` 或 `opsx-apply-x` 逐任务执行本计划。
> 本仓库虽有 git，但按约定不自动提交；步骤中的“验证”统一用构建 + 测试，不执行 git commit。
> 前置条件：`primitives-server-composition-1`、`primitives-server-composition-2` 与 `primitives-message-session` 已合入；示例已使用 `SampleChannelHandler` + `ChannelServerRunner`。

**Goal:** 把 `NetServer.ConsoleApp` 迁到 `context.AsLineTextSession()`，保留 8000 端口、每秒 `hello`、Ctrl+C 行为，并用测试和手工步骤证明多字节 UTF-8 不跨读取截断。

**Architecture:** `SampleChannelHandler.HandleAsync` 在终端处理器内创建行文本会话；`LineFramer` 负责 `\n` 边界，`Utf8StringCodec` 负责完整帧的 UTF-8 解码。`SenderBootstrapper` 发送 `hello\n`；宿主装配和 `ChannelServerRunner` 不变。

**Tech Stack:** .NET 10、`Microsoft.Extensions.Hosting`、`System.Net.Sockets`、`System.Text`、xunit 2.9.3。

---

## 1. 回归测试

- [x] 4.1 增加多字节 UTF-8 跨写入回归测试

**Files:**
- Modify: `tests/Galosys.Foundation.Primitives.Tests/SessionTests.cs`

在测试类中追加：

```csharp
[Fact]
public async Task LineTextSession_MultibyteUtf8SplitAcrossWrites_DecodesWholeCharacter()
{
    var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

    await using var server = await TestServer.StartAsync(async context =>
    {
        await using var session = context.AsLineTextSession();
        var line = await session.ReadAsync(context.ConnectionClosed);
        received.TrySetResult(line);
    });

    using var client = await TestClient.ConnectAsync(server.EndPoint);
    var stream = client.GetStream();
    var bytes = Encoding.UTF8.GetBytes("你好\n");

    await stream.WriteAsync(bytes.AsMemory(0, 1));
    await Task.Delay(50);
    await stream.WriteAsync(bytes.AsMemory(1));

    Assert.Equal("你好", await received.Task.WaitAsync(TestWaits.DefaultTimeout));
}
```

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj --filter FullyQualifiedName~LineTextSession_MultibyteUtf8SplitAcrossWrites_DecodesWholeCharacter`
Expected: 测试通过，说明相同的 `LineFramer` + `Utf8StringCodec` 组合不会截断多字节字符。

## 2. 迁移示例

- [x] 4.2 把 `SampleChannelHandler` 改为行文本会话

**Files:**
- Modify: `samples/NetServer.ConsoleApp/SampleChannelHandler.cs`

用以下内容替换 `HandleAsync`：

```csharp
public async Task HandleAsync(ChannelContext context)
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
        catch (OperationCanceledException) when (context.ConnectionClosed.IsCancellationRequested)
        {
            break;
        }

        _logger.LogInformation(">>>>> {Line} <<<<<", line);
    }
}
```

保留构造函数、`ILogger<SampleChannelHandler>` 和命名空间不变；删除原来的 `context.Reader.ReadAsync` 循环与 `Encoding.UTF8.GetString`。

- [x] 4.3 让 `SenderBootstrapper` 发送带换行的 hello

**Files:**
- Modify: `samples/NetServer.ConsoleApp/SenderBootstrapper.cs`

把：

```csharp
var bytes = Encoding.UTF8.GetBytes("hello");
```

改为：

```csharp
var bytes = Encoding.UTF8.GetBytes("hello\n");
```

其余连接、每秒发送和取消逻辑不变。

- [x] 4.4 构建示例

Run: `dotnet build samples/NetServer.ConsoleApp/NetServer.ConsoleApp.csproj`
Expected: 0 errors；允许既有 `CS9113` 警告。

## 3. 运行验证

- [x] 4.5 启动示例并验证每秒 hello

Run: `dotnet run --project samples/NetServer.ConsoleApp/NetServer.ConsoleApp.csproj`
Expected:
- 监听 8000 端口；
- 每秒输出一条 `>>>>> hello <<<<<`；
- 按 Ctrl+C 后进程退出，无未处理异常。

- [x] 4.6 手工验证多字节 UTF-8 不被截断

保持 4.5 的示例运行，另开一个 PowerShell 终端执行：

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

Expected: 示例终端出现 `>>>>> 你好 <<<<<`，且不出现 `�`。由于 `SenderBootstrapper` 仍在每秒发送 `hello\n`，同一终端也会持续出现 `>>>>> hello <<<<<`；目标行是 `你好`，不是唯一输出。

## 4. 文档

- [x] 4.7 更新 Primitives README 的示例与会话约束

**Files:**
- Modify: `src/Galosys.Foundation.Primitives/README.md`

**Step 1: 在“消息会话”章节补充示例入口**

加入以下片段：

````markdown
示例 `NetServer.ConsoleApp` 只在终端 handler 创建行文本会话：

```csharp
public async Task HandleAsync(ChannelContext context)
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

        _logger.LogInformation(">>>>> {Line} <<<<<", line);
    }
}
```

不要在 `IChannelPipeline.Use` 中间件里创建 session：中间件只包装 `Transport`，
读取业务数据会破坏管线契约。
````

**Step 2: 在约束列表补充**

```markdown
- 一个连接一个 session；session 只在终端 handler 创建。
- 行文本示例的线格式是 `hello\n`；`Utf8StringCodec` 拒绝空行。
- 多字节 UTF-8 只在完整帧上解码，不会跨 `ReadAsync` 截断。
```

- [x] 4.8 更新 sample README

**Files:**
- Modify: `samples/NetServer.ConsoleApp/README.md`

用以下内容替换：

````markdown
# NetServer.ConsoleApp

`Galosys.Foundation.Primitives` 的服务端示例：监听 `0.0.0.0:8000`，通过
`context.AsLineTextSession()` 按行读取并打印 UTF-8 文本，同时每秒向本机 8000 端口发送 `hello\n`。

## 运行

```powershell
dotnet run --project samples/NetServer.ConsoleApp/NetServer.ConsoleApp.csproj
```

预期输出（每秒一条）：

```text
>>>>> hello <<<<<
```

按 Ctrl+C 触发宿主优雅停机。

## 多字节 UTF-8 验证

保持示例运行，另开一个 PowerShell 终端：

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

同时仍会看到每秒的 `>>>>> hello <<<<<`；不应出现替换字符 `�`。

## 结构

| 文件 | 作用 |
|---|---|
| `Program.cs` | 注册 `ChannelServer` 工厂、`ChannelServerRunner` 与 `SenderBootstrapper` |
| `SampleChannelHandler.cs` | 终端处理器：用 `AsLineTextSession()` 读取并打印行 |
| `ChannelServerRunner.cs` | 把 `ChannelServer` 启停映射到宿主 |
| `SenderBootstrapper.cs` | 后台客户端：每秒发送 `hello\n`（P4 才迁移到框架客户端） |

## 说明

- session 只在 `SampleChannelHandler.HandleAsync` 中创建，不在 pipeline 中间件中创建。
- `LineFramer` 用 `\n` 作为帧边界；发送端必须包含换行，裸 `hello` 不会形成一条消息。
- `Utf8StringCodec` 拒绝空帧，因此示例协议不包含空行。
- `ChannelServerRunner` 必须先于 `SenderBootstrapper` 注册，确保 sender 连接前监听已就绪。
- 端口 8000 被占用时，`host.Run()` 在启动阶段失败并输出异常。
````

## 5. 最终验证

- [x] 4.9 全量构建与测试

Run: `dotnet build src/Galosys.Foundation.Primitives/Galosys.Foundation.Primitives.csproj`
Expected: 0 errors。

Run: `dotnet build samples/NetServer.ConsoleApp/NetServer.ConsoleApp.csproj`
Expected: 0 errors。

Run: `dotnet test tests/Galosys.Foundation.Primitives.Tests/Galosys.Foundation.Primitives.Tests.csproj`
Expected: 现有测试 + `FramingTests` + `MessageCodecTests` + `SessionTests` 全部通过。

- [x] 4.10 校验 OpenSpec artifact

Run: `openspec validate "primitives-message-sample" --strict --no-interactive`
Expected: `Change 'primitives-message-sample' is valid`。

---

## 实施记录

- 4.1–4.10 全部完成：新增多字节 UTF-8 跨写入回归测试（147/147 全量通过）；`SampleChannelHandler` 改用 `AsLineTextSession`；`SenderBootstrapper` 发送 `hello\n`；Primitives README 与 sample README 已更新。
- 运行验证：示例监听 8000，每秒输出 `>>>>> hello <<<<<`；分两次写入 `"你好\n"` 后日志出现 `>>>>> 你好 <<<<<`（日志文件字节 `c4 e3 ba c3` 为 GBK 编码的“你好”），无替换字符。
- 构建：Primitives 0 错误；`NetServer.ConsoleApp` 0 错误（CS1591 为项目既有警告）。
