namespace Galosys.Foundation.Channel
{
    /// <summary>内置会话的批量写入能力；仅框架内部使用。</summary>
    internal interface IChannelSessionBatchWriter<T>
    {
        /// <summary>按顺序写入一批消息；实现方负责一次 flush。</summary>
        ValueTask WriteBatchAsync(IReadOnlyList<T> messages, CancellationToken ct);
    }
}
