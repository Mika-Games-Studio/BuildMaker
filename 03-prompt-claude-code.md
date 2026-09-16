# Prompt para o Claude Code

> Cole o conteúdo abaixo no Claude Code, com `01-relatorio-arquitetura.md` e `02-especificacao-tecnica.md` no diretório do projeto.

---

## Contexto

Você vai construir o **UnityLocalCI**, um serviço de integração contínua que roda inteiramente em uma máquina Windows local. A empresa vetou o uso de CI em nuvem, então nada de GitHub Actions, Azure Pipelines hospedado ou agente self-hosted: a orquestração inteira precisa ser nossa.

Leia `01-relatorio-arquitetura.md` e `02-especificacao-tecnica.md` antes de escrever qualquer código. A especificação é a fonte da verdade; este prompt define como trabalhar.

**O que o sistema faz:** observa a branch de homologação de **um ou mais projetos Unity**, detecta commits novos, executa o build, compacta o resultado e grava o zip na pasta de cada projeto.

Cada projeto tem repositório, workspace, versão de editor e destino próprios. Um projeto nunca constrói duas vezes ao mesmo tempo; projetos distintos constroem em paralelo até o teto do scheduler. A configuração já nasce multiprojeto: não implemente para um só e generalize depois.

Não há integração com serviço externo, servidor HTTP, API ou painel web. A máquina de build já é acessível ao time, então a publicação é uma cópia de arquivo e a própria pasta comunica o estado, por meio de um `_STATUS.txt`. Resista à tentação de acrescentar um dashboard: foi decisão explícita não ter um.

**Ambiente:** Windows, .NET 8, Unity 6 com licença Pro por serial, alvo WebGL, repositório no Azure DevOps.

## Antes de começar

Verifique e me pergunte se algo não estiver claro:

1. Confirme as versões instaladas: `dotnet --version`, `git --version`, `unity --version`. Se o Unity CLI não estiver presente, pare e me avise antes de instalar qualquer coisa.
2. Confirme a URL do repositório, a versão exata do editor Unity e os caminhos de workspace e artefatos.
3. Não invente valores de configuração. Onde faltar informação, use um placeholder óbvio e liste tudo no README.

## Como trabalhar

Implemente em **três fases**, e **pare ao final de cada uma** para eu revisar e testar. Não avance sozinho.

### Fase 1 — Núcleo

Entrega: merge na HML gera zip em pasta local, sem intervenção manual.

- Solução .NET 8 com a estrutura de projetos da seção 2 da especificação
- Estado em SQLite conforme a seção 4, incluindo recuperação de build órfã no boot
- `GitWatcher` com polling e debounce, **uma instância por projeto** (seção 5.1)
- Scheduler com fila por projeto e semáforo global com guarda de RAM e disco (seção 5.2)
- Etapas Sync, Build e Package (seção 5.3)
- `FolderPublisher` com staging local antes da cópia para o destino
- Log por build em arquivo, mais console
- Leitura de segredos do Windows Credential Manager
- Execução como aplicação de console nesta fase; o serviço vem depois

**Pare aqui.** Eu vou rodar contra o repositório real antes de seguir.

### Fase 2 — Usabilidade

