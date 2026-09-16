# Especificação Técnica — UnityLocalCI

**Versão:** 1.0
**Alvo:** Windows 10/11 ou Windows Server, .NET 8, Unity 6 (WebGL), Unity Pro com serial

---

## 1. Visão geral

Serviço Windows que observa a branch de homologação de **um ou mais projetos Unity**, detecta commits novos, executa o build, empacota o resultado e copia o artefato para a pasta de cada projeto.

Cada projeto tem repositório, workspace, versão de editor e pasta de destino próprios. Um projeto nunca constrói duas vezes ao mesmo tempo; projetos distintos constroem em paralelo até o teto definido pelo scheduler.

Requisitos não funcionais:

- **Idempotência.** O mesmo commit nunca é construído duas vezes, mesmo após reinício da máquina.
- **Serialização por projeto.** Nunca mais de uma build simultânea do mesmo projeto; projetos distintos podem construir em paralelo, dentro do teto global do scheduler.
- **Degradação suave.** Falha na publicação remota não descarta o artefato.
- **Observabilidade.** Toda build tem log próprio, persistido e acessível.
- **Sem segredo em texto claro.** Nenhuma credencial em arquivo de configuração versionado.

## 2. Estrutura da solução

```
UnityLocalCI.sln
├── src/
│   ├── UnityLocalCI.Worker/          # host, Windows Service, DI
│   └── UnityLocalCI.Core/            # domínio, orquestração, interfaces
│   │   ├── Watching/                 # GitWatcher, hook listener
│   │   ├── Queue/                    # BuildQueue, debounce
│   │   ├── Pipeline/                 # BuildPipeline e etapas
│   │   ├── Publishing/               # IArtifactPublisher e implementações
│   │   ├── Notifications/            # INotifier e implementações
│   │   └── State/                    # persistência SQLite
├── tests/
│   └── UnityLocalCI.Tests/
├── unity/
│   └── Builder.cs                    # copiar para Assets/Editor/ do projeto
├── tools/
│   ├── install-service.ps1
│   ├── set-secrets.ps1
│   └── post-merge.hook
└── config/
    └── appsettings.json
```

## 3. Configuração

`appsettings.json`. Segredos são referenciados **por nome**, nunca por valor.

A configuração é uma **lista de projetos**. `Defaults` define os valores comuns; cada projeto sobrescreve apenas o que difere, para que acrescentar um projeto novo custe poucas linhas.

```jsonc
{
  "Scheduler": {
    "MaxConcurrentBuilds": 2,
    "MinFreeRamGb": 12,
    "GlobalStatusFile": "\\\\build01\\builds\\_STATUS-GERAL.txt"
  },
  "Defaults": {
    "Watcher": {
      "PollIntervalSeconds": 60,
      "DebounceSeconds": 120,
      "HookSignalPort": 8081
    },
    "Unity": {
      "EditorVersion": "6000.0.47f1",
      "BuildTarget": "WebGL",
      "ExecuteMethod": "Builder.PerformBuild",
      "TimeoutMinutes": 90,
      "ExtraArgs": []
    },
    "Packaging": {
      "NamePattern": "{project}-{branch}-{date}-{sha}.zip",
      "IncludeLauncher": true
    },
    "Publishing": {
      "StagingFolder": "D:\\ci\\staging",
      "MaintainLatestFolder": true,
      "WriteStatusFiles": true
    },
    "Retention": {
      "KeepLastBuilds": 10,
      "MinFreeDiskGb": 50
    }
  },
  "Projects": [
    {
      "Name": "Crash",
      "Enabled": true,
      "Repository": {
        "Url": "https://dev.azure.com/org/proj/_git/crash",
        "Branch": "HML",
        "WorkspacePath": "D:\\ci\\workspace\\crash",
        "PatCredentialName": "UnityLocalCI_AzureDevOpsPat"
      },
      "Publishing": { "ArtifactFolder": "\\\\build01\\builds\\crash\\hml" },
      "ManualTriggerFile": "D:\\ci\\triggers\\crash.txt"
    },
    {
      "Name": "Mines",
      "Enabled": true,
      "Repository": {
        "Url": "https://dev.azure.com/org/proj/_git/mines",
        "Branch": "HML",
        "WorkspacePath": "D:\\ci\\workspace\\mines",
        "PatCredentialName": "UnityLocalCI_AzureDevOpsPat"
      },
      "Unity": { "EditorVersion": "6000.0.32f1" },
      "Publishing": { "ArtifactFolder": "\\\\build01\\builds\\mines\\hml" },
      "ManualTriggerFile": "D:\\ci\\triggers\\mines.txt"
    }
  ],
  "Notifications": {
    "TeamsWebhookCredentialName": null
  }
}
```

