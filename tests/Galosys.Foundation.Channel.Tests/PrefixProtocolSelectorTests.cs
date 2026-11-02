using System.Buffers;
using System.Text;
using Microsoft.Extensions.Hosting.Net;
using Microsoft.Extensions.Options;

namespace Galosys.Foundation.Primitives.Tests;

public sealed class PrefixProtocolSelectorTests
{
    [Fact]
    public async Task SelectAsync_FirstMatchWins_ByRegistrationOrder()
    {
        var first = new RecordingProtocol();
        var second = new RecordingProtocol();
        var selector = new PrefixProtocolSelector(new[]
        {
            new PrefixProtocolRule("A"u8.ToArray(), first),
            new PrefixProtocolRule("AB"u8.ToArray(), second),
        });

        var connection = new FakeChannelConnection();
        await WriteAsync(connection, "AB");
        await using var context = new ChannelContext(
            "prefix-first-match",
            connection,
            EmptyServiceProvider.Instance);

        var result = await selector.SelectAsync(context, context.ConnectionClosed);

        Assert.Same(first, result);
    }

    [Fact]
    public async Task SelectAsync_LongerRuleFirst_SelectsLongerProtocol()
    {
        var longer = new RecordingProtocol();
        var shorter = new RecordingProtocol();
        var selector = new PrefixProtocolSelector(new[]
        {
            new PrefixProtocolRule("AB"u8.ToArray(), longer),
            new PrefixProtocolRule("A"u8.ToArray(), shorter),
        });

        var connection = new FakeChannelConnection();
        await WriteAsync(connection, "AB");
        await using var context = new ChannelContext(
            "prefix-overlap",
            connection,
            EmptyServiceProvider.Instance);

        var result = await selector.SelectAsync(context, context.ConnectionClosed);

        Assert.Same(longer, result);
    }

    [Fact]
    public async Task SelectAsync_PartialPrefix_WaitsForMoreData()
    {
        var protocol = new RecordingProtocol();
        var selector = new PrefixProtocolSelector(new[]
        {
            new PrefixProtocolRule("ABCD"u8.ToArray(), protocol),
        });

        var connection = new FakeChannelConnection();
        await WriteAsync(connection, "AB");
        await using var context = new ChannelContext(
            "prefix-partial",
            connection,
            EmptyServiceProvider.Instance);

        var selection = selector.SelectAsync(context, context.ConnectionClosed).AsTask();
        await Task.Delay(100);
        Assert.False(selection.IsCompleted);

        await WriteAsync(connection, "CD");

        var result = await selection.WaitAsync(TestWaits.DefaultTimeout);
        Assert.Same(protocol, result);
    }

    [Fact]
    public async Task SelectAsync_UnknownPrefix_ReturnsNull()
    {
        var protocol = new RecordingProtocol();
        var selector = new PrefixProtocolSelector(new[]
        {
            new PrefixProtocolRule("AB"u8.ToArray(), protocol),
        });

        var connection = new FakeChannelConnection();
        await WriteAsync(connection, "ZZ");
        await using var context = new ChannelContext(
            "prefix-unknown",
            connection,
            EmptyServiceProvider.Instance);

        var result = await selector.SelectAsync(context, context.ConnectionClosed);

        Assert.Null(result);
    }

    [Fact]
    public async Task SelectAsync_PartialPrefixAtEof_ReturnsNull()
    {
        var protocol = new RecordingProtocol();
        var selector = new PrefixProtocolSelector(new[]
        {
            new PrefixProtocolRule("ABCD"u8.ToArray(), protocol),
        });

        var connection = new FakeChannelConnection();
        await WriteAsync(connection, "AB");
        connection.Writer.Complete();
        await using var context = new ChannelContext(
            "prefix-eof",
            connection,
            EmptyServiceProvider.Instance);

        var result = await selector.SelectAsync(context, context.ConnectionClosed);

        Assert.Null(result);
    }

    [Fact]
    public async Task SelectAsync_Cancellation_DoesNotConsumeInput()
    {
        var protocol = new RecordingProtocol();
        var selector = new PrefixProtocolSelector(new[]
        {
            new PrefixProtocolRule("AB"u8.ToArray(), protocol),
        });

        var connection = new FakeChannelConnection();
        await using var context = new ChannelContext(
            "prefix-cancel",
            connection,
            EmptyServiceProvider.Instance);

        using var cts = new CancellationTokenSource();
        var selection = selector.SelectAsync(context, cts.Token).AsTask();
        await Task.Delay(50);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => selection);

        await WriteAsync(connection, "AB");
        var read = await context.Reader.ReadAsync();
        Assert.Equal("AB", Encoding.UTF8.GetString(read.Buffer.ToArray()));
        context.Reader.AdvanceTo(read.Buffer.End);
    }

    [Fact]
    public async Task SelectAsync_AfterMatch_DoesNotConsumePrefix()
    {
        var protocol = new RecordingProtocol();
        var selector = new PrefixProtocolSelector(new[]
        {
            new PrefixProtocolRule("AB"u8.ToArray(), protocol),
        });

        var connection = new FakeChannelConnection();
        await WriteAsync(connection, "ABpayload");
        await using var context = new ChannelContext(
            "prefix-peek",
            connection,
            EmptyServiceProvider.Instance);

        var result = await selector.SelectAsync(context, context.ConnectionClosed);
        Assert.Same(protocol, result);

        connection.Writer.Complete();
        var read = await context.Reader.ReadAsync();
        Assert.Equal("ABpayload", Encoding.UTF8.GetString(read.Buffer.ToArray()));
        context.Reader.AdvanceTo(read.Buffer.End);
    }

    [Fact]
    public async Task SelectAsync_MaxPrefixSizeReached_ReturnsNull()
    {
        var protocol = new RecordingProtocol();
        var selector = new PrefixProtocolSelector(
            new[] { new PrefixProtocolRule("ABCD"u8.ToArray(), protocol) },
            new PrefixProtocolSelectorOptions { MaxPrefixSize = 4 });

        var connection = new FakeChannelConnection();
        await WriteAsync(connection, "ABCE");
        await using var context = new ChannelContext(
            "prefix-max",
            connection,
            EmptyServiceProvider.Instance);

        var result = await selector.SelectAsync(context, context.ConnectionClosed);

        Assert.Null(result);
    }

    [Fact]
    public void Constructor_RejectsInvalidRules()
    {
        var protocol = new RecordingProtocol();

        Assert.Throws<ArgumentException>(
            () => new PrefixProtocolRule(ReadOnlyMemory<byte>.Empty, protocol));

        Assert.Throws<ArgumentException>(() => new PrefixProtocolSelector(new[]
        {
            new PrefixProtocolRule("ABCD"u8.ToArray(), protocol),
        }, new PrefixProtocolSelectorOptions { MaxPrefixSize = 3 }));

        Assert.Throws<ArgumentException>(() => new PrefixProtocolSelector(new[]
        {
            new PrefixProtocolRule("AB"u8.ToArray(), protocol),
            new PrefixProtocolRule("AB"u8.ToArray(), new RecordingProtocol()),
        }));

        Assert.Throws<ArgumentOutOfRangeException>(() => new PrefixProtocolSelector(
            Array.Empty<PrefixProtocolRule>(),
            new PrefixProtocolSelectorOptions { MaxPrefixSize = 0 }));
    }

    private static async Task WriteAsync(FakeChannelConnection connection, string text)
    {
        await connection.Writer.WriteAsync(Encoding.UTF8.GetBytes(text));
        await connection.Writer.FlushAsync();
    }
}
