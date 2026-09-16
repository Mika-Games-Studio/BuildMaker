using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Options;
using UnityLocalCI.Core.Configuration;

namespace UnityLocalCI.Core.Queue;

public interface ISystemResources
{
    double FreePhysicalMemoryGb { get; }
    double InstalledPhysicalMemoryGb { get; }
    double FreeDiskGb(string path);
}

public sealed class SystemResourceGuard : IResourceGuard
{
    private readonly ISystemResources _resources;
    private readonly IOptionsMonitor<CiOptions> _options;

    public SystemResourceGuard(ISystemResources resources, IOptionsMonitor<CiOptions> options)
    {
        _resources = resources;
        _options = options;
    }

    public ResourceCheck Check(ResolvedProject project)
    {
        var scheduler = _options.CurrentValue.Scheduler;

        var freeRam = _resources.FreePhysicalMemoryGb;
        if (freeRam < scheduler.MinFreeRamGb)
        {
            return ResourceCheck.Blocked(
                $"RAM livre {freeRam:0.0} GB abaixo do minimo de {scheduler.MinFreeRamGb} GB");
        }

        foreach (var folder in EnumerateLocalFolders(project))
        {
            var freeDisk = _resources.FreeDiskGb(folder);
            if (freeDisk < project.Retention.MinFreeDiskGb)
            {
                return ResourceCheck.Blocked(
                    $"espaco livre em {folder} e {freeDisk:0.0} GB, abaixo do minimo de {project.Retention.MinFreeDiskGb} GB");
            }
        }

        return ResourceCheck.Ok;
    }

    /// <summary>
    /// So checamos disco local. O destino pode ser um compartilhamento de rede
    /// indisponivel, e indisponibilidade la nao impede a build: o artefato fica
    /// em staging e a copia vira pendencia.
    /// </summary>
    private static IEnumerable<string> EnumerateLocalFolders(ResolvedProject project)
    {
        yield return project.Repository.WorkspacePath;
        yield return project.Publishing.StagingFolder;
    }
}

[SupportedOSPlatform("windows")]
public sealed class WindowsSystemResources : ISystemResources
{
    public double FreePhysicalMemoryGb
    {
        get
        {
            var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            return GlobalMemoryStatusEx(ref status)
                ? status.ullAvailPhys / (1024d * 1024d * 1024d)
                : 0d;
        }
    }

    public double InstalledPhysicalMemoryGb
    {
        get
        {
            // GetPhysicallyInstalledSystemMemory devolve a RAM dos modulos, nao a
            // visivel ao SO, que e o que o teto global precisa: 32 GB instalados
            // aparecem como ~31,7 GB visiveis e arredondariam para baixo.
            if (GetPhysicallyInstalledSystemMemory(out var kilobytes))
                return kilobytes / (1024d * 1024d);

            var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            return GlobalMemoryStatusEx(ref status)
                ? status.ullTotalPhys / (1024d * 1024d * 1024d)
                : 0d;
        }
    }

    public double FreeDiskGb(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return double.MaxValue;

            var drive = new DriveInfo(root);
            return drive.IsReady ? drive.AvailableFreeSpace / (1024d * 1024d * 1024d) : double.MaxValue;
        }
        catch (ArgumentException)
        {
            // Caminho UNC ou invalido: nao da para medir, e nao e motivo para bloquear.
            return double.MaxValue;
        }
        catch (IOException)
        {
            return double.MaxValue;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicallyInstalledSystemMemory(out long totalMemoryInKilobytes);
}
