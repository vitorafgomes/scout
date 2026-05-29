using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace Scout.ControlPlane.Providers;

/// <summary>
/// Default <see cref="IProcessLauncher"/> backed by <see cref="Process"/>.
/// stdout/stderr from the child are forwarded to the logger at debug level
/// so a noisy daemon does not pollute the worker's info stream.
/// </summary>
public sealed class SystemProcessLauncher : IProcessLauncher
{
    private readonly ILoggerFactory _loggerFactory;

    public SystemProcessLauncher(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    public ILaunchedProcess Launch(ProcessLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FileName);

        var psi = new ProcessStartInfo
        {
            FileName = request.FileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        if (OperatingSystem.IsWindows())
        {
            psi.WindowStyle = ProcessWindowStyle.Hidden;
        }

        foreach (var arg in request.Arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        if (request.EnvironmentOverrides is not null)
        {
            foreach (var kvp in request.EnvironmentOverrides)
            {
                if (kvp.Value is null)
                {
                    psi.Environment.Remove(kvp.Key);
                }
                else
                {
                    psi.Environment[kvp.Key] = kvp.Value;
                }
            }
        }

        var process = new Process
        {
            StartInfo = psi,
            EnableRaisingEvents = true,
        };

        var logger = _loggerFactory.CreateLogger<SystemProcessLauncher>();

        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                logger.LogDebug("[{FileName}:stdout] {Line}", request.FileName, e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                logger.LogDebug("[{FileName}:stderr] {Line}", request.FileName, e.Data);
            }
        };

        try
        {
            if (!process.Start())
            {
                process.Dispose();
                throw new InvalidOperationException(
                    $"Failed to start process '{request.FileName}'.");
            }
        }
        catch
        {
            process.Dispose();
            throw;
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        JobObjectHandle? jobHandle = null;
        if (OperatingSystem.IsWindows())
        {
            try
            {
                jobHandle = JobObjectHandle.CreateAndAssign(process, logger);
            }
            catch (Exception ex)
            {
                // Best-effort: if the job object cannot be created we still
                // launch the process - it just won't be reaped automatically
                // when the worker dies.
                logger.LogDebug(ex, "Failed to attach process {Pid} to a job object", process.Id);
            }
        }

        return new LaunchedProcess(process, jobHandle, logger);
    }

    private sealed class LaunchedProcess : ILaunchedProcess
    {
        private readonly Process _process;
        private readonly JobObjectHandle? _jobHandle;
        private readonly ILogger _logger;
        private int _disposed;

        public LaunchedProcess(Process process, JobObjectHandle? jobHandle, ILogger logger)
        {
            _process = process;
            _jobHandle = jobHandle;
            _logger = logger;
        }

        public int Pid => _process.Id;

        public bool HasExited => _process.HasExited;

        public async Task<int> WaitForExitAsync(CancellationToken ct)
        {
            await _process.WaitForExitAsync(ct).ConfigureAwait(false);
            return _process.ExitCode;
        }

        public async Task<bool> StopAsync(TimeSpan graceful, CancellationToken ct)
        {
            if (_process.HasExited)
            {
                return true;
            }

            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                TrySendSigTerm(_process.Id, _logger);

                using var graceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                graceCts.CancelAfter(graceful);
                try
                {
                    await _process.WaitForExitAsync(graceCts.Token).ConfigureAwait(false);
                    return true;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Grace period expired - fall through to force-kill.
                }
            }

            if (!_process.HasExited)
            {
                try
                {
                    _process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Race: process exited between HasExited check and Kill.
                    return true;
                }

                try
                {
                    await _process.WaitForExitAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Caller cancelled - bubble up below.
                    throw;
                }

                return false;
            }

            return true;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                if (!_process.HasExited)
                {
                    try
                    {
                        _process.Kill(entireProcessTree: true);
                    }
                    catch (InvalidOperationException)
                    {
                        // Already exited.
                    }

                    try
                    {
                        await _process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Best-effort during dispose.
                    }
                }
            }
            finally
            {
                if (_jobHandle is not null && OperatingSystem.IsWindows())
                {
                    _jobHandle.Dispose();
                }
                _process.Dispose();
            }
        }

        private static void TrySendSigTerm(int pid, ILogger logger)
        {
            try
            {
                using var killer = Process.Start(new ProcessStartInfo
                {
                    FileName = "kill",
                    ArgumentList = { "-TERM", pid.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });
                killer?.WaitForExit(2000);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "kill -TERM {Pid} failed", pid);
            }
        }
    }

    /// <summary>
    /// Windows JobObject wrapper. When the worker process dies the kernel
    /// closes this handle, which - with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>
    /// set - terminates every process assigned to the job. No-op on Unix.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private sealed class JobObjectHandle : IDisposable
    {
        private IntPtr _handle;

        private JobObjectHandle(IntPtr handle)
        {
            _handle = handle;
        }

        public static JobObjectHandle? CreateAndAssign(Process process, ILogger logger)
        {
            var handle = NativeMethods.CreateJobObject(IntPtr.Zero, null);
            if (handle == IntPtr.Zero)
            {
                logger.LogDebug("CreateJobObject returned NULL");
                return null;
            }

            var info = new NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new NativeMethods.JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = NativeMethods.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
                },
            };

            var infoSize = Marshal.SizeOf<NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            var infoPtr = Marshal.AllocHGlobal(infoSize);
            try
            {
                Marshal.StructureToPtr(info, infoPtr, fDeleteOld: false);
                if (!NativeMethods.SetInformationJobObject(
                    handle,
                    NativeMethods.JobObjectExtendedLimitInformation,
                    infoPtr,
                    (uint)infoSize))
                {
                    NativeMethods.CloseHandle(handle);
                    logger.LogDebug("SetInformationJobObject failed");
                    return null;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(infoPtr);
            }

            if (!NativeMethods.AssignProcessToJobObject(handle, process.Handle))
            {
                NativeMethods.CloseHandle(handle);
                logger.LogDebug("AssignProcessToJobObject failed for pid {Pid}", process.Id);
                return null;
            }

            return new JobObjectHandle(handle);
        }

        public void Dispose()
        {
            var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
            if (handle != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(handle);
            }
        }

        [SupportedOSPlatform("windows")]
        private static class NativeMethods
        {
            public const int JobObjectExtendedLimitInformation = 9;
            public const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            public static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool SetInformationJobObject(
                IntPtr hJob,
                int JobObjectInfoClass,
                IntPtr lpJobObjectInfo,
                uint cbJobObjectInfoLength);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool CloseHandle(IntPtr hObject);

            [StructLayout(LayoutKind.Sequential)]
            public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                public long PerProcessUserTimeLimit;
                public long PerJobUserTimeLimit;
                public uint LimitFlags;
                public UIntPtr MinimumWorkingSetSize;
                public UIntPtr MaximumWorkingSetSize;
                public uint ActiveProcessLimit;
                public UIntPtr Affinity;
                public uint PriorityClass;
                public uint SchedulingClass;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct IO_COUNTERS
            {
                public ulong ReadOperationCount;
                public ulong WriteOperationCount;
                public ulong OtherOperationCount;
                public ulong ReadTransferCount;
                public ulong WriteTransferCount;
                public ulong OtherTransferCount;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
                public IO_COUNTERS IoInfo;
                public UIntPtr ProcessMemoryLimit;
                public UIntPtr JobMemoryLimit;
                public UIntPtr PeakProcessMemoryUsed;
                public UIntPtr PeakJobMemoryUsed;
            }
        }
    }
}
