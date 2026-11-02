using Microsoft.Extensions.Options;

namespace Galosys.Foundation.Primitives.Tests;

public sealed class ChannelReconnectPolicyTests
{
    [Fact]
    public void Defaults_MatchDesign()
    {
        var policy = new ChannelReconnectPolicy();

        Assert.Equal(0, policy.MaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(1), policy.InitialDelay);
        Assert.Equal(TimeSpan.FromSeconds(30), policy.MaxDelay);
        Assert.Equal(2.0, policy.BackoffMultiplier);
        Assert.Equal(0.2, policy.JitterFactor);
        Assert.Null(policy.ShouldRetry);
    }

    [Fact]
    public void Validate_Throws_WhenMaxDelaySmallerThanInitialDelay()
    {
        var policy = new ChannelReconnectPolicy
        {
            InitialDelay = TimeSpan.FromSeconds(5),
            MaxDelay = TimeSpan.FromSeconds(1),
        };

        Assert.Throws<ArgumentException>(() => policy.Validate());
    }

    [Fact]
    public void Validate_Throws_WhenJitterOutOfRange()
    {
        var policy = new ChannelReconnectPolicy { JitterFactor = 1.5 };

        Assert.Throws<ArgumentException>(() => policy.Validate());
    }

    [Fact]
    public void Clone_PreservesShouldRetryAndJitterSource()
    {
        Func<Exception, bool> shouldRetry = _ => false;
        var policy = new ChannelReconnectPolicy
        {
            ShouldRetry = shouldRetry,
            JitterSource = () => 0.5,
        };

        var clone = policy.Clone();

        Assert.Same(shouldRetry, clone.ShouldRetry);
        Assert.Equal(0.5, clone.JitterSource());
        Assert.NotSame(policy, clone);
    }

    [Fact]
    public void NextDelay_UsesBackoffAndClampsAtMaxDelay()
    {
        var policy = new ChannelReconnectPolicy
        {
            InitialDelay = TimeSpan.FromMilliseconds(100),
            MaxDelay = TimeSpan.FromMilliseconds(250),
            BackoffMultiplier = 2,
        };

        var second = ChannelReconnectPolicy.NextDelay(TimeSpan.FromMilliseconds(100), policy);
        var third = ChannelReconnectPolicy.NextDelay(second, policy);
        var fourth = ChannelReconnectPolicy.NextDelay(third, policy);

        Assert.Equal(TimeSpan.FromMilliseconds(200), second);
        Assert.Equal(TimeSpan.FromMilliseconds(250), third);
        Assert.Equal(TimeSpan.FromMilliseconds(250), fourth);
    }

    [Fact]
    public void ApplyJitter_StaysWithinFactorRange()
    {
        var delay = TimeSpan.FromMilliseconds(100);

        Assert.Equal(
            TimeSpan.FromMilliseconds(80),
            ChannelReconnectPolicy.ApplyJitter(delay, 0, 0.2));
        Assert.Equal(
            TimeSpan.FromMilliseconds(100),
            ChannelReconnectPolicy.ApplyJitter(delay, 0.5, 0.2));
        Assert.Equal(
            TimeSpan.FromMilliseconds(120),
            ChannelReconnectPolicy.ApplyJitter(delay, 1, 0.2));
    }
}
