namespace Galosys.Foundation.Channel
{
    /// <summary>消息会话的批量写入扩展。</summary>
    public static class ChannelSessionBatchExtensions
    {
        /// <summary>按顺序写入一批消息；空批次不写、不 flush。</summary>
        /// <remarks>
        /// 内置会话在同一个写锁内编码并写出所有帧，只调用一次 <c>FlushAsync</c>；
        /// 自定义会话没有批量能力时退化为逐条 <see cref="IChannelSession{T}.WriteAsync"/>。
        /// </remarks>
        /// <exception cref="ArgumentNullException">session 或 messages 为 null。</exception>
        public static ValueTask WriteBatchAsync<T>(
            this IChannelSession<T> session,
            IReadOnlyList<T> messages,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(session);
            ArgumentNullException.ThrowIfNull(messages);

            if (messages.Count == 0)
                return ValueTask.CompletedTask;

            if (session is IChannelSessionBatchWriter<T> writer)
                return writer.WriteBatchAsync(messages, ct);

            return WriteSequentiallyAsync(session, messages, ct);
        }

        private static async ValueTask WriteSequentiallyAsync<T>(
            IChannelSession<T> session,
            IReadOnlyList<T> messages,
            CancellationToken ct)
        {
            for (var i = 0; i < messages.Count; i++)
                await session.WriteAsync(messages[i], ct).ConfigureAwait(false);
        }
    }
}