Repare que `Mines` declara uma versão de editor própria. Projetos diferentes frequentemente estão em versões diferentes do Unity, e o CLI resolve isso sozinho por projeto; a configuração só precisa permitir.

`Enabled: false` desliga um projeto sem apagar a configuração, útil enquanto um repositório está em migração.

### 3.1 Segredos

Armazenados no **Windows Credential Manager** (`CredentialManagement` ou P/Invoke de `CredRead`), como credenciais genéricas. O script `tools/set-secrets.ps1` grava; o serviço apenas lê.

Se uma credencial referenciada não existir, o serviço falha na inicialização com mensagem explícita, em vez de partir e quebrar no meio da primeira build.

## 4. Estado

SQLite em `D:\ci\state\unitylocalci.db`.

**Tabela `builds`**

| Coluna | Tipo | Observação |
|---|---|---|
| `id` | INTEGER PK | autoincremento |
| `commit_sha` | TEXT | SHA completo |
| `commit_message` | TEXT | primeira linha |
| `commit_author` | TEXT | |
| `project` | TEXT | nome do projeto na configuração |
| `branch` | TEXT | |
| `status` | TEXT | `Queued`, `Running`, `Succeeded`, `Failed`, `Cancelled`, `Interrupted` |
| `queued_at` | TEXT | ISO 8601 |
| `started_at` | TEXT | nulo enquanto na fila |
| `finished_at` | TEXT | |
| `duration_seconds` | INTEGER | |
| `artifact_path` | TEXT | caminho local do zip |
| `artifact_size_bytes` | INTEGER | |
| `artifact_sha256` | TEXT | |
| `published_path` | TEXT | caminho final do artefato |
| `publish_status` | TEXT | `Published`, `PendingCopy`, `Failed` |
| `log_path` | TEXT | |
| `error_summary` | TEXT | |

**Tabela `watcher_state`:** chave/valor **por projeto**, guardando `last_seen_sha` e `last_built_sha`. A chave é `{projeto}:{campo}`.

**Recuperação no boot:** qualquer registro com status `Running` na inicialização virou órfão por reinício. Marcar como `Interrupted` e reenfileirar o commit se ele ainda for o HEAD da branch daquele projeto. A recuperação é avaliada projeto a projeto.

## 5. Componentes

### 5.1 GitWatcher

Uma instância por projeto habilitado, cada uma como `BackgroundService` com laço a cada `PollIntervalSeconds`. As instâncias são independentes: erro de rede ou repositório indisponível em um projeto não interrompe os outros.

Os intervalos de polling devem ser espaçados na inicialização, para que N projetos não disparem `git fetch` no mesmo instante.

```
1. git fetch origin HML --prune
2. sha = git rev-parse origin/HML
3. se sha == last_built_sha → nada a fazer
4. se sha == last_seen_sha e passou o debounce → enfileirar
5. se sha != last_seen_sha → gravar last_seen_sha, reiniciar timer de debounce
```

O passo 5 é o debounce: qualquer commit novo reinicia a janela, então uma rajada de merges produz uma única build, do último commit.

Também escuta em `HookSignalPort` (loopback apenas) por um POST vazio, que força uma verificação imediata. O hook `post-merge` faz um `curl` para esse endereço, informando o nome do projeto no corpo. O hook nunca enfileira diretamente: toda decisão passa pelo watcher.

Autenticação do Git: o PAT é injetado via `http.extraHeader` na invocação, não gravado na URL do remote e não persistido em `.git/config`.

### 5.2 Scheduler e filas

Há **uma fila por projeto** e **um limite global de execução**. As duas coisas resolvem problemas diferentes e precisam existir juntas.