- Pasta `latest\` com a build descompactada, trocada por rename de diretório para nunca ficar em estado parcial
- `_STATUS.txt` e `_HISTORICO.txt` por projeto, mais o `_STATUS-GERAL.txt` consolidado, no formato da seção 5.3, etapa 4
- `rodar.bat` embutido no zip e na pasta `latest\`, com as três estratégias de servidor local em ordem de preferência
- Cópia do log da build para `_logs\`
- Disparo manual por arquivo observado (seção 5.4), sem endpoint HTTP
- Retenção por contagem e verificação de espaço em disco
- `Builder.cs` completo em `unity/`, com instruções de instalação no projeto Unity

**Pare aqui.**

### Fase 3 — Operação

- Notificador do Teams por webhook
- Reenvio de artefato com cópia pendente (`POST /api/builds/{id}/republish`)
- Hook `post-merge` e endpoint de sinal em loopback
- `install-service.ps1` e `set-secrets.ps1`
- Registro como Windows Service

## Regras que não podem ser violadas

Estas são as armadilhas que já custaram caro em projetos parecidos. Trate cada uma como requisito:

1. **O `git clean` precisa preservar `Library/`, `Temp/`, `obj/`, `Logs/` e `UserSettings/`.** Sem as exclusões, toda build vira build limpa e passa de 15 para 45 minutos. Escreva um teste que falhe se as exclusões sumirem.

2. **Sucesso da build é determinado pelo exit code, nunca pela existência da pasta de saída.** O Unity deixa artefatos parciais em disco mesmo quando falha.

3. **O `Builder.cs` precisa chamar `EditorApplication.Exit(1)` quando o `BuildReport` não for `Succeeded`.** Sem isso o Unity retorna 0 em build quebrada e o pipeline publica lixo.

4. **O timeout precisa encerrar a árvore de processos.** O Unity gera filhos de IL2CPP e Emscripten que sobrevivem a um kill do processo pai e travam o próximo build segurando o lock da `Library`.

5. **Nenhum segredo em arquivo de configuração, log, mensagem de erro ou URL de remote do Git.** O PAT vai por `http.extraHeader` na invocação, não persistido. Configuração guarda apenas o nome da credencial.

6. **Falha ao copiar para a pasta de destino não descarta o artefato.** O zip é gerado primeiro em staging local; a build continua bem-sucedida, a cópia fica marcada como pendente e é retentada. Nunca apagar o staging antes de confirmar a cópia.

6b. **O pipeline nunca altera Player Settings do Unity.** Compressão, qualidade e template já estão configurados no projeto e são responsabilidade de quem o mantém. O `Builder.cs` lê, nunca escreve. Sobrescrever faria uma mudança feita no Editor ser silenciosamente desfeita na build, o que é pior que qualquer problema que isso resolveria.

A única verificação permitida é um **aviso**: se `PlayerSettings.WebGL.compressionFormat` estiver em Brotli ou Gzip com `decompressionFallback` desligado, registrar alerta destacado no log e no `_STATUS.txt`. O motivo é que essa combinação exige `Content-Encoding: br` do servidor, e como a distribuição é por pasta, ninguém terá isso; o sintoma seria tela preta sem erro. Avise e siga em frente. Não corrija, não aborte.

6c. **A pasta `latest\` é trocada por rename de diretório**, nunca apagando e recopiando no lugar. Senão alguém abre a pasta no meio da cópia e pega uma build quebrada.

7. **Uma build por vez por projeto, nunca duas no mesmo workspace.** O Unity trava a `Library` do diretório; dois Editores no mesmo lugar corrompem o cache. Projetos em diretórios diferentes podem rodar em paralelo.

7b. **O teto global de concorrência é obrigatório, além do lock por projeto.** A memória não distingue projetos: dois builds WebGL simultâneos consomem 16 a 32 GB venham de onde vierem. Verifique RAM livre antes de tomar vaga no semáforo, e adie o job em vez de descartá-lo.

7c. **Falha em um projeto não pode afetar os outros.** Escopo de DI, `CancellationTokenSource` e encerramento de processos por timeout são todos por build.

8. **Build WebGL servido por HTTP exige os headers de `Content-Encoding` do Brotli.** Sem eles a página não carrega, e o sintoma é uma tela preta sem erro útil.

9. **Log do Unity é enorme.** Extraia e destaque erros de compilação nas primeiras linhas do resumo, em vez de deixar o usuário procurar em dezenas de milhares de linhas.

## Padrões de código

- C# moderno: nullable habilitado, `async/await` com `CancellationToken` propagado em toda operação de I/O
- Injeção de dependência via `Microsoft.Extensions.Hosting`, sem singleton estático
- Toda dependência externa atrás de interface: `IGitClient`, `IUnityCliClient`, `IArtifactPublisher`, `INotifier`, `IClock`
- Configuração tipada com `IOptions<T>` e validação na inicialização; credencial ausente derruba o serviço no start com mensagem acionável, não no meio da primeira build
- Logging estruturado com `ILogger<T>`, sempre incluindo `BuildId` e `CommitSha` no escopo
- Erros esperados (rede, disco, falha de build) tratados com resultado tipado; exceção reservada para o inesperado

## Testes

Teste de unidade com xUnit para a lógica que erra em silêncio:

- debounce: rajada de commits produz um job só, do mais recente
- scheduler: fila de um projeto não bloqueia a de outro; semáforo global respeita `MaxConcurrentBuilds`
- guarda de recursos: RAM abaixo do mínimo adia o job, não o descarta
- fila: job novo substitui o enfileirado, nunca o em execução
- exclusões do `git clean` (regressão dos itens da seção acima)
- parser de resultado do Unity, incluindo saída de falha
- publicação: cópia parcial nunca é exposta com o nome final; falha de cópia marca pendência sem derrubar a build
- geração do `_STATUS.txt` em caso de sucesso e de falha de compilação
- escrita concorrente do `_STATUS-GERAL.txt` por duas builds terminando juntas
- retenção: mantém exatamente `KeepLastBuilds`
- recuperação de build órfã no boot

Processos externos, Git, Unity CLI e sistema de arquivos devem ser mockados. Não escreva testes que dependam de rede nem de compartilhamento montado.

## Entregáveis

Ao final de cada fase, entregue código compilando e:

- `README.md` com instalação passo a passo, pré-requisitos e todos os placeholders de configuração listados
- `TROUBLESHOOTING.md` cobrindo, no mínimo: licença Unity não ativada, build travando sem retorno, tela preta ao abrir o WebGL, pasta de destino sem permissão ou offline, e disco cheio
- um resumo do que foi feito, o que ficou pendente e o que você precisa de mim para seguir

## Sobre comunicação

Se algo na especificação estiver errado, ambíguo ou for má ideia, **fale antes de implementar**. Prefiro discutir uma decisão a receber código que precise ser refeito.

Não instale dependências pesadas sem perguntar. Preferência por biblioteca padrão do .NET; a única dependência externa prevista é o acesso ao Credential Manager.

Comece confirmando os itens da seção "Antes de começar" e me apresentando seu plano para a Fase 1.
