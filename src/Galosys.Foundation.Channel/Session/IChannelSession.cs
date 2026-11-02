namespace Microsoft.Extensions.Hosting.Net
{
    /// <summary>每连接一个的拉模型消息会话；不拥有连接。</summary>
    public interface IChannelSession<T> : IAsyncDisposable
    {
        /// <summary>读取一条完整消息；干净 EOF 抛 <see cref="EndOfStreamException"/>。</summary>
        ValueTask<T> ReadAsync(CancellationToken ct);

        /// <summary>写入一条消息；并发写由 session 串行化。</summary>
        ValueTask WriteAsync(T message, CancellationToken ct);
    }
}
