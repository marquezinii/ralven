# Modelo de segurança

O Ralven altera configurações de alto impacto potencial. Segurança, explicabilidade e reversão são requisitos funcionais; não são uma tela de aviso adicionada depois.

## Invariantes

Uma ação aceita pelo produto precisa respeitar todos os itens abaixo:

1. **Escopo conhecido** — instalação e edição foram identificadas sem ambiguidade.
2. **FiveM Legacy dentro de Jogos** — a integração especializada bloqueia GTAV Enhanced com segurança.
3. **Processos encerrados** — nenhuma nova escrita ou limpeza começa com
   processos FiveM ativos. A única exceção é a compensação imediata e estreita
   do snapshot criado pela própria execução que acabou de falhar, descrita em
   "Controles de jogos do Windows"; ela apenas restaura o estado prévio.
4. **Alvo canônico** — o caminho final foi resolvido e permanece dentro do diretório esperado.
5. **Privilégio mínimo** — elevação acontece apenas para uma operação administrativa tipada.
6. **Prévia completa** — o usuário vê o que será alterado, por quê, risco e rollback.
7. **Operação idempotente** — repetir a ação não amplia seu escopo nem degrada o sistema.
8. **Registro local** — início, resultado, falha e restauração ficam auditáveis, sem dados sensíveis.
9. **Cancelamento seguro** — somente entre passos atômicos; nunca no meio de uma escrita crítica.
10. **Sem promessa universal** — a interface informa efeito esperado, não FPS garantido.
11. **Isolamento sem mascarar falha** — uma ação com falha genuína nunca é
    reportada como concluída, e sua reversão nunca é aplicada a outra ação
    que não falhou; ver "Execução isolada por ação" abaixo.

## Ações proibidas

O projeto não aceita implementações que:

- desativem Defender, firewall, SmartScreen, UAC ou antivírus de terceiros;
- adicionem exclusões de antivírus automaticamente ou sugiram desativar a proteção;
- injetem código, leiam/escrevam memória do FiveM ou modifiquem binários do GTA/FiveM;
- executem PowerShell, CMD ou scripts remotos arbitrários por meio do broker;
- apliquem prioridade `Realtime`, afinidade fixa ou desliguem SMT/Hyper-Threading;
- usem “debloat” genérico, removam AppX em massa ou desativem serviços sem relação comprovada;
- editem `commandline.txt` como otimização do FiveM (o FiveM bloqueia
  explicitamente a leitura desse arquivo do GTA — ver `docs/research.md`
  e `BlockLoadSetters.cpp` do próprio FiveM —, então isso nunca teria
  efeito real; a única exceção é o `commandline.txt` do **GTA V Legacy
  standalone**, gerenciado por `GtaVLaunchParametersActions.cs`, nunca
  como caminho de otimização do FiveM);
- sobrescrevam perfil NVIDIA ou ativem/limpem shader cache à força;
- removam dados de autenticação, entitlement, plugins ou configurações em perfis automáticos;
- escondam ações, usem ofuscação para ocultar ações ou payloads, ou baixem código executável depois da instalação;
- contornem anti-cheat, pure mode ou verificações de integridade;
- operem em FiveM/GTAV Enhanced enquanto esse adaptador estiver bloqueado.

## Otimizador geral do Windows

O plano geral usa `OptimizationScope.GeneralWindows` e não exige FiveM ou GTA V
instalado. Essa independência não amplia a allowlist: cada definição precisa
declarar explicitamente que suporta o escopo geral, e `PlanBuilder`, runtime e
broker reconstroem o plano com esse mesmo escopo antes de qualquer execução.
Uma ação exclusiva de FiveM/GTA nunca entra no plano geral por categoria,
prefixo de ID, instalação detectada ou fallback. A presença de GTAV Enhanced
continua bloqueando apenas o módulo especializado.

O primeiro escopo geral reutiliza somente capacidades já estreitas e testadas:

- diagnósticos locais de CPU, GPU, RAM, armazenamento/TRIM, drivers, tela/taxa
  de atualização, rede, pagefile/commit, energia, WHEA, uso de recursos,
  throttling, inicialização, proteções do Windows, aceleração do mouse e gargalo
  provável;
- limpeza allowlisted de arquivos antigos no diretório temporário do usuário,
  com idade mínima, prévia e aviso de irreversibilidade;
- Modo de Jogo e captura histórica em segundo plano pelos dois valores HKCU
  descritos na seção seguinte;
- seleção do plano de energia de desempenho com captura/restauração do GUID
  anterior e somente quando ligado à tomada;
