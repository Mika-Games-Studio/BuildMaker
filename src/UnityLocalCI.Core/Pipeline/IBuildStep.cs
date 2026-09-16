namespace UnityLocalCI.Core.Pipeline;

/// <summary>
/// Resultado tipado de uma etapa. Erros esperados (rede, disco, falha de build)
/// chegam por aqui; excecao fica reservada para o inesperado.
/// </summary>
public sealed record StepResult(bool Success, string? ErrorSummary = null)
{
    public static readonly StepResult Ok = new(true);
    public static StepResult Fail(string errorSummary) => new(false, errorSummary);
}

public interface IBuildStep
{
    string Name { get; }
    Task<StepResult> ExecuteAsync(BuildContext context, CancellationToken ct);
}
