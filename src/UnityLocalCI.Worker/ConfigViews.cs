using System.ComponentModel;
using System.Drawing.Design;
using UnityLocalCI.Core.Configuration;
using UnityLocalCI.Core.Unity;

namespace UnityLocalCI.App;

/// <summary>
/// Normalizacao do que e digitado nas telas de configuracao.
///
/// Espaco sobrando e invisivel e quebra tudo em silencio: um nome de credencial
/// colado com espaco no fim nao e encontrado no cofre, e a mensagem resultante
/// parece uma mentira — ela diz que a credencial nao existe, com o nome certo na
/// tela.
/// </summary>
internal static class Texto
{
    public static string Limpo(string? value) => value?.Trim() ?? "";

    public static string? OuNulo(string? value)
    {
        var texto = value?.Trim();
        return string.IsNullOrEmpty(texto) ? null : texto;
    }
}

/// <summary>
/// A aba Geral: fila, estado e os valores que todos os projetos herdam.
///
/// Existe por dois motivos. O primeiro e que os caminhos ganham caixa de
/// selecao de pasta — caminho digitado a mao e o tipo de erro que so aparece na
/// hora do build. O segundo e que o PropertyGrid mostra a descricao de cada
/// campo no rodape, e as classes de configuracao do Core nao podem carregar
/// atributos do WinForms.
/// </summary>
public sealed class GeneralView(CiOptions options)
{
    private readonly SchedulerOptions _scheduler = options.Scheduler;
    private readonly StateOptions _state = options.State;
    private readonly UnityOptions _unity = options.Defaults.Unity;
    private readonly PublishingOptions _publishing = options.Defaults.Publishing;

    // ------------------------------------------------------------------ fila

    [Category("Fila")]
    [Description("Teto de builds simultâneas. O valor que vale é o menor entre este e RAM instalada / 16 GB.")]
    public int MaxConcurrentBuilds
    {
        get => _scheduler.MaxConcurrentBuilds;
        set => _scheduler.MaxConcurrentBuilds = value;
    }

    [Category("Fila")]
    [Description("RAM livre mínima para uma build começar. Abaixo disso o job é adiado, nunca descartado.")]
    public int MinFreeRamGb
    {
        get => _scheduler.MinFreeRamGb;
        set => _scheduler.MinFreeRamGb = value;
    }

    [Category("Fila")]
    [Description("De quanto em quanto tempo reavaliar um job que está adiado por falta de recurso.")]
    public int ResourceRecheckSeconds
    {
        get => _scheduler.ResourceRecheckSeconds;
        set => _scheduler.ResourceRecheckSeconds = value;
    }

    [Category("Fila")]
    [Description("De quanto em quanto tempo tentar reenviar artefatos que não puderam ser copiados.")]
    public int PendingCopyRetryMinutes
    {
        get => _scheduler.PendingCopyRetryMinutes;
        set => _scheduler.PendingCopyRetryMinutes = value;
    }

    // --------------------------------------------------------------- arquivos

    [Category("Arquivos de status")]
    [Description("Arquivo único com uma linha por projeto. É o que responde \"a build saiu?\" sem abrir nada.")]
    [Editor(typeof(FilePathEditor), typeof(UITypeEditor))]
    public string? GlobalStatusFile
    {
        get => _scheduler.GlobalStatusFile;
        set => _scheduler.GlobalStatusFile = Texto.OuNulo(value);
    }

    [Category("Estado e logs")]
    [Description("Banco SQLite com o histórico das builds. Apagá-lo perde o histórico, não as builds.")]
    [Editor(typeof(FilePathEditor), typeof(UITypeEditor))]
    public string DatabasePath
    {
        get => _state.DatabasePath;
        set => _state.DatabasePath = Texto.Limpo(value);
    }

    [Category("Estado e logs")]
    [Description("Onde ficam os logs completos de cada build.")]
    [Editor(typeof(FolderPathEditor), typeof(UITypeEditor))]
    public string LogFolder
    {
        get => _state.LogFolder;
        set => _state.LogFolder = Texto.Limpo(value);
    }

    // ------------------------------------------------------ padroes herdados