- ASPM PCI Express apenas quando a configuração existe no plano ativo, com
  captura separada de AC/DC, compensação de falha parcial, pós-verificação e
  restauração dos dois valores sem sobrescrever uma escolha posterior;
- efeitos visuais, atraso de menus e aceleração do ponteiro allowlisted via
  `SystemParametersInfo`, com verificação e rollback. A ação do ponteiro é opt-in
  do Ultra, preserva a velocidade escolhida e restaura os três valores anteriores.

Nenhuma dessas ações autoriza mudar pagefile, limpar standby list, instalar ou
remover driver, alterar taxa de atualização, desabilitar item de inicialização,
serviço, proteção ou Windows Update. HAGS, afinidade, prioridade, timer,
debloat, AppX e ajustes de fabricante também não são promovidos ao plano geral.
Quando o Windows não fornece o fato necessário, o resultado é indisponível ou
`Skipped`; o aplicativo não adivinha um estado para conseguir escrever.

Os diagnósticos de TRIM e aceleração do mouse são consultas fixas e somente
leitura: respectivamente `fsutil behavior query DisableDeleteNotify` e
`SystemParametersInfo(SPI_GETMOUSE)`. O primeiro relata apenas a política de
delete notification para NTFS/ReFS, sem afirmar suporte do dispositivo. O
diagnóstico do mouse não altera preferências nem presume que um jogo use o
caminho de ponteiro do Windows; a alteração separada por `SPI_SETMOUSE` só entra
quando a preferência explícita do Ultra compõe a ação. Nenhuma delas atravessa o broker.

As ações compartilhadas de Modo de Jogo e captura preservam a verificação já
existente de processo FiveM. Assim, uma instalação ausente não impede a
execução, mas uma sessão FiveM detectada ou uma inspeção ambígua continua
bloqueando a escrita e a restauração desses valores.

## Controles de jogos do Windows

As informações de PC e saúde exibidas em **Sistema** são estritamente de
leitura. A saúde vem da API nativa da Central de Segurança do Windows em três
consultas independentes; falha, serviço parado ou resultado parcial permanece
explícito como indisponível e não é reinterpretado como seguro ou inseguro. O
estado de atualização automática não comprova que o Windows está atualizado e
o painel não busca, baixa, instala ou aprova atualizações. Essas leituras não
usam broker, elevação, PowerShell, linha de comando ou acesso de rede.

A página Sistema não abre Segurança do Windows, Windows Update ou Sobre. Ela
mostra somente os dados que o Ralven consegue ler com segurança e oferece apenas
as ações tipadas de jogos implementadas dentro do aplicativo. Um estado de
proteção que exige atenção permanece informativo; ele não autoriza correção
automática, alteração de antivírus, firewall ou Windows Update.

O painel **Sistema > Preparar o Windows para jogos** é uma exceção explícita ao
antigo comportamento somente de atalhos da página Sistema, não uma autorização
para alterações genéricas no Windows. Seu escopo é fixo:

- lê e altera somente `HKCU\Software\Microsoft\GameBar\AutoGameModeEnabled`
  e `HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR\HistoricalCaptureEnabled`;
- aceita somente valores `DWORD` ausentes, `0` ou `1`; tipo ou conteúdo
  inesperado torna o painel indisponível, sem tentativa de correção;
- mostra o estado detectado e exige confirmação antes da escrita;
- bloqueia aplicação e restauração enquanto o FiveM estiver ativo e repete essa
  verificação na fronteira de cada escrita; se não puder confirmar que o
  processo está encerrado, não inicia uma nova alteração;
- se o FiveM iniciar depois de uma escrita da mesma execução e a etapa seguinte
  falhar, o motor pode restaurar imediatamente apenas o snapshot recém-aplicado
  por essa execução. Essa compensação estreita evita deixar uma alteração
  parcial; não autoriza nova aplicação nem restauração solicitada pelo usuário
  ou pelo Histórico enquanto o FiveM estiver aberto;
- roda como usuário padrão, sem broker ou elevação;
- executa as duas ações tipadas em uma transação estrita e registra o estado
  anterior de cada valor; uma falha aciona tentativa de reversão e o resultado
  real, inclusive conflito ou falha de restauração, permanece no Histórico;
- a restauração não sobrescreve um valor alterado posteriormente por outro
  programa e, por isso, pode terminar com conflito em vez de prometer sucesso;
- não remove a Game Bar, não impede gravação manual e não promete ganho
  universal de FPS ou desempenho.

