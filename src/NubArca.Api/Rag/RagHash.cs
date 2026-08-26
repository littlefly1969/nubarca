using System.Security.Cryptography;
using System.Text;

namespace NubArca.Api.Rag;

/// SHA-256 hex, for source and chunk identity.
///
/// The same algorithm the blob store uses, for a different purpose: here it is
/// an IDEMPOTENCE key, not a deduplication key. Unchanged content hashes the
/// same, so reindexing keeps the chunks and therefore keeps the embeddings —
/// which is the difference between a second index run costing nothing and
/// costing an hour of inference.
public static class RagHash
{
    public static string Sha256Hex(string text)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text ?? string.Empty)));

    public static string Sha256Hex(ReadOnlySpan<byte> bytes)
        => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
