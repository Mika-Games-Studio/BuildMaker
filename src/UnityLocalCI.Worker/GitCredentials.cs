using System.Diagnostics;

namespace UnityLocalCI.App;

/// <summary>
/// A conta que o proprio Git desta maquina ja usa.
///
/// E assim que o GitHub Desktop funciona: ele entra pelo navegador uma vez e
/// guarda o token no Gerenciador de Credenciais do Windows, que e de onde o
/// git tira a credencial depois. Quem ja clonou por HTTPS nesta maquina, ou ja
/// usa o GitHub Desktop, tem esse token guardado — e nao ha motivo para pedir
/// outro.
///
/// A pergunta e feita ao proprio git, com 'credential fill', em vez de ler o
/// cofre direto: assim vale para qualquer auxiliar de credencial configurado —
/// o do GitHub Desktop, o Git Credential Manager, ou outro — sem esta classe
/// precisar conhecer nenhum deles.
/// </summary>
internal static class GitCredentials
{
    /// <summary>Token do host, ou nulo quando nao ha nada guardado.</summary>
    public static string? ReadToken(string host = "github.com")
    {
        try
        {
            var inicio = new ProcessStartInfo("git", "credential fill")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            // Sem interacao: o auxiliar de credencial do Windows e um programa
            // com janela propria, e uma janela dele esperando resposta atras da
            // nossa pareceria travamento.
            inicio.Environment["GIT_TERMINAL_PROMPT"] = "0";
            inicio.Environment["GCM_INTERACTIVE"] = "never";

            using var processo = Process.Start(inicio);
            if (processo is null) return null;

            processo.StandardInput.NewLine = "\n";
            processo.StandardInput.WriteLine("protocol=https");
            processo.StandardInput.WriteLine($"host={host}");
            processo.StandardInput.WriteLine();
            processo.StandardInput.Close();

            var saida = processo.StandardOutput.ReadToEnd();

            if (!processo.WaitForExit(10_000))
            {
                try { processo.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return null;
            }

            if (processo.ExitCode != 0) return null;

            foreach (var linha in saida.Split('\n'))
            {
                if (!linha.StartsWith("password=", StringComparison.Ordinal)) continue;

                var token = linha["password=".Length..].Trim();
                return token.Length == 0 ? null : token;
            }

            return null;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // git nao instalado.
            return null;
        }
    }

}
