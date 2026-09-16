# Relatório de Arquitetura — CI Local para Unity WebGL

**Projeto:** automação de build acionada por merge na branch HML
**Data:** 14/09/2026
**Status:** decisões validadas, pronto para implementação

---

## 1. Problema

Hoje a build de homologação é manual. Alguém precisa lembrar de abrir o Unity, buildar, compactar e enviar o arquivo para quem vai testar. Isso custa tempo, gera build de commit errado e depende de uma pessoa específica estar disponível.

O objetivo é reproduzir o comportamento do GitHub Actions (`on: push: branches: [HML]`) **inteiramente dentro da rede da empresa**, porque o uso de CI em nuvem foi vetado.

## 2. Restrições

| Restrição | Origem | Impacto |
|---|---|---|
| CI não pode rodar em nuvem | Política da empresa | Orquestração precisa morar na máquina de build |
| Máquina Windows única | Infraestrutura existente | Paralelismo limitado pela RAM; fila obrigatória |
| Unity Pro com serial | Licenciamento atual | Licença node-locked, ativa uma vez |
| Alvo WebGL | Produto | Build lento (20–50 min), sem assinatura de app |
| Sem dependência de outras áreas | Decisão de projeto | Publicação em pasta local, sem API externa |

## 3. Decisões

### 3.1 Gatilho: polling do `origin/HML` + hook local

**Escolhido:** um serviço local faz `git fetch` a cada 60 segundos e compara o SHA de `origin/HML` com o último construído. Em paralelo, um hook `post-merge` na máquina do desenvolvedor apenas sinaliza o serviço para verificar imediatamente.

**Alternativas descartadas:**

- *Webhook do Azure DevOps.* Exigiria que o servidor alcançasse a máquina local: porta aberta, IP fixo ou túnel, mais pedido formal para a infraestrutura. Alto custo político para benefício marginal.
- *Somente hook local.* Só dispara quando o merge acontece naquela máquina. Merge feito pela interface web do DevOps, ou por outro desenvolvedor, passaria despercebido. Hooks também vivem fora do versionamento e somem em um novo clone.

**Por que a combinação:** o polling é a fonte da verdade e garante que nenhum merge se perca, inclusive os feitos enquanto a máquina estava desligada. O hook é apenas otimização de latência e pode falhar sem consequência.

### 3.2 Publicação: cópia para pasta, sem camada de rede

**Escolhido:** o artefato é gravado em um diretório definido em configuração. A máquina de build já é acessível a todo o time, então quem precisa do zip abre a pasta e pega. Não há servidor HTTP, não há link, não há porta aberta.

**Motivo:** zero dependência de outras áreas e zero infraestrutura para manter. Some o servidor web, some a configuração de hostname, some a liberação de firewall, somem os headers de compressão. Menos superfície para quebrar, e qualquer pessoa da equipe entende o sistema em trinta segundos.

**Implicação para o projeto Unity:** sem um servidor sob nosso controle, a configuração de compressão WebGL do projeto passa a importar. Isso é responsabilidade de quem mantém o projeto, não do pipeline; ver 3.3.

**Alternativas descartadas:**

- *SharePoint via Microsoft Graph.* Exigiria App Registration no Entra ID com consentimento de administrador, introduzindo dependência de terceiro num projeto que não precisa dela.
- *Servidor HTTP local com links.* Resolveria o caso de acesso remoto e compartilhamento por chat, mas custa uma porta liberada, um serviço a mais e configuração de headers. Não se justifica quando todos já alcançam a pasta.

A camada de publicação continua sendo uma interface (`IArtifactPublisher`), então acrescentar um destino remoto depois não exige tocar no pipeline.

### 3.3 Configurações de build pertencem ao projeto Unity

**Decisão:** o pipeline não altera Player Settings. Compressão, qualidade, template de WebGL e afins ficam versionados no projeto e são responsabilidade de quem o mantém. O CI apenas executa o que estiver configurado.

**Motivo:** configuração em dois lugares sempre diverge. Se o pipeline sobrescrevesse, uma mudança feita no Editor seria silenciosamente desfeita na build, e o desenvolvedor levaria horas para entender por quê.

**Exceção, e ela é só de leitura:** antes de buildar, o pipeline inspeciona `PlayerSettings.WebGL.compressionFormat`. Se estiver em Brotli ou Gzip **e** `decompressionFallback` estiver desligado, ele registra um aviso destacado no log e no `_STATUS.txt`.

