# Regras para IAs

## Princípio de operação

Este projeto pode ser desenvolvido simultaneamente por pessoas e por múltiplos
agentes de IA. O fluxo normal deve exigir o mínimo possível de administração Git
pelo usuário: o agente é responsável por descobrir o estado do repositório,
isolar sua tarefa, implementar, validar, versionar o trabalho e preparar a
integração.

Não peça ao usuário para escolher branch, nome de worktree, estratégia mecânica
de Git ou outros detalhes que possam ser inferidos com segurança. Peça orientação
somente quando existir uma decisão real de produto, comportamento, segurança,
compatibilidade ou uma ambiguidade material que o repositório e sua documentação
não resolvam.

Código-fonte, testes e histórico Git são a fonte principal da verdade. A
documentação fornece contexto e regras de operação, mas pode estar defasada em
relação ao código. Preserve sempre trabalho existente, inclusive alterações
produzidas por outras pessoas ou agentes.

## Fontes de contexto do projeto

Ao iniciar uma tarefa de desenvolvimento, correção, refatoração, segurança, UI,
documentação técnica ou auditoria com alterações, o agente deve carregar apenas
o contexto necessário, nesta ordem:

1. `AI_RULES.md`, que define governança, Git, PRs, integração e release;
2. `PROJECT_STATE.md`, que descreve de forma compacta o estado **atual e já
   integrado** da próxima versão;
3. documentação diretamente relacionada à área afetada, especialmente
   `docs/architecture.md` e `docs/safety.md` quando aplicáveis;
4. código, testes e histórico Git recente das áreas que serão alteradas.

A leitura obrigatória deve ser **seletiva**. Não percorra documentação histórica,
PRs antigos ou arquivos sem relação com a tarefa apenas para “obter contexto”.
Isso aumenta custo, reduz foco e pode introduzir decisões obsoletas no raciocínio.

`PROJECT_HISTORY.md` **não é leitura padrão**. Consulte-o somente quando houver
uma necessidade concreta de reconstruir uma decisão antiga, investigar regressão,
compatibilidade legada, migração, release passada ou outro contexto histórico que
não esteja suficientemente explicado pelo código, pelo Git recente ou pela
documentação atual. Se a tarefa não exige arqueologia, não o leia.

## Roadmap público

`docs/public-roadmap.json` é a fonte de verdade do Roadmap da próxima versão em
`https://vemryx.com/Ralven/`. Ele é publicado automaticamente pelo workflow após
uma alteração integrada em `dev/proxima-versao`; nunca depende de pedido manual
do usuário ou de uma release.

Ao concluir uma capacidade que será integrada e possa ser comunicada
publicamente, o agente responsável deve atualizar esse arquivo no **mesmo Pull
Request**, com textos precisos em PT e EN, sem solicitar confirmação adicional.
O integrador deve conferir essa atualização antes de integrar a tarefa. Se a
mudança não tiver impacto público claro — por exemplo, refatoração, dependência,
teste, infraestrutura ou experimento — o arquivo deve permanecer inalterado.

Nunca infira itens a partir de mensagens de commit nem prometa recursos, datas,
resultados ou desempenho. Inclua somente comportamento real que o Pull Request
integrará ou que já esteja integrado, e preserve o `id` estável e o status
definidos em `docs/public-roadmap.md`.

Código-fonte e testes vigentes prevalecem sobre documentação desatualizada. Entre
os documentos de estado, `PROJECT_STATE.md` representa a visão canônica atual;
`PROJECT_HISTORY.md` nunca deve sobrescrever uma decisão atual apenas por conter
uma descrição antiga diferente.

## Governança do estado e do histórico

`PROJECT_STATE.md` é um **snapshot operacional**, não changelog, diário de agente,
relatório de PR ou arquivo de auditoria. Deve permanecer curto o bastante para ser
lido integralmente no início de toda tarefa sem desperdiçar contexto.

### O que pertence ao `PROJECT_STATE.md`

Mantenha apenas informação que altere a compreensão do estado atual integrado:

- snapshot da próxima versão e branches oficiais;
- arquitetura vigente e invariantes de segurança;
- capacidades atualmente existentes que sejam relevantes para novos trabalhos;
- contratos, limitações e decisões ainda válidas;
- pendências e decisões realmente abertas;
- baseline **mais recente e útil** de validação por superfície;
- comandos e documentos canônicos necessários para operar o projeto.

### O que não pertence ao `PROJECT_STATE.md`

Não registrar nele:

- uma seção por tarefa, PR, branch, agente, commit ou data;
- narrativa de como uma correção foi implementada;
- bugs já resolvidos sem efeito atual;
- listas cumulativas de merges e integrações;
- números antigos de testes quando já existe baseline posterior;
- detalhes que pertencem a documentação especializada;
- conteúdo que existe apenas para preservar memória histórica.

