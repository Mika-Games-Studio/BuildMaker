using UnityLocalCI.Core.Configuration;
using Xunit;

namespace UnityLocalCI.Tests;

/// <summary>
/// A pasta de destino e uma so, para todos os projetos.
///
/// Ela ja foi por projeto, e nao pagava o que custava: cada zip carrega o nome
/// do projeto, a branch, a data e o commit no proprio nome, entao uma pasta por
/// jogo nao separava nada que o nome do arquivo ja nao separasse. Em troca, era
/// mais um caminho para preencher a cada projeto novo — e um deles apontando
/// para o lugar errado e uma build que o time procura e nao acha.
/// </summary>
public class ArtifactFolderTests
{
    private static ProjectOptions Projeto(string? propria = null) => new()
    {
        Name = "Crash",
        Repository = new RepositoryOptions
        {
            Url = "https://example.invalid/crash",
            Branch = "HML",
            WorkspacePath = @"C:\ci\workspace\crash",
        },
        Publishing = propria is null ? null : new PublishingOptions { ArtifactFolder = propria },
    };

    private static ProjectDefaults Padroes(string? destino = @"D:\builds") => new()
    {
        Unity = new UnityOptions { EditorVersion = "6000.0.47f1" },
        Publishing = new PublishingOptions { StagingFolder = @"C:\ci\staging", ArtifactFolder = destino },
    };

    [Fact]
    public void A_pasta_vem_dos_padroes()
        => Assert.Equal(@"D:\builds", ProjectResolver.Resolve(Projeto(), Padroes()).Publishing.ArtifactFolder);

    /// <summary>
    /// E um valor no projeto nao sobrescreve mais nada. Importa porque as
    /// configuracoes que ja existem em disco tem esse campo preenchido: se ele
    /// ainda vencesse, a tela mostraria a pasta da aba Geral e as builds
    /// continuariam indo para outro lugar, sem nada acusar a diferenca.
    /// </summary>
    [Fact]
    public void Um_valor_no_projeto_e_ignorado()
    {
        var resolvido = ProjectResolver.Resolve(Projeto(propria: @"D:\antigo\crash"), Padroes());

        Assert.Equal(@"D:\builds", resolvido.Publishing.ArtifactFolder);
    }

    [Fact]
    public void Todos_os_projetos_recebem_a_mesma_pasta()
    {
        var options = new CiOptions { Defaults = Padroes() };
        options.Projects.Add(Projeto());
        options.Projects.Add(new ProjectOptions
        {
            Name = "Mines",
            Repository = new RepositoryOptions
            {
                Url = "https://example.invalid/mines",
                Branch = "HML",
                WorkspacePath = @"C:\ci\workspace\mines",
            },
            Publishing = new PublishingOptions { ArtifactFolder = @"D:\outro\lugar" },
        });

        var pastas = ProjectResolver.ResolveEnabled(options)
            .Select(p => p.Publishing.ArtifactFolder)
            .Distinct()
            .ToArray();

        Assert.Equal([@"D:\builds"], pastas);
    }

    /// <summary>
    /// Sem pasta nos padroes sobra vazio, e a validacao reclama disso. E o que
    /// se quer: melhor o servico recusar a subir do que construir e nao ter
    /// para onde copiar.
    /// </summary>
    [Fact]
    public void Sem_pasta_nos_padroes_o_resultado_e_vazio()
        => Assert.Equal("", ProjectResolver.Resolve(Projeto(), Padroes(destino: null)).Publishing.ArtifactFolder);
}
