using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace UnityLocalCI.Core.Abstractions;

/// <summary>
/// Job Object do Windows com KILL_ON_JOB_CLOSE.
///
/// O Unity gera filhos de IL2CPP e Emscripten que sobrevivem a um kill do
/// processo pai e seguram o lock da Library, travando o proximo build. Andar a
/// tabela de pais e filhos (Process.Kill(entireProcessTree)) perde os que ja
/// foram reparenteados; o Job Object nao, porque a associacao e herdada e
/// permanente.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsJobObject : IDisposable
{
    private IntPtr _handle;

    public WindowsJobObject()
    {
        _handle = CreateJobObject(IntPtr.Zero, null);
        if (_handle == IntPtr.Zero)
            throw new InvalidOperationException($"CreateJobObject falhou (erro {Marshal.GetLastWin32Error()}).");

        var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            }
        };

        var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limits, buffer, false);
            if (!SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, buffer, (uint)size))
                throw new InvalidOperationException($"SetInformationJobObject falhou (erro {Marshal.GetLastWin32Error()}).");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Assign(nint processHandle)
    {
        if (_handle == IntPtr.Zero) return;
        if (!AssignProcessToJobObject(_handle, processHandle))
        {
            var error = Marshal.GetLastWin32Error();
            // ERROR_ACCESS_DENIED acontece quando o processo ja terminou entre o
            // start e a associacao. Nao e motivo para derrubar a build.
            if (error is not (5 or 6)) // ACCESS_DENIED, INVALID_HANDLE
                throw new InvalidOperationException($"AssignProcessToJobObject falhou (erro {error}).");
        }
    }

    /// <summary>Encerra todo processo associado ao job, filhos e netos inclusive.</summary>
    public void TerminateAll()
    {
        if (_handle != IntPtr.Zero)
            TerminateJobObject(_handle, 1);
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        CloseHandle(_handle);
        _handle = IntPtr.Zero;
    }

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    private const int JobObjectExtendedLimitInformation = 9;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr securityAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }
}
