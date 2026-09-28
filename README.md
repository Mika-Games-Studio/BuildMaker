# BuildMaker

> O programa se chama **BuildMaker** na tela, com "Unity Local CI" como descritor. No código nada mudou: o namespace continua `UnityLocalCI.App`, o executável `UnityLocalCI.exe`, o serviço do Windows `UnityLocalCI` e a credencial `UnityLocalCI_GitHub` — renomear qualquer um deles quebraria as instalações que já existem.

Serviço de integração contínua que roda inteiramente numa máquina Windows local. Observa a branch de homologação de um ou mais projetos Unity, detecta commits novos, executa o build, compacta o resultado e copia o zip para a pasta de cada projeto.

Aplicação .NET que roda na própria máquina, com janela e ícone na bandeja. Sem nuvem e sem painel web: a pasta de destino continua sendo a interface para quem só quer o artefato.

**Estado atual: as três fases concluídas.** Ver [O que já existe](#o-que-já-existe-e-o-que-falta) ao final.

---

## Como instalar e executar

Quatro passos, do zero até a primeira build. O mesmo roteiro está **dentro do programa, na página Tutorial** — porque quem instala na máquina de build nem sempre é quem clonou este repositório.

### 1. Gerar o pacote

Na máquina onde está o código, com o [.NET SDK 10](https://dotnet.microsoft.com/download) instalado:

```bash
powershell -ExecutionPolicy Bypass -File tools\publicar.ps1
```

Roda os testes e publica um **executável único** de ~51 MB com o runtime .NET dentro dele, assinado. Quem receber esse arquivo **não precisa instalar .NET nenhum**.

Para publicar uma versão para o time, o caminho é uma tag — o workflow monta o `.exe` do Windows e os `.deb` do Ubuntu na mesma release:

```bash
git tag v1.1.0 && git push origin v1.1.0
```

### 2. Instalar

**O executável é o instalador.** Abra o `.exe` e clique em **Instalar** na faixa do topo da janela. Não há zip para extrair nem script para rodar.

O programa funciona antes de instalar — as builds rodam, a configuração salva. Instalar é o que cria os atalhos, liga o início automático e registra em Aplicativos Instalados. Depois de instalado a faixa não aparece mais, porque a cópia que roda passa a ser a de `Programs\BuildMaker`.

Para automação, sem janela:

```bash
UnityLocalCI.exe --instalar
```

Instala em `%LOCALAPPDATA%\Programs\BuildMaker`, cria atalho no menu Iniciar e na área de trabalho, e registra o programa em **Configurações → Aplicativos** — é de lá que ele aparece na busca do Windows e pode ser desinstalado. Uma reinstalação por cima preserva o `appsettings.json` e os projetos cadastrados.

**O programa e os dados ficam separados:** o binário em `Programs\BuildMaker`, o histórico e o status em `%LOCALAPPDATA%\BuildMaker`. É onde o VS Code e o Rider põem os seus, e é o que faz desinstalar apagar o programa sem tocar no histórico. Instalar em `C:\Program Files` exigiria administrador; instalação por usuário vive no perfil do usuário.

Versões anteriores instalavam direto em `%LOCALAPPDATA%\UnityLocalCI`. O instalador migra a configuração e os projetos de lá e remove a pasta antiga.

**Não pede administrador**, e o programa também não: ele roda com o privilégio de quem o abriu, sem UAC. O início automático usa a chave `Run` do usuário, que é a que aparece na aba **Inicializar** do Gerenciador de Tarefas — onde se desliga com um clique.

Ao terminar, a cópia instalada abre e a que estava rodando sai de cena. É troca, e não soma: duas cópias vivas disputariam o banco e os arquivos de gatilho, e dois watchers enfileirariam a mesma build.

No Windows ele se chama **BuildMaker** — no atalho, na busca, no Gerenciador de Tarefas e em Aplicativos. O arquivo continua `UnityLocalCI.exe`: o nome do executável, o do serviço e o da credencial são identidade, não rótulo, e renomeá-los quebraria as instalações que já existem. O nome exibido vem da informação de versão do binário.

### 2b. Instalar no Ubuntu

O `.deb` da sua arquitetura, da mesma release:

```bash
sudo apt install ./buildmaker_1.1.0_amd64.deb
sudo nano /etc/buildmaker/appsettings.json
buildmaker verificar
sudo systemctl enable --now buildmaker
```

**A versão do Ubuntu não tem janela.** A interface são 35 arquivos de WinForms, que só existe no Windows — e um servidor de build não tem ninguém sentado na frente dele. O serviço é o mesmo: mesmo `Core`, mesmos passos, mesmo banco.

| | |
|---|---|
| `buildmaker servico` | roda o CI até receber sinal de parada (é o que o systemd chama) |
| `buildmaker verificar` | valida a configuração e sai; 0 se estiver boa |
| `buildmaker projetos` | lista os projetos configurados |
| `buildmaker buildar [nome]` | aciona o gatilho manual de um projeto, ou de todos |

Onde cada coisa fica:

| | |
|---|---|
| `/opt/buildmaker/buildmaker` | o programa (link em `/usr/bin`) |
| `/etc/buildmaker/appsettings.json` | configuração, declarada como `conffile` — o `apt upgrade` não a sobrescreve |
| `/var/lib/buildmaker` | banco e histórico; sobrevive ao `apt purge` |

O serviço roda como o usuário `buildmaker`, criado na instalação, e não como root: o Unity e o git são processos filhos e não precisam de poder de root na máquina.

> **O cofre de credenciais é mais fraco aqui.** No Windows o segredo é cifrado pelo sistema com a chave da conta. No Linux ele é um arquivo com permissão `0600` em `~/.config/BuildMaker/credenciais` — quem ler o arquivo lê o segredo. A alternativa seria o Secret Service (libsecret), que é um serviço de sessão gráfica e precisa de uma carteira destrancada por alguém: num servidor sem interface, o pipeline travaria esperando. É o mesmo acordo que `git-credential-store`, `docker` e `kubectl` fazem.

### 3. Conectar ao GitHub e ativar a licença

Na página **Configuração → GitHub**, clique em **Conectar ao GitHub**. Na maioria das máquinas acaba aí: o botão procura primeiro uma conta que já exista aqui e, achando, guarda o acesso com o nome da máquina e aponta todos os projetos para ele. Ninguém digita token, nome de credencial nem Client ID.

A busca vai do mais específico para o mais geral, e só o último caminho abre o navegador:

| Ordem | Caminho | O que precisa |
|---|---|---|
| 1 | **O acesso que já está no cofre**, de uma conexão anterior | nada |
| 2 | **A conta que o Git desta máquina já usa** | nada, se alguém já clonou por HTTPS ou usa o GitHub Desktop aqui |
| 3 | **Sessão do GitHub CLI** | nada, se o `gh` está instalado e conectado |
| 4 | **Fluxo de dispositivo** (código no navegador) | o *Client ID* de um OAuth App, uma vez por empresa — ele é público, esse fluxo não usa client secret |

> **É assim que o GitHub Desktop funciona.** Ele é um OAuth App registrado, com o Client ID embutido no programa, entra pelo navegador e guarda o token no Gerenciador de Credenciais do Windows — que é de onde o `git` tira a credencial depois. Por isso os três primeiros caminhos existem: numa máquina que já clonou por HTTPS, o token já está lá, e perguntar de novo seria pedir o que já se tem. A pergunta é feita ao próprio git, com `git credential fill`, então vale para qualquer auxiliar de credencial configurado.
>
> Para ficar idêntico ao GitHub Desktop — nem o Client ID à vista —, registre um OAuth App em `github.com/settings/applications/new` com *Enable Device Flow* marcado e cole o id na constante `GitHubConnection.BuiltInClientId`. Usar o id de outro programa seria se passar por ele para o servidor e para quem autoriza, então a constante nasce vazia.

**Entrar com outra conta** ignora a busca e abre a janela de sempre — é o caminho para trocar de conta ou entrar numa máquina limpa.

Tudo isso mora na aba **GitHub** da configuração, e não no cadastro de cada projeto: a conexão é da máquina. A aba mostra **a foto e o @ de quem está conectado**, lidos do próprio GitHub — "conectado" sozinho não diz se a conta é a do time ou uma pessoal esquecida na máquina. Mostra também o estado (conectado, apontando para credencial inexistente, ou sem conexão) e quantos projetos herdam. **Enquanto a foto e o @ aparecem, a conexão está de pé**: quem responde é o próprio GitHub, com o acesso guardado — token revogado vira "não reconheceu" ali mesmo, antes de uma build falhar por causa disso.

Nada nessa aba é editável: o nome da credencial é escolha do programa, e o Client ID só aparece dentro do botão, na única situação em que ele faz falta. Campo para os dois convidava a mexer no que a conexão já resolve sozinha.

Se preferir gravar um token à mão, o caminho antigo continua valendo:

```bash
cmdkey /generic:UnityLocalCI_GitHubPat /user:pat /pass:SEU_PAT_AQUI
```

A licença do Unity é ativada uma vez por máquina, com escopo de máquina, para valer também quando o CI roda como serviço:

```bash
unity license activate --serial <SERIAL> --username <USUARIO> --password <SENHA>
```

### 4. Cadastrar o projeto pela janela

Na página **Configuração → Projetos**, clique em **Vincular projeto Unity** e escolha a pasta do projeto que já existe na máquina. Dela saem sozinhos:

- a **URL** e a **branch**, lidas do `.git` daquele clone;
- a **versão do editor**, lida do `ProjectSettings/ProjectVersion.txt` do projeto — nunca adivinhada, nunca digitada.

A **pasta de destino** não é pedida aqui: ela é uma só, para todos os projetos, e fica na aba **Geral**. Todos os campos de caminho abrem a caixa de seleção do Windows, em vez de esperar o caminho digitado. Depois marque `Enabled` como `True` e use **Salvar e reiniciar**.

> O CI **não constrói dentro da pasta que você escolheu**. Ele clona no workspace dele, que é exclusivo e onde ele apaga o que não estiver commitado antes de cada build. Por isso o workspace sugerido é outro caminho, e não a sua pasta de trabalho.

### Rodar sem instalar

Para experimentar a partir do código, sem gerar pacote:

```bash
dotnet run --project src/UnityLocalCI.Worker
```

E, para ver a ferramenta funcionando sem tocar em nenhum projeto Unity real, há um ambiente de teste descartável:

```bash
powershell -ExecutionPolicy Bypass -File tools\testar-local.ps1
```

### Assinatura do executável

Um executável sem assinatura não tem quem responda por ele: o Windows o trata como programa desconhecido, o SmartScreen avisa, e um antivírus corporativo pode bloquear a execução. Como cada build tem hash novo, o bloqueio volta a cada publicação.

Uma vez por máquina:

```bash
powershell -ExecutionPolicy Bypass -File tools\certificado.ps1
```

Cria um certificado de assinatura de código e faz **o seu usuário** confiar nele. A partir daí o `publicar.ps1` assina o executável sozinho, com carimbo de tempo — sem o carimbo, todo binário já distribuído viraria inválido no dia em que o certificado expirasse.

> **O alcance disso.** O certificado é auto-assinado: quem responde pelo programa é você, nesta máquina. Para o Windows aceitar, ele entra na **Raiz Confiável** e nos **Editores Confiáveis** do seu perfil — e daí em diante o seu usuário confia em qualquer programa assinado com aquela chave. É uma decisão de confiança real, e é por isso que está num script que você roda, e não escondido dentro do `publicar.ps1`.
>
> Vale só aqui. Em outra máquina o certificado não significa nada, e uma política corporativa pode exigir uma autoridade certificadora reconhecida e recusá-lo assim mesmo. Para o programa ser confiável em qualquer lugar — e para o SmartScreen parar de avisar — o caminho é um certificado OV ou EV comprado de uma CA, que é o que o Discord e o VS Code usam.

Para desfazer: `tools\certificado.ps1 -Remover`.

### Desinstalar

Por **Configurações → Aplicativos → BuildMaker → Desinstalar**. O Windows chama o próprio executável, que sabe se remover:

```bash
UnityLocalCI.exe --desinstalar
```

Remove arquivos, atalhos, o registro em Aplicativos e o início automático. **Não** apaga o histórico em `%LOCALAPPDATA%\BuildMaker` nem os artefatos das builds.

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

## Como o app se comporta

| Ação | O que acontece |
|---|---|
| Abrir o atalho | A janela abre, com o serviço rodando dentro |
| **Fechar no X** | A janela some, o ícone fica na bandeja e **as builds continuam** |
| Minimizar | Mesma coisa: vai para a bandeja |
| Clicar no ícone da bandeja | A janela volta |
| Botão direito no ícone | Menu com **Abrir**, **Iniciar com o Windows** e **Sair** |
| **Sair** | Aviso de que as builds param, e encerra de verdade |

Na primeira vez que a janela se esconde, um balão explica que o programa continua rodando — um app que some da barra de tarefas sem dizer nada parece ter sido encerrado.

> **O ícone vai para a área de transbordo.** O Windows 11 esconde ícones novos atrás do `^` na bandeja. Para fixá-lo ao lado do relógio, arraste-o para fora do painel do `^`, ou vá em *Configurações → Personalização → Barra de tarefas → Outros ícones da bandeja do sistema*.

> **Início automático ≠ serviço.** O atalho de início automático abre o app quando **você** faz login. Para o CI rodar com a máquina ligada e ninguém logado, registre-o como serviço do Windows — ver [Windows Service](#windows-service). Os dois podem conviver: o serviço constrói, e a janela é só para acompanhar.

### Quando uma build morre no meio

Cada commit é construído **no máximo uma vez**: o `last_built_sha` é gravado no momento em que a build entra na fila, e é isso que impede o mesmo commit de virar duas builds.

O efeito colateral aparece quando o processo morre no meio — queda, reinício da máquina, ou o antivírus matando o programa. O commit ficava marcado como construído para sempre, e o merge sumia sem aviso.

Ao religar, o serviço fecha o registro como **INTERROMPIDA** e devolve aquele commit para a fila. Ele volta no ciclo seguinte do watcher, já depois da janela de silêncio.

| | |
|---|---|
| **INTERROMPIDA** | volta para a fila, até `Scheduler.MaxInterruptedRetries` vezes (padrão: 1) |
| **FALHOU** | não volta — a build rodou e deu um veredito sobre aquele commit |
| **CANCELADA** | não volta — alguém mandou parar |
| commit que já tem build **SUCESSO** | não volta — o artefato existe, não há o que recuperar |

O teto existe por experiência: uma versão anterior refazia sem condição nenhuma e virava ciclo — sobe, começa a mesma build de quinze minutos, cai, sobe. `MaxInterruptedRetries: 0` desliga e devolve o comportamento antigo, em que só **Construir agora** refaz.

## A janela

Um executável só, dois modos:

| Como iniciar | O que acontece |
|---|---|
| `UnityLocalCI.exe` | Abre a janela, com o serviço rodando dentro |
| `UnityLocalCI.exe --service` | Roda sem interface, para o Windows Service |

A navegação é uma **coluna à esquerda**, com cinco páginas:

- **Projetos** — estado ao vivo de cada projeto, com botões para construir agora, abrir a pasta de destino, reenviar artefatos pendentes e parar ou iniciar o serviço
- **Builds** — histórico das últimas 200 builds e o log completo da selecionada
- **Log do serviço** — o que está acontecendo agora, ao vivo
- **Tutorial** — o roteiro completo de uso, do instalador à primeira build, com os comandos copiáveis por um clique
- **Configuração** — edita o `appsettings.json` pela interface, valida antes de gravar e oferece reiniciar o serviço

Cada página tem cabeçalho com as próprias ações, e o rodapé mostra o estado do serviço e os números da fila.

### Um arquivo de configuração por projeto

O `appsettings.json` guarda só o que é da máquina: fila, estado, padrões, notificações e o Client ID do GitHub. **Cada projeto é um arquivo com o nome dele**, na pasta `projetos\`:

```
UnityLocalCI\
├── appsettings.json          fila, estado, padrões
└── projetos\
    ├── CrashUnity.json
    └── HumanXRobots.json
```

Mexer num projeto não reescreve os outros, um erro de digitação num arquivo não derruba a leitura de todos — o que falha aparece com o nome do arquivo e o resto continua valendo —, e dá para copiar a configuração de um projeto para outra máquina sem levar o resto junto. Renomear o projeto renomeia o arquivo; removê-lo apaga o arquivo.

A migração é automática e acontece uma vez: quem já tinha projetos dentro do `appsettings.json` os encontra na pasta depois da primeira abertura, e o array some do arquivo. Os arquivos são escritos antes de o array ser removido — se a máquina cair no meio, o pior caso é a lista aparecer duplicada, não sumir.

### Cadastrar projeto sem digitar caminho

Na página Configuração, **nenhum caminho precisa ser digitado**: cada campo de pasta ou arquivo abre a caixa de seleção do Windows. Caminho digitado é o tipo de erro que só aparece depois, na hora do clone, do build ou da cópia.

O botão **Vincular projeto Unity** vai além: você aponta a pasta de um projeto que já existe na máquina e ele preenche o cadastro com o que aquele projeto já sabe dizer sobre si mesmo.

| O que é preenchido | De onde vem |
|---|---|
| URL do repositório | `.git/config`, remote `origin` (ou o primeiro que houver) |
| Branch | `.git/HEAD` |
| **Versão do editor** | `ProjectSettings/ProjectVersion.txt` do projeto |
| Nome, workspace e arquivo de gatilho | derivados do nome da pasta, seguindo o padrão dos projetos já cadastrados |

A versão do editor também é relida **sempre que o `WorkspacePath` é escolhido**, e a linha *EditorVersion no disco* mostra o que o projeto clonado diz agora — se divergir do campo gravado, a configuração ficou para trás depois de o time subir o projeto para outra versão do Unity. Buildar na versão errada produz um artefato que parece certo e não é, então essa é a única coisa da configuração que nunca é adivinhada.

Os arquivos do Git e o `ProjectVersion.txt` são **lidos direto, sem invocar o `git`**: isso roda na thread da interface, ao lado de uma caixa de diálogo, e um processo externo ali travaria a janela.

O campo **Branch é uma lista**, e ela traz **apenas branches de `origin`** — nunca branches locais: o CI observa o que está no servidor, e uma branch que só existe na máquina de alguém não dispara build nenhuma.

São duas fontes, nesta ordem: os refs de `origin` que o clone já conhece, lidos do disco (respondem na hora, servem offline e sem credencial), e `git ls-remote` no servidor. Quando o servidor responde, a lista dele **substitui** a do clone, e não soma — o clone guarda refs de `origin` que já foram apagadas lá até alguém podar, e somá-las traria de volta branch que não existe mais. A consulta ao servidor roda fora da thread da janela. A lista **não é exclusiva**: dá para digitar uma branch que ainda não existe, porque cadastrar o projeto antes de criar a branch de homologação é normal.

### A conexão com o Git é da máquina, não de cada jogo

A credencial mora em **`Defaults.Repository.PatCredentialName`**, e todo projeto a herda. É o que o botão **Conectar ao GitHub** preenche: conecta uma vez, vale para todos os projetos.

O campo `PatCredentialName` de cada projeto continua existindo, mas como **exceção** — só para um projeto que viva em outra organização ou outra conta. Vazio (ou apagado) herda a conexão da máquina; tratar vazio diferente de ausente faria "apagar para herdar" virar "ficar sem credencial".

A validação olha a credencial **resolvida**. Conferindo só a do projeto, uma conexão de máquina apontando para credencial inexistente passaria batido, e a falha apareceria no primeiro clone.

> **Lista vazia quase sempre é falta de acesso**, não ausência de branches: sem clone local e sem credencial válida, não há de onde tirar os nomes. A linha de mensagem diz qual repositório falhou; **Conectar ao GitHub** resolve.

> **O workspace nunca é a pasta que você escolheu.** Antes de cada build o pipeline apaga o que não estiver commitado; apontá-lo para a sua pasta de trabalho destruiria o que estivesse em andamento. Por isso o cadastro sugere um caminho próprio, e o projeto entra **desligado** até você conferir.

#### O visual

As duas cores da marca são o verde **`#6FAB16`** e o quase-preto **`#131A09`**. Elas têm o mesmo matiz — 84°, amarelo-esverdeado —, então a escala de fundos sai toda de uma família: fundo, superfície, superfície elevada, em vez de um cinza único. É isso que dá profundidade sem sombra. O destaque é **um só**, o verde, reservado para a ação principal, o item de navegação ativo e a build em execução; os cartões arredondados com contorno discreto fazem o resto, porque o WinForms não desenha sombra de verdade e uma sombra falsa sobre fundo liso fica pior que nenhuma.

Duas consequências dessa paleta que valem registro:

- o verde é claro demais para carregar texto branco — 2,7:1, reprovado —, então **o que se escreve em cima dele é o quase-preto**, que dá 7,7:1;
- o verde do "deu certo" **não é o verde da marca**, e sim um verde-água. Se fossem o mesmo, "terminou bem" e "este é o botão principal" falariam com a mesma voz.

O WinForms não tem tema — cada controle pinta com as cores do sistema —, então:

- os controles próprios (coluna de navegação, botões, cartões, rodapé, seletor segmentado da configuração, lista de projetos) **se desenham por inteiro**, com `TextRenderer` para o texto sair com o mesmo peso do resto do sistema
- os ícones da navegação são **desenhados em GDI+**, não tirados de uma fonte de símbolos: fonte de ícone que não existe na máquina vira quadradinho, e a lista de símbolos muda entre versões do Windows
- a barra de título escurece por uma chamada ao **DWM**
- barras de rolagem, caixas de diálogo e menus de contexto são desenhados pelo Windows, e escurecem por `Application.SetColorMode(SystemColorMode.Dark)` — a API ainda é marcada como experimental, então a chamada é protegida: se um dia ela mudar, o pior caso é essas partes voltarem a ser claras, e o resto da janela não depende dela

Ela não guarda estado próprio: tudo o que mostra vem do mesmo SQLite que o serviço escreve, então nunca discorda do que aconteceu de verdade, e fechá-la não perde nada.

Fechar pelo **X esconde na bandeja** e o serviço continua construindo. Sair de verdade é pelo menu da bandeja, que avisa que as builds param.

> **A janela aberta não substitui o serviço.** Com tudo num executável só, as builds só acontecem enquanto o programa estiver rodando e a sessão do usuário estiver aberta. Para o CI funcionar com a máquina ligada e ninguém logado, registre-o como serviço — ver [Windows Service](#windows-service). Os dois modos usam exatamente os mesmos componentes; o que muda é só quem os hospeda.

> **A página de Configuração edita o `appsettings.json`**, não o `appsettings.local.json`. Se houver um arquivo local sobrescrevendo valores, o que a janela mostra não é o que o serviço está usando.

> **Se o `.exe` reclamar que o .NET não foi encontrado:** o SDK está instalado no perfil do usuário (`%USERPROFILE%\.dotnet`) e o apphost só procura em `C:\Program Files\dotnet`. Rode por `dotnet UnityLocalCI.dll`, ou defina `DOTNET_ROOT`. Antes de registrar o Windows Service, instale o .NET para toda a máquina.

---

## Placeholders de configuração

Tudo que precisa ser preenchido antes do primeiro uso real. Nada disso foi inventado.

| Placeholder | Onde | O que é |
| `PREENCHER-NOME-DO-PROJETO` | `Projects[].Name` | Nome do projeto. Identifica a fila, o estado e aparece nos arquivos de status. |
| `PREENCHER-NOME-DO-PROJETO` | `Projects[].Repository.WorkspacePath` | Última pasta do caminho do workspace. |
| `PREENCHER-NOME-DO-PROJETO` | `Projects[].ManualTriggerFile` | Nome do arquivo de gatilho manual. |
| `PREENCHER-URL-DO-REPOSITORIO` | `Projects[].Repository.Url` | URL do repositório no Azure DevOps. |
| `PREENCHER-VERSAO-DO-EDITOR` | `Defaults.Unity.EditorVersion` | Versão exata do editor, ex.: `6000.0.47f1`. Cada projeto pode sobrescrever a sua. |
| `PREENCHER-PASTA-DE-DESTINO` | `Defaults.Publishing.ArtifactFolder` | Pasta onde o time pega os zips. É **uma só, para todos os projetos**: cada zip já tem projeto, branch, data e commit no nome. |
| `PREENCHER-NOME-DO-PROJETO` | `tools/post-merge.hook` | Nome do projeto no hook, se for usá-lo. |

Valores que já vêm prontos e você provavelmente quer conferir: `Branch` (`HML`) e os caminhos locais em `C:\ci\` (workspace, staging, triggers). A credencial não está nessa lista porque **não é por projeto**: ela mora em `Defaults.Repository.PatCredentialName` e é preenchida pelo botão **Conectar ao GitHub**.

O banco e os arquivos de status não aparecem na configuração porque não precisam: vão para `%LOCALAPPDATA%\BuildMaker\`, que é onde um aplicativo do Windows guarda o que é dele. Só o banco tem caminho configurável, em `State.DatabasePath`; o status é fixo. Já foi configurável, e um caminho relativo ali fazia o programa escrever dentro da própria pasta de instalação — inclusive criando uma pasta com o nome do placeholder que vinha no modelo.

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
UnityLocalCI.slnx
├── src/
│   ├── UnityLocalCI.Worker/      net10.0-windows: janela, bandeja, servico, instalador
│   ├── UnityLocalCI.Cli/         net10.0: o mesmo CI sem janela, para o Ubuntu
│   └── UnityLocalCI.Core/        net10.0: roda nos dois
│       ├── Configuration/        opções tipadas, merge de Defaults, validação
│       ├── Secrets/              cofre do Windows (CredRead) e arquivo 0600 no Linux
│       ├── Abstractions/         relógio, executor de processos, Job Object
│       ├── Git/                  IGitClient e as exclusões do git clean
│       ├── Unity/                IUnityCliClient e o parser de log
│       ├── State/                SQLite e recuperação de build órfã
│       ├── Watching/             GitWatcher, um por projeto
│       ├── Queue/                fila por projeto, semáforo global, guarda de recursos
│       ├── Pipeline/             Sync, Build, Package, Publish
│       ├── Publishing/           IArtifactPublisher e FolderPublisher
│       └── Notifications/        INotifier e LogNotifier
├── tests/UnityLocalCI.Tests/     319 testes xUnit
├── unity/                        Builder.cs e instrucoes de instalacao
├── tools/                        publicar, certificado, empacotar-deb, gatilhos
├── .github/workflows/release.yml cada tag vira .exe do Windows e .deb do Ubuntu
└── config/
```

**Por que o `Core` não é mais `-windows`.** Até a 1.0 tudo era `net10.0-windows`, porque tudo rodava só no Windows. O `Core` agora é `net10.0`: as três peças que dependiam do sistema — cofre de credenciais, job object e leitura de memória livre — já estavam atrás de interface, e a escolha entre as duas implementações passou a ser em tempo de execução, no `ServiceRegistration`. O mesmo `Core` compilado serve aos dois.

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
| Status e histórico em JSON, em `%LOCALAPPDATA%\BuildMaker\status\` | **pronto** |
| Gatilho manual por arquivo observado | **pronto** |
| Retenção por contagem | **pronto** |

**Fase 2 concluída.**

O `Builder.cs` vem primeiro porque é ele que faz o Unity retornar código diferente de zero em build quebrada. Enquanto ele não estiver instalado no projeto Unity, o pipeline pode publicar lixo — ver [`unity/README.md`](unity/README.md).

### Onde ficam os dados

A pasta de destino de cada projeto contém **só os zips das builds**. Nada mais: ela é o que o time abre para pegar a build, e qualquer outra coisa ali confunde quem recebe.

O que é do próprio aplicativo vive onde o Windows espera:

```
%LOCALAPPDATA%\BuildMaker\
├── state\buildmaker.db      histórico das builds
└── status\
    ├── geral.json           todos os projetos num arquivo
    └── Crash.json           última build, a anterior, avisos e as 20 do histórico
```

O `geral.json` é reescrito quando qualquer build termina **e** quando uma entra em execução, para que quem o ler durante uma build de 30 minutos veja o estado atual, e não o resultado da anterior. A escrita é serializada entre projetos: duas builds terminando juntas não podem produzir um arquivo que descreve um estado que nunca existiu.

O formato é JSON, e não texto alinhado a coluna, porque o leitor mudou. Quem quer olhar abre a janela do BuildMaker, que mostra tudo isso formatado; o que sobra para o arquivo é ser consumido por outra coisa — um script, um painel, um bot —, e para isso texto alinhado é péssimo.

**Nenhum log vai para disco** — nem o de cada build, nem o do serviço. Os dois existem em memória enquanto o programa está aberto, aparecem na janela linha a linha, e acabam junto com o processo. O que sobrevive é o que responde alguma pergunta depois: o registro da build no banco e o resumo do erro.

Isso é uma troca, e vale dizer qual. O log em arquivo foi o que permitiu provar, quando o aplicativo fechava sozinho no meio das builds, que quem o encerrava era o antivírus e não ele próprio — sem arquivo, um processo morto não deixa rastro nenhum. Se voltar a acontecer, o caminho passa a ser o Visualizador de Eventos do Windows e o log do agente de segurança.

Já houve uma pasta `latest\` ali, com a última build já descompactada e pronta para rodar. Ela saiu junto com o resto: a pasta de destino tem os zips, e só. Quem quer a build pega o zip.

### Por que o duplo clique no `index.html` não serve

Uma build WebGL não roda por `file://`: o navegador bloqueia `.wasm` e `.data` nesse protocolo, e o resultado é uma tela preta sem mensagem de erro. Com compressão, ainda falta o cabeçalho `Content-Encoding`. Descompacte o zip e aponte um servidor estático para a pasta — `python -m http.server` ou `npx serve` resolvem, e para uma build com Brotli é preciso um que envie `Content-Encoding` para `.br` e `.gz`.

> O CI já escreveu um `rodar.bat` dentro de cada build, que subia esse servidor sozinho. Ele saiu: o zip é o jogo, e arquivo do CI misturado aos arquivos do jogo confunde quem recebe o pacote e derruba a validação de um portal. A conveniência não pagava o preço.

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

Executada ao fim de cada build: mantém as `KeepLastBuilds` builds **bem-sucedidas** mais recentes e apaga, das demais, o zip no destino e o zip no staging.

Três detalhes que a implementação garante:

- **Contam-se as bem-sucedidas.** Uma sequência de falhas não empurra para fora o último artefato que de fato funciona.
- **Cópia pendente nunca perde o staging.** Se o destino estava fora do ar, aquele zip só existe ali.
- **A poda é guiada pelo banco, não por varredura da pasta.** Ela apaga exatamente os arquivos que cada build registrou, e nunca um zip que alguém copiou para lá na mão.

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

> Os demais campos de configuração da fase 2 (`WriteStatusFiles`, `Retention`, `ManualTriggerFile`) já existem e são validados, mas ainda não têm efeito.

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
