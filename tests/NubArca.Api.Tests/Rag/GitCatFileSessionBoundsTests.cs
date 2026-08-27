using System.Diagnostics;
using NubArca.Api.Rag.Sources;
using Xunit;

namespace NubArca.Api.Tests.Rag;

// What the `cat-file --batch` session does when the thing on the other end
// misbehaves.
//
// Two exposures, one mechanism. The session allocates `new byte[size]` from a
// number a subprocess printed, so a tracked four-gigabyte blob is an
// OutOfMemoryException in a service — from a file nothing was ever going to
// index. And a read that never completes hangs an index run forever with no
// reason code and no way out.
//
// Both are refusals to finish reading a response, and the stream is a single
// conversation: the unread bytes stay queued, so the next read would parse blob
// content as a header and every object after it would come back as somebody
// else's. There is no resynchronising without consuming what is left, and
// consuming it is the work being refused. So the session dies instead.
//
// A healthy `git cat-file` cannot demonstrate any of this — it answers
// correctly and fast, which is the problem — so these tests supply the process
// themselves through the internal seam.
public sealed class GitCatFileSessionBoundsTests
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(400);

    [SkippableFact]
    public async Task CatFileSession_RefusesOversizedHeaderBeforeAllocation()
    {
        Skip.If(OperatingSystem.IsWindows(), "The stand-in process is a POSIX shell.");

        // Announces a four-gigabyte object and then stalls. If the bound were
        // applied after the read rather than before it, this test would either
        // allocate 4 GiB or hang — the two outcomes it exists to prevent.
        await using var session = Shell(
            """read id; printf '%s blob 4294967296\n' "$id"; sleep 30""");

        var failure = await Assert.ThrowsAsync<RepositorySnapshotUnavailableException>(
            () => session.ReadAsync("0123456789abcdef0123456789abcdef01234567"));

        Assert.Equal("git-object-too-large", failure.Reason);
        Assert.True(session.IsFaulted);
    }

    [SkippableFact]
    public async Task An_Object_Under_The_Ceiling_Still_Reads()
    {
        Skip.If(OperatingSystem.IsWindows(), "The stand-in process is a POSIX shell.");

        // The control. A ceiling that refused everything would pass the test
        // above and index nothing.
        await using var session = Shell(
            """read id; printf 'x blob 5\nhello\n'""");

        var bytes = await session.ReadAsync("0123456789abcdef0123456789abcdef01234567");

        Assert.Equal("hello", System.Text.Encoding.UTF8.GetString(bytes!));
        Assert.False(session.IsFaulted);
    }

    [SkippableFact]
    public async Task CatFileReadTimeout_IsSanitized()
    {
        Skip.If(OperatingSystem.IsWindows(), "The stand-in process is a POSIX shell.");

        // Accepts the request and never answers.
        await using var session = Shell("sleep 30", ShortTimeout);

        var failure = await Assert.ThrowsAsync<RepositorySnapshotUnavailableException>(
            () => session.ReadAsync("0123456789abcdef0123456789abcdef01234567"));

        // A reason token and nothing else. No git stderr, no filesystem path,
        // no object id.
        Assert.Equal("git-object-read-timeout", failure.Reason);
        Assert.DoesNotContain("/", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "0123456789abcdef", failure.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task TimedOutCatFileSession_IsNotReused()
    {
        Skip.If(OperatingSystem.IsWindows(), "The stand-in process is a POSIX shell.");

        // Stalls once, then would answer. A session that recovered would return
        // that second response to the FIRST request's caller — which is exactly
        // the desynchronisation that makes a partially consumed stream unusable.
        await using var session = Shell(
            """sleep 2; read id; printf 'x blob 5\nhello\n'""", ShortTimeout);

        await Assert.ThrowsAsync<RepositorySnapshotUnavailableException>(
            () => session.ReadAsync("0123456789abcdef0123456789abcdef01234567"));

        var second = await Assert.ThrowsAsync<RepositorySnapshotUnavailableException>(
            () => session.ReadAsync("89abcdef0123456789abcdef0123456789abcdef"));

        Assert.Equal("git-object-read-timeout", second.Reason);
        Assert.True(session.IsFaulted);
    }

    [SkippableFact]
    public async Task Cancellation_RemainsDistinctFromTimeout()
    {
        Skip.If(OperatingSystem.IsWindows(), "The stand-in process is a POSIX shell.");

        // The operator stopped the run. That is not a fact about git and gets no
        // reason code — an OperationCanceledException must not arrive at the
        // caller dressed as a repository failure, because a cancelled index is
        // not a broken one and must not record a permanent failure.
        await using var session = Shell("sleep 30", TimeSpan.FromSeconds(30));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => session.ReadAsync(
                "0123456789abcdef0123456789abcdef01234567", cancellation.Token));

        // The response is just as half-consumed either way, so the session still
        // dies — it is the REASON that differs, not the damage.
        Assert.True(session.IsFaulted);
    }

    [SkippableFact]
    public async Task A_Healthy_Session_Survives_Many_Objects()
    {
        Skip.If(OperatingSystem.IsWindows(), "The stand-in process is a POSIX shell.");

        // The bounds must not cost the thing they protect: the framing still has
        // to stay in step across a long run of objects, which is what the whole
        // `--batch` design is for.
        await using var session = Shell(
            """while read id; do printf 'x blob 5\n%s\n' "obj-$(printf '%s' "$id" | cut -c1-1)"; done""");

        for (var i = 0; i < 20; i++)
        {
            var bytes = await session.ReadAsync($"{i % 10}bcdef0123456789abcdef0123456789abcdef01");
            Assert.Equal($"obj-{i % 10}", System.Text.Encoding.UTF8.GetString(bytes!));
        }

        Assert.False(session.IsFaulted);
    }

    // ---- fixture -------------------------------------------------------------

    /// A stand-in for `git cat-file --batch`: reads object ids on stdin and
    /// writes whatever the script says on stdout.
    private static GitCatFileSession Shell(string script, TimeSpan? readTimeout = null)
    {
        var info = new ProcessStartInfo("/bin/sh")
        {
            WorkingDirectory = Path.GetTempPath(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add(script);
        return GitCatFileSession.StartProcess(
            info, readTimeout ?? GitCatFileSession.DefaultReadTimeout);
    }
}
