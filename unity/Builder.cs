#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// Ponto de entrada do UnityLocalCI dentro do Unity.
///
/// Invocado pelo pipeline como 'Builder.PerformBuild', sem namespace de
/// proposito: e o nome que a configuracao usa em Unity.ExecuteMethod.
///
/// Este arquivo NAO altera Player Settings. Compressao, qualidade e template
/// sao configurados no projeto Unity e versionados junto dele; sobrescrever
/// aqui faria uma mudanca feita no Editor ser silenciosamente desfeita na
/// build, o que e pior que qualquer problema que isso resolveria. A unica
/// escrita possivel e o carimbo de bundleVersion, desligado por padrao e
/// ligado so por -ciStampVersion true.
/// </summary>
public static class Builder
{
    /// <summary>
    /// Prefixo que o pipeline procura no log. As linhas marcadas sao promovidas
    /// ao topo do resumo e para o _STATUS.txt, em vez de ficarem enterradas em
    /// dezenas de milhares de linhas de saida do Unity.
    /// </summary>
    private const string Marker = "[UnityLocalCI]";

    private const string ErrorPrefix = Marker + " ERRO:";
    private const string WarningPrefix = Marker + " AVISO:";
    private const string InfoPrefix = Marker;

    /// <summary>Quantas mensagens de erro do BuildReport transcrever no resumo.</summary>
    private const int MaxReportedErrors = 25;

    public static void PerformBuild()
    {
        try
        {
            var exitCode = Run();
            EditorApplication.Exit(exitCode);
        }
        catch (Exception exception)
        {
            // Qualquer excecao aqui e falha de build. Sem este catch, o Unity
            // pode encerrar com codigo 0 e o pipeline publicaria lixo.
            Debug.LogError(ErrorPrefix + " excecao nao tratada no Builder: " + exception.Message);
            Debug.LogError(exception.ToString());
            EditorApplication.Exit(1);
        }
    }

    private static int Run()
    {
        var args = ParseArguments(Environment.GetCommandLineArgs());

        var buildNumber = Get(args, "ciBuildNumber", "0");
        var commitSha = Get(args, "ciCommitSha", "");
        var branch = Get(args, "ciBranch", "");

        // O CLI do Unity encaminha --output-path como -buildOutput; -ciOutputPath
        // vem por --args. Os dois trazem o mesmo caminho, e lemos qualquer um dos
        // dois para nao depender de como o pipeline foi invocado.
        var outputPath = Get(args, "buildOutput", Get(args, "ciOutputPath", ""));

        var target = EditorUserBuildSettings.activeBuildTarget;

        Debug.Log(InfoPrefix + " build " + buildNumber +
                  " | commit " + Short(commitSha) +
                  " | branch " + branch +
                  " | alvo " + target +
                  " | editor " + Application.unityVersion);

        if (string.IsNullOrEmpty(outputPath))
        {
            Debug.LogError(ErrorPrefix + " caminho de saida nao informado. " +
                           "Esperado -buildOutput (via --output-path) ou -ciOutputPath (via --args).");
            return 1;
        }

        var scenes = EnabledScenes();
        if (scenes.Length == 0)
        {
            // Build sem cena compila e roda, e a tela fica preta sem erro nenhum.
            // Falhar aqui custa segundos; descobrir isso depois custa uma tarde.
            Debug.LogError(ErrorPrefix + " nenhuma cena habilitada em Build Settings. " +
                           "Uma build sem cenas gera uma tela preta sem mensagem de erro.");
            return 1;
        }

        Debug.Log(InfoPrefix + " " + scenes.Length + " cena(s) habilitada(s): " + string.Join(", ", scenes));

        InspectWebGlCompression(target);

        if (ShouldStampVersion(args))
        {
            StampBundleVersion(buildNumber, commitSha);
        }

        PrepareOutputFolder(outputPath);

        var options = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = outputPath,
            target = target,
            targetGroup = BuildPipeline.GetBuildTargetGroup(target),
            options = BuildOptions.None,
        };

