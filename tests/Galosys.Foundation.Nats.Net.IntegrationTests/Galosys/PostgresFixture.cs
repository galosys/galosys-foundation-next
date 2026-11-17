using Testcontainers.PostgreSql;

namespace Galosys.Foundation.Nats.Net.IntegrationTests
{
    public class PostgresFixture
        : IAsyncLifetime
    {
        private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("docker.1ms.run/postgres:16-alpine")
            .Build();

        public string ConnectionString => _pg.GetConnectionString();

        public async Task DisposeAsync()
        {
            await _pg.DisposeAsync();
        }

        public async Task InitializeAsync()
        {
            await _pg.StartAsync();
        }
    }

    [CollectionDefinition(nameof(PostgresFixture))]
    public class PostgresCollection : ICollectionFixture<PostgresFixture>
    { }
}
