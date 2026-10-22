using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NubArca.Api.MailArchive;

// Opens a file the PST engine wrote, from the worker, without trusting
// anything about it but its name. The engine is confined (src/sandbox.rs), but
// the worker reading its output is not: a parser compromised by a malformed
// PST could leave `i42/att-0.bin` as a symlink (or a hard link, a FIFO, a
// directory) pointing at a file only the worker can read, and the worker would
// import those bytes as an attachment. So the reference is resolved by the
// kernel relative to the staging directory, never following a symlink in any
// component (openat2 RESOLVE_BENEATH | RESOLVE_NO_SYMLINKS, or a per-component
// O_NOFOLLOW walk where openat2 is missing), and the opened descriptor itself
// must be a regular file with a single link. The
// checks run on the descriptor that is then read, so there is no window
// between check and use.
public static class EngineFiles
{
    public const string UnsafeOutput = "unsafe_engine_output";

    // Opens `relative` ("i42/att-0.bin", as the engine reported it) inside
    // `baseDirectory/requestId/`. Throws PstEngineException(UnsafeOutput) for
    // anything but a plain regular file there, FileNotFoundException when it
    // does not exist. The stream's Length is the opened file's size.
    public static FileStream Open(string baseDirectory, string requestId, string relative)
    {
        var components = Components(requestId, relative);
        if (!OperatingSystem.IsLinux())
        {
            // The product ships the Linux engine only; other systems run the
            // tests' fake engine. Best effort there: no link in any component.
            return OpenPortable(baseDirectory, components);
        }
        return Linux.Open(baseDirectory, components);
    }

    // The engine's reference must name a file below its own request directory
    // with plain components only: no root, no "." or "..", no empty part.
    internal static string[] Components(string requestId, string relative)
    {
        var parts = relative.Split('/');
        if (parts.Length < 2 || parts[0] != requestId
            || parts.Any(p => p.Length == 0 || p == "." || p == ".." || p.Contains('\\') || p.Contains('\0')))
        {
            throw Unsafe("The PST engine referenced a file outside its request directory.");
        }
        return parts;
    }

    private static PstEngineException Unsafe(string message) => new(UnsafeOutput, message);

    private static FileStream OpenPortable(string baseDirectory, string[] components)
    {
        var path = baseDirectory;
        foreach (var part in components)
        {
            path = Path.Combine(path, part);
            var info = new FileInfo(path);
            if (info.LinkTarget is not null)
            {
                throw Unsafe("The PST engine left a link in its output.");
            }
        }
        return File.OpenRead(path);
    }

    private static class Linux
    {
        private const int OCloexec = 0x80000;
        private const int ONonblock = 0x800;
        private const int OPath = 0x200000;
        private const int AtEmptyPath = 0x1000;
        private const ulong ResolveNoXdev = 0x01, ResolveNoMagiclinks = 0x02, ResolveNoSymlinks = 0x04, ResolveBeneath = 0x08;
        private const long OpenAt2Number = 437; // same number on x86_64 and arm64
        private const int Enoent = 2, Enosys = 38;
        private const uint StatxType = 0x1, StatxMode = 0x2, StatxNlink = 0x4;
        private const int SIfmt = 0xF000, SIfreg = 0x8000;

        // O_DIRECTORY and O_NOFOLLOW are architecture-specific.
        private static (int Directory, int NoFollow) ArchFlags() => RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => (0x10000, 0x20000),
            Architecture.Arm64 => (0x4000, 0x8000),
            _ => throw new PlatformNotSupportedException("MailArchive engine output: unsupported architecture."),
        };

        [StructLayout(LayoutKind.Sequential)]
        private struct OpenHow
        {
            public ulong Flags;
            public ulong Mode;
            public ulong Resolve;
        }

        [DllImport("libc", SetLastError = true, EntryPoint = "open")]
        private static extern int SysOpen(string path, int flags, int mode);

        [DllImport("libc", SetLastError = true, EntryPoint = "openat")]
        private static extern int SysOpenAt(int dirfd, string path, int flags, int mode);

        [DllImport("libc", SetLastError = true, EntryPoint = "syscall")]
        private static extern long SysOpenat2(long number, int dirfd, string path, ref OpenHow how, nuint size);

        [DllImport("libc", SetLastError = true, EntryPoint = "statx")]
        private static extern int SysStatx(int dirfd, string path, int flags, uint mask, byte[] buffer);