O painel nunca aceita caminho, hive, nome de valor, ação ou comando fornecido
pela UI. Qualquer novo ajuste geral do Windows exige ação própria, evidência,
detecção, confirmação, validação, reversibilidade e testes independentes.

## Inventário e atualização de aplicativos

O inventário da página **Aplicativos** faz somente descoberta local como
usuário padrão:

- lê os registros de desinstalação em HKCU/HKLM nas visões de 32 e 64 bits para
  nome, versão, fabricante e tamanho estimado;
- lê apenas os nomes registrados em `Run`/`RunOnce` e os nomes de arquivos nas
  pastas Startup; conteúdo de comandos não é carregado nem exibido;
- nunca lê ou executa `UninstallString` e nunca lê ou escreve
  `StartupApproved`;
- trata acesso negado e fontes indisponíveis como resultado parcial explícito,
  sem transformar ausência de dados em sucesso completo;
- não instala, desinstala, habilita ou desabilita software e não usa o broker.

O centro de pacotes é uma superfície separada, limitada ao Windows Package
Manager:

- o Ralven consulta somente as origens padrão confiáveis `winget` e `msstore`;
  fontes personalizadas, Windows Update e drivers ficam fora do escopo;
- consultas não aceitam termos silenciosamente. Instalação e atualização só
  aceitam termos depois da confirmação que os apresenta ao usuário;
- somente IDs de pacote que passaram pela validação local e vieram do snapshot
  atual podem ser enviados a instalação, atualização ou desinstalação;
- o comando usa argumentos fixos, `--id` com correspondência exata e a origem
  validada na allowlist; nenhum texto, caminho ou argumento livre fornecido
  pela UI é executado;
- o usuário confirma nome, ID, origem, possível UAC e ausência de rollback antes
  de cada instalação/desinstalação ou do lote selecionado de atualizações;
- o lote é sequencial, para em pontos seguros no cancelamento e nunca transforma
  sucesso parcial em sucesso total;
- o Ralven não usa `--force`, `--ignore-security-hash`, `--allow-reboot`, scripts,
  shell ou broker. As verificações do WinGet permanecem ativas;
- o processo principal continua sem elevação. O instalador do fabricante pode
  abrir e o Windows pode solicitar UAC diretamente quando necessário;
- sucesso e falha vêm do código de saída do WinGet. Uma falha nunca é mostrada
  como atualização concluída.

Instalar, atualizar ou desinstalar software de terceiros não entra no motor
transacional de otimizações: o instalador de cada fabricante controla arquivos,
processos e eventual recuperação, portanto o Ralven não promete rollback.
Atualizações ignoradas são apenas uma preferência local `origem|id`, não alteram
o pacote e podem ser restauradas na própria página. Alterações de inicialização
continuam nas superfícies confiáveis do Windows. Texto descoberto no registro
nunca pode ser promovido a comando executável.

## Escopo de edição gráfica

`LegacyGraphicsPresetAction` e `DisplayPreferencesAction` só escrevem opções
já existentes nos arquivos gráficos do FiveM/GTA V Legacy (nunca criam nós
novos) e sempre com backup, hash de verificação e rollback. Dentro desse
modelo:

- os presets Leve/Equilibrado/Agressivo só **reduzem** valores existentes
  (`GraphicsPresetDirection.LowerOnly`); o preset de Qualidade, opt-in e
  nunca automático, é a única exceção que **eleva** valores, até um teto
  conservador documentado no próprio catálogo;
- `DisplayPreferencesAction` só escreve `Windowed`/`VSync` (modo de janela e
  VSync), preservando o formato original do valor (`"true"/"false"` ou
  `"0"/"1"`, conforme o que já estava no arquivo);
- resolução, taxa de atualização, adaptador de vídeo, proporção de tela,
  limite de FPS, escala de resolução e versão do DirectX **não são
  ajustados automaticamente** por nenhuma ação: escolher uma resolução ou
  taxa de atualização não suportada pelo monitor pode deixar a tela preta
  ou o jogo num estado inválido, e o produto não valida ainda essas
  combinações contra os modos realmente suportados pelo monitor. Ver
  `PROJECT_STATE.md` para o registro dessa decisão e do que ficou de fora
  nesta etapa.

## Parâmetros de inicialização do GTA V standalone

`GtaVLaunchParametersActions.cs` gerencia o `commandline.txt` do GTA V
Legacy **standalone**, nunca do FiveM (ver exceção documentada acima em
"Ações proibidas"). Regras específicas:

