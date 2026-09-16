# TROUBLESHOOTING

Sintomas reais e o que fazer com cada um. Começa pelos que já aconteceram nesta máquina.

---

## O serviço não sobe

### "Configuracao invalida. Corrija os itens abaixo em appsettings.json"

Funcionando como esperado: a validação roda na inicialização justamente para não quebrar no meio da primeira build, quarenta minutos depois. A mensagem lista cada item. Os mais comuns:

**"a credencial X nao existe no Windows Credential Manager"**

```bash
cmdkey /generic:UnityLocalCI_AzureDevOpsPat /user:pat /pass:SEU_PAT_AQUI
```

Confira que a credencial foi gravada **na mesma conta de usuário** que roda o serviço. O Credential Manager é por usuário, não por máquina. Se o serviço roda sob uma conta de serviço, grave logado como ela.

```bash
cmdkey /list:UnityLocalCI_AzureDevOpsPat
```

**"Unity.EditorVersion nao configurado"** — nem o projeto nem `Defaults` trazem a versão. Não é adivinhada de propósito: buildar na versão errada do editor produz um artefato que parece certo e não é.

**"compartilha o workspace X com Y"** — dois projetos apontando para o mesmo diretório. O Unity trava a `Library` do diretório; dois Editores no mesmo lugar corrompem o cache. Cada projeto precisa do seu.

### "You must install .NET to run this application"

O `.exe` procura o runtime em `C:\Program Files\dotnet` e o seu está no perfil do usuário (`%USERPROFILE%\.dotnet`).

Contorno imediato:

```bash
dotnet src/UnityLocalCI.Worker/bin/Debug/net10.0-windows/UnityLocalCI.Worker.dll
```

Correção definitiva, e obrigatória antes da fase 3: instale o .NET para toda a máquina, ou defina `DOTNET_ROOT` no ambiente do serviço. Uma conta de serviço não enxerga o `.dotnet` do seu perfil.

---

## Licença do Unity

### Build falha logo no início com erro de licença, ou o editor abre pedindo login

A licença não foi ativada nesta máquina, ou foi ativada sob outro usuário.

```bash
unity license status
```

Se não houver licença ativa:

```bash
unity license activate --serial <SERIAL> --username <USER> --password <PASS>
```

Unity Pro com serial é *node-locked*: ativa uma vez, o `.ulf` fica em `C:\ProgramData\Unity\Unity_lic.ulf` e vale **para a máquina**, não para o usuário. Se `unity license status` acusa licença válida mas o build reclama, confira se esse arquivo existe e se a conta de serviço tem permissão de leitura nele.

Não há devolução de seat por build, ao contrário do modelo de licença flutuante. Se o mesmo serial estiver ativado em outra máquina, desative lá primeiro.

---

## Build travando sem retorno

### A build fica horas em `Running` e nada acontece

O timeout (`Unity.TimeoutMinutes`, padrão 90) encerra a **árvore inteira** de processos, não só o processo pai. O Unity gera filhos de IL2CPP e Emscripten que sobrevivem a um kill simples e ficam segurando o lock da `Library`, travando o próximo build também.

Se você precisa intervir antes do timeout:

```bash
tasklist | findstr /i "Unity il2cpp emcc node clang"
```

Mate a árvore pelo PID do Unity, nunca só o processo pai:

```bash
taskkill /PID <pid-do-unity> /T /F
```

Depois confira se sobrou lock no workspace. Se o próximo build falhar reclamando da `Library`, apague `Library/Lock`:

```bash
del /f C:\ci\workspace\<projeto>\Library\Lock
```

**Nunca apague a `Library` inteira para "destravar".** Ela é o cache que mantém a build em 8–15 minutos em vez de 20–50. A próxima build a reconstrói do zero.

### Toda build está demorando o tempo de uma build limpa

O cache do Unity está sendo apagado a cada sincronização. Quase sempre é o `git clean` sem exclusões.

O `clean` correto exclui `Library/`, `Temp/`, `obj/`, `Logs/` e `UserSettings/`. Existe um teste de regressão sobre isso (`GitCleanArgumentsTests`); se alguém encurtou a lista, ele falha:

```bash
dotnet test --filter GitCleanArguments
```

Se o teste passa e a build continua lenta, confira se alguém roda `git clean -xdf` por fora do pipeline, num script ou numa tarefa agendada.

---

## Tela preta ao abrir o WebGL

O build carrega, o console do navegador não mostra erro útil, e a tela fica preta. Há três causas, em ordem de frequência.

### 1. Abriu o `index.html` por duplo clique

O navegador bloqueia `.wasm` e `.data` no protocolo `file://`. Não é bug da build.

A partir da fase 2, use o `rodar.bat` que vem no zip. Enquanto ele não existe, suba um servidor estático na pasta:

```bash
python -m http.server 8000
```

Depois abra `http://localhost:8000`.

### 2. Compressão Brotli ou Gzip sem `decompressionFallback`

A causa mais cara de diagnosticar, e a razão de o pipeline emitir um aviso destacado no log.

São **duas compressões independentes**, em camadas diferentes:

- o **zip** da etapa 3 é transporte: some quando a pessoa extrai;
- a **compressão do Unity** acontece dentro do build e grava `game.wasm.br` e `game.data.br`, que **continuam comprimidos depois de o zip ser extraído**.

Quem deveria descomprimir esses arquivos é o navegador, e ele só faz isso se o servidor enviar `Content-Encoding: br`. Nenhum servidor estático simples faz isso. Como a distribuição aqui é por pasta, a combinação Brotli sem fallback produz um build inutilizável na prática.

