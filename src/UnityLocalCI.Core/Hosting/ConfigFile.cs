using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using UnityLocalCI.Core.Configuration;
using UnityLocalCI.Core.Secrets;

namespace UnityLocalCI.Core.Hosting;

/// <summary>
/// Leitura e escrita do appsettings.json pela janela.
///
/// A gravacao passa pela mesma validacao que o servico usa na inicializacao:
/// salvar uma configuracao que derruba o servico no proximo start seria pior
/// que nao deixar salvar.
/// </summary>
public static class ConfigFile
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "appsettings.json");

    public static CiOptions Load(string path)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Path.GetDirectoryName(Path.GetFullPath(path))!)
            .AddJsonFile(Path.GetFileName(path), optional: false)
            .Build();

        return configuration.Get<CiOptions>() ?? new CiOptions();
    }

    /// <summary>Valida e grava. Retorna a lista de problemas; vazia significa gravado.</summary>
    public static IReadOnlyList<string> Save(string path, CiOptions options, ICredentialStore credentials)
    {
        var validation = new CiOptionsValidator(credentials).Validate(null, options);
        if (validation.Failed) return validation.Failures!.ToList();

        // A secao Logging nao pertence ao CiOptions e seria perdida numa
        // serializacao ingenua do objeto tipado.
        var logging = ReadLoggingSection(path);

        var document = new Dictionary<string, object?>();
        if (logging is not null) document["Logging"] = logging;

        document["Scheduler"] = options.Scheduler;
        document["State"] = options.State;
        document["Defaults"] = options.Defaults;
        document["Projects"] = options.Projects;
        document["Notifications"] = options.Notifications;

        var json = JsonSerializer.Serialize(document, WriteOptions);

        // Mesma tatica dos arquivos de status: ninguem pode ler o appsettings
        // pela metade, nem perde-lo se a gravacao falhar no meio.
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, json, new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);

        return Array.Empty<string>();
    }

    private static JsonElement? ReadLoggingSection(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("Logging", out var logging)
                ? logging.Clone()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