- só toca em linhas cujo parâmetro pertence a um conjunto allowlisted por
  ação (`GtaVCommandLineFile.Merge`); qualquer outra linha do arquivo,
  incluindo parâmetros que o produto não conhece, é preservada exatamente
  como estava;
- `-width`/`-height`/`-RefreshRate`/`-scOfflineOnly` e demais parâmetros de
  resolução/adaptador **não são gerenciados**, pela mesma razão da seção
  acima (risco de escolher um modo não suportado pelo monitor);
- `-disableHyperthreading` foi avaliado e **deliberadamente não
  implementado**: desligar SMT/Hyper-Threading já é uma proibição explícita
  deste documento ("Ações proibidas"), e a lista de parâmetros pedida não
  altera esse invariante só porque vem de um parâmetro oficial do jogo;
- `-safemode`, `-useMinimumSettings` e `-UseAutoSettings` são tratados como
  reparo temporário: a própria ação (`GtaVRepairLaunchParametersAction`) e
  o aviso do plano (`gtav-repair-launch-parameters-are-temporary`) deixam
  explícito que devem ser revertidos após o diagnóstico, nunca deixados
  ativos permanentemente;
- toda escrita usa backup + restauração exata via rollback da transação,
  igual ao padrão já usado pelas ações gráficas.

## Proteção de caminhos

### Nunca remover automaticamente

- `FiveM.app\data\game-storage`;
- `FiveM.app\data\nui-storage`;
- `FiveM.app\data\ipfs`;
- `FiveM.app\CitizenFX.ini`;
- `FiveM.app\plugins`;
- `%APPDATA%\CitizenFX\gta5_settings.xml`;
- `%APPDATA%\CitizenFX\fivem.cfg`;
- qualquer `fivem_set.bin`;
- `%APPDATA%\CitizenFX\ros_id.dat`;
- `%LOCALAPPDATA%\DigitalEntitlements`;
- arquivos da instalação original do GTAV.

Configurações podem ser editadas por uma ação tipada, mas nunca tratadas como lixo.

#### Exceção documentada: reparo de dados de entitlement

`ros_id.dat` e `%LOCALAPPDATA%\DigitalEntitlements` continuam proibidos de
remoção automática em qualquer perfil (Leve/Médio/Agressivo). A única
exceção é a ação opt-in `fivem.legacy.auth-data.repair`
(`StaleAuthDataRepairAction`), que só existe para o cenário específico de
falha de inicialização por entitlement corrompido, e que respeita todas as
condições abaixo simultaneamente:

- nunca faz parte de nenhum perfil automático (`ActionOptionGate` próprio,
  desligado por padrão; precisa ser habilitado explicitamente fora dos
  perfis padrão);
- só toca em algum arquivo depois de detectar, em log recente dentro da janela
  da sessão, uma frase completa allowlisted de erro de entitlement/autenticação
  — log antigo, timestamp futuro ou substring ambígua falham fechados;
- move os itens para quarentena em vez de apagar diretamente, preservando a
  reversibilidade até a confirmação final da transação, igual ao padrão já
  usado para `server-cache`/`server-cache-priv`;
- vincula o snapshot aos pais canônicos e ao manifesto SHA-256 exato da
  quarentena; conteúdo novo, alterado ou reparse point impede commit/rollback;
- exige que o FiveM esteja fechado, como qualquer outra limpeza condicionada.

### Limpeza condicionada

| Alvo                                                                     | Condição                                                                          | Aviso obrigatório                                                    |
| -------------------------------------------------------------------------- | ------------------------------------------------------------------------------------ | ------------------------------------------------------------------- |
| `data\server-cache`                                                      | FiveM encerrado; usuário abriu manutenção/reparo                                  | recursos serão baixados novamente                                    |
| `data\server-cache-priv`                                                 | mesmas condições                                                                   | clipes antigos do Rockstar Editor podem deixar de funcionar          |
| `crashes`                                                                 | dumps não serão enviados ao suporte                                                | dumps podem ser essenciais para diagnóstico                          |
| `logs`                                                                    | somente arquivos antigos e reconhecidos                                            | logs recentes devem ser preservados                                  |
| `content_index.xml` ou `caches.xml`                                       | erro de integridade/componente correspondente                                      | FiveM fará nova verificação/download                                 |
| `server-cache`+`server-cache-priv`+`logs`+`crashes` (recriação completa)  | FiveM encerrado; ação opt-in `fivem.legacy.local-data.recreate`, nunca automática   | reparo, não otimização diária; primeiro carregamento fica mais lento |
| `ros_id.dat` + `DigitalEntitlements`                                      | FiveM encerrado; padrão de erro de entitlement detectado no log; ação opt-in       | exigirá novo login no próximo início do FiveM                        |

