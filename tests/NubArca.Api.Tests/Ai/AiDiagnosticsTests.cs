using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NubArca.Api.Ai.Diagnostics;
using NubArca.Api.Ai.Resolution;
using NubArca.Api.Data;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Tests.Ai;

public sealed class AiDiagnosticsTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    public AiDiagnosticsTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public void Sanitizer_Collapses_Newlines_And_Truncates()
    {
        // A stack-trace-like multi-line string must not survive as multiple lines.
        var stackish = "System.Exception: boom\n   at Foo.Bar()\n   at Baz.Qux()";
        var sanitized = AiDiagnosticSanitizer.Sanitize(stackish);

        Assert.NotNull(sanitized);
        Assert.DoesNotContain('\n', sanitized!);
        Assert.DoesNotContain('\t', sanitized!);
        Assert.True(sanitized!.Length <= AiDiagnosticSanitizer.MaxMessageLength);
    }

    [Fact]
    public void Sanitizer_Returns_Null_For_Empty_Or_Whitespace()
    {
        Assert.Null(AiDiagnosticSanitizer.Sanitize(null));
        Assert.Null(AiDiagnosticSanitizer.Sanitize("   \n\t "));
    }

    [Fact]
    public void Sanitizer_Truncates_Long_Input()
    {
        var sanitized = AiDiagnosticSanitizer.Sanitize(new string('x', 5000));
        Assert.Equal(AiDiagnosticSanitizer.MaxMessageLength, sanitized!.Length);
    }

    [Fact]
    public async Task Provider_Unavailable_Diagnostic_Is_Aggregate_Only_And_Content_Free()
    {
        var writer = new AiDiagnosticsWriter(_db, TimeProvider.System);
        var profileId = Guid.NewGuid();

        await writer.RecordProviderUnavailableAsync(
            AiCapabilities.ImageEmbedding, profileId, AiUnavailableReasons.ProviderNone);

        var row = await _db.AiIndexDiagnostics.SingleAsync();

        Assert.Equal(AiDiagnosticTargetKinds.Provider, row.TargetKind);
        Assert.Equal(AiCapabilities.ImageEmbedding, row.Capability);
        Assert.Equal(profileId, row.ProfileId);
        Assert.Equal(AiUnavailableReasons.ProviderNone, row.ErrorCode);
        Assert.False(row.IsPermanent);

        // No target ids, no free-text message — nothing that could carry a
        // stack trace, path, SHA, storage key, vector, or secret.
        Assert.Null(row.SanitizedMessage);
        Assert.Null(row.BlobObjectId);
        Assert.Null(row.DocumentChunkId);
        Assert.Null(row.FaceDetectionId);
        Assert.Null(row.OwnerUserId);
        Assert.DoesNotContain('\n', row.ErrorCode);
        Assert.DoesNotContain('/', row.ErrorCode);
    }
}