**Fila por projeto.** Canal serial (`System.Threading.Channels`) com capacidade 1 e política de substituição: se já existe um job enfileirado para aquele projeto e chega outro, o novo substitui o antigo, e o substituído é marcado como `Cancelled`. Um job em execução nunca é substituído. Isso garante que nunca haja dois Editores no mesmo diretório, que é o que corromperia a `Library`.

**Limite global.** Um `SemaphoreSlim` com `MaxConcurrentBuilds` posições, atravessado por qualquer build antes de iniciar, independentemente do projeto.

> **Por que o limite global existe.** A memória não distingue projetos. Um build WebGL consome de 8 a 16 GB na fase de link do IL2CPP com Emscripten, então duas execuções simultâneas custam o mesmo seja Crash e Mines, sejam duas branches do mesmo jogo. Estourar a RAM não deixa o build lento, faz ele morrer, e o erro do Emscripten nesse caso é bastante obscuro.

**Guarda de recursos.** Antes de tomar uma vaga do semáforo, verificar memória física livre contra `MinFreeRamGb` e espaço em disco contra `MinFreeDiskGb`. Abaixo do limite, o job espera em vez de iniciar, e o motivo da espera aparece no log. Um job nunca é descartado por falta de recurso, apenas adiado.

**Valor padrão.** `MaxConcurrentBuilds` deve vir com o menor entre o valor configurado e `RAM_total_GB / 16`, arredondado para baixo, com mínimo de 1. Numa máquina de 32 GB isso resulta em 2; numa de 16 GB, em 1.

**Isolamento de falha.** Falha, timeout ou travamento de um projeto não pode afetar os demais. Cada pipeline roda em seu próprio escopo de DI, com seu próprio `CancellationTokenSource`, e o encerramento por timeout mata apenas a árvore de processos daquele build.

### 5.3 Etapas do pipeline

Cada etapa implementa `IBuildStep` com `Task<StepResult> ExecuteAsync(BuildContext ctx, CancellationToken ct)`. Falha de uma etapa interrompe a sequência e marca a build como `Failed`, exceto a publicação, que tem fallback próprio.

#### Etapa 1 — Sync

```bash
git fetch origin HML --prune
git reset --hard <sha>
git clean -xdf -e Library/ -e Temp/ -e obj/ -e Logs/ -e UserSettings/
git lfs pull            # somente se .gitattributes indicar LFS
```

> **Crítico:** as exclusões do `git clean` protegem o cache do Unity. Sem elas toda build vira build limpa, triplicando o tempo. Deve haver teste cobrindo isso.

Se o workspace não existir, faz `git clone` e registra que a primeira build será lenta.

#### Etapa 2 — Build

```bash
unity build "<WorkspacePath>" ^
  --editor-version 6000.0.47f1 ^
  --target WebGL ^
  --execute-method Builder.PerformBuild ^
  --non-interactive ^
  --allow-install ^
  --format json
```

Argumentos custom são passados ao método via `--` e lidos pelo `Builder.cs`:

```
-- -ciBuildNumber 42 -ciCommitSha a1b2c3d -ciBranch HML -ciOutputPath D:\ci\build\webgl
```

Regras:

- stdout e stderr gravados em tempo real em `logs/build-{id}.log`
- exit code diferente de zero significa falha; **nunca** inferir sucesso pela existência da pasta de saída
- `--format json` para leitura programática; ler falhas de stdout pelo campo `success`, não de stderr
- timeout encerra a **árvore** de processos (o Unity gera filhos de IL2CPP e Emscripten que sobrevivem a um kill simples)

#### Etapa 3 — Package

1. Validar a pasta de saída: `index.html` e `Build/` devem existir
2. Gerar `manifest.json` com sha do commit, autor, mensagem, data, versão do editor, duração e tamanho
3. Se `IncludeLauncher`, copiar `rodar.bat` para a raiz
4. Compactar com `System.IO.Compression`, nível `Optimal`
5. Calcular SHA-256 do zip

Nome pelo padrão configurado, com `{sha}` sendo os 7 primeiros caracteres: `MeuJogo-HML-20260914-a1b2c3d.zip`.