O motivo do aviso é que essa combinação gera um build que só funciona atrás de um servidor configurado para enviar `Content-Encoding: br`. Como a distribuição aqui é por pasta, e a pessoa vai abrir com um servidor estático qualquer, o sintoma seria tela preta sem mensagem de erro. O pipeline não corrige nada, apenas avisa, e a decisão continua sendo de quem cuida do projeto.

### 3.4 A pasta é a interface

Sem painel web, a própria pasta precisa comunicar o estado. Layout proposto:

```
\\build01\builds\hml\
├── _STATUS.txt                          última build: resultado, commit, autor, data, duração
├── _HISTORICO.txt                       últimas 20 builds, uma linha cada
├── latest\                              última build, descompactada e pronta para rodar
│   └── rodar.bat
├── MeuJogo-HML-20260914-a1b2c3d.zip
├── MeuJogo-HML-20260913-9f8e7d6.zip
└── _logs\
    └── build-42.log
```

`_STATUS.txt` é o que evita a pergunta "a build saiu?" no chat. Quando falha, ele traz o erro de compilação resumido, e quem quiser detalhe abre o log ao lado.

A pasta `latest\` descompactada é o atalho para quem só quer testar: entra, roda o `rodar.bat`, joga. Não precisa baixar nem descompactar nada.

### 3.5 Múltiplos projetos, serialização por projeto

**Escolhido:** a configuração é uma lista de projetos. Cada um tem repositório, branch, workspace, versão de editor e pasta de destino próprios. Um projeto nunca constrói duas vezes ao mesmo tempo; projetos distintos constroem em paralelo.

**Motivo do limite por projeto:** o Unity coloca um lock na `Library` do diretório do projeto. Dois Editores no mesmo diretório corrompem o cache. Em diretórios diferentes são dois processos independentes, que não se conhecem.

**Motivo do teto global:** a memória não distingue projetos. Um build WebGL consome de 8 a 16 GB na fase de link do IL2CPP com Emscripten, então dois em paralelo custam o mesmo sejam eles de jogos diferentes ou não. Estourar a RAM não deixa lento, faz o build morrer com um erro obscuro do Emscripten. O teto padrão é derivado da RAM instalada, com mínimo de 1.

**Alternativa descartada:** paralelizar variantes do mesmo projeto em workspaces clonados. Daria ganho de tempo real, mas cada workspace carrega uma `Library` de 20 a 60 GB, e o custo em disco não se justifica quando o objetivo é apenas não esperar um jogo para construir outro.

### 3.6 Workspace persistente

**Escolhido:** um clone dedicado e permanente da HML, reaproveitado entre builds, preservando `Library/` e o cache do IL2CPP.

**Motivo:** build WebGL limpo leva 20–50 minutos; incremental cai para 8–15. Em um pipeline disparado a cada merge, isso é a diferença entre uma ferramenta usada e uma ignorada. Não é otimização, é requisito.

**Cuidado associado:** o `git clean` do passo de sincronização precisa excluir `Library/`, `Temp/` e `obj/` explicitamente. Um `git clean -xdf` sem exclusões apaga o cache e transforma toda build em build limpa.

### 3.7 Fila serial com debounce

**Escolhido:** uma build por vez **por projeto**, com janela de silêncio de 2 minutos antes de iniciar. Merges que chegarem durante a espera colapsam em um único job, do commit mais recente.

**Motivo:** o Unity trava a pasta `Library`; duas execuções simultâneas no mesmo workspace corrompem o cache. E com build de 30 minutos, enfileirar três merges sequenciais significaria 90 minutos produzindo dois artefatos que já nascem obsoletos.

### 3.8 Stack: .NET 8 Worker Service

**Escolhido:** C# sobre .NET 8, rodando como Windows Service.

**Motivo:** o time já é C# por causa do Unity, então a manutenção não fica dependente de uma pessoa. Windows Service é nativo, com reinício automático e integração com o Event Log, e o projeto não precisa de nenhuma dependência além da biblioteca padrão.

**Alternativa descartada:** Python. Sai do chão mais rápido, mas na prática vira o script que só o autor mantém.

### 3.9 Licença: ativação única

Unity Pro com serial é node-locked. A ativação acontece uma vez, no setup, e o arquivo resultante fica em `C:\ProgramData\Unity\Unity_lic.ulf`, que é **por máquina e não por usuário**. Isso significa que o serviço pode rodar sob uma conta de serviço sem sessão interativa. Não há devolução de seat por build, ao contrário do modelo de licença flutuante.

## 4. Arquitetura

```
                    ┌──────────────────────────────┐
   merge na HML ──► │  Watcher                     │
   (qualquer fonte) │  git fetch a cada 60s        │
                    │  compara SHA vs estado       │
                    └──────────────┬───────────────┘
   hook post-merge ────────────────┤ (sinal opcional, só antecipa)
                                   ▼
                    ┌──────────────────────────────┐
                    │  Fila  (1 slot, debounce 2m) │
                    └──────────────┬───────────────┘
                                   ▼
     ┌─────────────────────────────────────────────────────────┐
     │  Pipeline                                               │
     │                                                         │
     │  1. Sync      git fetch + reset --hard origin/HML       │
     │               clean preservando Library/Temp/obj        │
     │  2. Build     unity build --target WebGL                │
     │  3. Package   zip + manifest.json + rodar.bat           │
     │  4. Publish   copia zip + atualiza latest\ e _STATUS.txt │
     │  5. Notify    Teams (opcional) + log                    │
     └─────────────────────────────────────────────────────────┘
                                   │
                    ┌──────────────┴───────────────┐
                    │  \\build01\builds\hml\        │
                    │  zips, latest\, _STATUS.txt   │
                    │  acesso direto pelo time      │
                    └──────────────────────────────┘
