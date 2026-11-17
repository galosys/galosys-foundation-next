namespace Galosys.Foundation.Nats.Net.IntegrationTests
{
    [Collection(nameof(InfrasCollection))]
    public class PostgresTests
    {
        private readonly PostgresFixture _fixture;

        public PostgresTests(PostgresFixture fixture)
        {
            _fixture = fixture;
        }

        [Fact]
        public void Get_ConnectionStringAsync()
        {
            var connectionString = _fixture.ConnectionString;

            Assert.NotEmpty(connectionString);
        }
    }
}
