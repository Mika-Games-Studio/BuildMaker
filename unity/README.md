# Builder.cs — instalação no projeto Unity

Este arquivo é a única peça do UnityLocalCI que vive **dentro do projeto Unity**, e não no serviço. Ele é o que faz o Unity retornar código diferente de zero quando a build quebra.

## Instalação

1. Copie [`Builder.cs`](Builder.cs) para `Assets/Editor/Builder.cs` no projeto Unity.
2. Commite o arquivo na branch de homologação. Ele precisa estar versionado: o workspace do CI é recriado por `git reset --hard`, e o que não está no repositório não chega lá.
3. Abra o projeto uma vez no Editor e confirme que não há erro de compilação no console.

A pasta `Assets/Editor/` não precisa existir antes — pode criá-la. Qualquer script dentro de uma pasta chamada `Editor` é compilado só para o Editor e não entra no player.

O arquivo também está protegido por `#if UNITY_EDITOR`, então mesmo que alguém o mova para fora de `Assets/Editor/` o build do player continua compilando.

## Verificação antes de ligar o CI

Confira que as cenas estão marcadas em *File → Build Settings*. O Builder recusa a build se não houver nenhuma cena habilitada — de propósito, porque uma build sem cenas compila, roda e mostra uma tela preta sem mensagem de erro.

Para testar sem o serviço, rode pela linha de comando:

```bash
unity --non-interactive build . --target WebGL --execute-method Builder.PerformBuild --output-path C:\temp\teste-webgl
```

Confira o código de saída. Zero significa sucesso; qualquer outro valor significa falha, e é assim que o pipeline decide se publica ou não.

```bash
echo %ERRORLEVEL%
```

## O que ele faz

| Etapa | Comportamento |
|---|---|
| Lê os argumentos | `-ciBuildNumber`, `-ciCommitSha`, `-ciBranch`, e o caminho de saída por `-buildOutput` ou `-ciOutputPath` |
| Monta a lista de cenas | Apenas as habilitadas em `EditorBuildSettings`, na ordem definida lá |
| Inspeciona a compressão WebGL | **Somente leitura**, e apenas para avisar |
| Executa `BuildPipeline.BuildPlayer` | Com o alvo ativo que o CLI já selecionou |
| Encerra o processo | `EditorApplication.Exit(0)` em sucesso, `Exit(1)` em qualquer outro resultado |

## O que ele não faz, e por quê

**Não altera Player Settings.** Compressão, qualidade, template de WebGL e afins são configurados no projeto Unity e versionados junto dele. Se o Builder sobrescrevesse, uma mudança feita no Editor seria silenciosamente desfeita na build, e quem a fez levaria horas para entender por quê.

A única inspeção é um **aviso**: se `PlayerSettings.WebGL.compressionFormat` estiver em Brotli ou Gzip com `decompressionFallback` desligado, ele registra um alerta destacado e segue em frente. Não corrige e não aborta.

O motivo do aviso: os arquivos `.wasm` e `.data` continuam comprimidos depois de o zip ser extraído, e só carregam se o servidor enviar `Content-Encoding: br`. Como a distribuição aqui é por pasta, ninguém terá esse servidor, e o sintoma é uma tela preta sem erro útil. Quem cuida do projeto decide entre desligar a compressão — a pasta cresce de 2 a 3 vezes, mas o zip final cresce pouco, porque dado já comprimido não comprime de novo — ou manter Brotli e ligar `decompressionFallback`, ao custo de alguns segundos em todo carregamento.

## Carimbo de versão: desligado por padrão

A seção 6 da especificação técnica pede que a `bundleVersion` venha do sha e do número da build. A regra 6b do prompt, que está entre as invioláveis, diz que o Builder lê e nunca escreve Player Settings — e `bundleVersion` é um Player Setting. As duas se contradizem.

O padrão respeita a regra: **nada é escrito**. Quem quiser o carimbo liga por configuração, acrescentando o argumento em `Unity.ExtraArgs` do projeto:

```jsonc
"Unity": {
  "ExtraArgs": ["--args", "-ciStampVersion true"]
}
```

Com ele ligado, a `bundleVersion` vira `<versão atual>+<número da build>.<sha curto>` — por exemplo `1.4.2+42.a1b2c3d` —, o que permite ver dentro do jogo de qual build aquele bundle veio. A escrita suja o `ProjectSettings.asset` do workspace, mas o passo de Sync faz `reset --hard` antes de toda build, então ela nunca se acumula.

Vale ligar se o time costuma perguntar "que build é essa?" olhando o jogo rodando. Se não, deixe desligado.

## Contrato de log com o pipeline

O Builder marca as linhas que o serviço precisa promover ao topo do resumo e ao `_STATUS.txt`, em vez de deixá-las enterradas em dezenas de milhares de linhas de saída do Unity:

| Prefixo | Destino |
|---|---|
| `[UnityLocalCI] ERRO:` | Resumo do erro, com prioridade sobre qualquer outro |
| `[UnityLocalCI] AVISO:` | Bloco de avisos, sem afetar o resultado da build |
| `[UnityLocalCI]` | Informativo; não vira erro nem aviso |

Se você acrescentar mensagens próprias ao Builder, use esses prefixos para que elas cheguem a quem abrir a pasta. O lado do serviço que interpreta isso é `UnityLogParser`, e há teste cobrindo cada um dos três casos.