        [DllImport("libc", SetLastError = true, EntryPoint = "close")]
        private static extern int SysClose(int fd);

        public static FileStream Open(string baseDirectory, string[] components)
        {
            var (oDirectory, oNoFollow) = ArchFlags();
            // The staging directory itself is the worker's (its parent is not
            // writable by the engine), so it is opened normally.
            var baseFd = SysOpen(baseDirectory, OPath | oDirectory | OCloexec, 0);
            if (baseFd < 0)
            {
                throw new IOException("The engine staging directory cannot be opened.");
            }
            int fd;
            try
            {
                fd = OpenBeneath(baseFd, components, oDirectory, oNoFollow);
            }
            finally
            {
                SysClose(baseFd);
            }

            var handle = new SafeFileHandle(fd, ownsHandle: true);
            try
            {
                var stat = new byte[256];
                if (SysStatx(fd, string.Empty, AtEmptyPath, StatxType | StatxMode | StatxNlink, stat) != 0)
                {
                    throw Unsafe("The PST engine output cannot be inspected.");
                }
                var nlink = BitConverter.ToUInt32(stat, 16);
                var mode = BitConverter.ToUInt16(stat, 28);
                if ((mode & SIfmt) != SIfreg)
                {
                    throw Unsafe("The PST engine output is not a regular file.");
                }
                if (nlink != 1)
                {
                    throw Unsafe("The PST engine output is linked elsewhere.");
                }
                return new FileStream(handle, FileAccess.Read, bufferSize: 1 << 16);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        private static int OpenBeneath(int baseFd, string[] components, int oDirectory, int oNoFollow)
        {
            // O_NONBLOCK: opening a FIFO the engine planted must not hang the
            // worker; it is rejected by the type check right after.
            var how = new OpenHow
            {
                Flags = (ulong)(OCloexec | ONonblock | oNoFollow),
                Resolve = ResolveBeneath | ResolveNoSymlinks | ResolveNoMagiclinks | ResolveNoXdev,
            };
            var fd = (int)SysOpenat2(OpenAt2Number, baseFd, string.Join('/', components), ref how, (nuint)Marshal.SizeOf<OpenHow>());
            if (fd >= 0)
            {
                return fd;
            }
            var error = Marshal.GetLastPInvokeError();
            if (error == Enosys)
            {
                return OpenByWalk(baseFd, components, oDirectory, oNoFollow);
            }
            throw Failure(error);
        }

        // Kernels before 5.6: the same guarantee, one component at a time.
        internal static int OpenByWalk(int baseFd, string[] components, int oDirectory, int oNoFollow)
        {
            var current = baseFd;
            try
            {
                for (var i = 0; i < components.Length; i++)
                {
                    var last = i == components.Length - 1;
                    var flags = last
                        ? OCloexec | ONonblock | oNoFollow
                        : OPath | oDirectory | OCloexec | oNoFollow;
                    var next = SysOpenAt(current, components[i], flags, 0);
                    if (next < 0)
                    {
                        throw Failure(Marshal.GetLastPInvokeError());
                    }
                    if (current != baseFd)
                    {
                        SysClose(current);
                    }
                    current = next;
                }
                return current;
            }
            catch
            {
                if (current != baseFd)
                {
                    SysClose(current);
                }
                throw;
            }
        }

        private static Exception Failure(int error) => error == Enoent
            ? new FileNotFoundException("The PST engine output is missing.")
            : Unsafe("The PST engine output cannot be opened safely.");

        // For tests: the pre-openat2 path, exercised explicitly.
        internal static FileStream OpenWithWalk(string baseDirectory, string[] components)
        {
            var (oDirectory, oNoFollow) = ArchFlags();
            var baseFd = SysOpen(baseDirectory, OPath | oDirectory | OCloexec, 0);
            try
            {
                var fd = OpenByWalk(baseFd, components, oDirectory, oNoFollow);
                return new FileStream(new SafeFileHandle(fd, ownsHandle: true), FileAccess.Read);
            }
            finally
            {
                SysClose(baseFd);
            }
        }
    }

    internal static FileStream OpenWithWalkForTests(string baseDirectory, string requestId, string relative) =>
        Linux.OpenWithWalk(baseDirectory, Components(requestId, relative));
}
