using Microsoft.Extensions.Logging.Abstractions;
using UnityLocalCI.Core.Abstractions;
using UnityLocalCI.Core.Configuration;
using UnityLocalCI.Core.Git;
using UnityLocalCI.Core.Secrets;

namespace UnityLocalCI.App;

/// <summary>
/// As branches conhecidas de cada repositorio, para a tela de configuracao
/// oferecer a lista em vez de esperar o nome digitado.
///
/// Nome de branch errado e um erro que so aparece na primeira build, e aparece
/// disfarcado: o git diz que a referencia nao existe, e quem le entende que o
/// repositorio esta inacessivel.
///
/// Duas fontes, nesta ordem:
///
///   1. os refs do clone local, lidos do disco — respondem na hora, servem
///      offline e nao precisam de credencial;
///   2. 'git ls-remote' no servidor — e a lista de verdade, inclusive das
///      branches criadas depois do ultimo fetch.
///
/// A consulta ao servidor nunca acontece na thread da janela: ela leva o tempo
/// que a rede levar.
/// </summary>
public sealed class BranchCatalog
{
    private readonly ICredentialStore _credentials;
    private readonly IGitClient _git;
    private readonly object _gate = new();

    private readonly Dictionary<string, IReadOnlyList<string>> _porRepositorio = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _emAndamento = new(StringComparer.OrdinalIgnoreCase);

    public BranchCatalog(ICredentialStore credentials, IGitClient? git = null)
    {
        _credentials = credentials;

        // Cliente proprio, e nao o do host: esta tela funciona com o servico
        // parado, que e exatamente quando alguem esta configurando.
        _git = git ?? new GitClient(
            new ProcessRunner(NullLogger<ProcessRunner>.Instance),
            NullLogger<GitClient>.Instance);
    }

    /// <summary>Avisa que a lista de um repositorio mudou. Chamado fora da thread da janela.</summary>
    public event Action<string>? Updated;

    /// <summary>O que ja se sabe. Nunca bloqueia.</summary>
    public IReadOnlyList<string> Known(string? repositoryUrl)
    {
        if (string.IsNullOrWhiteSpace(repositoryUrl)) return [];

        lock (_gate)
            return _porRepositorio.TryGetValue(repositoryUrl, out var lista) ? lista : [];
    }

    /// <summary>
    /// Esquece o que foi descoberto. Serve para depois de conectar ao GitHub: a
    /// lista vazia de antes foi resultado da falta de acesso, e insistir nela
    /// esconderia justamente o que acabou de passar a funcionar.
    /// </summary>
    public void Clear()
    {
        lock (_gate) _porRepositorio.Clear();
    }

    /// <summary>
    /// Usado pelos testes para provar o caminho do dropdown sem depender de rede
    /// nem de clone em disco.
    /// </summary>
    internal void Seed(string url, IEnumerable<string> branches)
        => Publicar(url, branches, concluido: true);

    public bool IsLoading(string? repositoryUrl)
    {
        if (string.IsNullOrWhiteSpace(repositoryUrl)) return false;

        lock (_gate) return _emAndamento.Contains(repositoryUrl);
    }

    /// <summary>
    /// Garante que a lista deste projeto esta sendo carregada. Volta na hora; o
    /// resultado aparece quando chegar.
    /// </summary>
    /// <param name="defaults">
    /// Padroes da maquina, para a credencial herdada valer aqui tambem. Sem
    /// isto, a listagem tentaria sem token justamente nos projetos que nao
    /// definem credencial propria — que passaram a ser todos.
    /// </param>
    public void EnsureLoaded(ProjectOptions project, ProjectDefaults defaults, bool force = false)
    {
        var url = project.Repository?.Url;
        if (string.IsNullOrWhiteSpace(url)) return;

        lock (_gate)
        {
            if (_emAndamento.Contains(url)) return;
            if (!force && _porRepositorio.ContainsKey(url)) return;

            _emAndamento.Add(url);
        }

        var workspace = project.Repository?.WorkspacePath;
        var credencial = ProjectResolver.ResolveCredential(project, defaults);

        _ = Task.Run(() => CarregarAsync(url, workspace, credencial));
    }

    private async Task CarregarAsync(string url, string? workspace, string? credentialName)
    {
        // O que o clone conhece de origin. Nunca branch local: o CI observa o
        // que esta no servidor, e uma branch que so existe na maquina de alguem
        // nao tem como disparar build nenhuma.
        var doClone = LocalRepositoryInfo.ReadKnownBranches(workspace);
        var resultado = doClone;

        try
        {
            // Resposta provisoria, para o dropdown nao ficar vazio enquanto a
            // rede nao volta.
            Publicar(url, doClone, concluido: false);

            var pat = string.IsNullOrWhiteSpace(credentialName) ? null : _credentials.Read(credentialName);

            var remotas = await _git.ListRemoteBranchesAsync(
                new GitContext
                {
                    WorkspacePath = workspace ?? "",
                    RepositoryUrl = url,
                    Branch = "",
                    PersonalAccessToken = pat,
                },
                CancellationToken.None).ConfigureAwait(false);

            // SUBSTITUI, nao soma: o servidor e a verdade. O clone guarda refs de
            // origin que ja foram apagadas la, e some-las traria de volta branch
            // que nao existe mais — justamente o erro que a lista veio evitar.
            resultado = remotas;
        }
        catch (Exception)
        {
            // Sem rede, sem credencial ou URL errada: fica o que o clone sabia.
            // Esta lista e uma comodidade; ela nunca pode virar um erro na cara
            // de quem esta so preenchendo a configuracao.
        }
        finally
        {
            lock (_gate) _emAndamento.Remove(url);
            Publicar(url, resultado, concluido: true);
        }
    }

    private void Publicar(string url, IEnumerable<string> branches, bool concluido)
    {
        var lista = branches
            .Where(b => !string.IsNullOrWhiteSpace(b))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(b => b, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (lista.Length == 0 && !concluido) return;

        lock (_gate) _porRepositorio[url] = lista;

        Updated?.Invoke(url);
    }
}
