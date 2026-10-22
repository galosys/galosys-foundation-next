namespace Microsoft.Extensions.Hosting
{
    /// <summary>
    /// 批次结果封装
    /// </summary>
    public class BatchResult<TKey, TMessage>
    {
        /// <summary>
        /// 
        /// </summary>
        public TKey GroupKey { get; }

        /// <summary>
        /// 
        /// </summary>
        public IReadOnlyList<TMessage> Messages { get; }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="groupKey"></param>
        /// <param name="messages"></param>
        public BatchResult(TKey groupKey, IReadOnlyList<TMessage> messages)
        {
            GroupKey = groupKey;
            Messages = messages;
        }
    }
}