A limpeza de cache não entra implicitamente nos modos Leve, Médio ou Agressivo.

A limpeza do cache gerado pelo próprio Ralven é uma ferramenta manual separada
dos perfis e das ações sobre o FiveM. Ela aceita somente os roots e padrões
descartáveis documentados em [`docs/cache.md`](cache.md), revalida contenção e
reparse points antes da exclusão e preserva configurações, sessão, dados do
usuário, telemetria pendente, transações, quarentenas e segurança do updater.
Falhas de acesso ou arquivos em uso produzem sucesso parcial verificável; nunca
ampliam o escopo da exclusão.

### Monitor de sessão somente leitura

O monitor de sessão da Visão geral é uma capacidade manual e local, limitada ao
FiveM sobre GTAV Legacy. Uma presença positiva exige nome de processo
allowlisted e imagem canônica dentro da raiz FiveM diagnosticada, com validação
contra reparse points. Falha de enumeração ou leitura da imagem é estado
indeterminado, nunca presença nem ausência confirmada; duas ausências
confirmadas consecutivas são necessárias para encerrar a sessão.

O monitor mantém apenas estado e duração em memória, continua funcionando com
o Ralven na bandeja e termina quando o aplicativo fecha. Ele não usa rede,
telemetria, persistência, broker, leitura de memória, hooks ou injeção e não
altera FiveM, GTA V ou Windows. Sua existência não autoriza prioridade,
afinidade, plano de energia, timer resolution ou qualquer outra mutação por
sessão sem arquitetura própria de rollback e recuperação.

O alvo FiveM do painel de desempenho é uma leitura separada e ainda mais
restrita ao primeiro plano: só consulta tempo de CPU e working set que o Windows
expõe para processos com imagem validada dentro da raiz Legacy diagnosticada.
Ele pausa com a Visão geral, não lê o conteúdo da memória do processo e não
autoriza hooks, injeção, overlay, telemetria, persistência ou qualquer mutação.
Métricas por processo que não possam ser obtidas com esse contrato aparecem
como indisponíveis.

### Encerramento de processo travado

A ação opt-in `fivem.legacy.stuck-process.terminate`
(`StuckProcessTerminationAction`) é a única capacidade do produto que
encerra um processo, e só o faz sob todas as condições abaixo:

- o processo alvo precisa ter a imagem executável dentro da pasta de
  instalação do FiveM (mesma verificação usada por `IFiveMProcessInspector`);
  nunca um processo de terceiros, do GTA V ou do sistema;
- o processo precisa estar comprovadamente sem resposta (`Process.
  Responding == false`) no momento da leitura; um processo respondendo
  normalmente nunca é encerrado;
- nunca faz parte de nenhum perfil automático — é opt-in, desligado por
  padrão, e existe apenas para desbloquear uma limpeza de cache impedida por
  uma instância travada.

Isso não é uma exceção às proibições de "afinidade fixa/prioridade
Realtime/SMT" nem de manipulação de outros processos: o escopo é
estritamente o próprio processo do FiveM, nunca outro.

## Ciclo transacional

Cada execução segue o mesmo protocolo:

```text
Descobrir → Planejar → Validar → Criar snapshot → Aplicar → Verificar → Confirmar
                                          ↘ falha → Restaurar → Relatar
```

### Execução isolada por ação

A execução do usuário padrão (`AppOptimizationService`) roda o motor com
`IsolateFailures = true`. Cada ação do plano é uma mini-transação
independente — verifica, aplica, valida e registra o próprio resultado —
mas os invariantes acima continuam valendo integralmente:

- **rollback atômico por ação é preservado**: uma falha reverte somente a
  ação que falhou, usando exatamente o mesmo par aplicar/reverter já
  existente; nenhuma ação nunca fica com escrita parcial;
- **dependência declarada é respeitada**: uma ação com pré-requisito não
  atendido (por exemplo, uma ação de gráficos sem a verificação de processo
  encerrado bem-sucedida) é marcada `Skipped`, nunca executada às cegas;
- **falha crítica aborta com segurança**: ações marcadas `IsCritical`
  (as verificações de processo do FiveM/GTA V) que falham interrompem as
  ações independentes restantes, que ficam `NotRun` — a run nunca continua
  escrevendo depois que uma pré-condição de segurança não pôde ser
  confirmada;
