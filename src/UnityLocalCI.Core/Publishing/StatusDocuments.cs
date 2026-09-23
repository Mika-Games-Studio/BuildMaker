using System.Text.Json;
using System.Text.Json.Serialization;
using UnityLocalCI.Core.State;

namespace UnityLocalCI.Core.Publishing;

/// <summary>Uma build, como ela aparece no status e no historico.</summary>
public sealed record BuildStatusDocument(
    long Id,
    string Status,
    DateTimeOffset? Quando,
    int? DuracaoSegundos,
    string Commit,
    string? Autor,
    string? Mensagem,
    string? Arquivo,
    long? TamanhoBytes,
    string? Erro);

/// <summary>O status de um projeto: a build atual, a anterior e o historico.</summary>
public sealed record ProjectStatusDocument(
    string Projeto,
    DateTimeOffset GeradoEm,
    BuildStatusDocument Atual,
    BuildStatusDocument? Anterior,
    IReadOnlyList<string> Avisos,
    IReadOnlyList<BuildStatusDocument> Historico);

/// <summary>Uma linha do arquivo consolidado.</summary>
public sealed record GlobalProjectDocument(
    string Projeto,
    bool EmExecucao,
    DateTimeOffset? IniciouEm,
    BuildStatusDocument? UltimaBuild);

/// <summary>Todos os projetos num arquivo so.</summary>
public sealed record GlobalStatusDocument(
    DateTimeOffset GeradoEm,
    int NaFila,
    int EmExecucao,
    int MaximoSimultaneo,
    IReadOnlyList<GlobalProjectDocument> Projetos);

/// <summary>
/// Os documentos de status, em JSON.
///
/// Eram tres arquivos de texto alinhado a coluna, pensados para serem lidos de
/// relance numa pasta compartilhada. Viraram JSON porque o consumidor mudou: a
/// janela do BuildMaker mostra tudo isso formatado, e o que sobra para o arquivo
/// e ser lido por outra coisa — um script, um painel, um bot. Texto alinhado e
/// bom para o olho e pessimo para qualquer um dos tres.
///
/// A serializacao fica aqui, e nao espalhada, para o formato ser fixado por
/// teste em vez de conferido a olho.
/// </summary>
public static class StatusDocuments
{
    /// <summary>Quantas builds o historico de cada projeto guarda.</summary>
    public const int HistoryLength = 20;

    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        // Sem escapar acentos: o arquivo e para ser aberto e lido, e "SUCESSO"
        // virando "SUCESSO" com Ç no meio nao ajuda ninguem.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string Serialize<T>(T document) => JsonSerializer.Serialize(document, Options);

    public static BuildStatusDocument Describe(BuildRecord build) => new(
        Id: build.Id,
        Status: StatusFormatter.Label(build.Status),
        Quando: (build.FinishedAt ?? build.StartedAt ?? build.QueuedAt).ToLocalTime(),
        DuracaoSegundos: build.DurationSeconds,
        Commit: build.ShortSha,
        Autor: build.CommitAuthor,
        Mensagem: build.CommitMessage,
        Arquivo: ArtifactName(build),
        TamanhoBytes: build.ArtifactSizeBytes,
        Erro: string.IsNullOrWhiteSpace(build.ErrorSummary) ? null : Collapse(build.ErrorSummary));

    private static string? ArtifactName(BuildRecord build)
    {
        var path = build.PublishedPath ?? build.ArtifactPath;
        return string.IsNullOrWhiteSpace(path) ? null : Path.GetFileName(path);
    }

    /// <summary>
    /// O resumo de erro vem do log e chega com quebras de linha. Em JSON isso
    /// vira '\n' no meio de um valor, que ninguem le; uma linha so e mais util.
    /// </summary>
    private static string Collapse(string text)
        => string.Join(' ', text.Split('\n', '\r').Select(l => l.Trim()).Where(l => l.Length > 0));
}
