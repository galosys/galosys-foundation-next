namespace Galosys.Foundation.Nats.Net.IntegrationTests.Xunit
{
    [CollectionDefinition(nameof(InfrasCollection))]
    public class InfrasCollection
        : ICollectionFixture<PostgresFixture>
    { }
}
