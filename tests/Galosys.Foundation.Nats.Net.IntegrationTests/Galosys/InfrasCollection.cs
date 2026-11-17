namespace Galosys.Foundation.Nats.Net.IntegrationTests
{
    [CollectionDefinition(nameof(InfrasCollection))]
    public class InfrasCollection
        : ICollectionFixture<PostgresFixture>
    { }
}
