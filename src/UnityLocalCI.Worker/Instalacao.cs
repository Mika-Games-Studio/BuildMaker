using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace UnityLocalCI.App;

/// <summary>
/// O programa instala a si mesmo.
///
/// POR QUE DENTRO DO EXECUTAVEL, E NAO NUM INSTALADOR SEPARADO
///
/// O que se distribui e um arquivo so. Quem baixa abre o executavel, ele roda —
/// de verdade, nao uma tela de instalacao — e oferece um botao "Instalar" na
/// janela. Depois de instalado o botao some, porque nao ha mais o que instalar.
///
/// E o formato do RustDesk, e a razao de ele funcionar e que nao ha nada a
/// desempacotar: o executavel ja e self-contained, com o runtime .NET dentro.
/// Instalar e copiar um arquivo para o lugar certo e anotar quatro coisas no
/// registro. Um NSIS ou um MSI ao redor disso seria uma segunda ferramenta, com
/// sua propria linguagem e seus proprios modos de falhar, para fazer um
/// Copy e quatro RegSetValue.
///
/// O QUE INSTALAR SIGNIFICA AQUI
///
///   %LOCALAPPDATA%\Programs\BuildMaker\UnityLocalCI.exe   o programa
///   atalhos no menu Iniciar e na area de trabalho
///   HKCU\...\Uninstall\BuildMaker                          Aplicativos Instalados
///   HKCU\...\CurrentVersion\Run                            inicio automatico
///
/// Nada disto pede administrador: e instalacao por usuario, no perfil do
/// usuario. Os dados ficam noutro lugar de proposito (%LOCALAPPDATA%\BuildMaker,
/// ver AppPaths): desinstalar apaga o programa e nao toca no historico.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class Instalacao
{
    /// <summary>
    /// O nome do arquivo nao muda com a marca.
    ///
    /// O que o Windows mostra vem da informacao de versao do binario, que diz
    /// BuildMaker. O nome do arquivo, o do servico e o da credencial no cofre
    /// sao identidade: renomear qualquer um quebra as instalacoes que ja
    /// existem.
    /// </summary>
    private const string NomeExe = "UnityLocalCI.exe";

    private const string ChaveRun = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValorRun = "BuildMaker";
    private const string ValorRunAntigo = "UnityLocalCI";

    private const string ChaveDesinstalar =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\BuildMaker";

    private const string NomeAtalho = "BuildMaker.lnk";
    private const string NomeAtalhoAntigo = "UnityLocalCI.lnk";

    /// <summary>%LOCALAPPDATA%\Programs\BuildMaker.</summary>
    public static string PastaDestino => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "BuildMaker");

    /// <summary>Onde as versoes ate a 1.0 instalavam.</summary>
    private static string PastaAntiga => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnityLocalCI");

    public static string ExeInstalado => Path.Combine(PastaDestino, NomeExe);

    /// <summary>
    /// O caminho do executavel que esta rodando agora.
    ///
    /// Environment.ProcessPath, e nao Assembly.Location: num executavel de
    /// arquivo unico — que e o formato que se distribui — o Location devolve
    /// string vazia, porque nao ha assembly em disco para apontar. A instalacao
    /// inteira depende deste valor, entao a alternativa e a pasta do processo, e
    /// nao vazio.
    /// </summary>
    public static string ExeAtual =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, NomeExe);

    /// <summary>
    /// Verdadeiro quando este processo E a copia instalada.
    ///
    /// A comparacao e pelo caminho, e nao pela existencia do arquivo no destino:
    /// se alguem baixou o executavel de novo e abriu da pasta Downloads, ele
    /// precisa oferecer "Instalar" para substituir a copia antiga — e e isso que
    /// atualizar significa neste formato.
    /// </summary>
    public static bool EstaInstalado =>
        string.Equals(
            Path.GetFullPath(ExeAtual).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(ExeInstalado).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Verdadeiro quando ja existe uma copia instalada em outro lugar — o que
    /// muda o rotulo do botao de "Instalar" para "Atualizar".
    /// </summary>
    public static bool JaExisteInstalacao => File.Exists(ExeInstalado);

    // ------------------------------------------------------------- instalar

    /// <param name="anotar">
    /// Recebe cada passo. A janela usa para mostrar o progresso; o modo
    /// silencioso joga no console.
    /// </param>
    public static void Instalar(Action<string> anotar, bool inicioAutomatico = true)
    {
        var origem = ExeAtual;
        if (EstaInstalado) throw new InvalidOperationException("Ja instalado.");

        Directory.CreateDirectory(PastaDestino);

        EncerrarInstanciaAnterior(anotar);
        MigrarDaPastaAntiga(anotar);

        File.Copy(origem, ExeInstalado, overwrite: true);
        anotar($"programa copiado para {PastaDestino}");

        EscreverConfiguracaoInicial(anotar);
        CriarAtalhos(anotar);
        RegistrarEmAplicativosInstalados(anotar);

        if (inicioAutomatico) LigarInicioAutomatico(anotar);
    }

    /// <summary>
    /// A copia instalada pode estar rodando — e um "Atualizar" e exatamente esse
    /// caso. Sem encerra-la, o File.Copy bate em arquivo em uso.
    /// </summary>
    private static void EncerrarInstanciaAnterior(Action<string> anotar)
    {
        if (!File.Exists(ExeInstalado)) return;

        foreach (var processo in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(NomeExe)))
        {
            using (processo)
            {
                if (processo.Id == Environment.ProcessId) continue;

                try
                {
                    // O caminho pode nao ser legivel (processo de outro usuario);
                    // nesse caso nao e nossa copia e nao ha o que encerrar.
                    if (!string.Equals(processo.MainModule?.FileName, ExeInstalado,
                            StringComparison.OrdinalIgnoreCase))
                        continue;
                }
                catch (Exception) { continue; }

                anotar("encerrando a versao que estava rodando");

                // Fechar a janela primeiro: o encerramento limpo espera a build
                // corrente e fecha o banco. Matar direto deixaria o SQLite com
                // journal aberto.
                try
                {
                    processo.CloseMainWindow();
                    if (!processo.WaitForExit(5000)) processo.Kill(entireProcessTree: true);
                    processo.WaitForExit(5000);
                }
                catch (Exception) { /* ja saiu */ }
            }
        }
    }

    /// <summary>
    /// A configuracao e os projetos da instalacao anterior vem junto; o resto
    /// daquela pasta e programa, e o programa esta sendo reinstalado.
    /// </summary>
    private static void MigrarDaPastaAntiga(Action<string> anotar)
    {
        if (!Directory.Exists(PastaAntiga)) return;

        var configAntigo = Path.Combine(PastaAntiga, "appsettings.json");
        var configNovo = Path.Combine(PastaDestino, "appsettings.json");
        if (File.Exists(configAntigo) && !File.Exists(configNovo))
        {
            File.Copy(configAntigo, configNovo);
            anotar("configuracao trazida da instalacao anterior");
        }

        var projetosAntigos = Path.Combine(PastaAntiga, "projetos");
        var projetosNovos = Path.Combine(PastaDestino, "projetos");
        if (Directory.Exists(projetosAntigos) && !Directory.Exists(projetosNovos))
        {
            Directory.CreateDirectory(projetosNovos);
            foreach (var arquivo in Directory.GetFiles(projetosAntigos, "*.json"))
                File.Copy(arquivo, Path.Combine(projetosNovos, Path.GetFileName(arquivo)));

            anotar($"{Directory.GetFiles(projetosNovos, "*.json").Length} projeto(s) trazidos");
        }
    }

    /// <summary>
    /// So quando nao existe: uma atualizacao nunca sobrescreve a configuracao de
    /// quem ja usa o programa.
    /// </summary>
    private static void EscreverConfiguracaoInicial(Action<string> anotar)
    {
        var destino = Path.Combine(PastaDestino, "appsettings.json");
        Directory.CreateDirectory(Path.Combine(PastaDestino, "projetos"));

        if (File.Exists(destino))
        {
            anotar("configuracao existente preservada");
            return;
        }

        var modelo = LerRecurso("UnityLocalCI.appsettings.json");
        if (modelo is null) return;

        File.WriteAllText(destino, modelo, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        anotar("configuracao inicial criada");
    }

    private static string? LerRecurso(string nome)
    {
        using var fluxo = Assembly.GetExecutingAssembly().GetManifestResourceStream(nome);
        if (fluxo is null) return null;

        using var leitor = new StreamReader(fluxo, Encoding.UTF8);
        return leitor.ReadToEnd();
    }

    // -------------------------------------------------------------- atalhos

    private static void CriarAtalhos(Action<string> anotar)
    {
        foreach (var pasta in PastasDeAtalho())
        {
            // O atalho com o nome antigo sai: senao ficariam dois apontando para
            // o mesmo programa, e a busca do Windows mostraria os dois.
            var antigo = Path.Combine(pasta, NomeAtalhoAntigo);
            if (File.Exists(antigo)) File.Delete(antigo);

            if (CriarAtalho(Path.Combine(pasta, NomeAtalho)))
                anotar($"atalho criado em {Path.GetFileName(pasta)}");
            else
                anotar($"[!] nao foi possivel criar o atalho em {pasta}");
        }
    }

    private static IEnumerable<string> PastasDeAtalho()
    {
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Windows\Start Menu\Programs");

        yield return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    }

    /// <summary>
    /// WScript.Shell por COM tardio.
    ///
    /// Um .lnk e um formato binario com um COM por tras (IShellLink), e nao ha
    /// API gerenciada para ele. Declarar a interface a mao seria umas 60 linhas
    /// de atributos de interop para chamar dois metodos; o ProgID resolve em
    /// seis. O custo e que os erros so aparecem em tempo de execucao — por isso
    /// o try, e por isso a funcao devolve se deu certo em vez de deixar a
    /// excecao derrubar a instalacao inteira por causa de um atalho.
    /// </summary>
    private static bool CriarAtalho(string caminho)
    {
        var tipo = Type.GetTypeFromProgID("WScript.Shell");
        if (tipo is null) return false;

        object? shell = null;
        try
        {
            shell = Activator.CreateInstance(tipo);
            if (shell is null) return false;

            dynamic atalho = tipo.InvokeMember("CreateShortcut",
                System.Reflection.BindingFlags.InvokeMethod, null, shell, [caminho])!;

            atalho.TargetPath = ExeInstalado;
            atalho.WorkingDirectory = PastaDestino;
            atalho.IconLocation = ExeInstalado;
            atalho.Description = "CI local para projetos Unity";
            atalho.Save();

            return File.Exists(caminho);
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            if (shell is not null) System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
        }
    }

    // ------------------------------------------------- Aplicativos Instalados

    /// <summary>
    /// Sem esta chave o programa nao aparece em Configuracoes &gt; Aplicativos,
    /// nao e achado pela busca do Windows e nao tem como ser desinstalado.
    ///
    /// Em HKCU porque a instalacao e por usuario: registrar em HKLM anunciaria
    /// para todos os usuarios da maquina um programa que so existe para um.
    /// </summary>
    private static void RegistrarEmAplicativosInstalados(Action<string> anotar)
    {
        using var chave = Registry.CurrentUser.CreateSubKey(ChaveDesinstalar);

        var tamanhoKb = (int)(new DirectoryInfo(PastaDestino)
            .EnumerateFiles("*", SearchOption.AllDirectories)
            .Sum(f => f.Length) / 1024);

        var versao = FileVersionInfo.GetVersionInfo(ExeInstalado).FileVersion ?? "1.0.0";

        chave.SetValue("DisplayName", AppNames.Display);
        chave.SetValue("DisplayVersion", versao);
        chave.SetValue("Publisher", "BSA Tech");
        chave.SetValue("DisplayIcon", ExeInstalado);
        chave.SetValue("InstallLocation", PastaDestino);

        // O proprio executavel desinstala. Antes isto apontava para um
        // powershell.exe rodando o instalar.ps1 copiado para dentro da pasta —
        // o que dependia de o script continuar la, e da politica de execucao do
        // PowerShell permitir roda-lo.
        chave.SetValue("UninstallString", $"\"{ExeInstalado}\" --desinstalar");
        chave.SetValue("QuietUninstallString", $"\"{ExeInstalado}\" --desinstalar --silencioso");

        chave.SetValue("EstimatedSize", tamanhoKb, RegistryValueKind.DWord);
        chave.SetValue("NoModify", 1, RegistryValueKind.DWord);
        chave.SetValue("NoRepair", 1, RegistryValueKind.DWord);

        anotar($"registrado como '{AppNames.Display}' em Aplicativos Instalados");
    }

    /// <summary>
    /// A chave Run, e nao uma tarefa agendada: e ela que aparece na aba
    /// Inicializar do Gerenciador de Tarefas, onde se desliga o inicio
    /// automatico com um clique.
    /// </summary>
    private static void LigarInicioAutomatico(Action<string> anotar)
    {
        using var chave = Registry.CurrentUser.CreateSubKey(ChaveRun);

        // O nome antigo sai junto: com os dois, a aba Inicializar mostraria duas
        // entradas para o mesmo programa.
        chave.DeleteValue(ValorRunAntigo, throwOnMissingValue: false);

        // Aspas: o caminho passa por %LOCALAPPDATA%, que costuma ter espaco no
        // nome do usuario, e sem elas a shell corta no primeiro espaco.
        chave.SetValue(ValorRun, $"\"{ExeInstalado}\"");

        anotar("inicio automatico ligado");
    }

    // ---------------------------------------------------------- desinstalar

    public static void Desinstalar(Action<string> anotar)
    {
        using (var run = Registry.CurrentUser.OpenSubKey(ChaveRun, writable: true))
        {
            run?.DeleteValue(ValorRun, throwOnMissingValue: false);
            run?.DeleteValue(ValorRunAntigo, throwOnMissingValue: false);
        }
        anotar("inicio automatico removido");

        Registry.CurrentUser.DeleteSubKeyTree(ChaveDesinstalar, throwOnMissingSubKey: false);
        anotar("registro em Aplicativos Instalados removido");

        foreach (var pasta in PastasDeAtalho())
        {
            foreach (var nome in new[] { NomeAtalho, NomeAtalhoAntigo })
            {
                var lnk = Path.Combine(pasta, nome);
                if (File.Exists(lnk)) { File.Delete(lnk); anotar($"atalho removido de {Path.GetFileName(pasta)}"); }
            }
        }

        // A pasta do programa some; os dados em %LOCALAPPDATA%\BuildMaker ficam.
        // Desinstalar nao e apagar o historico de builds de ninguem.
        AgendarRemocaoDaPasta(anotar);
    }

    /// <summary>
    /// Um processo nao consegue apagar o proprio executavel enquanto roda.
    ///
    /// Entao quem apaga e um cmd.exe que sobrevive a este processo: ele espera o
    /// executavel ficar livre, remove a pasta e se encerra. O laco tem teto — se
    /// em trinta tentativas o arquivo continuar preso, ele desiste em vez de
    /// virar um processo eterno.
    /// </summary>
    private static void AgendarRemocaoDaPasta(Action<string> anotar)
    {
        if (!Directory.Exists(PastaDestino)) return;

        var comando =
            $"for /l %i in (1,1,30) do (timeout /t 1 /nobreak >nul & " +
            $"rmdir /s /q \"{PastaDestino}\" 2>nul & " +
            $"if not exist \"{PastaDestino}\" exit)";

        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c {comando}")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            });

            anotar("arquivos do programa serao removidos em instantes");
        }
        catch (Exception excecao)
        {
            anotar($"[!] nao foi possivel remover {PastaDestino}: {excecao.Message}");
        }
    }

    /// <summary>
    /// Abre a copia instalada e devolve se conseguiu.
    ///
    /// Quem inicia e o Explorer (UseShellExecute), e nao este processo. Nao e
    /// detalhe: um executavel iniciado por outro processo e um padrao que o
    /// Behavior Monitoring do antivirus corporativo registra; iniciado pela
    /// shell, e como qualquer programa de area de trabalho comeca.
    /// </summary>
    public static bool AbrirCopiaInstalada()
    {
        try
        {
            Process.Start(new ProcessStartInfo(ExeInstalado)
            {
                UseShellExecute = true,
                WorkingDirectory = PastaDestino,
            });

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
