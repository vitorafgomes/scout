using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Scout.Host.ControlPlane.Transport;

/// <summary>
/// Minimal P/Invoke surface for the UDS anti-hijack pre-flight: effective
/// uid plus a stat that reports owner and file-type. Linux and macOS use
/// different <c>struct stat</c> layouts, so the call is dispatched per-OS.
/// </summary>
[UnsupportedOSPlatform("windows")]
internal static partial class NativeUnix
{
    private const int S_IFMT = 0xF000;
    private const int S_IFSOCK = 0xC000;

    public readonly record struct StatInfo(long OwnerUid, bool IsSocket);

    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEUid();

    public static long GetEffectiveUserId() => GetEUid();

    public static bool TryStat(string path, out StatInfo info)
    {
        info = default;
        if (OperatingSystem.IsMacOS())
        {
            if (MacStat(path, out var uid, out var mode))
            {
                info = new StatInfo(uid, (mode & S_IFMT) == S_IFSOCK);
                return true;
            }
            return false;
        }

        if (LinuxStat(path, out var luid, out var lmode))
        {
            info = new StatInfo(luid, (lmode & S_IFMT) == S_IFSOCK);
            return true;
        }
        return false;
    }

    // ---- Linux: __xstat / statx is glibc-versioned; use the stable stat via
    // a fixed-size buffer large enough for any platform struct stat. We only
    // read st_mode and st_uid at their known offsets for the LP64 layout.

    [LibraryImport("libc", EntryPoint = "stat", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int LinuxStatNative(string path, byte[] buf);

    private static bool LinuxStat(string path, out long uid, out int mode)
    {
        uid = 0;
        mode = 0;
        // glibc x86_64 struct stat: st_mode at offset 24 (uint), st_uid at 28.
        var buf = new byte[144];
        if (LinuxStatNative(path, buf) != 0)
        {
            return false;
        }
        mode = (int)BitConverter.ToUInt32(buf, 24);
        uid = BitConverter.ToUInt32(buf, 28);
        return true;
    }

    [LibraryImport("libc", EntryPoint = "stat", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int MacStatNative(string path, byte[] buf);

    private static bool MacStat(string path, out long uid, out int mode)
    {
        uid = 0;
        mode = 0;
        // macOS struct stat (st_mode at offset 4 as uint16, st_uid at offset 16).
        var buf = new byte[256];
        if (MacStatNative(path, buf) != 0)
        {
            return false;
        }
        mode = BitConverter.ToUInt16(buf, 4);
        uid = BitConverter.ToUInt32(buf, 16);
        return true;
    }
}
