using UnityLocalCI.Core.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using UnityLocalCI.Core.Git;
using Xunit;

namespace UnityLocalCI.Tests;

public class GitClientTests
{
    private static GitContext Context(string? pat = null) => new()
    {
        WorkspacePath = Path.Combine(Path.GetTempPath(), "unitylocalci-tests", "ws"),
        RepositoryUrl = "https://example.invalid/repo",
        Branch = "HML",
        PersonalAccessToken = pat,
    };

    private static GitClient Create(RecordingProcessRunner runner)
        => new(runner, NullLogger<GitClient>.Instance);

    [Fact]
    public async Task Checkout_invoca_o_clean_com_as_exclusoes()
    {
        var runner = new RecordingProcessRunner();
        var client = Create(runner);

        await client.CheckoutAsync(Context(), new string('a', 40), CancellationToken.None);

        var clean = runner.Requests.Single(r => r.Arguments.Contains("clean"));
        foreach (var preserved in GitCleanArguments.PreservedPaths)
            Assert.Contains(preserved, clean.Arguments);
    }

    [Fact]
    public async Task Pat_vai_por_extraheader_e_nunca_na_url_do_remote()
    {
        var runner = new RecordingProcessRunner();
        var client = Create(runner);
        const string pat = "segredo-do-pat";

        await client.FetchAsync(Context(pat), CancellationToken.None);

        var request = runner.Requests.Single();

        Assert.Contains(request.Arguments, a => a.StartsWith("http.extraHeader=Authorization: Basic", StringComparison.Ordinal));
        Assert.DoesNotContain(request.Arguments, a => a.Contains(pat, StringComparison.Ordinal));
        Assert.DoesNotContain(request.Arguments, a => a.Contains("@example.invalid", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Linha_de_comando_logada_nao_contem_o_segredo()
    {
        var runner = new RecordingProcessRunner();
        var client = Create(runner);
        const string pat = "segredo-do-pat";

        await client.FetchAsync(Context(pat), CancellationToken.None);

        var safeCommandLine = runner.Requests.Single().SafeCommandLine;

        Assert.DoesNotContain(pat, safeCommandLine, StringComparison.Ordinal);
        // Tambem nao pode vazar em base64, que e como o header carrega o PAT.
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($":{pat}"));
        Assert.DoesNotContain(encoded, safeCommandLine, StringComparison.Ordinal);
        Assert.Contains("<REDACTED>", safeCommandLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Falha_do_git_vira_excecao_tipada_com_stderr_redigido()
    {
        const string pat = "segredo-do-pat";
        var runner = new RecordingProcessRunner
        {
            Respond = _ => new ProcessResult(128, false, "", $"fatal: Authentication failed for {pat}"),
        };
        var client = Create(runner);

        var exception = await Assert.ThrowsAsync<GitCommandException>(
            () => client.FetchAsync(Context(pat), CancellationToken.None));

        Assert.Equal(128, exception.ExitCode);
        Assert.DoesNotContain(pat, exception.StandardError, StringComparison.Ordinal);
        Assert.Contains("<REDACTED>", exception.StandardError, StringComparison.Ordinal);
    }
}
