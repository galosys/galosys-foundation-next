using Galosys.Foundation.Channel;

namespace Galosys.Foundation.Channel.Tests;

public sealed class DependencyBoundaryTests
{
    [Fact]
    public void CoreAssembly_DoesNotReferenceAspNetCore()
    {
        var references = typeof(ChannelServer).Assembly.GetReferencedAssemblies();

        Assert.DoesNotContain(
            references,
            reference => reference.Name is not null
                && reference.Name.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal));
    }
}