- **nenhum sucesso parcial é relatado como sucesso total**: o resultado
  final da transação (`CommittedWithErrors` vs. `Committed`) e o relatório
  estruturado (`OptimizationReportDto`) só marcam sucesso quando nenhuma
  ação terminou como `Failed` ou `RollbackFailed`;
- **cancelamento seguro é preservado**: o cancelamento entre etapas continua
  aceito apenas entre ações atômicas, nunca no meio de uma escrita.

O **broker elevado** continua executando no modo estrito original
(tudo‑ou‑nada com rollback total em falha) *dentro da própria fase
elevada*, pois cada plano tipicamente delega apenas uma ação administrativa
por vez (o plano de energia de desempenho); a superfície de falha isolada
não se aplica lá.

**Mas uma falha ou cancelamento dessa fase elevada não desfaz mais as ações
de usuário padrão já confirmadas na fase anterior.** Até 26/07/2026, quando
o broker falhava (ou o UAC era cancelado), `AppOptimizationService` desfazia
todas as ações de usuário padrão já aplicadas com sucesso — o que produzia
o efeito relatado de "várias otimizações falhando de uma vez" quando, na
prática, só a ativação do plano de alto desempenho havia falhado. Agora,
uma falha nessa fase marca somente a própria ação administrativa como
`Failed` (via `WindowsTransactionEngine.MarkAdministratorPhaseFailedAsync`),
preserva as ações já `Committed` e conclui a transação como
`CommittedWithErrors`. Isso não é uma exceção ao invariante "uma ação que
falha reverte só a própria ação" — é a correção de um caso em que ele não
estava sendo respeitado entre as duas fases da mesma transação.

As mutações administrativas (`EnableSessionPerformancePowerPlan` e o experimento
opt-in `ToggleHags`) não são executadas na fase de usuário padrão. A ação de
energia pode fazer uma leitura preliminar sem elevação para reconhecer que o
plano de alto desempenho já está ativo; qualquer mudança continua obrigatoriamente
no broker. O broker elevado valida o plano e mantém em `HKLM` a cópia autoritativa
dos campos do journal que controlam ações administrativas. Essa cópia protegida é atualizada
antes do journal local a cada transição e restaura um arquivo local atrasado ou
adulterado. Rollback elevado falha fechado quando o recibo não existe, está
corrompido ou não corresponde à identidade das ações; recibos terminais são
retidos para impedir replay. Registros legados sem recibo continuam visíveis no
histórico, mas não oferecem rollback administrativo.

A restauração solicitada pelo usuário segue a ordem inversa das fases:
primeiro o broker restaura as ações administrativas, depois o app restaura as
ações de usuário padrão. Isso devolve o plano de energia original antes de
restaurar sua política ASPM. Cancelamento do UAC ou falha administrativa impede
a fase local; mudanças posteriores do usuário continuam protegidas pelas
verificações de snapshot. `AwaitingStandardRollback` confirma somente o término
da etapa administrativa, nunca a restauração completa.

Uma falha local isolada permite executar ações administrativas ainda pendentes;
a falha original permanece no journal e no relatório. Ações falhadas não são
reexecutadas e uma falha crítica continua impedindo as etapas restantes.
Cancelamento, inclusive entre ações, finaliza a transação como
`CommittedWithErrors`, registra as ações não executadas e preserva os snapshots
das mudanças confirmadas para o histórico e rollback.

Esse modelo atende ao requisito de "tratar erro sem interromper
inutilmente todo o processo" sem abrir mão de nenhum dos invariantes de
segurança documentados nesta página.

### Descobrir

- localizar a instalação padrão ou personalizada;
- canonicalizar caminhos e resolver links/reparse points;
- identificar Legacy versus Enhanced;
- obter versão do Windows, espaço livre, GPU, VRAM e RAM;
- detectar processos cuja imagem pertence à instalação FiveM.

### Planejar

O plano é imutável depois da confirmação e contém:

- identificador e versão de cada ação;
- estado observado e estado desejado;
- arquivos/valores que poderão ser tocados;
- necessidade de privilégio e reinício;
- estimativa de espaço recuperável;
- risco, evidência e estratégia de rollback.

### Criar snapshot

- arquivos pequenos são copiados com metadados e hash;
- valores Windows preservam tipo e existência, não apenas conteúdo;
- XML é validado antes e depois da cópia;
- caches grandes não são duplicados silenciosamente;
- quando há espaço, uma limpeza pode usar quarentena no mesmo volume;
- sem espaço para quarentena, a exclusão irreversível exige confirmação explícita.

### Aplicar e verificar

