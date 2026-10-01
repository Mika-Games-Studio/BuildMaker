using System.Globalization;

namespace UnityLocalCI.Core.Queue;

/// <summary>
/// Memoria lida de /proc/meminfo; disco, do ponto de montagem que cobre o
/// caminho.
///
/// No Windows a mesma informacao sai de duas chamadas do kernel32. No Linux ela
/// e um arquivo de texto — o que parece fragil e nao e: /proc/meminfo e a
/// interface estavel do kernel para isto, e e dela que free, top e htop leem.
///
/// Sem UnsupportedOSPlatform de proposito, para a suite poder exercita-la a
/// partir do Windows. Numa maquina sem /proc a leitura de memoria devolve zero
/// em vez de estourar — o que faz o guarda de recursos bloquear, que e o lado
/// seguro de errar. Quem escolhe entre esta e a versao do Windows e o
/// ServiceRegistration, em tempo de execucao.
/// </summary>
public sealed class LinuxSystemResources : ISystemResources
{
    /// <summary>
    /// MemAvailable, e nao MemFree.
    ///
    /// MemFree e so a memoria que nao esta sendo usada para nada, e num Linux
    /// saudavel ela e quase zero: o kernel usa toda a sobra como cache de disco e
    /// devolve na hora que alguem precisa. Usar MemFree aqui faria o guarda de
    /// recursos bloquear toda build numa maquina perfeitamente ociosa.
    ///
    /// MemAvailable e a estimativa do proprio kernel de quanto da para alocar sem
    /// entrar em swap — que e exatamente a pergunta que o guarda esta fazendo.
    /// </summary>
    public double FreePhysicalMemoryGb => LerMeminfoGb("MemAvailable") ?? LerMeminfoGb("MemFree") ?? 0d;

    public double InstalledPhysicalMemoryGb => LerMeminfoGb("MemTotal") ?? 0d;

    public double FreeDiskGb(string path)
    {
        try
        {
            var completo = Path.GetFullPath(path);

            // Path.GetPathRoot devolve "/" em qualquer caminho do Linux, o que
            // mediria sempre a raiz. Aqui os discos sao pontos de montagem: o que
            // vale e o mais longo que seja prefixo do caminho — com /mnt/builds
            // montado a parte, um caminho dentro dele tem espaco proprio, e nao o
            // da raiz.
            DriveInfo? melhor = null;
            foreach (var drive in DriveInfo.GetDrives())
            {
                var ponto = drive.RootDirectory.FullName;
                if (!CobreOCaminho(ponto, completo)) continue;
                if (melhor is null || ponto.Length > melhor.RootDirectory.FullName.Length)
                    melhor = drive;
            }

            if (melhor is null || !melhor.IsReady) return double.MaxValue;
            return melhor.AvailableFreeSpace / (1024d * 1024d * 1024d);
        }
        catch (ArgumentException)
        {
            // Caminho invalido: nao da para medir, e nao e motivo para bloquear.
            return double.MaxValue;
        }
        catch (IOException)
        {
            return double.MaxValue;
        }
        catch (UnauthorizedAccessException)
        {
            return double.MaxValue;
        }
    }

    /// <summary>
    /// Compara por segmento, e nao por texto: sem isto, "/mnt/build" apareceria
    /// como dono de "/mnt/builds2".
    /// </summary>
    private static bool CobreOCaminho(string pontoDeMontagem, string caminho)
    {
        var ponto = pontoDeMontagem.TrimEnd('/');
        if (ponto.Length == 0) return true; // a raiz cobre tudo
        if (!caminho.StartsWith(ponto, StringComparison.Ordinal)) return false;
        return caminho.Length == ponto.Length || caminho[ponto.Length] == '/';
    }

    /// <summary>Le uma linha de /proc/meminfo, que vem sempre em kB.</summary>
    private static double? LerMeminfoGb(string campo)
    {
        try
        {
            foreach (var linha in File.ReadLines("/proc/meminfo"))
            {
                if (!linha.StartsWith(campo, StringComparison.Ordinal)) continue;
                if (linha.Length <= campo.Length || linha[campo.Length] != ':') continue;

                // "MemAvailable:   16323176 kB"
                var partes = linha[(campo.Length + 1)..]
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                if (partes.Length > 0 &&
                    double.TryParse(partes[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var kb))
                {
                    return kb / (1024d * 1024d);
                }
            }
        }
        catch (IOException) { /* sem /proc: cai para o padrao de quem chamou */ }
        catch (UnauthorizedAccessException) { }

        return null;
    }
}
