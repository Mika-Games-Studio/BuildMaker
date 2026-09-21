using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UnityLocalCI.Core.Configuration;

/// <summary>
/// Um arquivo de configuracao por projeto, na pasta 'projetos'.
///
/// Antes todos os projetos viviam dentro de um array no appsettings.json. Com
/// dois ou tres ja era desconfortavel: mexer num deles reescrevia o arquivo
/// inteiro, um erro de digitacao derrubava a leitura de todos, e nao dava para
/// copiar a configuracao de um projeto para outra maquina sem levar junto o
/// resto.
///
/// Agora cada projeto e um arquivo com o nome dele. O appsettings.json fica so
/// com o que e da maquina: fila, estado, padroes e notificacoes.
/// </summary>
public static class ProjectFiles
{
    public const string FolderName = "projetos";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>A pasta de projetos que acompanha um appsettings.json.</summary>
    public static string FolderFor(string configPath)
        => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? ".", FolderName);

    /// <summary>
    /// Nome de arquivo derivado do nome do projeto. Caractere que o Windows nao
    /// aceita em nome de arquivo vira '-', para o nome do projeto continuar
    /// livre.
    /// </summary>
    public static string FileNameFor(string projectName)
    {
        var limpo = new string((projectName ?? "").Trim()
            .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c)
            .ToArray());

        if (limpo.Length == 0) limpo = "projeto";

        return limpo + ".json";
    }

    /// <summary>
    /// Le todos os projetos da pasta, em ordem de nome de arquivo para a lista
    /// nao dancar entre execucoes.
    ///
    /// Um arquivo quebrado nao derruba os outros: ele entra na lista de
    /// problemas e o resto continua valendo. O contrario — um projeto sumir em
    /// silencio porque outro tem uma virgula a mais — seria pior.
    /// </summary>
    public static IReadOnlyList<ProjectOptions> LoadAll(string folder, out IReadOnlyList<string> problems)
    {
        var projetos = new List<ProjectOptions>();
        var erros = new List<string>();

        problems = erros;

        if (!Directory.Exists(folder)) return projetos;

        foreach (var arquivo in Directory.EnumerateFiles(folder, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var projeto = JsonSerializer.Deserialize<ProjectOptions>(File.ReadAllText(arquivo), ReadOptions);

                if (projeto is null)
                {
                    erros.Add($"{Path.GetFileName(arquivo)}: arquivo vazio.");
                    continue;
                }

                projetos.Add(projeto);
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
            {
                erros.Add($"{Path.GetFileName(arquivo)}: {exception.Message}");
            }
        }

        return projetos;
    }

    public static IReadOnlyList<ProjectOptions> LoadAll(string folder) => LoadAll(folder, out _);

    /// <summary>
    /// Grava a lista inteira: um arquivo por projeto, e apaga os arquivos dos
    /// projetos que nao existem mais. Renomear um projeto renomeia o arquivo,
    /// que e o efeito que qualquer um espera.
    /// </summary>
    public static void SaveAll(string folder, IReadOnlyList<ProjectOptions> projects)
    {
        Directory.CreateDirectory(folder);

        var esperados = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var projeto in projects)
        {
            var nome = FileNameFor(projeto.Name);
            esperados.Add(nome);

            var destino = Path.Combine(folder, nome);
            var json = JsonSerializer.Serialize(projeto, WriteOptions);

            // Mesma tatica do appsettings: ninguem pode ler o arquivo pela
            // metade, nem perde-lo se a gravacao falhar no meio.
            var temporario = destino + ".tmp";
            File.WriteAllText(temporario, json, new UTF8Encoding(false));
            File.Move(temporario, destino, overwrite: true);
        }

        foreach (var arquivo in Directory.EnumerateFiles(folder, "*.json"))
        {
            if (!esperados.Contains(Path.GetFileName(arquivo)))
                File.Delete(arquivo);
        }
    }

    /// <summary>
    /// Move os projetos que ainda estao dentro do appsettings.json para a pasta,
    /// uma unica vez.
    ///
    /// Idempotente e conservadora: se a pasta ja tem arquivos, nao mexe em nada.
    /// Devolve quantos projetos foram movidos, e zero quando nao havia o que
    /// fazer. A configuracao de quem ja estava rodando nao pode se perder numa
    /// atualizacao.
    /// </summary>
    public static int MigrateFromAppSettings(string configPath)
    {
        if (!File.Exists(configPath)) return 0;

        var folder = FolderFor(configPath);

        if (Directory.Exists(folder) && Directory.EnumerateFiles(folder, "*.json").Any()) return 0;

        System.Text.Json.Nodes.JsonObject? documento;
        try
        {
            documento = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(configPath))
                        as System.Text.Json.Nodes.JsonObject;
        }
        catch (JsonException)
        {
            return 0;
        }

        if (documento?["Projects"] is not System.Text.Json.Nodes.JsonArray array || array.Count == 0) return 0;

        var projetos = array.Deserialize<List<ProjectOptions>>(ReadOptions);
        if (projetos is null || projetos.Count == 0) return 0;

        SaveAll(folder, projetos);

        // Só depois de os arquivos existirem: se a máquina cair no meio, o pior
        // caso é a lista aparecer duplicada, e não desaparecer.
        documento.Remove("Projects");

        var temporario = configPath + ".tmp";
        File.WriteAllText(temporario, documento.ToJsonString(WriteOptions), new UTF8Encoding(false));
        File.Move(temporario, configPath, overwrite: true);

        return projetos.Count;
    }
}
