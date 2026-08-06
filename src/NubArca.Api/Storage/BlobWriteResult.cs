namespace NubArca.Api.Storage;

public sealed record BlobWriteResult(
    string Sha256,
    string StorageKey,
    long SizeBytes,
    bool AlreadyExisted,
    // Slice 82: low-overhead wall-clock split of the streaming write loop so an
    // import can attribute time to source read vs SHA hashing vs destination
    // write. Always measured (negligible cost); normal uploads ignore them.
    long ReadMillis = 0,
    long HashMillis = 0,
    long WriteMillis = 0
);