Quando uma pendência for resolvida, **remova-a ou substitua o estado correspondente**;
não acrescente uma entrada “resolvido em...”. Quando uma arquitetura mudar,
atualize a descrição vigente em vez de manter lado a lado a arquitetura antiga.
Quando um baseline novo substituir o anterior, mantenha somente os valores ainda
úteis para comparação ou referência.

### Limite operacional

A meta é manter `PROJECT_STATE.md` em aproximadamente **200 linhas** e,
preferencialmente, abaixo de **20 KB**. Esses valores são orçamento de contexto,
não motivo para mutilar informação essencial. Se uma integração fizer o arquivo
crescer de forma sustentada além disso, o próprio integrador deve compactá-lo,
consolidar repetições e mover detalhes para o documento canônico adequado antes
de concluir.

### Papel do `PROJECT_HISTORY.md`

`PROJECT_HISTORY.md` é um **arquivo histórico/legado**. Sua existência
permite retirar cronologia do estado canônico sem destruir contexto antigo, mas ele
não substitui Git, Pull Requests ou `CHANGELOG.md`.

- tarefas normais não o leem nem o atualizam;
- integrações normais não precisam acrescentar um resumo de cada PR;
- só adicionar conteúdo quando houver valor histórico durável que não esteja
  adequadamente preservado em Git/PR/CHANGELOG, ou quando o usuário pedir uma
  manutenção/arquivamento histórico explícito;
- fatos históricos nunca têm precedência automática sobre código e documentação
  atuais.

Tarefas isoladas não devem editar `PROJECT_STATE.md` nem `PROJECT_HISTORY.md` por
padrão. A tarefa comunica suas mudanças pelo próprio código, commits e Pull
Request; o integrador consolida no `PROJECT_STATE.md` apenas o que efetivamente
se tornou estado oficial.

## Branches, worktrees e isolamento

- `main` contém exclusivamente versões públicas já publicadas e só é alterada
  durante uma **publicação oficial**.
- `dev/proxima-versao` é a branch oficial de **integração** da próxima versão.
  Tarefas normais não desenvolvem diretamente nela.
- Cada tarefa que produzir alterações deve usar uma branch temporária baseada em
  `dev/proxima-versao`, nomeada pelo **objetivo da mudança**, não pela identidade
  da IA.
- É **obrigatório** criar uma nova branch e uma nova worktree exclusivas para
  cada tarefa que produzir alterações, mesmo que já exista uma branch ou
  worktree semelhante, relacionada ou usada para o mesmo propósito. Nunca
  reutilize uma branch ou worktree existente para iniciar a tarefa.

Use, quando aplicável, os prefixos:

- `feat/<slug>` para nova funcionalidade;
- `fix/<slug>` para correção;
- `refactor/<slug>` para refatoração sem mudança funcional intencional;
- `perf/<slug>` para desempenho;
- `security/<slug>` para hardening ou correção de segurança;
- `test/<slug>` para testes;
- `docs/<slug>` para documentação;
- `chore/<slug>` para manutenção;
- `task/<slug>` quando nenhuma categoria anterior representar bem a tarefa.
- `release/vX.Y.Z` exclusivamente para o candidato imutável de uma publicação
  oficial já autorizada.

Exemplos: `feat/account-security`, `fix/updater-timeout`,
`refactor/telemetry-pipeline`. Gere automaticamente um slug curto e descritivo;
se o nome já existir, crie uma variante única sem interromper o usuário.

### Preparação automática de uma tarefa

Para cada nova tarefa normal, o agente deve:

1. localizar a raiz do repositório e ler o contexto obrigatório;
2. verificar `git status`, branch atual, histórico recente, branches e worktrees;
3. quando houver acesso ao remoto, executar `git fetch` antes de definir a base;
4. partir do estado mais recente e seguro de `dev/proxima-versao` ou
   `origin/dev/proxima-versao`;
5. criar a branch da tarefa automaticamente;
6. criar um **novo worktree exclusivo** para essa branch; mesmo que já exista
   uma branch ou worktree semelhante, relacionada ou usada para o mesmo
   propósito, não reutilizar a existente;
7. planejar o handoff no corpo do Pull Request, sem criar arquivo compartilhado
   na raiz da branch;
8. executar alterações, testes e commits somente nesse checkout isolado.

Nunca troque a branch de um checkout que possa estar sendo usado por outro agente
ou processo. Se um worktree não for tecnicamente possível, preserve o checkout
compartilhado e use a alternativa mais segura disponível, informando a limitação
somente quando ela afetar a execução ou a segurança do trabalho.

## Segurança no trabalho concorrente

Assuma sempre que outros agentes podem estar trabalhando simultaneamente.

- Nunca apagar, resetar, descartar, sobrescrever ou sincronizar trabalho de outra
  branch ou worktree.
- Nunca usar `git reset --hard` em trabalho que possa não pertencer exclusivamente
  à tarefa atual e nunca usar force push no fluxo normal.
- Nunca alterar arquivos sem relação com a tarefa apenas por limpeza estética;
  evite reformatações massivas e refatorações oportunistas.
