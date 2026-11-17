using Xunit;

namespace Relay.IntegrationTests.Fixtures;

[CollectionDefinition("DatabaseCollection", DisableParallelization = true)]
public class DatabaseCollection : ICollectionFixture<RelayApiFactory>
{
}