**`rodar.bat`:** o build WebGL não funciona aberto por `file://`, porque o navegador bloqueia `.wasm` e `.data` nesse protocolo. O launcher sobe um servidor estático na pasta e abre o navegador. Implementar preferindo, nesta ordem, o que existir na máquina de quem baixou: `python -m http.server`, `npx serve`, ou um `dotnet` de arquivo único incluído no zip. Se nada existir, exibir mensagem clara explicando o motivo em vez de falhar em silêncio.

#### Etapa 4 — Publish

O destino é um diretório definido em configuração. A máquina de build já é acessível ao time, então a publicação é uma cópia de arquivo: não há servidor HTTP, não há link, não há porta aberta.

```csharp
public interface IArtifactPublisher
{
    string Name { get; }
    Task<PublishResult> PublishAsync(FileInfo artifact, BuildContext ctx, CancellationToken ct);
}

public record PublishResult(bool Success, string? Location, string? Error);
```

A interface existe mesmo havendo uma implementação só. É barata e mantém aberta a porta para um destino remoto no futuro, sem tocar no pipeline.

**FolderPublisher**

1. Gravar o zip primeiro em `StagingFolder`, que é sempre disco local
2. Copiar para `ArtifactFolder` com nome temporário (`.part`) e renomear ao final, para nunca expor um zip parcial a quem estiver olhando a pasta
3. Verificar o SHA-256 após a cópia; divergência é falha de publicação, não sucesso silencioso
4. Se `MaintainLatestFolder`, substituir o conteúdo de `latest\` pela build descompactada, gravando em `latest.new\` e trocando por rename
5. Se `WriteStatusFiles`, reescrever `_STATUS.txt` e acrescentar linha em `_HISTORICO.txt`
6. Copiar o log da build para `_logs\build-{id}.log`
7. Reescrever o `GlobalStatusFile`, consolidando o estado de todos os projetos

**Formato do `_STATUS-GERAL.txt`.** Uma linha por projeto, reescrito sempre que qualquer build termina ou entra em execução. Existe porque, com vários projetos, abrir N pastas para saber o que está acontecendo é o principal incômodo de não haver interface:

```
CI LOCAL — 14/09/2026 14:47

PROJETO   ESTADO       ÚLTIMA BUILD       COMMIT   ARQUIVO
Crash     ok           14/09 14:32        a1b2c3d  Crash-HML-20260914-a1b2c3d.zip
Mines     construindo  (iniciou 14:41)    9f8e7d6  —
Rocket    FALHOU       13/09 18:02        4e5f6a7  ver _logs\build-39.log

Fila: 0 aguardando  |  Em execução: 1 de 2
```

A escrita é feita com lock entre projetos, já que builds paralelas podem terminar ao mesmo tempo.

**Layout final da pasta**

```
\\build01\builds\hml\
├── _STATUS.txt                       resultado da última build
├── _HISTORICO.txt                    últimas 20 builds, uma linha cada
├── latest\                           última build descompactada, com rodar.bat
├── MeuJogo-HML-20260914-a1b2c3d.zip
└── _logs\
    └── build-42.log
```

**Formato do `_STATUS.txt`.** Texto puro, legível sem ferramenta, reescrito a cada build:

```
ÚLTIMA BUILD: SUCESSO
Data......: 14/09/2026 14:32
Commit....: a1b2c3d  "corrige colisão do player"
Autor.....: Fulano de Tal
Duração...: 12 min 40 s
Arquivo...: MeuJogo-HML-20260914-a1b2c3d.zip  (78,4 MB)
Jogar.....: abra latest\rodar.bat

