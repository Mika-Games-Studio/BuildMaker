namespace UnityLocalCI.Core.Configuration;

/// <summary>Raiz do appsettings.json. Ver secao 3 da especificacao.</summary>
public sealed class CiOptions
{
    public const string SectionName = "";

    public SchedulerOptions Scheduler { get; set; } = new();
    public ProjectDefaults Defaults { get; set; } = new();

    /// <summary>
    /// Preenchida a partir dos arquivos da pasta 'projetos', um por projeto.
    /// Nao e mais lida do appsettings.json.
    /// </summary>
    public List<ProjectOptions> Projects { get; set; } = new();

    public NotificationOptions Notifications { get; set; } = new();
    public StateOptions State { get; set; } = new();
    public GitHubOptions GitHub { get; set; } = new();
}

/// <summary>Dados do OAuth App usado para conectar ao GitHub pela janela.</summary>
public sealed class GitHubOptions
{
    /// <summary>
    /// Client ID do OAuth App. E publico — o fluxo de dispositivo nao usa client
    /// secret, e e por isso que ele serve a um programa instalado. Vazio faz a
    /// janela oferecer a conexao pela sessao do GitHub CLI.
    /// </summary>
    public string? ClientId { get; set; }
}

public sealed class SchedulerOptions
{
    /// <summary>Teto global de builds simultaneas. O valor efetivo e o menor entre este e RAM_instalada_GB / 16.</summary>
    public int MaxConcurrentBuilds { get; set; } = 2;

    /// <summary>RAM fisica livre minima para uma build tomar vaga no semaforo. Abaixo disso o job e adiado, nunca descartado.</summary>
    public int MinFreeRamGb { get; set; } = 12;

    public string? GlobalStatusFile { get; set; }

    /// <summary>Intervalo de reavaliacao quando um job esta adiado por falta de recurso.</summary>
    public int ResourceRecheckSeconds { get; set; } = 30;

    /// <summary>De quanto em quanto tempo tentar reenviar artefatos com copia pendente.</summary>
    public int PendingCopyRetryMinutes { get; set; } = 10;
}

public sealed class StateOptions
{
    public string DatabasePath { get; set; } = @"C:\ci\state\unitylocalci.db";
    public string LogFolder { get; set; } = @"C:\ci\logs";
}

public sealed class NotificationOptions
{
    /// <summary>Nome da credencial no Credential Manager, nunca o valor. Fase 3.</summary>
    public string? TeamsWebhookCredentialName { get; set; }
}

/// <summary>Valores comuns a todos os projetos; cada projeto sobrescreve apenas o que difere.</summary>
public sealed class ProjectDefaults
{
    /// <summary>
    /// O acesso ao Git e da maquina, nao de cada jogo: quem conecta uma vez
    /// conecta para todos. Um projeto so precisa da propria credencial quando
    /// vive em outra organizacao ou outra conta.
    /// </summary>
    public RepositoryDefaults Repository { get; set; } = new();

    public WatcherOptions Watcher { get; set; } = new();
    public UnityOptions Unity { get; set; } = new();
    public PackagingOptions Packaging { get; set; } = new();
    public PublishingOptions Publishing { get; set; } = new();
    public RetentionOptions Retention { get; set; } = new();
}

public sealed class ProjectOptions
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public RepositoryOptions Repository { get; set; } = new();
    public string? ManualTriggerFile { get; set; }

    // Sobrescritas parciais: nulo significa "herda de Defaults".
    public WatcherOptions? Watcher { get; set; }
    public UnityOptions? Unity { get; set; }
    public PackagingOptions? Packaging { get; set; }
    public PublishingOptions? Publishing { get; set; }
    public RetentionOptions? Retention { get; set; }
}

public sealed class RepositoryOptions
{
    public string Url { get; set; } = "";
    public string Branch { get; set; } = "HML";
    public string WorkspacePath { get; set; } = "";

    /// <summary>
    /// Nome da credencial generica no Windows Credential Manager. Nunca o PAT em
    /// si. Nulo herda a conexao da maquina, em Defaults.Repository.
    /// </summary>
    public string? PatCredentialName { get; set; }
}

/// <summary>A conexao com o Git que vale para a maquina inteira.</summary>
public sealed class RepositoryDefaults
{
    /// <summary>
    /// Nome da credencial usada por todo projeto que nao definir a propria. E o
    /// que o botao "Conectar ao GitHub" preenche.
    /// </summary>
    public string? PatCredentialName { get; set; }
}

public sealed class WatcherOptions
{
    public int? PollIntervalSeconds { get; set; }
    public int? DebounceSeconds { get; set; }
    public int? HookSignalPort { get; set; }
}

public sealed class UnityOptions
{
    public string? EditorVersion { get; set; }
    public string? BuildTarget { get; set; }
    public string? ExecuteMethod { get; set; }
    public int? TimeoutMinutes { get; set; }
    public string[]? ExtraArgs { get; set; }
}

public sealed class PackagingOptions
{
    public string? NamePattern { get; set; }
    public bool? IncludeLauncher { get; set; }
}

public sealed class PublishingOptions
{
    public string? StagingFolder { get; set; }
    public string? ArtifactFolder { get; set; }
    public bool? MaintainLatestFolder { get; set; }
    public bool? WriteStatusFiles { get; set; }
}

public sealed class RetentionOptions
{
    public int? KeepLastBuilds { get; set; }
    public int? MinFreeDiskGb { get; set; }
}
