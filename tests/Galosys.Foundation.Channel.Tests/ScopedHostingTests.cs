using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Galosys.Foundation.Channel;
using Microsoft.Extensions.Options;

namespace Galosys.Foundation.Channel.Tests;

internal sealed class ScopedProbe : IDisposable
{
    public static readonly ConcurrentBag<ScopedProbe> Instances = new();

    private static int _disposedCount;

    public ScopedProbe() => Instances.Add(this);

    public static int DisposedCount => Volatile.Read(ref _disposedCount);

    public static void Reset()
    {
        Instances.Clear();
        Interlocked.Exchange(ref _disposedCount, 0);
    }

    public void Dispose() => Interlocked.Increment(ref _disposedCount);
}

public sealed class ScopedHostingTests
{
    [Fact]
    public async Task EachConnection_GetsIndependentScopedInstance_AndItIsDisposed()
    {
        ScopedProbe.Reset();

        var services = new ServiceCollection();
        services.AddScoped<ScopedProbe>();

        await using var provider = services.BuildServiceProvider();
        await using var server = ChannelServerBuilder
            .Create(o =>
            {
                o.EndPoint = new IPEndPoint(IPAddress.Loopback, 0);
                o.ShutdownTimeout = TimeSpan.FromSeconds(1);
            })
            .Run(context =>
            {
                _ = context.Services.GetRequiredService<ScopedProbe>();
                return Task.CompletedTask;
            })
            .WithServiceScopeFactory(provider.GetRequiredService<IServiceScopeFactory>())
            .Build();

        await server.StartAsync(CancellationToken.None);

        var endPoint = (IPEndPoint)server.LocalEndPoint!;

        for (var i = 0; i < 2; i++)
        {
            using var client = await TestClient.ConnectAsync(endPoint);
            Assert.Equal(0, await TestClient.ReadOneByteAsync(client.GetStream()));
        }

        await WaitForAsync(() => ScopedProbe.DisposedCount == 2);

        Assert.Equal(2, ScopedProbe.Instances.Count);
        Assert.Equal(2, ScopedProbe.Instances.Distinct().Count());
        Assert.Equal(2, ScopedProbe.DisposedCount);

        await server.StopAsync(CancellationToken.None);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TestWaits.DefaultTimeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("条件在超时时间内未满足。");

            await Task.Delay(10);
        }
    }
}