    [Category("Padrões dos projetos")]
    [Description("Pasta de trabalho onde o zip é montado antes de ser copiado para o destino.")]
    [Editor(typeof(FolderPathEditor), typeof(UITypeEditor))]
    public string? StagingFolder
    {
        get => _publishing.StagingFolder;
        set => _publishing.StagingFolder = Texto.OuNulo(value);
    }

    [Category("Padrões dos projetos")]
    [Description("Versão do editor usada por quem não definir a própria. Cada projeto costuma ter a sua.")]
    public string? EditorVersion
    {
        get => _unity.EditorVersion;
        set => _unity.EditorVersion = Texto.OuNulo(value);
    }

    [Category("Padrões dos projetos")]
    [Description("Plataforma do build. WebGL é o padrão.")]
    public string? BuildTarget
    {
        get => _unity.BuildTarget;
        set => _unity.BuildTarget = Texto.OuNulo(value);
    }
}

/// <summary>
/// Oferece as branches do repositorio como lista no campo Branch.
///
/// Nao exclusiva de proposito: da para digitar uma branch que ainda nao existe
/// no servidor — e comum cadastrar o projeto antes de criar a branch de
/// homologacao. A lista evita o erro de digitacao; ela nao manda em quem sabe o
/// que esta fazendo.
/// </summary>
public sealed class BranchConverter : StringConverter
{
    public override bool GetStandardValuesSupported(ITypeDescriptorContext? context) => true;

    public override bool GetStandardValuesExclusive(ITypeDescriptorContext? context) => false;

    public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? context)
        => new((context?.Instance as ProjectView)?.KnownBranches.ToArray() ?? []);
}

/// <summary>
/// Achata o projeto para o PropertyGrid: sem isto, Repository, Unity e
/// Publishing apareceriam como sub-objetos que o usuario precisa expandir um a
/// um, e os campos herdados de Defaults nao teriam explicacao nenhuma.
/// </summary>
public sealed class ProjectView
{
    private readonly ProjectOptions _project;
    private readonly RepositoryOptions _repository;
    private readonly PublishingOptions _publishing;

    private readonly BranchCatalog? _branches;

    private string? _detectedFor;
    private string? _detected;

    public ProjectView(ProjectOptions project) : this(project, null) { }

    public ProjectView(ProjectOptions project, BranchCatalog? branches)
    {
        _project = project;
        _branches = branches;

        // Guardados em campos nao-anulaveis: as sobrescritas por projeto sao
        // opcionais na configuracao, mas a tela sempre tem onde escrever.
        _repository = _project.Repository ??= new RepositoryOptions();
        _publishing = _project.Publishing ??= new PublishingOptions();

        // Se a versao ainda nao foi preenchida e o clone ja existe, ela vem do
        // proprio projeto, que e quem sabe.
        if (string.IsNullOrWhiteSpace(_project.Unity?.EditorVersion))
        {
            var detectada = Detect(_repository.WorkspacePath);
            if (detectada is not null) EditorVersion = detectada;
        }
    }

    /// <summary>
    /// Le a versao do disco no maximo uma vez por caminho, e com prazo.
    ///
    /// O PropertyGrid chama os getters a cada repintura: sem esta memoria, a
    /// linha "EditorVersion no disco" faria uma leitura de arquivo por quadro —
    /// imperceptivel num SSD local, e um congelamento a cada repintura quando o
    /// workspace estiver num caminho de rede fora do ar.
    /// </summary>
    private string? Detect(string? path)
    {
        if (path == _detectedFor) return _detected;

        _detectedFor = path;
        _detected = BoundedIo.Run(() => UnityProjectVersion.Read(path));

        return _detected;
    }

    [Category("Projeto")]
    [Description("Identifica a fila, o estado e aparece nos arquivos de status. Precisa ser único.")]
    public string Name
    {
        get => _project.Name;
        set => _project.Name = Texto.Limpo(value);
    }

    [Category("Projeto")]
    [Description("Desligado, o projeto fica na configuração mas não é observado nem construído.")]
    public bool Enabled
    {
        get => _project.Enabled;
        set => _project.Enabled = value;
    }

    [Category("Repositório")]
    [Description("URL do repositório remoto. O PAT nunca entra aqui.")]
    public string Url
    {
        get => _repository.Url;
        set => _repository.Url = Texto.Limpo(value);
    }

