namespace NATS.Net
{
    public class NatsConsumerAttribute : Attribute
    {
        public NatsConsumerAttribute(
            string subject,
            string? queueGroup = null)
        {
            Subject = subject;
            QueueGroup = queueGroup;
        }

        public string Subject { get; }
        public string? QueueGroup { get; }
    }
}