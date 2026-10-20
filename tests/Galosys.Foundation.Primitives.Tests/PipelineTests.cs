using Microsoft.Extensions.Hosting.Net;

namespace Galosys.Foundation.Primitives.Tests;

public sealed class PipelineTests
{
    [Fact]
    public async Task Build_ExecutesMiddlewareInRegistrationOrder()
    {
        var log = new List<string>();
        var pipeline = new ChannelPipeline();
        pipeline.Use(async (context, next) =>
        {
            log.Add("A-in");
            await next(context);
            log.Add("A-out");
        });
        pipeline.Use(async (context, next) =>
        {
            log.Add("B-in");
            await next(context);
            log.Add("B-out");
        });
        pipeline.Run(_ =>
        {
            log.Add("handler");
            return Task.CompletedTask;
        });

        await pipeline.Build()(null!);

        Assert.Equal(new[] { "A-in", "B-in", "handler", "B-out", "A-out" }, log);
    }

    [Fact]
    public void Run_CannotBeCalledTwice()
    {
        var pipeline = new ChannelPipeline();
        pipeline.Run(_ => Task.CompletedTask);

        Assert.Throws<InvalidOperationException>(() => pipeline.Run(_ => Task.CompletedTask));
    }

    [Fact]
    public void Build_Throws_WhenRunWasNotCalled()
    {
        var pipeline = new ChannelPipeline();
        pipeline.Use((context, next) => next(context));

        Assert.Throws<InvalidOperationException>(() => pipeline.Build());
    }
}