- usar escrita temporária e troca atômica para configurações;
- conferir pós-condições de cada ação;
- interromper a sequência ao primeiro erro não recuperável;
- não reportar sucesso parcial como otimização concluída;
- restaurar automaticamente o passo atual quando a pós-condição falhar.

### Restaurar

Rollback precisa ser testável e simétrico. Restaurar significa recuperar:

- conteúdo e localização do arquivo;
- valor, tipo e existência de configuração;
- seleção de perfil e campos gráficos alterados;
- estado de energia somente se a aplicação o criou ou modificou.

Cache já removido sem quarentena é explicitamente marcado como não reversível; sua recuperação ocorrerá por novo download do FiveM.

**O journal é pré-requisito do rollback.** Restaurar uma transação passada
depende de conseguir desserializar `Transactions/<id>.json`, que pode ter sido
escrito por uma versão anterior do aplicativo. Um journal que não carrega não
falha de forma visível: a transação simplesmente desaparece do histórico e
deixa de ser reversível. Por isso os enums persistidos são contrato durável —
membros só podem ser acrescentados, nunca renomeados, removidos ou
renumerados — e `PersistedEnumContractTests` existe para tornar essa quebra
impossível de passar despercebida. Ver `docs/architecture.md`, seções
"Resultado" e "Persistência".

## Broker elevado

A interface e a maior parte do motor executam sem elevação. O broker administrativo:

- recebe contratos tipados e versionados;
- não aceita linha de comando ou script arbitrário;
- restringe o servidor do pipe ao usuário atual; o cliente elevado evita a
  validação redundante de proprietário do .NET, que não é estável através do UAC;
  valida o identificador efêmero da sessão, a edição e o alvo novamente;
- usa allowlist de ações administrativas;
- resolve caminhos do próprio lado;
- encerra quando a sequência privilegiada termina;
- retorna resultado estruturado, sem texto usado como comando subsequente.

Uma operação em arquivos `%LOCALAPPDATA%` ou `%APPDATA%` normalmente não precisa do broker.

Desde 26/07/2026, o broker também grava um log local de ciclo de vida
(`%LOCALAPPDATA%\Ralven\Logs\broker-diagnostics.log`, apenas nome do
evento + timestamp + ID de transação, sem caminhos, sem dados do plano) em
marcos como `broker-started`, `pipe-connected`, `elevation-confirmed`,
`action-started`, `terminal-event-sent`. Cada linha é gravada com
`FileOptions.WriteThrough` (flush imediato em disco), justamente para
sobreviver a um encerramento externo do processo (ver seção sobre
antivírus/SmartScreen abaixo) e permitir distinguir, depois, em qual etapa
exata o broker parou.

## Compatibilidade com antivírus

Não é possível garantir ausência de falsos positivos em todos os produtos. O processo de distribuição deve reduzir superfície suspeita:

- binários e instalador assinados;
- builds determinísticos e hashes de release publicados;
- código-fonte correspondente a cada release;
- sem packers ou payload embutido inesperado. A distribuição de release
  ofusca assemblies internos de `Core` e `Windows` após a compilação e antes
  de hash, assinatura e empacotamento; isso não é evasão de antivírus e é
  verificado no pipeline. O código-fonte permanece disponível para auditoria;
  veja [hardening da release](release-hardening.md). A única exceção de
  atualização é o `Ralven.Updater.exe` autocontido, empacotado pelo
  instalador, copiado para `%LOCALAPPDATA%\Ralven\Updater` e limitado a
  executar o instalador GitHub já validado por nome, caminho, tamanho e SHA-256;
- sem persistência, driver, injeção ou manipulação de processo;
- manifesto do broker com escopo mínimo;
- comunicação clara de cada alteração administrativa.

O instalador ainda não possui assinatura digital de uma autoridade pública e pode não possuir reputação no SmartScreen. O usuário deve conferir o GitHub Release e o SHA-256, mas essa conferência não autoriza evasão: o Ralven não desativa proteções, não cria exclusões e não recomenda renomear, reempacotar ou ofuscar binários para evitar detecção. Se a política do computador bloquear o instalador, a alternativa segura é não executá-lo, compilar o código revisado ou aguardar uma release assinada. Veja [release, integridade e simulação](release-preview.md).

Uma exceção de antivírus recomendada pelo suporte do FiveM para um erro específico não autoriza o Ralven a criar essa exceção automaticamente.

## Dados e privacidade

