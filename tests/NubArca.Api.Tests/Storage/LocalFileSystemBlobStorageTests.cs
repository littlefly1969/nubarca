using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using NubArca.Api.Storage;

namespace NubArca.Api.Tests.Storage;

public class LocalFileSystemBlobStorageTests : IDisposable
{
    private readonly string _root;
    private readonly LocalFileSystemBlobStorage _storage;

    public LocalFileSystemBlobStorageTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"nubarca-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);

        var options = Options.Create(new BlobStorageOptions { RootPath = _root });
        _storage = new LocalFileSystemBlobStorage(options);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // best effort
        }

        GC.SuppressFinalize(this);
    }

    private static string Sha256Hex(byte[] data)
    {
        return Convert.ToHexStringLower(SHA256.HashData(data));
    }

    [Fact]
    public async Task WriteAsync_Stores_Content_At_Hash_Derived_Path()
    {
        var content = Encoding.UTF8.GetBytes("hello, nubarca");
        var expectedHash = Sha256Hex(content);

        var result = await _storage.WriteAsync(new MemoryStream(content));

        Assert.Equal(expectedHash, result.Sha256);
        Assert.Equal(content.LongLength, result.SizeBytes);
        Assert.False(result.AlreadyExisted);
        Assert.Equal($"objects/{expectedHash[..2]}/{expectedHash[2..4]}/{expectedHash}", result.StorageKey);

        var expectedFile = Path.Combine(_root, "objects", expectedHash[..2], expectedHash[2..4], expectedHash);
        Assert.True(File.Exists(expectedFile));
        Assert.Equal(content, await File.ReadAllBytesAsync(expectedFile));
    }

    [Fact]
    public async Task WriteAsync_Empty_Content_Is_Stored_With_Empty_Sha256()
    {
        var result = await _storage.WriteAsync(new MemoryStream(Array.Empty<byte>()));

        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", result.Sha256);
        Assert.Equal(0, result.SizeBytes);
        Assert.False(result.AlreadyExisted);
    }

    [Fact]
    public async Task WriteAsync_Is_Idempotent_For_Identical_Content()
    {
        var content = Encoding.UTF8.GetBytes("dedup-me");

        var first = await _storage.WriteAsync(new MemoryStream(content));
        var second = await _storage.WriteAsync(new MemoryStream(content));

        Assert.False(first.AlreadyExisted);
        Assert.True(second.AlreadyExisted);
        Assert.Equal(first.Sha256, second.Sha256);
        Assert.Equal(first.StorageKey, second.StorageKey);
        Assert.Equal(first.SizeBytes, second.SizeBytes);

        // Final file count = 1 (no duplicates) and tmp directory is empty.
        var blobDir = Path.Combine(_root, "objects", first.Sha256[..2], first.Sha256[2..4]);
        Assert.Single(Directory.GetFiles(blobDir));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "tmp")));
    }

    [Fact]
    public async Task OpenReadAsync_Returns_Stored_Content()
    {
        var content = Encoding.UTF8.GetBytes("readback");
        var write = await _storage.WriteAsync(new MemoryStream(content));

        await using var stream = await _storage.OpenReadAsync(write.StorageKey);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);

        Assert.Equal(content, ms.ToArray());
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("../../etc/passwd")]
    [InlineData("objects/../etc/passwd")]
    [InlineData("objects/ab/cd/../../../etc/passwd")]
    [InlineData("objects/ab/cd/abcd")]                                                                    // too short
    [InlineData("objects/AB/CD/ABCDEF1234567890ABCDEF1234567890ABCDEF1234567890ABCDEF1234567890ABCD")]    // uppercase rejected
    [InlineData("objects/zz/zz/zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]        // non-hex
    [InlineData("notobjects/ab/cd/abcdef1234567890abcdef1234567890abcdef1234567890abcdef12345678")]       // wrong root segment
    [InlineData("")]
    public async Task OpenReadAsync_Rejects_Invalid_Storage_Keys(string key)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(
            async () => await _storage.OpenReadAsync(key));
    }

    [Fact]
    public async Task OpenReadAsync_Rejects_Key_With_Mismatched_Directory_Prefix()
    {
        // syntactically valid (hex pairs + 64-hex sha) but the directories do not match sha[..4].
        const string key = "objects/ff/ff/abcdef1234567890abcdef1234567890abcdef1234567890abcdef12345678";

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await _storage.OpenReadAsync(key));
    }

    [Fact]
    public async Task ExistsAsync_Returns_True_For_Stored_Blob_And_False_Otherwise()
    {
        var content = Encoding.UTF8.GetBytes("exists-check");
        var write = await _storage.WriteAsync(new MemoryStream(content));

        Assert.True(await _storage.ExistsAsync(write.StorageKey));

        // A well-formed but unstored key.
        const string missing = "objects/00/00/0000000000000000000000000000000000000000000000000000000000000000";
        Assert.False(await _storage.ExistsAsync(missing));
    }

    [Fact]
    public async Task WriteAsync_Cancelled_Mid_Stream_Leaves_No_Final_Blob_And_No_Temp_File()
    {
        using var cts = new CancellationTokenSource();
        var content = Encoding.UTF8.GetBytes("data-that-will-not-finish");
        var stream = new CancellingStream(content, cts, cancelAfterBytes: 8);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await _storage.WriteAsync(stream, cts.Token));

        Assert.False(Directory.EnumerateFiles(Path.Combine(_root, "objects"), "*", SearchOption.AllDirectories).Any());
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "tmp")));
    }

    [Fact]
    public async Task WriteAsync_Throwing_Source_Stream_Leaves_No_Final_Blob_And_No_Temp_File()
    {
        var stream = new ThrowingStream(throwAfterBytes: 4);

        await Assert.ThrowsAsync<IOException>(
            async () => await _storage.WriteAsync(stream));

        Assert.False(Directory.EnumerateFiles(Path.Combine(_root, "objects"), "*", SearchOption.AllDirectories).Any());
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "tmp")));
    }

    [Fact]
    public void Constructor_Throws_If_RootPath_Is_Empty()
    {
        var options = Options.Create(new BlobStorageOptions { RootPath = "" });
        Assert.Throws<InvalidOperationException>(() => new LocalFileSystemBlobStorage(options));
    }

    [Fact]
    public async Task DeleteAsync_Removes_Existing_Blob()
    {
        var content = Encoding.UTF8.GetBytes("delete-me");
        var write = await _storage.WriteAsync(new MemoryStream(content));
        Assert.True(await _storage.ExistsAsync(write.StorageKey));

        await _storage.DeleteAsync(write.StorageKey);

        Assert.False(await _storage.ExistsAsync(write.StorageKey));
    }

    [Fact]
    public async Task DeleteAsync_Is_Idempotent_For_Missing_Blob()
    {
        // Never-stored, well-formed key.
        const string key = "objects/00/00/0000000000000000000000000000000000000000000000000000000000000000";

        await _storage.DeleteAsync(key); // first call — no exception
        await _storage.DeleteAsync(key); // second call — still no exception
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("../../etc/passwd")]
    [InlineData("objects/../etc/passwd")]
    [InlineData("objects/ab/cd/../../../etc/passwd")]
    [InlineData("objects/AB/CD/ABCDEF1234567890ABCDEF1234567890ABCDEF1234567890ABCDEF1234567890ABCD")]
    [InlineData("objects/zz/zz/zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    [InlineData("")]
    public async Task DeleteAsync_Rejects_Invalid_Storage_Keys(string key)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(
            async () => await _storage.DeleteAsync(key));
    }

    private sealed class CancellingStream : Stream
    {
        private readonly byte[] _data;
        private readonly CancellationTokenSource _cts;
        private readonly int _cancelAfterBytes;
        private int _position;

        public CancellingStream(byte[] data, CancellationTokenSource cts, int cancelAfterBytes)
        {
            _data = data;
            _cts = cts;
            _cancelAfterBytes = cancelAfterBytes;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_position >= _cancelAfterBytes)
            {
                _cts.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }

            var remaining = _data.Length - _position;
            if (remaining <= 0)
            {
                return ValueTask.FromResult(0);
            }

            var toCopy = Math.Min(Math.Min(buffer.Length, remaining), 4);
            _data.AsMemory(_position, toCopy).CopyTo(buffer);
            _position += toCopy;
            return ValueTask.FromResult(toCopy);
        }
    }

    private sealed class ThrowingStream : Stream
    {
        private readonly int _throwAfterBytes;
        private int _read;

        public ThrowingStream(int throwAfterBytes)
        {
            _throwAfterBytes = throwAfterBytes;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_read >= _throwAfterBytes)
            {
                throw new IOException("simulated source-stream failure");
            }

            var toCopy = Math.Min(buffer.Length, 2);
            for (int i = 0; i < toCopy; i++)
            {
                buffer.Span[i] = (byte)'x';
            }
            _read += toCopy;
            return ValueTask.FromResult(toCopy);
        }
    }
}
