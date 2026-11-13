using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Scout.Host.ControlPlane.Transport;

/// <summary>
/// Minimal P/Invoke surface for the UDS anti-hijack pre-flight: effective
/// uid plus a stat that reports owner and file-type. The native <c>stat</c>
/// symbol and the <c>struct stat</c> layout vary across libc (glibc &lt;2.33,
/// musl) and CPU arch, so every native call degrades safely: a resolution
/// failure or an unsupported arch yields <see cref="StatOutcome.Unavailable"/>
/// and never crashes the bind. Callers must treat Unavailable as fail-closed.
/// </summary>
[UnsupportedOSPlatform("windows")]
internal static partial class NativeUnix
{
    private const int S_IFMT = 0xF000;
    private const int S_IFSOCK = 0xC000;
    private const int S_IFREG = 0x8000;
    private const int ENOENT = 2;

    public readonly record struct StatInfo(long OwnerUid, bool IsSocket, bool IsRegularFile);

    /// <summary>Outcome of a <see cref="TryStat"/> call.</summary>
    public enum StatOutcome
    {
        /// <summary>The path exists and ownership/type were read successfully.</summary>
        Found,

        /// <summary>The path does not exist (stat returned ENOENT).</summary>
        NotFound,

        /// <summary>
        /// Ownership could not be verified: native symbol unresolved,
        /// unsupported arch, or stat failed for a reason other than ENOENT.
        /// Treat as fail-closed.
        /// </summary>
        Unavailable,
    }

    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEUid();

    public static long GetEffectiveUserId() => GetEUid();

    /// <summary>
    /// True only for the LP64 architectures whose <c>struct stat</c> field
    /// offsets this code hardcodes. Any other arch is rejected so a future
    /// mis-port fails closed rather than misreading bytes.
    /// </summary>
    private static bool IsSupportedArchitecture =>
        RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64;

    /// <summary>
    /// Stat <paramref name="path"/> for owner uid and socket-ness. Returns a
    /// tri-state outcome; only <see cref="StatOutcome.Found"/> populates
    /// <paramref name="info"/>.
    /// </summary>
    public static StatOutcome TryStat(string path, out StatInfo info)
    {
        info = default;

        if (!IsSupportedArchitecture)
        {
            return StatOutcome.Unavailable;
        }

        try
        {
            if (OperatingSystem.IsMacOS())
            {
                return MacStat(path, out info);
            }
            return LinuxStat(path, out info);
        }
        catch (DllNotFoundException)
        {
            // libc unresolved (extremely unlikely on Unix, but never crash).
            return StatOutcome.Unavailable;
        }
        catch (EntryPointNotFoundException)
        {
            // 'stat' not exported by this libc (e.g. older glibc inlines it).
            return StatOutcome.Unavailable;
        }
    }

    /// <summary>
    /// Startup self-test: stat a known regular file and assert the reported uid
    /// equals geteuid and the reported type is a regular file. Returns false
    /// when the native stat cannot be trusted on this platform so the host can
    /// decide whether ownership checks are usable.
    /// </summary>
    public static bool SelfTest()
    {
        try
        {
            var probe = Path.Combine(Path.GetTempPath(), $"scout-stat-selftest-{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            try
            {
                var outcome = TryStat(probe, out var info);
                if (outcome != StatOutcome.Found)
                {
                    return false;
                }
                return IsTrustedProbe(info, GetEffectiveUserId());
            }
            finally
            {
                File.Delete(probe);
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// True when the stat of the self-test's own regular file reports the
    /// current user as owner and a regular file as type. Both must hold: where
    /// uid and gid are equal, a wrong offset can still read the right owner.
    /// </summary>
    internal static bool IsTrustedProbe(StatInfo info, long effectiveUserId) =>
        info.OwnerUid == effectiveUserId && info.IsRegularFile;

    /// <summary>
    /// Reads owner and file type from a Linux <c>struct stat</c> at the offsets
    /// of <paramref name="architecture"/>: x86_64 has st_mode at 24 and st_uid
    /// at 28, arm64 has st_mode at 16 and st_uid at 24.
    /// </summary>
    internal static StatInfo ReadLinuxStat(ReadOnlySpan<byte> buffer, Architecture architecture)
    {
        var (modeOffset, uidOffset) = architecture switch
        {
            Architecture.X64 => (24, 28),
            Architecture.Arm64 => (16, 24),
            _ => throw new ArgumentOutOfRangeException(nameof(architecture), architecture, null),
        };
        var mode = (int)BitConverter.ToUInt32(buffer[modeOffset..]);
        var uid = BitConverter.ToUInt32(buffer[uidOffset..]);
        return new StatInfo(uid, (mode & S_IFMT) == S_IFSOCK, (mode & S_IFMT) == S_IFREG);
    }

    /// <summary>
    /// The plain <c>stat</c> symbol, a real export on glibc 2.33 and later and
    /// on musl; <c>__xstat</c> and <c>statx</c> are glibc-versioned.
    /// </summary>
    [LibraryImport("libc", EntryPoint = "stat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int LinuxStatNative(string path, [Out] byte[] buf);

    private static StatOutcome LinuxStat(string path, out StatInfo info)
    {
        info = default;
        var buf = new byte[144];
        if (LinuxStatNative(path, buf) != 0)
        {
            return Marshal.GetLastSystemError() == ENOENT
                ? StatOutcome.NotFound
                : StatOutcome.Unavailable;
        }
        info = ReadLinuxStat(buf, RuntimeInformation.ProcessArchitecture);
        return StatOutcome.Found;
    }

    [LibraryImport("libc", EntryPoint = "stat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int MacStatNative(string path, [Out] byte[] buf);

    private static StatOutcome MacStat(string path, out StatInfo info)
    {
        info = default;
        // macOS struct stat (st_mode at offset 4 as uint16, st_uid at offset 16).
        var buf = new byte[256];
        if (MacStatNative(path, buf) != 0)
        {
            return Marshal.GetLastSystemError() == ENOENT
                ? StatOutcome.NotFound
                : StatOutcome.Unavailable;
        }
        var mode = BitConverter.ToUInt16(buf, 4);
        var uid = BitConverter.ToUInt32(buf, 16);
        info = new StatInfo(uid, (mode & S_IFMT) == S_IFSOCK, (mode & S_IFMT) == S_IFREG);
        return StatOutcome.Found;
    }
}
