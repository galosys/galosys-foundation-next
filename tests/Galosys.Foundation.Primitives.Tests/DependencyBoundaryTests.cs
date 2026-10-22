using Microsoft.Extensions.Hosting.Net;

namespace Galosys.Foundation.Primitives.Tests;

public sealed class DependencyBoundaryTests
{
    [Fact]
    public void CoreAssembly_DoesNotReferenceAspNetCore()
    {
        var references = typeof(ChannelServerBase).Assembly.GetReferencedAssemblies();

        Assert.DoesNotContain(
            references,
            reference => reference.Name is not null
                && reference.Name.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal));
    }
}
