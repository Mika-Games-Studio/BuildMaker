using System.Text.Json;
using UnityLocalCI.Core.Notifications;
using UnityLocalCI.Core.State;
using Xunit;

namespace UnityLocalCI.Tests;

public class TeamsNotifierTests
{
    private static BuildRecord Build(BuildStatus status = BuildStatus.Succeeded, string? error = null) => new()
    {
        Id = 42,
        CommitSha = "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0",
        CommitMessage = "corrige colisao do player",
        CommitAuthor = "Fulano de Tal",
        Project = "Crash",
        Branch = "HML",
        Status = status,
        QueuedAt = DateTimeOffset.UnixEpoch,
        DurationSeconds = 760,
        ArtifactSizeBytes = 82_208_358,
        PublishedPath = @"\\build01\builds\crash\hml\Crash-HML-20260914-a1b2c3d.zip",
        ErrorSummary = error,
    };

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Payload_e_um_adaptive_card_valido()
    {
        var json = TeamsNotifier.BuildPayload(Build(), Array.Empty<string>());
        var root = Parse(json);

        Assert.Equal("message", root.GetProperty("type").GetString());

        var content = root.GetProperty("attachments")[0].GetProperty("content");
        Assert.Equal("AdaptiveCard", content.GetProperty("type").GetString());

        // O campo e "$schema" no Adaptive Card, e "$" nao e identificador valido
        // em C#: se a troca do nome sumir, o Teams recusa o card.
        Assert.True(content.TryGetProperty("$schema", out _));
        Assert.False(content.TryGetProperty("schema", out _));
    }

    [Fact]
    public void Sucesso_traz_commit_autor_duracao_e_arquivo()
    {
        var json = TeamsNotifier.BuildPayload(Build(), Array.Empty<string>());

        Assert.Contains("Build OK", json);
        Assert.Contains("a1b2c3d", json);
        Assert.Contains("Fulano de Tal", json);
        Assert.Contains("12 min 40 s", json);
        Assert.Contains("Crash-HML-20260914-a1b2c3d.zip", json);
    }

    [Fact]
    public void Falha_traz_o_erro_resumido()
    {
        var json = TeamsNotifier.BuildPayload(
            Build(BuildStatus.Failed, "Assets\\Player.cs(42,17): error CS0103: nao existe\nsegunda linha"),
            Array.Empty<string>());

        Assert.Contains("Build falhou", json);
        Assert.Contains("CS0103", json);
        // So a primeira linha: o card nao e o log.
        Assert.DoesNotContain("segunda linha", json);
    }

    [Fact]
    public void Avisos_aparecem_no_card()
    {
        var json = TeamsNotifier.BuildPayload(
            Build(), new[] { "compressao Brotli sem decompressionFallback" });

        Assert.Contains("Brotli", json);
    }

    [Fact]
    public void Muitos_avisos_nao_estouram_o_card()
    {
        var avisos = Enumerable.Range(1, 20).Select(i => $"aviso {i}").ToArray();

        var json = TeamsNotifier.BuildPayload(Build(), avisos);

        Assert.Contains("aviso 5", json);
        Assert.DoesNotContain("aviso 6", json);
    }
}
