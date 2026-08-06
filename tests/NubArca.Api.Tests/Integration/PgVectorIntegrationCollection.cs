namespace NubArca.Api.Tests.Integration;

// Dedicated collection for pgvector-backed photo-similarity integration tests.
// Separate from PostgresIntegrationCollection because it needs the pgvector image
// (PgVectorContainerFixture); a single shared, freshly-migrated container is
// reused across the collection's test classes (run serially by xUnit).
[CollectionDefinition(Name)]
public sealed class PgVectorIntegrationCollection : ICollectionFixture<PgVectorContainerFixture>
{
    public const string Name = "PgVectorIntegration";
}