        var report = BuildPipeline.BuildPlayer(options);
        return ReportResult(report, outputPath);
    }

    // ---------------------------------------------------------------- resultado

    /// <summary>
    /// O ponto mais importante deste arquivo: encerrar com codigo diferente de
    /// zero em qualquer resultado que nao seja Succeeded. Sem isso o Unity
    /// termina com 0 mesmo em build quebrada, e o pipeline publica lixo.
    /// </summary>
    private static int ReportResult(BuildReport report, string outputPath)
    {
        if (report == null)
        {
            Debug.LogError(ErrorPrefix + " BuildPipeline.BuildPlayer nao retornou relatorio. Tratando como falha.");
            return 1;
        }

        var summary = report.summary;
        var seconds = summary.totalTime.TotalSeconds;

        if (summary.result == BuildResult.Succeeded)
        {
            Debug.Log(InfoPrefix + " build concluida em " + seconds.ToString("0", CultureInfo.InvariantCulture) + "s" +
                      " | " + FormatSize((long)summary.totalSize) +
                      " | " + summary.totalWarnings + " aviso(s)" +
                      " | saida " + outputPath);
            return 0;
        }

        Debug.LogError(ErrorPrefix + " build terminou como " + summary.result +
                       " apos " + seconds.ToString("0", CultureInfo.InvariantCulture) + "s" +
                       " com " + summary.totalErrors + " erro(s).");

        foreach (var message in CollectErrors(report))
        {
            Debug.LogError(ErrorPrefix + " " + message);
        }

        return 1;
    }

    /// <summary>
    /// Transcreve os erros do BuildReport marcados, para que o pipeline os
    /// promova ao topo do resumo em vez de deixar o usuario procurar.
    /// </summary>
    private static IEnumerable<string> CollectErrors(BuildReport report)
    {
        var seen = new HashSet<string>();
        var count = 0;

        foreach (var step in report.steps)
        {
            foreach (var message in step.messages)
            {
                if (message.type != LogType.Error &&
                    message.type != LogType.Exception &&
                    message.type != LogType.Assert)
                {
                    continue;
                }

                var text = (message.content ?? string.Empty).Trim();
                if (text.Length == 0 || !seen.Add(text)) continue;

                yield return FirstLine(text);

                if (++count >= MaxReportedErrors)
                {
                    yield return "(demais erros omitidos; ver o log completo)";
                    yield break;
                }
            }
        }
    }

    // ------------------------------------------------------- Player Settings

    /// <summary>
    /// Inspecao somente leitura da compressao WebGL.
    ///
    /// Brotli ou Gzip sem decompressionFallback gera um build que so funciona
    /// atras de um servidor que envie Content-Encoding: br. Como a distribuicao
    /// aqui e por pasta, e a pessoa vai abrir com um servidor estatico qualquer,
    /// o sintoma seria tela preta sem mensagem de erro.
    ///
    /// O pipeline avisa e segue em frente. Nao corrige e nao aborta: a decisao
    /// continua sendo de quem cuida do projeto Unity.
    /// </summary>
    private static void InspectWebGlCompression(BuildTarget target)
    {
        if (target != BuildTarget.WebGL) return;

        var format = PlayerSettings.WebGL.compressionFormat;
        var fallback = PlayerSettings.WebGL.decompressionFallback;

        Debug.Log(InfoPrefix + " compressao WebGL: " + format + ", decompressionFallback " + (fallback ? "ligado" : "desligado") + ".");

        var compressed = format == WebGLCompressionFormat.Brotli || format == WebGLCompressionFormat.Gzip;
        if (!compressed || fallback) return;

        Debug.LogWarning(WarningPrefix + " compressao " + format + " com decompressionFallback desligado. " +
                         "Os arquivos .wasm e .data continuam comprimidos depois de o zip ser extraido, e so carregam " +
                         "se o servidor enviar Content-Encoding. Como a distribuicao e por pasta, o sintoma sera tela preta " +
                         "sem erro. Corrija no projeto Unity: desligue a compressao, ou ligue decompressionFallback.");
    }

    /// <summary>
    /// Carimbo de versao, DESLIGADO por padrao.
    ///
    /// A secao 6 da especificacao pede que a bundleVersion venha do sha e do
    /// numero da build; a regra 6b diz que o Builder le e nunca escreve Player
    /// Settings. Como bundleVersion e um Player Setting, as duas se contradizem.
    /// O padrao respeita a regra; quem quiser o carimbo passa -ciStampVersion true
    /// em Unity.ExtraArgs.
    ///
    /// A escrita suja o ProjectSettings.asset do workspace, mas o passo de Sync
    /// faz reset --hard antes de toda build, entao ela nunca se acumula.
    /// </summary>
    private static bool ShouldStampVersion(IDictionary<string, string> args)
    {
        var value = Get(args, "ciStampVersion", "false");
        return value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1";
    }

    private static void StampBundleVersion(string buildNumber, string commitSha)
    {
        var previous = PlayerSettings.bundleVersion;
        var stamped = previous + "+" + buildNumber + "." + Short(commitSha);

        PlayerSettings.bundleVersion = stamped;

        Debug.LogWarning(WarningPrefix + " bundleVersion alterada de '" + previous + "' para '" + stamped +
                         "' porque -ciStampVersion esta ligado. Esta e a unica escrita em Player Settings que o " +
                         "Builder faz, e o proximo Sync a reverte.");
    }

    // -------------------------------------------------------------- utilidades

    private static string[] EnabledScenes()
    {
        return EditorBuildSettings.scenes
            .Where(scene => scene.enabled && !string.IsNullOrEmpty(scene.path))
            .Select(scene => scene.path)
            .ToArray();
    }

    /// <summary>
    /// Saida sempre limpa. O pipeline ja apaga a pasta antes de invocar o Unity,
    /// mas quem rodar o Builder direto do Editor nao tem essa garantia, e restos
    /// de uma build anterior confundem o empacotamento.
    /// </summary>
    private static void PrepareOutputFolder(string outputPath)
    {
        var folder = Path.HasExtension(outputPath) ? Path.GetDirectoryName(outputPath) : outputPath;
        if (string.IsNullOrEmpty(folder)) return;

        if (!Directory.Exists(folder))
        {
            Directory.CreateDirectory(folder);
        }
    }

    /// <summary>
    /// Le os argumentos no formato '-nome valor'. Um '-nome' seguido de outro
    /// '-nome' e tratado como flag com valor 'true'.
    /// </summary>
    private static IDictionary<string, string> ParseArguments(string[] commandLine)
    {
        var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < commandLine.Length; i++)
        {
            var current = commandLine[i];
            if (string.IsNullOrEmpty(current) || current[0] != '-') continue;

            var name = current.Substring(1);
            if (name.Length == 0) continue;

            var hasValue = i + 1 < commandLine.Length &&
                           !string.IsNullOrEmpty(commandLine[i + 1]) &&
                           commandLine[i + 1][0] != '-';

            parsed[name] = hasValue ? commandLine[++i] : "true";
        }

        return parsed;
    }

    private static string Get(IDictionary<string, string> args, string name, string fallback)
    {
        string value;
        return args.TryGetValue(name, out value) && !string.IsNullOrEmpty(value) ? value : fallback;
    }

    private static string Short(string sha)
    {
        if (string.IsNullOrEmpty(sha)) return "(sem sha)";
        return sha.Length >= 7 ? sha.Substring(0, 7) : sha;
    }

    private static string FirstLine(string text)
    {
        var index = text.IndexOf('\n');
        var line = index >= 0 ? text.Substring(0, index) : text;
        return line.TrimEnd('\r');
    }

    private static string FormatSize(long bytes)
    {
        const double megabyte = 1024d * 1024d;
        return bytes >= megabyte
            ? (bytes / megabyte).ToString("0.0", CultureInfo.InvariantCulture) + " MB"
            : (bytes / 1024d).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
    }
}
#endif
