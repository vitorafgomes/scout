using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Scout.Host.Worker.Features;

/// <summary>
/// Windows Credential Manager backed <see cref="ICredentialStore"/>.
/// Uses direct P/Invoke against <c>advapi32.dll</c> so the project does
/// not have to take a NuGet dependency that would only build on Windows.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WinCredCredentialStore : ICredentialStore
{
    private const uint CRED_TYPE_GENERIC = 1;
    private const uint CRED_PERSIST_LOCAL_MACHINE = 2;
    private const int ERROR_NOT_FOUND = 1168;

    private readonly ILogger<WinCredCredentialStore> _logger;

    /// <summary>Build a Windows credential store.</summary>
    public WinCredCredentialStore(ILogger<WinCredCredentialStore> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public bool IsAvailable => OperatingSystem.IsWindows();

    /// <inheritdoc/>
    public Task<bool> StoreAsync(string key, string value, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        ct.ThrowIfCancellationRequested();

        if (!IsAvailable)
        {
            return Task.FromResult(false);
        }

        var bytes = Encoding.Unicode.GetBytes(value);
        var blob = IntPtr.Zero;
        try
        {
            blob = Marshal.AllocCoTaskMem(bytes.Length);
            Marshal.Copy(bytes, 0, blob, bytes.Length);

            var cred = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = TargetFor(key),
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                UserName = key,
            };

            if (!CredWriteW(ref cred, 0))
            {
                var err = Marshal.GetLastWin32Error();
                _logger.LogWarning(
                    "CredWrite failed for {Key} with Win32 error {Error}",
                    key,
                    err);
                return Task.FromResult(false);
            }
        }
        finally
        {
            if (blob != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(blob);
            }
        }

        return Task.FromResult(true);
    }

    /// <inheritdoc/>
    public Task<string?> GetAsync(string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ct.ThrowIfCancellationRequested();

        if (!IsAvailable)
        {
            return Task.FromResult<string?>(null);
        }

        var ptr = IntPtr.Zero;
        try
        {
            if (!CredReadW(TargetFor(key), CRED_TYPE_GENERIC, 0, out ptr))
            {
                var err = Marshal.GetLastWin32Error();
                if (err != ERROR_NOT_FOUND)
                {
                    _logger.LogWarning(
                        "CredRead failed for {Key} with Win32 error {Error}",
                        key,
                        err);
                }
                return Task.FromResult<string?>(null);
            }

            var cred = Marshal.PtrToStructure<CREDENTIAL>(ptr);
            if (cred.CredentialBlobSize == 0 || cred.CredentialBlob == IntPtr.Zero)
            {
                return Task.FromResult<string?>(string.Empty);
            }

            var buffer = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, buffer, 0, buffer.Length);
            return Task.FromResult<string?>(Encoding.Unicode.GetString(buffer));
        }
        finally
        {
            if (ptr != IntPtr.Zero)
            {
                CredFree(ptr);
            }
        }
    }

    /// <inheritdoc/>
    public Task DeleteAsync(string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ct.ThrowIfCancellationRequested();

        if (!IsAvailable)
        {
            return Task.CompletedTask;
        }

        if (!CredDeleteW(TargetFor(key), CRED_TYPE_GENERIC, 0))
        {
            var err = Marshal.GetLastWin32Error();
            if (err != ERROR_NOT_FOUND)
            {
                _logger.LogWarning(
                    "CredDelete failed for {Key} with Win32 error {Error}",
                    key,
                    err);
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task<bool> ContainsAsync(string key, CancellationToken ct)
    {
        var value = await GetAsync(key, ct).ConfigureAwait(false);
        return value is not null;
    }

    private static string TargetFor(string key) => $"scout:{key}";

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredWriteW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool CredWriteW(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredReadW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool CredReadW(string target, uint type, uint flags, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredDeleteW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool CredDeleteW(string target, uint type, uint flags);

    [DllImport("advapi32.dll", SetLastError = false)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void CredFree(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

}