Build anterior: 13/09/2026 09:12  SUCESSO
```

Em falha, o bloco de resultado traz o erro de compilação resumido e o caminho do log completo. O objetivo é que ninguém precise perguntar no chat se a build saiu.

**Regras de resiliência**

- O staging local vem antes da cópia para a rede. Se o compartilhamento estiver indisponível, a build permanece bem-sucedida, o artefato existe em disco local, a publicação fica marcada como pendente e é retentada na próxima execução
- Nunca apagar o artefato do staging antes de confirmar a cópia no destino
- Falha de permissão de escrita precisa aparecer como erro acionável ("sem permissão de escrita em X"), não como exceção de I/O crua
- A troca de `latest\` é feita por rename de diretório para que ninguém pegue a pasta em estado intermediário

#### Etapa 5 — Notify e retenção

Notificadores: log em arquivo (sempre), `_STATUS.txt` e `_HISTORICO.txt` na pasta de artefatos (sempre), Teams por webhook (opcional). Mensagem com status, commit, autor, duração, tamanho e caminho do artefato.

Retenção executada após cada build: manter as `KeepLastBuilds` mais recentes bem-sucedidas, apagando zips e logs anteriores, e as pastas de build descompactadas correspondentes. Antes de iniciar qualquer build, verificar `MinFreeDiskGb`; abaixo do limite, apagar as mais antigas e, se ainda assim faltar espaço, falhar com mensagem clara em vez de começar uma build que vai morrer no meio.

### 5.4 Sem painel web

Não há servidor HTTP, API nem porta exposta. O estado do sistema é comunicado por arquivos na própria pasta de artefatos, descritos na etapa 4.

**Disparo manual.** Em vez de um endpoint, cada projeto observa o arquivo apontado por seu `ManualTriggerFile`. Criar ou tocar esse arquivo enfileira uma build do HEAD atual daquele projeto, e o serviço o apaga ao consumir. Um atalho na área de trabalho por projeto resolve o caso de uso sem infraestrutura.

Para construir vários projetos de uma vez, basta tocar mais de um arquivo: cada fila recebe seu job e o scheduler distribui conforme as vagas disponíveis. Um script `buildar-tudo.bat` que toca todos os arquivos de gatilho cobre o caso de "constrói tudo agora".

**Visão consolidada.** O `_STATUS-GERAL.txt` na raiz do compartilhamento resume todos os projetos em um arquivo, evitando abrir uma pasta por jogo.

**Diagnóstico.** O serviço escreve no Event Log do Windows na inicialização e em falhas de configuração, e mantém `logs\service.log` com rotação diária.

Se um painel for desejado no futuro, a camada de estado em SQLite já contém tudo o que ele precisaria; seria apenas uma leitura, sem mudança no pipeline.

## 6. Builder.cs

Copiado para `Assets/Editor/` do projeto Unity.

Responsabilidades:

- ler os argumentos `-ci*` de `Environment.GetCommandLineArgs()`
- montar `BuildPlayerOptions` com as cenas habilitadas em `EditorBuildSettings`
- aplicar `PlayerSettings.bundleVersion` a partir do sha e do número da build
- **não alterar Player Settings.** Compressão, qualidade e template são configurados no projeto Unity e versionados junto dele; o Builder apenas executa o que estiver lá
- inspecionar `PlayerSettings.WebGL.compressionFormat` **somente para leitura** e emitir aviso quando estiver em Brotli ou Gzip com `decompressionFallback` desligado

> **Por que esse aviso existe, e por que não é uma correção.** São duas compressões independentes, em camadas diferentes, e confundi-las custa caro.
>
> O **zip** da etapa 3 é transporte: compacta a pasta inteira e desaparece quando a pessoa extrai.
>
> A **compressão do Unity** acontece dentro do build, antes de existir qualquer zip. Ela grava `game.wasm.br` e `game.data.br`, e esses arquivos **continuam comprimidos depois de o zip ser extraído**. Quem deveria descomprimi-los é o navegador, e ele só faz isso se o servidor enviar `Content-Encoding: br`, o que nenhum servidor estático simples faz. O resultado é tela preta sem erro útil.
>
> Como a distribuição aqui é por pasta, a combinação Brotli sem fallback produz um build inutilizável na prática. Ainda assim o pipeline **não** deve mudar a configuração: sobrescrever Player Settings faz uma mudança feita no Editor ser silenciosamente desfeita, o que é pior que o problema original. Avisar alto e deixar a decisão com quem cuida do projeto.
>
> Para referência de quem for decidir: desligar a compressão faz a pasta crescer de 2 a 3 vezes, mas o zip final cresce pouco, porque dado já comprimido não comprime de novo. A alternativa é manter Brotli e ligar `decompressionFallback`, ao custo de alguns segundos em todo carregamento.

- chamar `BuildPipeline.BuildPlayer` e inspecionar o `BuildReport`
- **encerrar com `EditorApplication.Exit(1)` em qualquer resultado diferente de `BuildResult.Succeeded`**

Esse último ponto é o mais importante do arquivo. Sem ele o Unity termina com código 0 mesmo em build quebrada, e o pipeline publica lixo.

Erros de compilação devem ser capturados e resumidos nas primeiras linhas do log, não enterrados em 40 mil linhas de saída do Unity.

## 7. Instalação

`tools/install-service.ps1`, executado como administrador:

1. Verificar .NET 8 Runtime, Git e Unity CLI (`unity --version`)
2. Criar diretórios de workspace, staging, artefatos, gatilhos, logs e estado **para cada projeto configurado**, validando permissão de escrita em cada destino
3. Solicitar e gravar segredos no Credential Manager
4. Registrar o serviço (`sc.exe create UnityLocalCI ... start= auto`) sob a conta definida
5. Iniciar o serviço e validar escrevendo um `_STATUS.txt` inicial na pasta de destino

Ativação da licença Unity, uma vez apenas:

```powershell
unity license activate --serial <SERIAL> --username <USER> --password <PASS>
unity license status
```

O `.ulf` resultante fica em `C:\ProgramData\Unity\`, com escopo de máquina, então funciona sob a conta de serviço.

## 8. Critérios de aceite

1. Merge na HML resulta em zip publicado, sem intervenção manual
2. Dois merges em 30 segundos no mesmo projeto produzem exatamente uma build, do commit mais recente
3. Build com erro de compilação termina como `Failed`, com a mensagem do erro resumida no `_STATUS.txt`, e nenhum zip é publicado
4. Serviço reiniciado no meio de uma build retoma o commit pendente ao subir
5. Build subsequente sem mudança de pacotes leva menos da metade do tempo da primeira
6. Zip baixado e descompactado abre e joga por duplo clique no `rodar.bat`
7. Pasta de destino indisponível não derruba a build: artefato fica no staging, marcado como cópia pendente, e é reenviado depois
8. Nenhum segredo aparece em arquivo de configuração, log ou histórico do Git
9. `_STATUS.txt` reflete corretamente o resultado da última build, inclusive em falha
9b. `_STATUS-GERAL.txt` mostra todos os projetos e é escrito sem corrupção quando duas builds terminam simultaneamente
11. A pasta `latest\` sempre contém a build bem-sucedida mais recente, nunca um estado parcial
12. Abrir `latest\rodar.bat` em outra máquina da rede carrega e roda o jogo
10. Com `KeepLastBuilds = 10`, a décima primeira build de um projeto apaga a mais antiga daquele projeto, sem tocar nos demais
13. Merges simultâneos em dois projetos resultam em dois builds em paralelo, respeitando `MaxConcurrentBuilds`
14. Com `MaxConcurrentBuilds = 1`, o segundo projeto espera o primeiro terminar em vez de falhar
15. Build travado ou com timeout em um projeto não afeta a fila nem a execução dos outros
16. Acrescentar um projeto novo exige apenas um bloco em `Projects`, sem mudança de código

## 9. Registro de decisões

| Tema | Decisão | Por quê |
|---|---|---|
| Gatilho | Polling com hook como sinal | Sem dependência de rede de entrada |
| Concorrência | Serial por projeto, paralela entre projetos | Unity trava a `Library` do diretório, não a máquina |
| Teto global | Derivado da RAM | Build WebGL consome 8–16 GB no link do IL2CPP |
| Workspace | Persistente | Build limpa é 3x mais lenta |
| Publicação | Cópia para pasta | Máquina já é acessível; zero infraestrutura |
| Painel | Não existe | `_STATUS.txt` resolve o mesmo problema sem serviço web |
| Sucesso da build | Exit code | Pasta de saída pode existir mesmo em falha |
| Segredos | Credential Manager | Nunca em disco nem no repositório |
| Player Settings | Pipeline não altera, só avisa | Configuração em dois lugares sempre diverge |