O Ralven AI é um fluxo remoto explícito e separado da telemetria. Ele não pode
receber paths do diagnóstico, arquivos, credenciais, tokens ou dados que
identifiquem diretamente a conta, nem executar a saída do modelo. A resposta
pode solicitar somente uma ferramenta local allowlisted sem argumentos livres:
atualizar diagnóstico, abrir uma tela existente ou preparar a revisão de um
perfil. O App revalida a solicitação; nenhuma ferramenta escreve no PC e toda
alteração posterior continua no fluxo transacional confirmado pelo usuário. Um
identificador HMAC pseudônimo, com segredo exclusivo do Worker e separação de
domínio, é o único vínculo de conta enviado ao provedor. O UUID de idempotência
do cliente não é encaminhado. O contrato e os limites estão em
[`docs/ralven-ai.md`](ralven-ai.md).

O diagnóstico permanece local por padrão. Relatórios exportados devem:

- remover nome de usuário dos caminhos;
- não incluir tokens, cookies, entitlement ou conteúdo de chat;
- não anexar dumps, ETW traces ou logs sem seleção explícita;
- mostrar uma prévia do pacote antes de salvar ou compartilhar;
- indicar que ETW e dumps podem conter dados sensíveis.

O formulário de bug é uma exceção explícita ao processamento apenas local: depois do clique em **Enviar**, os campos autorizados (somente texto — categoria, resumo, descrição, versão, perfil, e-mail e log opcionais) são encaminhados ao Worker Cloudflare do projeto (rota `/bugs`), não mais ao FormSubmit. Não há mais anexo/captura de tela nesse formulário. O app não envia esse conteúdo em segundo plano, não repete automaticamente uma falha e oferece cópia local do texto. Consulte [Relatos de bug e privacidade](bug-reports.md) antes de usar o canal.

Os diagnósticos técnicos, incluindo categoria allowlisted de erro, duração e
resultado de uma otimização, versão do aplicativo, Windows, arquitetura,
hardware, perfil e IDs das ações, são opcionais e controlados em
Configurações. Eles não leem nem enviam logs, arquivos, documentos, histórico,
caminhos, identificador de máquina ou outros dados pessoais. A especificação,
o provedor e o limite de metadados de transporte estão documentados em
[Diagnósticos essenciais, dados opcionais e privacidade](telemetry.md).

O relatório automático de falhas (Sentry) também é opcional e nunca roda no
broker elevado (`ICrashReportingService` só existe na camada App). O DSN vem de um arquivo
de configuração por ambiente, nunca de um literal no código-fonte; todo
evento é sanitizado (`CrashReportSanitizer`, reaproveitando `ReportSanitizer`)
antes de sair do processo, removendo nome de máquina, IP, identificador de
usuário e qualquer caminho pessoal. Detalhes completos em
[Telemetria opcional](telemetry.md).

O scaffold do painel administrativo (`infra/dashboard/`) e do Worker que o
alimenta (`infra/cloudflare-worker/`) não fazem parte do aplicativo
Ralven distribuído: rodam apenas na infraestrutura Cloudflare, nunca
no processo do usuário nem no broker. A senha de administrador nunca fica em
texto puro em nenhum lugar do repositório — só um hash PBKDF2 gerado
localmente, guardado exclusivamente como Secret do Worker.

O relatório técnico do otimizador (botão "Copiar relatório técnico" ao final
de uma execução) segue a mesma política: `ReportSanitizer` substitui
`%LOCALAPPDATA%`, `%APPDATA%` e `%USERPROFILE%` reais por seus nomes de
variável antes de qualquer texto ser copiado, e o modelo do relatório
(`OptimizationReportDto`) nunca carrega tokens, credenciais, conteúdo de
chat ou dados de autenticação — apenas IDs de ação, resultado e contagens.
A cópia é sempre uma ação explícita do usuário; nada é enviado pela rede.

## Comunicação de vulnerabilidades

Não publique exploits ou bypasses em issues. Siga [SECURITY.md](../SECURITY.md).

## Ultra e acesso Pro

O [Ultra](ultra.md) compõe apenas ações tipadas e suportadas no Windows geral.
Sua ação exclusiva de resposta do ponteiro só pode ser selecionada por uma
preferência pessoal presente no plano; não é habilitada por opções dos perfis
gratuitos. Runtime e broker recompõem o plano e rejeitam opções divergentes.
Limpeza permanece opt-in; ASPM não entra no perfil pessoal. Acompanhamento e
medições somente leem o PC e não reaplicam ajustes. Pro é revalidado antes de novas operações, preservando transações em
andamento, registros locais e rollback após expiração.
