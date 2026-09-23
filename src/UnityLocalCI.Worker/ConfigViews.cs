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

    [Category("FILA")]
    [Description("Teto de builds simultâneas. O valor que vale é o menor entre este e RAM instalada / 16 GB.")]
    public int MaxConcurrentBuilds
    {
        get => _scheduler.MaxConcurrentBuilds;
        set => _scheduler.MaxConcurrentBuilds = value;
    }

    [Category("FILA")]
    [Description("RAM livre mínima para uma build começar. Abaixo disso o job é adiado, nunca descartado.")]
    public int MinFreeRamGb
    {
        get => _scheduler.MinFreeRamGb;
        set => _scheduler.MinFreeRamGb = value;
    }

    [Category("FILA")]
    [Description("De quanto em quanto tempo reavaliar um job que está adiado por falta de recurso.")]
    public int ResourceRecheckSeconds
    {
        get => _scheduler.ResourceRecheckSeconds;
        set => _scheduler.ResourceRecheckSeconds = value;
    }

    [Category("FILA")]
    [Description("De quanto em quanto tempo tentar reenviar artefatos que não puderam ser copiados.")]
    public int PendingCopyRetryMinutes
    {
        get => _scheduler.PendingCopyRetryMinutes;
        set => _scheduler.PendingCopyRetryMinutes = value;
    }

    // --------------------------------------------------------------- arquivos

    [Category("ESTADO E LOGS")]
    [Description("Banco SQLite com o histórico das builds. Apagá-lo perde o histórico, não as builds.")]
    [Editor(typeof(FilePathEditor), typeof(UITypeEditor))]
    public string DatabasePath
    {
        get => _state.DatabasePath;
        set => _state.DatabasePath = Texto.Limpo(value);
    }

    // ------------------------------------------------------ padroes herdados

    /// <summary>
    /// A pasta de destino, para todos os projetos.
    ///
    /// Era por projeto e virou uma so. Cada zip ja carrega o nome do projeto, a
    /// branch, a data e o commit no proprio nome, entao separar por pasta nao
    /// acrescentava nada — e obrigava a preencher o mesmo caminho a cada projeto
    /// novo, com a chance de um deles ficar apontando para outro lugar sem
    /// ninguem perceber.
    /// </summary>
    [Category("PADRÕES DOS PROJETOS")]
    [Description("Pasta onde o time pega os zips, de todos os projetos. É a interface para quem só quer o artefato.")]
    [Editor(typeof(FolderPathEditor), typeof(UITypeEditor))]
    public string? ArtifactFolder
    {
        get => _publishing.ArtifactFolder;
        set => _publishing.ArtifactFolder = Texto.OuNulo(value);
    }

    [Category("PADRÕES DOS PROJETOS")]
    [Description("Pasta de trabalho onde o zip é montado antes de ser copiado para o destino.")]
    [Editor(typeof(FolderPathEditor), typeof(UITypeEditor))]
    public string? StagingFolder
    {
        get => _publishing.StagingFolder;
        set => _publishing.StagingFolder = Texto.OuNulo(value);
    }

    [Category("PADRÕES DOS PROJETOS")]
    [Description("Versão do editor usada por quem não definir a própria. Cada projeto costuma ter a sua.")]
    public string? EditorVersion
    {
        get => _unity.EditorVersion;
        set => _unity.EditorVersion = Texto.OuNulo(value);
    }

    [Category("PADRÕES DOS PROJETOS")]
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
/// Plataformas do build oferecidas como lista, com WebGL na frente por ser a
/// deste time.
///
/// Nao exclusiva: o Unity tem alguma dezena de alvos, e travar a escolha nos
/// seis daqui impediria de configurar um projeto para um alvo legitimo que nao
/// esta nesta lista. Ela existe para acertar a grafia — 'WebGl' ou 'webgl' nao
/// sao aceitos pelo Unity CLI, e o erro so apareceria na primeira build.
/// </summary>
public sealed class BuildTargetConverter : StringConverter
{
    /// <summary>O primeiro e o padrao do projeto todo.</summary>
    public static readonly string[] Alvos =
    [
        "WebGL",
        "StandaloneWindows64",
        "StandaloneOSX",
        "StandaloneLinux64",
        "Android",
        "iOS",
    ];

    public override bool GetStandardValuesSupported(ITypeDescriptorContext? context) => true;

    public override bool GetStandardValuesExclusive(ITypeDescriptorContext? context) => false;

