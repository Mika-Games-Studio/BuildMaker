# UnityLocalCI

Serviço de integração contínua que roda inteiramente numa máquina Windows local. Observa a branch de homologação de um ou mais projetos Unity, detecta commits novos, executa o build, compacta o resultado e copia o zip para a pasta de cada projeto.

Sem nuvem, sem servidor HTTP, sem API, sem painel web. A pasta de destino é a interface.

**Estado atual: fase 1 concluída, fase 2 em andamento.** Ver [O que já existe](#o-que-já-existe-e-o-que-falta) ao final.

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

Na fase 1 é uma aplicação de console. O registro como Windows Service é fase 3.

> **Se o `.exe` reclamar que o .NET não foi encontrado:** o SDK está instalado no perfil do usuário (`%USERPROFILE%\.dotnet`) e o apphost só procura em `C:\Program Files\dotnet`. Rode por `dotnet UnityLocalCI.Worker.dll`, ou defina `DOTNET_ROOT`. Para o serviço da fase 3, instale o .NET para toda a máquina.

---

## Placeholders de configuração

Tudo que precisa ser preenchido antes do primeiro uso real. Nada disso foi inventado.

| Placeholder | Onde | O que é |
|---|---|---|
| `PREENCHER-NOME-DO-PROJETO` | `Projects[].Name` | Nome do projeto. Identifica a fila, o estado e aparece nos arquivos de status. |
| `PREENCHER-NOME-DO-PROJETO` | `Projects[].Repository.WorkspacePath` | Última pasta do caminho do workspace. |
| `PREENCHER-NOME-DO-PROJETO` | `Projects[].ManualTriggerFile` | Nome do arquivo de gatilho manual (usado a partir da fase 2). |
| `PREENCHER-URL-DO-REPOSITORIO` | `Projects[].Repository.Url` | URL do repositório no Azure DevOps. |
| `PREENCHER-VERSAO-DO-EDITOR` | `Defaults.Unity.EditorVersion` | Versão exata do editor, ex.: `6000.0.47f1`. Cada projeto pode sobrescrever a sua. |
| `PREENCHER-PASTA-DE-DESTINO` | `Projects[].Publishing.ArtifactFolder` | Pasta onde o time pega o zip. |
| `PREENCHER-DESTINO-RAIZ` | `Scheduler.GlobalStatusFile` | Raiz do compartilhamento, onde vai o `_STATUS-GERAL.txt` (fase 2). |

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

**Falha ao copiar não descarta o artefato.** O zip nasce no staging local. Se o destino estiver fora do ar ou sem permissão, a build permanece bem-sucedida, o zip continua no staging e a cópia fica marcada como pendente. O reenvio automático é fase 3.

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
├── tools/                        scripts de instalação (fase 3)
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
| Gatilho manual por arquivo observado | a fazer |
| Retenção por contagem | a fazer |

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

> Os demais campos de configuração da fase 2 (`MaintainLatestFolder`, `WriteStatusFiles`, `IncludeLauncher`, `Retention`, `ManualTriggerFile`, `GlobalStatusFile`) já existem e são validados, mas ainda não têm efeito.

### Fase 3 — operação (não começou)

Teams por webhook, reenvio de artefato pendente, hook `post-merge`, `install-service.ps1`, `set-secrets.ps1`, registro como Windows Service.
