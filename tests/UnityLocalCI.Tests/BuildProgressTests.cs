using UnityLocalCI.Core.Pipeline;
using Xunit;

namespace UnityLocalCI.Tests;

/// <summary>
/// O letreiro da build em execucao.
///
/// O que importa aqui e o fim: uma etapa que fica no mapa depois de a build
/// acabar vira uma tela que diz "Compilando" para uma build que terminou ha
/// meia hora — pior que nao dizer nada.
/// </summary>
public class BuildProgressTests
{
    [Fact]
    public void Sem_etapa_registrada_a_build_nao_tem_o_que_mostrar()
        => Assert.Null(new BuildProgress().Etapa(1));

    [Fact]
    public void A_ultima_etapa_em_que_entrou_e_a_que_aparece()
    {
        var progresso = new BuildProgress();

        progresso.Entrou(7, "Sync");
        progresso.Entrou(7, "Build");

        Assert.Equal("Build", progresso.Etapa(7));
    }

    [Fact]
    public void Build_que_terminou_sai_do_letreiro()
    {
        var progresso = new BuildProgress();

        progresso.Entrou(7, "Build");
        progresso.Saiu(7);

        Assert.Null(progresso.Etapa(7));
    }

    /// <summary>
    /// Duas builds correm ao mesmo tempo quando o teto permite; uma nao pode
    /// mostrar a etapa da outra.
    /// </summary>
    [Fact]
    public void Cada_build_tem_a_propria_etapa()
    {
        var progresso = new BuildProgress();

        progresso.Entrou(1, "Sync");
        progresso.Entrou(2, "Publicacao");
        progresso.Saiu(1);

        Assert.Null(progresso.Etapa(1));
        Assert.Equal("Publicacao", progresso.Etapa(2));
    }
}