    public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? context) => new(Alvos);
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
    private readonly ProjectDefaults? _defaults;

    private string? _detectedFor;
    private string? _detected;

    public ProjectView(ProjectOptions project) : this(project, null, null) { }

    public ProjectView(ProjectOptions project, BranchCatalog? branches) : this(project, branches, null) { }

    public ProjectView(ProjectOptions project, BranchCatalog? branches, ProjectDefaults? defaults)
    {
        _project = project;
        _branches = branches;
        _defaults = defaults;

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

    /// <summary>
    /// So leitura, e de proposito.
    ///
    /// O nome nao e um rotulo: ele identifica a fila, da nome ao arquivo de
    /// configuracao, ao arquivo de gatilho e a pasta de workspace, e e a chave
    /// do historico de builds no banco. Renomear aqui deixaria o historico
    /// orfao e o gatilho apontando para o nome velho, tudo em silencio. Ele e
    /// definido uma vez, ao vincular o projeto.
    /// </summary>
    [Category("PROJETO")]
    [ReadOnly(true)]
    [Description("Definido ao vincular o projeto. Identifica a fila, o histórico, o arquivo de configuração e o gatilho — por isso não muda depois.")]
    public string Name
    {
        get => _project.Name;
        set => _project.Name = Texto.Limpo(value);
    }

    [Category("PROJETO")]
    [Description("Desligado, o projeto fica na configuração mas não é observado nem construído.")]
    public bool Enabled
    {
        get => _project.Enabled;
        set => _project.Enabled = value;
    }

    [Category("REPOSITÓRIO")]
    [Description("URL do repositório remoto. O PAT nunca entra aqui.")]
    public string Url
    {
        get => _repository.Url;
        set => _repository.Url = Texto.Limpo(value);
    }

    [Category("REPOSITÓRIO")]
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

    [Category("REPOSITÓRIO")]
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

    /// <summary>
    /// Fora da grade: o valor continua existindo na configuracao e no codigo,
    /// mas quem edita e o botao "Conectar ao GitHub", nao o usuario digitando.
    /// </summary>
    [Browsable(false)]
    public string? PatCredentialName
    {
        get => _repository.PatCredentialName;
        set => _repository.PatCredentialName = Texto.OuNulo(value);
    }

    /// <summary>
    /// A credencial que este projeto vai usar de verdade, so para ver.
    ///
    /// Mostra tambem DE ONDE ela vem: a diferenca entre "a maquina esta
    /// conectada" e "este projeto tem uma excecao" e justamente o que alguem
    /// precisa saber quando um projeto falha no clone e o outro nao.
    /// </summary>
    [Category("REPOSITÓRIO")]
    [DisplayName("Credencial")]
    [ReadOnly(true)]
    [Description(
        "Preenchida pelo botão 'Conectar ao GitHub'. O acesso ao Git é da máquina, não de cada jogo. " +
        "Um projeto só precisa de credencial própria se viver em outra conta ou organização — e isso se " +
        "ajusta no arquivo dele, em projetos\\<nome>.json.")]
    public string Credencial
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_repository.PatCredentialName))
                return _repository.PatCredentialName + "  (exceção deste projeto)";

            var daMaquina = _defaults?.Repository.PatCredentialName;

            return string.IsNullOrWhiteSpace(daMaquina)
                ? "(sem conexão — use 'Conectar ao GitHub')"
                : daMaquina + "  (conexão da máquina)";
        }
    }

    [Category("UNITY")]
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
    [Category("UNITY")]
    [DisplayName("EditorVersion no disco")]
    [Description("O que o ProjectVersion.txt do workspace diz agora. Se divergir do campo acima, a configuração está desatualizada.")]
    [ReadOnly(true)]
    public string DetectedEditorVersion
    {
        get
        {
            var noDisco = Detect(_repository.WorkspacePath);
            if (noDisco is null) return "(workspace ainda não clonado)";

            // A divergencia e dita na propria linha, e nao so na ajuda de baixo:
            // ninguem le a ajuda de um campo que parece estar certo, e uma build
            // numa versao de editor que o time ja abandonou falha longe daqui.
            return Diverge(noDisco) ? noDisco + "  —  diverge do gravado acima" : noDisco;
        }
    }

    /// <summary>
    /// A frase do rodape quando a versao gravada e a do disco discordam, ou
    /// nulo quando estao de acordo.
    ///
    /// Fora do PropertyGrid de proposito: e um aviso sobre o conjunto, nao um
    /// campo que se edita.
    /// </summary>
    [Browsable(false)]
    public string? DivergenciaDeEditor
    {
        get
        {
            var noDisco = Detect(_repository.WorkspacePath);

            return noDisco is not null && Diverge(noDisco)
                ? $"EditorVersion diverge do projeto clonado: {_project.Unity!.EditorVersion!.Trim()} gravado, " +
                  $"{noDisco} no disco."
                : null;
        }
    }

    /// <summary>
    /// A versao gravada e a do disco discordam. Campo vazio nao diverge: ele
    /// herda de Defaults de proposito.
    /// </summary>
    private bool Diverge(string noDisco)
    {
        var gravada = _project.Unity?.EditorVersion;

        return !string.IsNullOrWhiteSpace(gravada)
               && !string.Equals(gravada.Trim(), noDisco, StringComparison.OrdinalIgnoreCase);
    }

    [Category("UNITY")]
    [Description("Plataforma do build. Vazio herda de Defaults, que vem como WebGL.")]
    [TypeConverter(typeof(BuildTargetConverter))]
    public string? BuildTarget
    {
        get => _project.Unity?.BuildTarget;
        set
        {
            _project.Unity ??= new UnityOptions();
            _project.Unity.BuildTarget = Texto.OuNulo(value);
        }
    }

    // A pasta de destino nao aparece aqui: ela e uma so, para todos os projetos,
    // e vive na aba Geral. Ver GeneralView.ArtifactFolder.

    [Category("PUBLICAÇÃO")]
    [Description("Tocar este arquivo enfileira uma build do HEAD atual. O serviço o apaga ao consumir.")]
    [Editor(typeof(FilePathEditor), typeof(UITypeEditor))]
    public string? ManualTriggerFile
    {
        get => _project.ManualTriggerFile;
        set => _project.ManualTriggerFile = Texto.OuNulo(value);
    }

    public override string ToString() => _project.Name;
}