- Limite as mudanças ao escopo solicitado e preserve o comportamento existente
  fora dele.
- Antes de editar, inspecione os arquivos, contratos e testes afetados. Não
  reverta uma alteração anterior sem compreender sua motivação e impacto.
- Preserve os limites de segurança em `docs/safety.md` e a separação arquitetural
  em `docs/architecture.md`.
- Conflitos devem ser resolvidos semanticamente. Nunca use `ours`, `theirs`,
  sobrescrita integral ou qualquer atalho equivalente sem verificar qual
  comportamento precisa sobreviver.
- Um merge Git sem conflito textual **não prova compatibilidade**. Sempre avalie
  também conflitos lógicos, contratos, dependências e comportamento combinado.

## Implementação e validação

O agente deve trabalhar de forma autônoma dentro do escopo da tarefa:

- compreender o comportamento atual antes de modificá-lo;
- preferir mudanças coesas e mínimas, sem sacrificar a correção arquitetural;
- adicionar ou ajustar testes quando a mudança alterar comportamento relevante;
- executar os testes mais específicos durante o desenvolvimento e a validação
  aplicável ao final;
- não remover, enfraquecer ou contornar testes apenas para obter uma execução
  verde;
- não afirmar que algo foi testado, validado ou corrigido sem evidência concreta.

Antes de concluir, revise o diff e confirme que não entraram segredos, dados
locais, caches, builds, arquivos temporários ou mudanças acidentais.

## Commits

Commits locais são parte normal da tarefa e não exigem autorização adicional.
Use um ou mais commits **coerentes e concluídos** quando isso melhorar a clareza;
não crie commits de tentativa, `WIP` ou checkpoints sem valor histórico.

