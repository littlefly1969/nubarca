namespace NubArca.Api.Tests.Integration;

// Shared collection for all PostgreSQL-backed integration tests. xUnit runs every
// test class in the same collection serially, so the single PostgresContainerFixture
// instance is safe to share across classes — and each test class only pays the
// container startup cost once for the whole suite, not once per class.
[CollectionDefinition(Name)]
public sealed class PostgresIntegrationCollection : ICollectionFixture<PostgresContainerFixture>
{
    public const string Name = "PostgresIntegration";
}
