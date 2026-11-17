namespace Xunit
{
    [CollectionDefinition(nameof(InfrasCollection))]
    public class InfrasCollection
        : ICollectionFixture<PostgresFixture>
    { }
}