    [Category("Repositório")]
    [Description(
        "Branch observada. A build dispara quando o HEAD dela muda. A lista vem do repositório — do clone " +
        "local na hora, e do servidor assim que ele responder. Dá para digitar uma que ainda não existe.")]
    [TypeConverter(typeof(BranchConverter))]
    public string Branch
    {
        get => _repository.Branch;
        set => _repository.Branch = Texto.Limpo(value);
    }

    /// <summary>
    /// O que o dropdown de branches oferece. Fora da grade: e uma lista de
    /// apoio, nao um campo da configuracao.
    /// </summary>
    [Browsable(false)]
    public IReadOnlyList<string> KnownBranches => _branches?.Known(_repository.Url) ?? [];

    [Category("Repositório")]
    [Description(
        "Clone dedicado e permanente DO CI. Dois projetos nunca podem compartilhar o mesmo caminho: o Unity " +
        "tranca a Library do diretório. Não aponte para a sua pasta de trabalho: o CI apaga o que não estiver " +
        "commitado. Ao escolher uma pasta que já tem um projeto Unity, a versão do editor é preenchida sozinha.")]
    [Editor(typeof(FolderPathEditor), typeof(UITypeEditor))]
    public string WorkspacePath
    {
        get => _repository.WorkspacePath;
        set
        {
            _repository.WorkspacePath = Texto.Limpo(value);

            // O projeto no disco vale mais que o que estava digitado: buildar na
            // versao errada produz um artefato que parece certo e nao e.
            var detectada = Detect(_repository.WorkspacePath);
            if (detectada is not null) EditorVersion = detectada;
        }
    }

    [Category("Repositório")]
    [Description("NOME da credencial no Gerenciador de Credenciais do Windows, nunca o PAT. Grave o valor com cmdkey ou tools\\set-secrets.ps1.")]
    public string? PatCredentialName
    {
        get => _repository.PatCredentialName;
        set => _repository.PatCredentialName = Texto.OuNulo(value);
    }

    [Category("Unity")]
    [Description(
        "Versão exata do editor. Vem sozinha do ProjectSettings\\ProjectVersion.txt do projeto quando o " +
        "workspace existe. Vazio herda de Defaults. Nunca é adivinhada.")]
    public string? EditorVersion
    {
        get => _project.Unity?.EditorVersion;
        set
        {
            _project.Unity ??= new UnityOptions();
            _project.Unity.EditorVersion = string.IsNullOrWhiteSpace(value) ? null : value;
        }
    }

    /// <summary>
    /// So leitura: o que o projeto no disco diz neste momento. Serve para
    /// enxergar, sem salvar nada, que a configuracao ficou para tras depois de
    /// o time subir o projeto para outra versao do Unity.
    /// </summary>
    [Category("Unity")]
    [DisplayName("EditorVersion no disco")]
    [Description("O que o ProjectVersion.txt do workspace diz agora. Se divergir do campo acima, a configuração está desatualizada.")]
    [ReadOnly(true)]
    public string DetectedEditorVersion
        => Detect(_repository.WorkspacePath) ?? "(workspace ainda não clonado)";

    [Category("Unity")]
    [Description("Plataforma do build. Vazio herda de Defaults (WebGL).")]
    public string? BuildTarget
    {
        get => _project.Unity?.BuildTarget;
        set
        {
            _project.Unity ??= new UnityOptions();
            _project.Unity.BuildTarget = string.IsNullOrWhiteSpace(value) ? null : value;
        }
    }

    [Category("Publicação")]
    [Description("Pasta onde o time pega o zip. É a interface para quem só quer o artefato.")]
    [Editor(typeof(FolderPathEditor), typeof(UITypeEditor))]
    public string? ArtifactFolder
    {
        get => _publishing.ArtifactFolder;
        set => _publishing.ArtifactFolder = Texto.OuNulo(value);
    }

    [Category("Publicação")]
    [Description("Tocar este arquivo enfileira uma build do HEAD atual. O serviço o apaga ao consumir.")]
    [Editor(typeof(FilePathEditor), typeof(UITypeEditor))]
    public string? ManualTriggerFile
    {
        get => _project.ManualTriggerFile;
        set => _project.ManualTriggerFile = Texto.OuNulo(value);
    }

    public override string ToString() => _project.Name;
}
