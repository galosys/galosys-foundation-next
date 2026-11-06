using System.Reflection;

namespace Galosys.Foundation.Nats.Net
{
    /// <summary>
    /// 一个已建立的 NATS 订阅描述。供启动器持有并驱动其消费循环。
    /// </summary>
    internal sealed record NatsSubscription(
        string Subject,
        string? QueueGroup,
        MethodInfo HandlerMethod,
        Type MessageType,
        int Concurrency,
        int MaxRetryCount,
        int RetryBaseDelayMs);
}