**O pipeline não corrige isso, e não vai corrigir.** Player Settings pertencem ao projeto Unity e são responsabilidade de quem o mantém; sobrescrever faria uma mudança feita no Editor ser silenciosamente desfeita na build, o que é pior que o problema original.

Quem cuida do projeto tem duas saídas, em *Project Settings → Player → WebGL → Publishing Settings*:

| Opção | Custo |
|---|---|
| Desligar a compressão | A pasta cresce de 2 a 3 vezes; o zip final cresce pouco, porque dado já comprimido não comprime de novo. |
| Manter Brotli e ligar `decompressionFallback` | Alguns segundos a mais em todo carregamento. |

### 3. Erro de JavaScript no carregamento

Abra o console do navegador (F12). Se houver erro de `.data` não encontrado, o zip foi extraído parcialmente ou a pasta `Build/` não veio junto. Compare o SHA-256 do zip no destino com o do staging.

---

## Pasta de destino sem permissão ou offline

### O log diz "sem permissao de escrita em X" ou "pasta de destino X indisponivel"

A build **não falhou**. Isso é por construção: o zip nasce no staging local e só depois é copiado. Ele continua em `Publishing.StagingFolder` e a cópia fica marcada como `PendingCopy` no banco.

Confira o que está no staging:

```bash
dir C:\ci\staging\*.zip
```

Teste a permissão com a conta que roda o serviço:

```bash
echo teste > \\build01\builds\crash\hml\_teste.txt
```

Causas comuns: conta de serviço sem permissão no compartilhamento (o serviço não roda com o seu usuário), compartilhamento fora do ar, ou pasta pai inexistente.

**O reenvio automático é fase 3.** Por ora, depois de corrigir a permissão, copie o zip do staging para o destino manualmente.

### Sobrou um arquivo `.part` no destino

Uma cópia foi interrompida no meio. O nome temporário existe exatamente para que ninguém pegue um zip pela metade com o nome final. Pode apagar: o artefato íntegro continua no staging.

---

## Disco cheio

### O log diz "espaco livre em X e N GB, abaixo do minimo"

A build foi **adiada**, não descartada. Ela espera na fila e roda assim que houver espaço; o motivo da espera aparece no log a cada verificação.

O que costuma ocupar o disco, em ordem de tamanho:

| O que | Onde | Tamanho típico |
|---|---|---|
| `Library/` de cada projeto | `C:\ci\workspace\<projeto>\Library` | 20 a 60 GB **cada** |
| Zips antigos | `Publishing.ArtifactFolder` | 50 a 200 MB cada |
| Staging | `C:\ci\staging` | um zip por build recente |
| Logs | `C:\ci\logs` | pequeno |

A retenção automática é fase 2. Por ora, apague os zips mais antigos do destino e do staging — **nunca a `Library`**, que é o que mantém a build rápida.

Se precisar mesmo recuperar espaço e só sobrar a `Library`, apague a de um projeto **desabilitado** (`Enabled: false`). A próxima build dele será lenta, mas nenhuma outra é afetada.

Ajuste `Retention.MinFreeDiskGb` por projeto se 50 GB for conservador demais para a sua máquina. Baixar demais troca "build adiada" por "build que morre no meio", que é bem pior.

---

## Nada acontece depois de um merge

### O watcher não enfileira nada

Primeiro, o debounce: a janela padrão é de 2 minutos de silêncio **depois** do último commit. Merges em sequência colapsam num job só. Antes disso o log mostra:

```
[Crash] commit novo a1b2c3d; aguardando 120s de silencio antes de enfileirar.
```

Se nem isso aparece, o polling não está enxergando o commit:

```bash
git ls-remote <url-do-repo> HML
```

Compare com o que o serviço tem gravado. O estado fica em `watcher_state`, na chave `<projeto>:last_built_sha`:

```bash
sqlite3 C:\ci\state\unitylocalci.db "select * from watcher_state;"
```

Se `last_built_sha` já for o SHA do merge, o commit foi construído: procure o resultado em `builds`.

```bash
sqlite3 C:\ci\state\unitylocalci.db "select id,project,status,error_summary from builds order by id desc limit 10;"
```

### O log diz "falha ao consultar o repositorio"

Erro de rede ou autenticação. É tratado como condição esperada: o watcher loga e tenta de novo no próximo ciclo, sem derrubar o projeto nem os outros.

Se persistir, o PAT provavelmente expirou. Gere outro no Azure DevOps com escopo `Code: Read` e regrave com `cmdkey`. O serviço lê a credencial a cada invocação, mas a validação de existência é no start — reinicie depois de trocar.

---

## Uma build afetou outro projeto

Não deveria acontecer. Cada build roda em seu próprio escopo de DI, com seu próprio `CancellationTokenSource`, e o encerramento por timeout mata apenas a árvore de processos daquele build.

O que **é** compartilhado por construção: o teto global de builds simultâneas. Com `MaxConcurrentBuilds = 1`, o segundo projeto espera o primeiro terminar — isso é o comportamento pretendido, não uma falha. O log mostra o teto efetivo na inicialização:

```
Scheduler com 2 projeto(s) e teto global de 2 build(s) simultanea(s) (configurado 2, RAM instalada 32.0 GB)
```

Se o teto efetivo for menor que o configurado, é a RAM: o valor é o menor entre o configurado e `RAM_instalada / 16`. Não aumente isso à força. Dois builds WebGL simultâneos consomem de 16 a 32 GB na fase de link do IL2CPP, e estourar a RAM não deixa o build lento — faz ele morrer com um erro obscuro do Emscripten.
