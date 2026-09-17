using System.Runtime.Versioning;
using Microsoft.Win32;

namespace UnityLocalCI.App;

/// <summary>
/// Inicio automatico com o Windows, pela chave Run do usuario.
///
/// HKCU e nao HKLM: nao pede administrador e vale so para quem instalou, que e
/// o comportamento certo para um app que roda na sessao da pessoa. Para o CI
/// rodar sem ninguem logado, o caminho e o Windows Service.
/// </summary>
[SupportedOSPlatform("windows")]
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "UnityLocalCI";

    /// <summary>
    /// Caminho do executavel para o registro. Rodando por 'dotnet run' nao ha
    /// executavel proprio, e ai nao ha o que registrar.
    /// </summary>
    public static string? ExecutablePath
    {
        get
        {
            var path = Environment.ProcessPath;

            if (string.IsNullOrEmpty(path)) return null;
            if (Path.GetFileName(path).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)) return null;

            return path;
        }
    }

    public static bool IsSupported => ExecutablePath is not null;

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    /// <summary>Liga ou desliga. Retorna a mensagem de erro, ou nulo em caso de sucesso.</summary>
    public static string? Set(bool enabled)
    {
        var executable = ExecutablePath;
        if (executable is null)
            return "o inicio automatico so funciona no executavel instalado, nao em 'dotnet run'";

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null) return "nao foi possivel abrir a chave Run do usuario";

            if (enabled)
            {
                // Aspas: o caminho tipico tem espacos em "Program Files" ou no
                // nome do usuario, e sem elas a shell corta no primeiro espaco.
                key.SetValue(ValueName, $"\"{executable}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return exception.Message;
        }
    }
}
