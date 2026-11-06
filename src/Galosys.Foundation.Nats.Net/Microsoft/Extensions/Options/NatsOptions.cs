using System;
using System.Collections.Generic;

namespace Microsoft.Extensions.Options
{
    /// <summary>
    /// NATS 消费者选项。仅承载本框架自身的关注点；连接参数（地址、认证、TLS）由上游
    /// <c>NATS.Client.Core.NatsOpts</c> 经 <c>AddNatsClient</c> 管理，不在此重复。
    /// </summary>
    public sealed class NatsOptions
    {
        /// <summary>
        /// 每个 subject 的并发派发数，默认 1。
        /// </summary>
        public int Concurrency { get; set; } = 1;

        /// <summary>
        /// 处理器抛出异常后的最大重试次数，默认 0（不重试）。
        /// </summary>
        public int MaxRetryCount { get; set; }

        /// <summary>
        /// 重试基础延迟（毫秒），默认 100；第 n 次重试的延迟为该值乘以 2 的 n 次方。
        /// </summary>
        public int RetryBaseDelayMs { get; set; } = 100;

        /// <summary>
        /// 按 subject 覆盖的处理器设置。键为 subject。
        /// </summary>
        public IDictionary<string, NatsHandlerProperty> Handlers { get; set; }
            = new Dictionary<string, NatsHandlerProperty>(StringComparer.Ordinal);
    }

    /// <summary>
    /// 单个 subject 的消费者设置，用于覆盖 <see cref="NatsOptions"/> 的全局值。
    /// </summary>
    public sealed class NatsHandlerProperty
    {
        /// <summary>
        /// 覆盖该 subject 的并发派发数。
        /// </summary>
        public int? Concurrency { get; set; }

        /// <summary>
        /// 覆盖该 subject 的最大重试次数。
        /// </summary>
        public int? MaxRetryCount { get; set; }

        /// <summary>
        /// 覆盖该 subject 的重试基础延迟（毫秒）。
        /// </summary>
        public int? RetryBaseDelayMs { get; set; }
    }
}