Toda mensagem de commit segue
[Conventional Commits](https://www.conventionalcommits.org/pt-br/):
`tipo(escopo opcional): descrição curta no imperativo`, por exemplo
`fix(worker): corrige rate limit da rota de telemetria` ou
`docs: atualiza README com nova estrutura de pastas`.

Tipos comuns: `feat`, `fix`, `docs`, `refactor`, `test`, `chore`, `ci`, `perf`,
`build`, `revert`.

- Não use mensagens genéricas como `WIP`, `update`, `fix stuff` ou nomes de
  artefatos de build.
- Não invente número de versão; a versão só muda em publicação oficial.
- Não mencione nome de agente de IA, prompt ou ferramenta interna na mensagem.
- Nunca reescreva histórico já publicado para adequá-lo a estas regras.

## Pull Requests e conclusão automática da tarefa

O **Pull Request é o handoff padrão** de uma tarefa concluída. Ele substitui a
necessidade de manter um relatório `.ai/tasks/` para toda alteração rotineira.

Ao terminar uma tarefa, o agente deve automaticamente:

1. revisar o diff final e o escopo;
2. executar build, testes, lint, typecheck e demais validações disponíveis e
   aplicáveis;
3. corrigir falhas introduzidas pela própria tarefa;
4. criar os commits finais profissionais;
5. preparar no corpo do Pull Request o objetivo, escopo, critérios e resultado
   factual da tarefa;
6. quando houver remoto e autenticação disponíveis, enviar **somente a branch da
   tarefa** para o remoto;
7. criar ou atualizar um Pull Request dessa branch para `dev/proxima-versao`;
8. deixar no PR um resumo objetivo das mudanças, validações executadas,
   limitações/riscos conhecidos e dependências de outros PRs quando existirem;
9. informar ao usuário o resultado da tarefa, incluindo branch, PR, testes e
   qualquer limitação relevante.

A criação e atualização desse PR são autorizadas por estas regras e não exigem
confirmação adicional do usuário. O agente **não** deve fazer merge do próprio PR
em `dev/proxima-versao` apenas porque terminou a tarefa.

Se o remoto, GitHub CLI/API ou autenticação não estiverem disponíveis, mantenha a
branch local pronta para integração e informe claramente o que não pôde ser feito.
Nunca simule a existência de push ou PR.

### Conteúdo do Pull Request

Use título curto e orientado à mudança. O corpo deve conter, quando aplicável:

- **Resumo**: o que mudou e por quê;
- **Validação**: comandos/testes realmente executados e seus resultados;
- **Riscos/limitações**: o que merece atenção na integração;
- **Dependências**: outros PRs ou mudanças das quais este trabalho depende.

Não inclua segredos, caminhos locais desnecessários, prompts ou detalhes internos
da ferramenta/agente.

### Handoff obrigatório no Pull Request

Toda tarefa que produzir um Pull Request deve registrar no corpo do PR:
objetivo, escopo (incluindo fora de escopo quando relevante), critérios de
conclusão, resultado entregue, validações executadas e limitações reais. O
integrador compara esse handoff com o diff e os testes, sem tratar a
autoavaliação como evidência suficiente.

Não use `OBJECTIVE.md` na raiz para contratos por tarefa. Esse arquivo é
compartilhado por todas as branches e cria conflitos textuais sem relação com
o código, serializando indevidamente a integração. Use `.ai/tasks/` somente
quando houver necessidade real de estado persistente fora do PR.

O PR pode conter detalhes de implementação e validação que **não devem ser
copiados integralmente para `PROJECT_STATE.md`**. O PR é o handoff detalhado; o
estado canônico recebe apenas a consequência durável da mudança após o merge.

### `.ai/tasks/` é excepcional

Arquivos em `.ai/tasks/` deixam de ser obrigatórios no fluxo normal. Use-os
somente quando houver necessidade real de estado persistente fora do PR, por
exemplo:

- tarefa longa que atravessa várias sessões antes de existir um PR;
- trabalho interrompido que precisa de handoff detalhado;
- auditoria extensa com achados que ainda não viraram mudanças;
- ambiente sem remoto/PR em que um registro persistente seja necessário.

Quando existir, o relatório deve ser curto e não duplicar desnecessariamente o
conteúdo do PR. Tarefas isoladas continuam sem alterar `PROJECT_STATE.md` por
padrão.

## Integração das tarefas

Frases como “integrar trabalhos”, “integrar os PRs”, “integrar branches”,
“integrar tarefas concluídas” ou “preparar a dev” ativam o modo **agente
integrador**. Essa autorização permite integrar os trabalhos concluídos em
`dev/proxima-versao`, inclusive atualizar o remoto dessa branch quando necessário
para concluir a integração. Ela **não** autoriza alterar `main`, criar tag ou
publicar release.

No modo integrador, o agente deve:

1. atualizar referências remotas e analisar o estado atual de
   `dev/proxima-versao`;
2. descobrir os PRs abertos destinados a `dev/proxima-versao`; se não houver PR
   para algum trabalho relevante, examinar também branches de tarefa e relatórios
   excepcionais em `.ai/tasks/`;
3. validar quais trabalhos estão realmente concluídos e quais ainda são draft,
   incompletos, falhos ou dependentes de outro trabalho;
4. determinar uma ordem de integração baseada em dependências, áreas
   sobrepostas e risco;
5. para cada PR, ler primeiro o handoff no corpo do PR e comparar seu objetivo,
   escopo e critérios de conclusão com o diff, os testes e o resultado entregue;
   avaliar se a solução cumpre esse objetivo com escopo proporcional antes de
   revisar contratos afetados e possíveis conflitos
   **textuais e lógicos** com a `dev` atual e com os demais PRs;
6. atualizar a branch do PR com a base atual quando necessário e resolver
   conflitos preservando a intenção válida dos dois lados;
7. executar validações focadas antes do merge quando o risco justificar;
8. integrar um PR por vez em `dev/proxima-versao`, usando preferencialmente
   **squash merge** para tarefas comuns; preserve commits separados quando eles
   tiverem valor histórico ou técnico claro;
9. executar testes relevantes entre integrações que interagem entre si e a suíte
   completa aplicável ao final;
10. corrigir incompatibilidades de integração em uma branch/PR apropriada ou em
    um commit de integração claramente identificado, sem esconder a causa;
11. atualizar `PROJECT_STATE.md` como **snapshot do estado resultante**, e não
    como registro dos PRs: incorporar somente mudanças que alterem arquitetura,
    capacidade atual, invariantes, pendências, decisões abertas ou baseline útil;
    remover informações substituídas/resolvidas e compactar o arquivo se o
    orçamento de contexto estiver sendo ultrapassado;
12. não atualizar `PROJECT_HISTORY.md` rotineiramente; use-o somente se a
    integração produzir contexto histórico durável que realmente precise ser
    preservado fora de Git/PR/CHANGELOG;
13. reconstruir a simulação local da próxima versão com
    `scripts\Install-DevelopmentShortcut.ps1 -Build` e confirmar que o atalho
    `Ralven - Desenvolvimento` aponta para
    `scripts\Start-DevelopmentApp.ps1`;
14. garantir que `origin/dev/proxima-versao` reflita o estado integrado e validado;
15. após confirmação de merge e validação, remover worktrees locais temporários e
    branches de tarefa já incorporadas quando isso for seguro. Branches remotas de
    PR já mergeadas podem ser removidas como limpeza normal.

Ao concluir qualquer tarefa — exceto tarefas que envolvam diretamente
instalador/updater (`Ralven.Updater`, `Ralven.UpdateRuntime`,
`Ralven.ReleaseTool`, `installer/`, fluxos de staging/ativação/rollback) —
o agente deve **sempre** reconstruir o atalho `Ralven - Desenvolvimento`
com `scripts\Install-DevelopmentShortcut.ps1 -Build`, executado a partir do
próprio checkout/worktree da tarefa, para que ele reflita o app com as
últimas mudanças implementadas, pronto para o usuário testar quando quiser.
Isso vale tanto para tarefas isoladas quanto para a integração da
`dev/proxima-versao`.

O script não aponta o atalho para o worktree que o executou: ele espelha a
árvore de trabalho atual (exceto `.git`, `bin`, `obj`, `artifacts`,
`node_modules`) para uma pasta irmã fixa e permanente,
`Ralven-dev-shortcut`, e aponta o atalho para essa cópia estável. Assim
o atalho nunca fica órfão quando um worktree de tarefa é removido após o
merge — a próxima tarefa ou integração que reconstruir o atalho simplesmente
sobrescreve o espelho com o estado mais recente.

Se dois trabalhos conflitarem conceitualmente, não escolha um lado apenas porque
o Git resolveu o texto. Compare os objetivos, contratos, testes e comportamento
esperado; preserve ambos quando forem compatíveis e documente qualquer decisão de
precedência necessária.

## Operações remotas

Estas regras já autorizam automaticamente, durante uma tarefa normal:

- push da **branch da tarefa atual**;
- criação e atualização do PR dessa branch para `dev/proxima-versao`.

Durante uma **integração explicitamente solicitada**, também ficam autorizados:

- atualizar branches de PR quando necessário para resolver integração;
- realizar os merges dos PRs aprovados em `dev/proxima-versao`;
- atualizar `origin/dev/proxima-versao`;
- remover branches remotas temporárias já mergeadas quando for seguro.

Fora desses casos, uma operação remota exige autorização explícita do usuário.
Em especial, tarefas e integrações normais nunca autorizam:

- push ou merge em `main`;
- criação de tags ou GitHub Releases;
- publicação de instalador, site ou outros artefatos públicos;
- deploy público;
- force push ou reescrita de histórico remoto.

Se um push, atualização ou merge remoto falhar, não force a operação. Preserve o
trabalho, diagnostique a causa e relate a limitação real.

## Publicação oficial

### Retenção e custo do R2

- O bucket `ralven-releases` mantém os objetos versionados das **7 releases
  SemVer mais recentes**. A limpeza ocorre somente ao final de uma publicação
  bem-sucedida e deve falhar de forma segura se a release corrente não estiver
  entre as versões preservadas.
- Os aliases e manifestos em `stable/` nunca participam da limpeza por versão.
  Não crie expiração por idade para `releases/`: ela poderia remover a versão
  pública atual durante um intervalo longo sem lançamento.
- O lifecycle do bucket pode abortar uploads multipart incompletos após 1 dia;
  ele não deve expirar artefatos completos.
- Mantenha um alerta de orçamento da conta Cloudflare em **US$ 5**. O alerta é
  informativo, não um limite rígido; preserve cache imutável, retenção e
  monitoramento de uso.

É disparada somente por frase como “publicar versão”, “lançar versão”, “criar
release”, “publicar atualização” ou “fazer release oficial”. Ela sempre parte
do estado já integrado e consistente de `dev/proxima-versao`; branches
temporárias de tarefa nunca são publicadas diretamente e tarefas paralelas incompletas não entram na
publicação.

Ao ser disparada, a IA deve:

1. revisar o conjunto integrado desde a última GitHub Release estável, os
   contratos de distribuição e a documentação diretamente afetada, sem reabrir
   uma auditoria geral não relacionada já coberta pela integração e pela CI;
2. confirmar que `PROJECT_STATE.md` representa o estado integrado atual e continua
   compacto; corrigir inconsistências de estado antes de gerar notas públicas;
3. calcular a próxima versão com [Semantic Versioning](https://semver.org/lang/pt-BR/),
   usando todas as mudanças efetivamente integradas desde a última **GitHub
   Release estável publicada**;
4. atualizar todos os arquivos de versão, `CHANGELOG.md`, notas de release,
   instalador, site e demais artefatos de distribuição em uma branch nova e
   exclusiva `release/vX.Y.Z`, criada do SHA atual de `dev/proxima-versao`;
5. abrir o Pull Request dessa branch diretamente para `main`, conferir o SHA
   candidato e iniciar `promote-release.yml` informando o número do PR;
6. depois que o GitHub aceitar a execução, informar versão, SHA, PR e URL/ID da
   execução e encerrar a participação, sem consultas repetidas de status. A
   automação fixa o candidato, espera todos os checks obrigatórios, promove o
   SHA exato, valida `origin/main`, cria e publica a tag em comandos separados,
   chama diretamente a distribuição protegida, verifica feeds/artefatos e
   sincroniza `dev/proxima-versao`;
7. só retomar atuação quando a automação notificar falha ou o usuário solicitar
   diagnóstico. Início, GitHub Release criada e publicação verificada são
   estados distintos; nunca declarar conclusão apenas porque o workflow iniciou.

O workflow `promote-release.yml` é o coordenador determinístico da publicação.
Ele usa `--match-head-commit`, exige `Required CI gate` verde para o SHA fixado,
serializa promoções e chama `release.yml` por `workflow_call`, pois tags criadas
com `GITHUB_TOKEN` não disparam automaticamente outro workflow. A autorização
humana ocorre uma vez no ambiente protegido `release-signing`; o job de
`production` continua isolando seus próprios secrets, mas só inicia depois da
assinatura aprovada e validada.

O modo manual `plan` de `release.yml` é a única simulação sem assinatura nem
efeitos externos. `build` acessa o ambiente de assinatura e produz candidato
assinado sem publicar; `publish` distribui. Nunca apresente `build` como dry-run.

Durante a primeira adoção, se `promote-release.yml` ainda não existir em `main`
e portanto não puder receber `workflow_dispatch`, conclua essa única promoção
pelo procedimento seguro anterior. Depois que a automação estiver na branch
padrão, não substitua seu encadeamento por polling manual do agente.

Um push autorizado não permite ocultar falhas: build, testes, lint, typecheck,
empacotamento e validação de versão devem passar, ou o bloqueio deve ser
informado claramente.

### Tags sem release publicada

Antes de calcular a versão, confirme no GitHub a última release estável, além
das tags existentes. Uma tag sem GitHub Release pública não é uma versão
publicada e não pode reduzir o intervalo de mudanças das Release Notes.

- nunca mova, reaproveite, force-push ou apague uma tag publicada/protegida;
- escolha a próxima versão SemVer disponível após a maior tag estável existente;
- gere changelog e Release Notes a partir da última GitHub Release estável,
  incluindo as mudanças presentes em qualquer tag sem release;
- trate uma tag sem release como pendência de publicação até a próxima release
  estável válida concluir todo o fluxo.

### Levantamento das mudanças integradas

O passo 3 ("calcular a próxima versão... usando todas as mudanças
efetivamente integradas desde a última tag") e o passo 4 ("atualizar...
`CHANGELOG.md`, notas de release...") exigem um levantamento real, não uma
lembrança aproximada do que foi feito. Antes de escrever qualquer changelog,
nota de release ou classificar a versão, a IA que publica deve:

1. determinar o intervalo exato: da última **GitHub Release estável publicada**
   (confirmada por `gh release view`/API; não apenas por `git describe`) até o
   SHA fixado da branch `release/vX.Y.Z`, criada diretamente do estado escolhido
   de `dev/proxima-versao`;
2. listar **todos** os commits desse intervalo (`git log <última-tag>..HEAD
   --oneline` em `dev/proxima-versao`) e, quando existirem, os Pull Requests
   correspondentes — não confiar apenas na memória da sessão ou em um
   resumo parcial de integração anterior;
3. para cada commit/PR, identificar a mudança real por trás da mensagem
   crua (`git show`/diff quando o resumo do commit não for autoexplicativo)
   e classificá-la como pertencente ao produto publicado — nunca incluir
   trabalho que ficou só em branch de tarefa não integrada, PR fechado sem
   merge, ou revertido antes da publicação;
4. cruzar essa lista com o que já existe em `CHANGELOG.md` para essa faixa
   de commits, preenchendo lacunas e removendo qualquer entrada que não
   corresponda a uma mudança realmente integrada;
5. só então compor as entradas do `CHANGELOG.md` (histórico técnico
   completo) e, a partir delas, o corpo da GitHub Release seguindo o
   [Padrão das GitHub Releases](#padrão-das-github-releases-release-notes) —
   a Release nunca é escrita "de memória" sem essa varredura, e nunca
   contradiz o `CHANGELOG.md` correspondente.

Se o histórico for grande ou abranger muitas integrações, é aceitável (e
recomendado) delegar esse levantamento a um agente dedicado só para
sumarizar o intervalo de commits/PRs antes de redigir o texto final —
contanto que a IA responsável pela publicação revise e confirme o resultado
antes de publicar, em vez de copiá-lo sem verificação.

### Sincronização após a publicação

Depois de uma publicação oficial bem-sucedida, `main` e
`dev/proxima-versao` devem apontar para o mesmo conteúdo e histórico. A branch
de integração fica preparada como base das próximas tarefas, que voltarão a
nascer em branches temporárias de tarefa isoladas.

### Validação do atualizador

Antes de considerar qualquer publicação concluída, valide, sempre que possível:

- consulta da fonte de atualizações pelo aplicativo instalado;
- detecção e comparação corretas da nova versão;
- disponibilidade do artefato oficial de instalação/atualização;
- coerência de links, manifestos, hashes e metadados;
- aviso correto ao usuário sobre a atualização disponível.

Quando a validação completa depender de instalação real, rede externa ou
interação manual, relate exatamente o que foi verificado e o que permanece
pendente. Nunca afirme que o atualizador funciona sem evidência concreta.

### Padrão das GitHub Releases (Release Notes)

O corpo (`body`) de toda GitHub Release estável é consumido automaticamente
pelo canal oficial de atualizações do Discord. Por isso, a partir do momento
em que essa automação existir, as Release Notes deixam de ser um detalhe
interno e passam a ser uma **saída pública oficial do projeto**, com padrão
obrigatório.

**Tag e título**

- Tag: `vMAJOR.MINOR.PATCH` (ex.: `v1.4.2`) — sem `v` duplicado, sem espaços
  e sem formato alternativo.
- Título: `Ralven vMAJOR.MINOR.PATCH` (ex.: `Ralven v1.4.2`).

**Tipo de release**

Enquanto o projeto usar somente o canal estável: toda release oficial é uma
release normal/stable — nunca marcada como `pre-release`, nunca deixada como
`draft` ao final da publicação. Branches temporárias de tarefa jamais geram
release pública
(já coberto em "Publicação oficial" acima).

**Estrutura obrigatória do corpo**

Markdown, usando somente as seções abaixo que forem aplicáveis, sempre nesta
ordem, sem seções vazias (se não houver item real para uma seção, omita a
seção inteira — nunca escreva algo como "Nenhuma alteração"):

```markdown
## ✨ Novidades

- ...

## 🔧 Melhorias

- ...

## 🐛 Correções

- ...

## 🔒 Segurança

- ...

## ⚙️ Alterações técnicas

- ...
```

- `## ✨ Novidades`: novas funcionalidades, novas capacidades públicas,
  recursos percebidos diretamente pelo usuário.
- `## 🔧 Melhorias`: UX, desempenho, confiabilidade, estabilidade,
  refinamento de comportamento já existente.
- `## 🐛 Correções`: bugs, regressões e comportamentos incorretos
  efetivamente corrigidos.
- `## 🔒 Segurança`: correções ou hardening relevantes, validações
  adicionais, mitigação de risco — descritas de forma responsável, sem
  detalhes que facilitem exploração de uma vulnerabilidade ainda relevante.
- `## ⚙️ Alterações técnicas`: refatorações relevantes, dependências,
  arquitetura, build, updater, telemetria, instalador e manutenção técnica
  relevante.

**Regras de conteúdo**

1. Nunca publicar uma release oficial sem Release Notes.
2. Antes de escrever, analise efetivamente todas as mudanças integradas
   desde a última versão pública/tag — as notas devem refletir somente
   mudanças realmente presentes na versão publicada.
3. Nunca invente funcionalidades, melhorias, correções, resultados de teste,
   ganhos de desempenho ou melhorias de segurança; não prometa recursos
   futuros.
4. Não inclua trabalho que ficou só em branches temporárias de tarefa sem integrar, nem
   tarefas canceladas ou experimentais.
5. Escreva sempre em português do Brasil, para o usuário final: claro,
   profissional, objetivo, curto, compreensível, sem jargão interno
   desnecessário. Traduza mensagens de commit cruas (`fix null ref
   AccountVM`, `bump package`, `cleanup`) em descrições públicas
   compreensíveis quando forem relevantes.
6. Cada bullet representa uma mudança concreta; não repita a mesma mudança
   em seções diferentes; agrupe alterações muito pequenas quando fizer
   sentido, mas sem esconder mudanças relevantes.
7. Preserve nomes oficiais de funcionalidades, telas e componentes públicos
   do Ralven.
8. Nunca inclua hashes de commit, nomes de branch internas, caminhos locais,
   worktrees, prompts, nomes de agentes de IA, detalhes de processo interno,
   segredos, tokens ou dados pessoais.
9. Uma alteração técnica sem impacto ou relevância pública pode permanecer
   só no `CHANGELOG.md` técnico e não precisa aparecer na GitHub Release.

**Relação com o `CHANGELOG.md`**

`CHANGELOG.md` continua sendo o histórico completo e oficial das versões; a
GitHub Release é a apresentação pública resumida e organizada daquela mesma
versão. Os dois devem permanecer coerentes: a Release nunca pode contradizer
o `CHANGELOG.md`, e uma mudança relevante da versão não deve desaparecer das
Release Notes sem motivo. Informação puramente interna pode continuar só no
changelog técnico.

**Integração com Discord**

O corpo da GitHub Release é consumido automaticamente pelo sistema oficial
de notificações do Discord. Por isso:

- não insira um cabeçalho manual como "Ralven vX.Y.Z está disponível"
  nem repita a versão no início do corpo — o sistema do Discord já cria esse
  cabeçalho a partir do título/tag;
- não adicione links genéricos de download no corpo só para o Discord — a
  automação já anexa o asset da release separadamente;
- nunca use `@everyone`, `@here` ou menções de cargo/usuário;
- evite emojis além dos já padronizados nos títulos das seções;
- evite tabelas Markdown (a apresentação no Discord pode ficar ruim) —
  prefira listas simples com bullets;
- mantenha o Markdown compatível com GitHub e Discord ao mesmo tempo;
- mantenha as notas concisas, mas completas.

**Qualidade antes de publicar**

Antes de criar/publicar a GitHub Release, confirme: versão, tag e título
corretos; Release Notes geradas a partir das mudanças reais; nenhuma seção
vazia; nenhuma mudança inventada ou item relevante omitido; nenhum dado
interno/sensível; coerência entre Release Notes, `CHANGELOG.md`, código
publicado e versão; assets oficiais corretos anexados; release não marcada
como pre-release enquanto o projeto for só stable. Se qualquer verificação
falhar, corrija antes de publicar.

**Exemplo estrutural** (apenas de formato — nunca copie um item dele para
uma release real sem que a mudança correspondente exista de fato):

```markdown
## ✨ Novidades

- Adicionado diagnóstico detalhado das configurações relevantes para o FiveM.
- Adicionada nova visualização das otimizações aplicadas.

## 🔧 Melhorias

- Melhorado o desempenho da análise inicial do sistema.
- Aprimorada a experiência da tela de restauração.

## 🐛 Correções

- Corrigido problema que poderia impedir determinadas otimizações no Windows 11.
- Corrigida inconsistência na exibição do status de algumas ações.

## ⚙️ Alterações técnicas

- Melhorado o tratamento interno de erros e logs.
- Atualizadas dependências utilizadas pelo processo de atualização.
```

### Classificação de versão (Semantic Versioning)

- **patch** (`X.Y.Z` → `X.Y.(Z+1)`): correções, ajustes visuais, segurança,
  documentação de release ou melhorias internas compatíveis sem nova capacidade
  pública relevante;
- **minor** (`X.Y.Z` → `X.(Y+1).0`): novas funcionalidades públicas
  compatíveis ou melhorias de produto que ampliam capacidade sem quebrar
  integrações;
- **major** (`X.Y.Z` → `(X+1).0.0`): mudança incompatível de contrato,
  instalação, atualização, dados persistidos ou comportamento público.

O componente alterado evolui numericamente a partir da versão existente. O
patch é um inteiro decimal SemVer sem largura fixa ou zero à esquerda: `1.1.9`,
`1.1.10`, `1.1.99` e `1.1.100` são válidos. A categoria é decidida pelo
conjunto real de mudanças integradas desde a última versão publicada, não por
uma sequência fixa de increments.

O bloco **Últimas atualizações** deve refletir apenas mudanças presentes no
commit e na release, sem inventar resultados ou prometer itens não testados:

```text
Últimas atualizações:
Versão 1.2.3

- Corrigido: descrição objetiva da correção.
- Melhorado: descrição objetiva da melhoria.
- Atualizado: descrição objetiva de dependências, componentes ou dados.
```

Esse bloco continua sendo usado onde o projeto hoje espera esse formato (por
exemplo, README e site público) e deve ser derivado das mesmas mudanças
reais usadas no `CHANGELOG.md` e nas GitHub Release Notes — nunca pode
divergir da release publicada. As categorias `Corrigido`, `Melhorado` e
`Atualizado` continuam seguindo as regras de conteúdo já definidas acima; ele
não substitui a estrutura de seções (`Novidades`/`Melhorias`/`Correções`/
`Segurança`/`Alterações técnicas`) exigida para o corpo da GitHub Release em
[Padrão das GitHub Releases](#padrão-das-github-releases-release-notes).

Alterações exclusivamente em `AI_RULES.md` ou em outra documentação de
governança podem seguir o fluxo normal de branch + PR para
`dev/proxima-versao`, sem criar versão pública; nunca devem ser apresentadas
como mudança do aplicativo.

## Fluxo de trabalho

```text
main
→ versão pública estável

        ↑ somente publicação oficial

dev/proxima-versao
→ integração oficial da próxima versão

        ↑ PRs revisados e validados

feat/* | fix/* | refactor/* | perf/* | security/* | test/* | docs/* | chore/* | task/*
→ branches temporárias por tarefa

Nova tarefa
→ ler AI_RULES + PROJECT_STATE + documentação relevante
→ inspecionar Git e atualizar referências remotas
→ criar branch pelo objetivo da mudança
→ criar novo worktree exclusivo, sem reutilizar worktree semelhante
→ implementar e testar
→ reconstruir Ralven - Desenvolvimento (exceto tarefas de instalador/updater)
→ commit(s) profissionais
→ push automático somente da branch da tarefa
→ criar/atualizar PR automático → dev/proxima-versao
→ pronta para integração

Integração solicitada
→ descobrir PRs destinados à dev
→ avaliar dependências e conflitos textuais/lógicos
→ atualizar branches quando necessário
→ integrar um PR por vez
→ validar estado combinado
→ consolidar PROJECT_STATE como snapshot curto, sem cronologia
→ não atualizar PROJECT_HISTORY rotineiramente
→ reconstruir Ralven - Desenvolvimento
→ atualizar origin/dev/proxima-versao
→ limpar worktrees/branches temporários já mergeados
→ dev pronta

Publicação oficial solicitada
→ revisão focada, SemVer, changelog e artefatos na release/vX.Y.Z
→ PR release/vX.Y.Z → main
→ dispatch de promote-release.yml e encerramento da participação da IA
→ automação espera gates e promove o SHA exato
→ tag + build protegido + assinatura + publicação + validação do updater
→ automação sincroniza main/dev e registra o resultado
```
