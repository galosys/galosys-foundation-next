using System.Runtime.CompilerServices;

namespace Galosys.Foundation.Nats.Net.IntegrationTests
{
    internal static class TestEnvironment
    {
        [ModuleInitializer]
        internal static void Initialize()
        {
            /**
             * wsl Podman 
             */
            Environment.SetEnvironmentVariable("TESTCONTAINERS_RYUK_DISABLED", "true");
            Environment.SetEnvironmentVariable("TESTCONTAINERS_HOST_OVERRIDE", "localhost");
        }
    }
}
