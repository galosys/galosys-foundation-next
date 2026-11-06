using System;

namespace NATS.Net
{
    /// <summary>
    /// 标记一个消息处理方法为 NATS 消费者，声明其订阅的 subject 与可选的队列组。
    /// </summary>
    /// <remarks>
    /// 可重复标注在同一方法上以声明多个订阅；每个标记各自建立独立订阅。
    /// </remarks>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
    public sealed class NatsConsumerAttribute : Attribute
    {
        /// <summary>
        /// 初始化消费者标记。
        /// </summary>
        /// <param name="subject">要订阅的 NATS subject。</param>
        /// <param name="queueGroup">可选的队列组；为 null 时表示不加入队列组（广播语义）。</param>
        public NatsConsumerAttribute(string subject, string? queueGroup = null)
        {
            Subject = subject;
            QueueGroup = queueGroup;
        }

        /// <summary>
        /// 要订阅的 NATS subject。
        /// </summary>
        public string Subject { get; }

        /// <summary>
        /// 队列组名称；为 null 时表示不加入队列组。
        /// </summary>
        public string? QueueGroup { get; }
    }
}