```

## 5. Riscos

| Risco | Probabilidade | Impacto | Mitigação |
|---|---|---|---|
| Pasta de destino indisponível (rede caiu) | Média | Médio | Grava primeiro em disco local, copia depois; artefato nunca se perde |
| WebGL não abre ao dar duplo clique no `index.html` | Certa, se não tratado | Médio | Compressão desligada no build e `rodar.bat` incluído |
| Disco enche com builds e caches | Alta | Alto | Retenção por contagem, mais checagem de espaço livre antes de iniciar |
| Time não sabe se a build saiu | Alta | Baixo | `_STATUS.txt` na raiz da pasta, atualizado a cada execução |
| Build trava sem retornar | Média | Médio | Timeout de 90 min com encerramento da árvore de processos |
| `git clean` apaga o cache do Unity | Média | Alto | Exclusões explícitas, cobertas por teste |
| Máquina reinicia no meio da build | Média | Baixo | Estado persistido; job interrompido volta para a fila no boot |
| Serial do Unity vaza em arquivo de config | Média | Alto | Segredos no Windows Credential Manager, config guarda só o nome da chave |

## 6. Entrega em fases

**Fase 1 — núcleo funcional.** Watcher com polling, fila, sync do git, build Unity, zip, publicação em pasta local, log em arquivo. Já resolve o problema principal e não depende de ninguém de fora.

**Fase 2 — usabilidade.** Pasta `latest\` descompactada, `rodar.bat`, `_STATUS.txt`, `_HISTORICO.txt`, logs organizados, retenção automática.

**Fase 3 — operação.** Notificação no Teams, hook `post-merge`, instalação como Windows Service, scripts de setup.

A fase 1 já resolve o problema central. A fase 2 é o que faz o time adotar, porque tira a dúvida de "saiu ou não" e permite testar sem descompactar. A fase 3 é conveniência operacional e pode esperar.

## 7. Dependências externas

Com a publicação em pasta local, o projeto deixou de depender de outras áreas. Resta o mínimo:

| Item | Responsável | Bloqueia |
|---|---|---|
| PAT do Azure DevOps com escopo `Code: Read` | Próprio usuário | Fase 1, se o repositório exigir autenticação |
| Serial do Unity Pro | Já disponível | Fase 1 |
| Confirmar que a licença permite dois Editores simultâneos | Verificar na primeira execução paralela | Paralelismo entre projetos |
| Pasta de destino com permissão de escrita | Próprio usuário | Fase 1 |
| Acesso de leitura do time à pasta de destino | Já existe | Fase 1 |

## 8. Fora de escopo

Execução de testes automatizados, build de outras plataformas, paralelismo de variantes dentro de um mesmo projeto, múltiplas máquinas de build, deploy em produção e assinatura de artefatos. Painel web, publicação em SharePoint e qualquer destino remoto também ficam de fora por ora, mas a camada de publicação é uma interface justamente para acomodá-la depois sem reescrita.
