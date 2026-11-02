using System;

namespace Microsoft.Extensions.Options
{
    /// <summary>客户端重连与退避策略。</summary>
    public sealed class ChannelReconnectPolicy
    {
        /// <summary>首次失败后的最大重试次数；0 表示无限。</summary>
        public int MaxAttempts { get; set; }

        /// <summary>第一次重试前的等待，默认 1 秒。</summary>
        public TimeSpan InitialDelay { get; set; } = TimeSpan.FromSeconds(1);

        /// <summary>退避上限，默认 30 秒。</summary>
        public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>每次失败后的退避倍数，默认 2。</summary>
        public double BackoffMultiplier { get; set; } = 2.0;

        /// <summary>抖动比例，默认 0.2；范围 [0, 1]。</summary>
        public double JitterFactor { get; set; } = 0.2;

        /// <summary>自定义重试判定；null 时使用 <see cref="ChannelConnectionFailure.IsRetryable"/>。</summary>
        public Func<Exception, bool>? ShouldRetry { get; set; }

        internal Func<double> JitterSource { get; set; } = () => Random.Shared.NextDouble();

        internal void Validate()
        {
            if (MaxAttempts < 0)
                throw new ArgumentException("MaxAttempts 不能为负数。", nameof(MaxAttempts));

            if (InitialDelay <= TimeSpan.Zero || InitialDelay > TimeSpan.FromDays(1))
                throw new ArgumentException("InitialDelay 必须大于 0 且不超过 1 天。", nameof(InitialDelay));

            if (MaxDelay < InitialDelay || MaxDelay > TimeSpan.FromDays(1))
                throw new ArgumentException("MaxDelay 必须不小于 InitialDelay 且不超过 1 天。", nameof(MaxDelay));

            if (BackoffMultiplier < 1 || double.IsNaN(BackoffMultiplier) || double.IsInfinity(BackoffMultiplier))
                throw new ArgumentException("BackoffMultiplier 必须是大于等于 1 的有限数。", nameof(BackoffMultiplier));

            if (JitterFactor is < 0 or > 1 || double.IsNaN(JitterFactor))
                throw new ArgumentException("JitterFactor 必须在 [0, 1] 内。", nameof(JitterFactor));
        }

        internal ChannelReconnectPolicy Clone()
        {
            var clone = (ChannelReconnectPolicy)MemberwiseClone();
            clone.JitterSource = JitterSource;
            return clone;
        }

        internal static TimeSpan NextDelay(TimeSpan current, ChannelReconnectPolicy policy)
        {
            var next = TimeSpan.FromTicks((long)(current.Ticks * policy.BackoffMultiplier));
            return next > policy.MaxDelay ? policy.MaxDelay : next;
        }

        internal static TimeSpan ApplyJitter(TimeSpan delay, double random, double jitterFactor)
        {
            var factor = 1 + ((random * 2) - 1) * jitterFactor;
            return TimeSpan.FromTicks(Math.Max(1, (long)(delay.Ticks * factor)));
        }
    }
}
