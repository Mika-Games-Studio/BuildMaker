using UnityLocalCI.Core.Configuration;

namespace UnityLocalCI.Core.Queue;

public sealed record ResourceCheck(bool CanStart, string? Reason)
{
    public static readonly ResourceCheck Ok = new(true, null);
    public static ResourceCheck Blocked(string reason) => new(false, reason);
}

/// <summary>
/// Guarda de recursos. Um build WebGL consome de 8 a 16 GB na fase de link do
/// IL2CPP: estourar a RAM nao deixa o build lento, faz ele morrer com um erro
/// obscuro do Emscripten. Abaixo do limite o job e adiado, nunca descartado.
/// </summary>
public interface IResourceGuard
{
    ResourceCheck Check(ResolvedProject project);
}

public static class SchedulerLimits
{
    /// <summary>Quanta RAM reservar por build simultanea ao derivar o teto global.</summary>
    public const int GigabytesPerBuild = 16;

    /// <summary>
    /// Teto efetivo: o menor entre o configurado e RAM_instalada / 16, com minimo de 1.
    /// Usa a RAM instalada, nao a visivel ao SO: numa maquina de 32 GB o SO
    /// reporta ~31,7 GB e o arredondamento daria 1 em vez dos 2 pretendidos.
    /// </summary>
    public static int EffectiveMaxConcurrentBuilds(int configured, double installedRamGb)
    {
        var derived = (int)Math.Floor(installedRamGb / GigabytesPerBuild);
        return Math.Max(1, Math.Min(configured, derived));
    }
}
