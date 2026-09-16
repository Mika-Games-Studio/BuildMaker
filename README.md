# UnityLocalCI

Serviço de integração contínua que roda inteiramente numa máquina Windows local. Observa a branch de homologação de um ou mais projetos Unity, detecta commits novos, executa o build, compacta o resultado e copia o zip para a pasta de cada projeto.

Sem nuvem, sem servidor HTTP, sem API, sem painel web. A pasta de destino é a interface.

**Estado atual: as três fases concluídas.** Ver [O que já existe](#o-que-já-existe-e-o-que-falta) ao final.

---

## Pré-requisitos

| Item | Versão | Como conferir |
|---|---|---|
| .NET SDK | 10.0 | `dotnet --version` |
| Git | 2.x | `git --version` |
| Unity CLI | 1.0.0-beta.5 ou mais novo | `unity --version` |
| Unity Editor | a versão de cada projeto | `unity editors --json` |
| Windows | 10/11 ou Server | — |

> **A especificação pedia .NET 8.** Esta máquina só tem o SDK/runtime 10, então a solução mira `net10.0-windows`. Para voltar ao .NET 8 basta trocar `TargetFramework` em `Directory.Build.props` e instalar o runtime 8.

> **`net10.0-windows`, não `net10.0`:** o serviço usa Job Objects e o Credential Manager, ambos exclusivos do Windows. Sem o sufixo, o compilador reprova as chamadas.

### Licença do Unity

Ativação única, por máquina, feita uma vez no setup:

```bash
unity license activate --serial <SERIAL> --username <USER> --password <PASS>
```

```bash
unity license status
```

O `.ulf` resultante fica em `C:\ProgramData\Unity\`, com escopo de máquina, então funciona sob uma conta de serviço sem sessão interativa.

---

## Instalação

### 1. Compilar

```bash
dotnet build -c Release
```

### 2. Gravar os segredos no Windows Credential Manager

O serviço apenas lê; nada de segredo em arquivo de configuração, log ou URL de remote do Git. A configuração guarda só o **nome** da credencial.

```bash
cmdkey /generic:UnityLocalCI_AzureDevOpsPat /user:pat /pass:SEU_PAT_AQUI
```

O PAT precisa do escopo `Code: Read`. Se a credencial referenciada não existir, o serviço recusa subir e diz exatamente qual é e como gravá-la.

### 3. Preencher `appsettings.json`

Todos os placeholders estão listados na seção seguinte. Enquanto houver um `PREENCHER-`, o serviço sobe mas não faz nada de útil.

### 4. Rodar

```bash
dotnet run --project src/UnityLocalCI.Worker
```

Isso roda como aplicação de console, que é o modo de conferir a configuração. Para instalar como serviço, ver [Windows Service](#windows-service).

> **Se o `.exe` reclamar que o .NET não foi encontrado:** o SDK está instalado no perfil do usuário (`%USERPROFILE%\.dotnet`) e o apphost só procura em `C:\Program Files\dotnet`. Rode por `dotnet UnityLocalCI.Worker.dll`, ou defina `DOTNET_ROOT`. Antes de registrar o Windows Service, instale o .NET para toda a máquina.

---

## Placeholders de configuração

Tudo que precisa ser preenchido antes do primeiro uso real. Nada disso foi inventado.

| Placeholder | Onde | O que é |
|---|---|---|
| `PREENCHER-NOME-DO-PROJETO` | `Projects[].Name` | Nome do projeto. Identifica a fila, o estado e aparece nos arquivos de status. |
| `PREENCHER-NOME-DO-PROJETO` | `Projects[].Repository.WorkspacePath` | Última pasta do caminho do workspace. |
| `PREENCHER-NOME-DO-PROJETO` | `Projects[].ManualTriggerFile` | Nome do arquivo de gatilho manual. |
| `PREENCHER-URL-DO-REPOSITORIO` | `Projects[].Repository.Url` | URL do repositório no Azure DevOps. |
| `PREENCHER-VERSAO-DO-EDITOR` | `Defaults.Unity.EditorVersion` | Versão exata do editor, ex.: `6000.0.47f1`. Cada projeto pode sobrescrever a sua. |
| `PREENCHER-PASTA-DE-DESTINO` | `Projects[].Publishing.ArtifactFolder` | Pasta onde o time pega o zip. |
| `PREENCHER-DESTINO-RAIZ` | `Scheduler.GlobalStatusFile` | Raiz do compartilhamento, onde vai o `_STATUS-GERAL.txt`. |
| `PREENCHER-NOME-DO-PROJETO` | `tools/post-merge.hook` | Nome do projeto no hook, se for usá-lo. |

Valores que já vêm prontos e você provavelmente quer conferir: `Branch` (`HML`), `PatCredentialName` (`UnityLocalCI_AzureDevOpsPat`), e os caminhos locais em `C:\ci\` (workspace, staging, state, logs, triggers).

> **Os caminhos usam `C:\ci\`, não `D:\ci\` como na especificação**, porque esta máquina só tem o drive C:. Se a máquina de build tiver um D:, troque nos quatro lugares: `State.DatabasePath`, `State.LogFolder`, `Defaults.Publishing.StagingFolder` e `Projects[].Repository.WorkspacePath`.

### Acrescentar um projeto

Um bloco novo em `Projects`, sem mudança de código. `Defaults` cobre o resto; sobrescreva só o que difere:

```jsonc
{
  "Name": "Mines",
  "Enabled": true,
  "Repository": {
    "Url": "https://dev.azure.com/org/proj/_git/mines",
    "Branch": "HML",
    "WorkspacePath": "C:\\ci\\workspace\\mines",
    "PatCredentialName": "UnityLocalCI_AzureDevOpsPat"
  },
  "Unity": { "EditorVersion": "6000.0.32f1" },
  "Publishing": { "ArtifactFolder": "\\\\build01\\builds\\mines\\hml" },
  "ManualTriggerFile": "C:\\ci\\triggers\\mines.txt"
}
```

`Enabled: false` desliga um projeto sem apagar a configuração.

Dois projetos **não podem** compartilhar o mesmo `WorkspacePath`: o Unity trava a `Library` do diretório e dois Editores no mesmo lugar corrompem o cache. A validação na inicialização recusa isso.

---

## Como funciona

```
merge na HML ──► GitWatcher (1 por projeto, polling + debounce)
                      │
                      ▼
              Fila do projeto (capacidade 1, substituição)
                      │
                      ▼
              Semáforo global + guarda de RAM/disco
                      │
                      ▼
         Sync ──► Build ──► Package ──► Publish
                      │
                      ▼
              pasta de destino: zip
```

**Uma build por vez por projeto, projetos distintos em paralelo.** A fila por projeto impede dois Editores no mesmo diretório; o teto global impede que dois builds WebGL estourem a RAM.

**O teto global é derivado da RAM instalada:** o menor entre `MaxConcurrentBuilds` e `RAM_instalada_GB / 16`, com mínimo de 1. Numa máquina de 32 GB dá 2.

> A especificação dizia "RAM total"; usamos a **instalada** (`GetPhysicallyInstalledSystemMemory`), não a visível ao SO. Numa máquina de 32 GB o Windows reporta ~31,7 GB visíveis, e o arredondamento daria 1 em vez dos 2 pretendidos.

**Debounce de 2 minutos:** qualquer commit novo reinicia a janela, então uma rajada de merges produz uma única build, do último commit.

**Sucesso é o exit code, e só.** A pasta de saída pode existir cheia de artefatos parciais mesmo quando o build falhou.

**Falha ao copiar não descarta o artefato.** O zip nasce no staging local. Se o destino estiver fora do ar ou sem permissão, a build permanece bem-sucedida, o zip continua no staging e a cópia fica marcada como pendente. O reenvio é automático: na inicialização e a cada 10 minutos.

### Invocação do Unity: desvio consciente da especificação

A especificação previa argumentos custom depois de `--` e `--format json` por comando. A CLI instalada não tem `--`: extras vão por `--args "<string>"`, e `-o/--output-path` é encaminhado ao editor como **`-buildOutput`**.

O que o pipeline manda hoje:

```
unity --non-interactive --no-banner build <workspace>
      --target WebGL --execute-method Builder.PerformBuild
      --editor-version <versão> --output-path <saída>
      --log-file <log>.unity.log --allow-install
      --args "-ciBuildNumber 42 -ciCommitSha <sha> -ciBranch HML -ciOutputPath <saída>"
```

O caminho de saída vai pelos dois nomes (`-buildOutput` via `--output-path` e `-ciOutputPath` via `--args`), então o [`Builder.cs`](unity/Builder.cs) lê qualquer um dos dois.

Também não usamos `--format json`: o log é transmitido em tempo real para `logs/build-{id}.log` enquanto roda, o que não conviveria com uma saída JSON única no fim. Se o build travar e for morto pelo timeout, o que já saiu está gravado.

---

## Estrutura

```
UnityLocalCI.sln
├── src/
│   ├── UnityLocalCI.Worker/      host, DI, appsettings.json
│   └── UnityLocalCI.Core/
│       ├── Configuration/        opções tipadas, merge de Defaults, validação
│       ├── Secrets/              Windows Credential Manager (P/Invoke CredRead)
│       ├── Abstractions/         relógio, executor de processos, Job Object
│       ├── Git/                  IGitClient e as exclusões do git clean
│       ├── Unity/                IUnityCliClient e o parser de log
│       ├── State/                SQLite e recuperação de build órfã
│       ├── Watching/             GitWatcher, um por projeto
│       ├── Queue/                fila por projeto, semáforo global, guarda de recursos
│       ├── Pipeline/             Sync, Build, Package, Publish
│       ├── Publishing/           IArtifactPublisher e FolderPublisher
│       └── Notifications/        INotifier e LogNotifier
├── tests/UnityLocalCI.Tests/     52 testes xUnit
├── unity/                        Builder.cs e instrucoes de instalacao
├── tools/                        buildar-tudo, install-service, set-secrets, post-merge
└── config/
```

## Testes

```bash
dotnet test
```

52 testes, sem rede e sem compartilhamento montado. Git, Unity CLI e processos externos são mockados. O que eles cobrem, em ordem de importância:

- **exclusões do `git clean`** — regressão: se alguém encurtar a lista, toda build vira build limpa e o tempo triplica
- **segredos** — o PAT nunca aparece na linha de comando registrada, nem em claro nem em base64, nem no stderr propagado
- **debounce** — rajada de commits produz um job só, do mais recente; commit novo reinicia a janela
- **fila** — job novo substitui o enfileirado; job em execução nunca é substituído
- **scheduler** — fila de um projeto não bloqueia a de outro; o semáforo global respeita `MaxConcurrentBuilds`
- **guarda de recursos** — RAM ou disco abaixo do mínimo adia o job, não o descarta
- **publicação** — cópia parcial nunca é exposta com o nome final; SHA divergente é falha; destino indisponível não apaga o staging
- **parser do Unity** — erro de compilação no meio de 10 mil linhas sai no topo do resumo
- **recuperação de órfã** — `Running` no boot vira `Interrupted` e só volta para a fila se ainda for o HEAD
- **configuração** — herança de `Defaults`, credencial ausente, workspace duplicado, versão de editor ausente

---

## O que já existe e o que falta

### Fase 1 — núcleo (pronto)

- Solução .NET com a estrutura da seção 2 da especificação
- Estado em SQLite, incluindo recuperação de build órfã no boot
- `GitWatcher` com polling e debounce, uma instância por projeto
- Scheduler com fila por projeto e semáforo global com guarda de RAM e disco
- Etapas Sync, Build e Package
- `FolderPublisher` com staging local antes da cópia
- Log por build em arquivo, mais console
- Segredos no Windows Credential Manager
- Execução como aplicação de console

### Fase 2 — usabilidade (em andamento)

| Item | Estado |
|---|---|
| `Builder.cs` em `unity/`, com instruções de instalação | **pronto** |
| Contrato de log entre o `Builder.cs` e o pipeline | **pronto** |
| `_STATUS.txt`, `_HISTORICO.txt` e `_STATUS-GERAL.txt` | **pronto** |
| Pasta `latest\` trocada por rename de diretório | **pronto** |
| `rodar.bat` no zip e em `latest\` | **pronto** |
| Cópia do log da build para `_logs\` | **pronto** |
| Gatilho manual por arquivo observado | **pronto** |
| Retenção por contagem | **pronto** |

**Fase 2 concluída.**

O `Builder.cs` vem primeiro porque é ele que faz o Unity retornar código diferente de zero em build quebrada. Enquanto ele não estiver instalado no projeto Unity, o pipeline pode publicar lixo — ver [`unity/README.md`](unity/README.md).

### A pasta como interface

Sem painel web, a própria pasta comunica o estado. O que o time encontra hoje no destino de cada projeto:

```
\\build01\builds\crash\hml\
├── _STATUS.txt        resultado da última build, com o erro resumido em caso de falha
├── _HISTORICO.txt     últimas 20 builds que rodaram, uma linha cada
└── _logs\
    └── build-42.log   o log completo, ao lado do status que aponta para ele
```

E na raiz do compartilhamento, um arquivo consolidando todos os projetos, para não ser preciso abrir uma pasta por jogo:

```
CI LOCAL — 16/09/2026 15:27

PROJETO  ESTADO       ÚLTIMA BUILD  COMMIT   ARQUIVO
Crash    ok           16/09 15:27   a1b2c3d  Crash-HML-20260916-a1b2c3d.zip
Mines    construindo  (iniciou 15:22)  9f8e7d6  —
Rocket   FALHOU       15/09 18:02   4e5f6a7  ver _logs\build-39.log

Fila: 0 aguardando  |  Em execução: 1 de 2
```

O `_STATUS-GERAL.txt` é reescrito quando qualquer build termina **e** quando uma entra em execução, para que quem o abrir durante uma build de 30 minutos veja `construindo`, e não o resultado da anterior. A escrita é serializada entre projetos: duas builds terminando juntas não podem produzir um arquivo que descreve um estado que nunca existiu.

A pasta `latest\` é a build mais recente já descompactada: quem só quer testar entra, roda o `rodar.bat` e joga. A troca é feita por rename de diretório — a build nova é copiada inteira para `latest.new\` e só então assume o nome —, porque copiar por cima deixaria a pasta em estado parcial por vários segundos, e quem a abrisse nesse intervalo pegaria uma build quebrada sem nenhum sinal disso.

### `rodar.bat`: por que o duplo clique no `index.html` não serve

Uma build WebGL não roda por `file://`: o navegador bloqueia `.wasm` e `.data` nesse protocolo, e o resultado é uma tela preta sem mensagem de erro. O `rodar.bat` sobe um servidor estático na própria pasta e abre o navegador. Ele tenta, nesta ordem, o que existir na máquina de quem baixou:

1. `python -m http.server`
2. `npx serve`
3. PowerShell, pelo `_servidor.ps1` que acompanha o launcher

Se nada existir, ele explica o motivo e aponta onde instalar, em vez de falhar em silêncio. Para usar outra porta: `rodar.bat 8090`.

> **Desvio da especificação.** Ela previa, como terceira estratégia, um executável .NET de arquivo único embutido no zip. Trocamos por PowerShell porque ele já está em toda máquina Windows e não acrescenta dezenas de MB a **cada** artefato. O servidor em PowerShell ainda tem uma vantagem sobre os outros dois: ele envia `Content-Encoding` para arquivos `.br` e `.gz`, então roda até uma build compactada com Brotli — exatamente o caso que quebraria num `python -m http.server`.

### Construir agora, sem esperar o merge

Não há endpoint HTTP nem painel. Cada projeto observa o arquivo apontado por seu `ManualTriggerFile`: criar ou tocar esse arquivo enfileira uma build do HEAD atual, e o serviço o apaga ao consumir.

Para um projeto, um atalho na área de trabalho apontando para um `.bat` de uma linha resolve:

```bat
type nul > C:\ci\triggers\crash.txt
```

Para todos de uma vez, [`tools/buildar-tudo.bat`](tools/buildar-tudo.bat) lê o `appsettings.json` e toca o gatilho de cada projeto habilitado:

```bash
tools\buildar-tudo.bat
```

Tocar N arquivos não dispara N builds simultâneas: cada fila recebe seu job e o scheduler distribui conforme as vagas livres. Para um subconjunto, passe os nomes:

```bash
tools\buildar-tudo.bat -Projeto Crash,Mines
```

O gatilho manual **ignora o debounce** de propósito — ele existe justamente para dizer "constrói agora" — e reconstrói o mesmo commit se você pedir, o que é o caso comum depois de uma falha.

### Retenção

Executada ao fim de cada build: mantém as `KeepLastBuilds` builds **bem-sucedidas** mais recentes e apaga, das demais, o zip no destino, o zip no staging, o log local e a cópia em `_logs\`.

Três detalhes que a implementação garante:

- **Contam-se as bem-sucedidas.** Uma sequência de falhas não empurra para fora o último artefato que de fato funciona.
- **Cópia pendente nunca perde o staging.** Se o destino estava fora do ar, aquele zip só existe ali.
- **A poda é guiada pelo banco, não por varredura da pasta.** Ela apaga exatamente os arquivos que cada build registrou, e nunca um `_STATUS.txt`, a pasta `latest\`, ou um zip que alguém copiou para lá na mão.

A retenção também roda quando um job está adiado por falta de disco. Sem isso ele esperaria para sempre: a poda só acontece ao fim de uma build, e nenhuma ia começar.

---

## Operação

### Notificação no Teams

Opcional. Sem `Notifications.TeamsWebhookCredentialName` configurado, o notificador não faz nada.

A URL do webhook é um segredo como qualquer outro — quem a tem pode postar no canal —, então ela vive no Credential Manager e a configuração guarda só o nome:

```bash
cmdkey /generic:UnityLocalCI_TeamsWebhook /user:unitylocalci /pass:https://SEU-WEBHOOK
```

```jsonc
"Notifications": { "TeamsWebhookCredentialName": "UnityLocalCI_TeamsWebhook" }
```

O payload é um **Adaptive Card**, que é o formato esperado pelos webhooks de fluxo do Power Automate. Se o seu canal ainda usa um connector antigo do Office 365, a URL espera o formato `MessageCard`: troque o corpo de `TeamsNotifier.BuildPayload`, que é o único lugar que conhece o formato.

Falha ao notificar nunca muda o resultado de uma build que já terminou — vira aviso no log.

### Sinal em loopback e hook `post-merge`

O serviço ouve em `http://127.0.0.1:<HookSignalPort>/`, **só em loopback**. Não é um painel e não é uma API de artefatos: o prefixo é `127.0.0.1` (não `localhost`, não `+`), então nada fora da máquina alcança a porta, não há hostname para configurar e não há firewall para liberar.

| Rota | Efeito |
|---|---|
| `POST /` ou `GET /` | Antecipa a verificação. Corpo com o nome do projeto, ou vazio para todos. |
| `POST /api/builds/{id}/republish` | Reenvia o artefato de uma build com cópia pendente. |

O hook **nunca enfileira direto**: ele só antecipa a verificação do watcher, que continua sendo quem conhece o debounce e o último sha. É otimização de latência e pode falhar sem consequência — o polling continua sendo a fonte da verdade, e nenhum merge se perde.

Instalação: copie [`tools/post-merge.hook`](tools/post-merge.hook) para `.git/hooks/post-merge` no clone de quem faz merge, ajuste `PROJETO` e `SERVIDOR`, e marque como executável.

```bash
curl --data "Crash" http://127.0.0.1:8081/
```

> **Um POST sem corpo recebe 411.** O `http.sys` do Windows rejeita `POST` sem `Content-Length` antes de a requisição chegar ao serviço — não dá para tratar isso de dentro do `HttpListener`. Por isso a rota de sinal também aceita `GET`, e o hook envia `--data`. A rota de reenvio continua só por `POST`, porque ela tem efeito.

### Reenvio de artefato pendente

Quando o compartilhamento está fora do ar, a build permanece bem-sucedida e o zip fica no staging marcado como `PendingCopy`. O reenvio acontece sozinho: na inicialização e a cada `PendingCopyRetryMinutes` (padrão 10). A máquina que caiu durante a noite reencontra o compartilhamento sem ninguém lembrar.

Para forçar agora:

```bash
curl -X POST --data "" http://127.0.0.1:8081/api/builds/42/republish
```

O staging **nunca** é apagado pelo reenvio. Quem remove é a retenção, e só depois de a cópia estar confirmada.

### Windows Service

```bash
dotnet publish src/UnityLocalCI.Worker -c Release -o publicado
```

```bash
powershell -ExecutionPolicy Bypass -File tools\install-service.ps1
```

O script confere .NET, Git e Unity CLI, cria os diretórios de cada projeto **escrevendo de verdade em cada um** (permissão só se descobre tentando), avisa quais credenciais faltam, registra o serviço com reinício automático e o inicia.

Para rodar sob uma conta de serviço:

```bash
powershell -ExecutionPolicy Bypass -File tools\install-service.ps1 -Conta "DOMINIO\svc-ci"
```

> **Duas armadilhas de conta de serviço.** O Credential Manager é **por usuário**: os segredos precisam ser gravados logado como a conta que executa o serviço, senão ele sobe e não encontra nada. E o .NET instalado no perfil de um usuário (`%USERPROFILE%\.dotnet`) não é visto por outra conta — instale-o para a máquina inteira, ou defina `DOTNET_ROOT` no ambiente do serviço. O `install-service.ps1` avisa sobre as duas.

> Os demais campos de configuração da fase 2 (`MaintainLatestFolder`, `WriteStatusFiles`, `IncludeLauncher`, `Retention`, `ManualTriggerFile`, `GlobalStatusFile`) já existem e são validados, mas ainda não têm efeito.

### Fase 3 — operação (concluída)

| Item | Estado |
|---|---|
| Notificador do Teams por webhook | **pronto** |
| Reenvio de artefato com cópia pendente | **pronto** |
| Hook `post-merge` e sinal em loopback | **pronto** |
| `install-service.ps1` e `set-secrets.ps1` | **pronto** |
| Registro como Windows Service | **pronto** |

> **Desvio da especificação, e por quê.** A seção 5.4 diz que não há servidor HTTP, API nem porta exposta; a lista da fase 3 pede um `POST /api/builds/{id}/republish`. As duas coisas foram reconciliadas num ouvinte **só de loopback**, que é o mesmo previsto na seção 5.1 para o sinal do hook: uma porta em `127.0.0.1` não é exposta, não pede liberação de firewall e não é alcançável de fora da máquina. Não há painel, não há link para compartilhar e a pasta continua sendo a interface.
>
> O reenvio, além disso, **não depende** desse endpoint: ele acontece sozinho na inicialização e a cada 10 minutos. A rota existe para forçar agora, não para o mecanismo funcionar.
