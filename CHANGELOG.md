# Changelog

Todas as mudanças relevantes deste projeto são registradas aqui. O versionamento
segue [Semantic Versioning](https://semver.org/lang/pt-BR/): correções usam
`patch`, melhorias compatíveis usam `minor` e mudanças incompatíveis usam
`major`.

## [1.7.1] - 2026-09-14

### Corrigido

- Corrigida uma falha que podia impedir operações autorizadas pelo
  administrador de concluir após a confirmação do UAC.

### Alterações técnicas

- Preparado o vínculo seguro entre conta e Discord para ativação posterior,
  mantendo a interface oculta enquanto o bot oficial e seu segredo de serviço
  não estiverem configurados.
- Reorganizada a automação de anúncios de release no Discord e atualizado o
  analisador estático compatível do código .NET.

## [1.7.0] - 2026-09-11

### Adicionado

- Adicionado o centro de Aplicativos para pesquisar, instalar, atualizar e
  desinstalar pacotes suportados com origem e confirmação explícitas.
- Adicionados o hub dedicado do FiveM Legacy, seleção manual validada da
  instalação e suporte da interface em francês.
- Reforçada a proteção de contas com autenticação em dois fatores e códigos de
  recuperação.

### Melhorado

- Refinadas as experiências de Visão geral, Sistema, Jogos, bandeja, diálogos e
  instalador, com diagnósticos mais claros e menos coleta em segundo plano.
- Ampliados os diagnósticos e relatórios para identificar falhas por ação sem
  esconder o resultado real da execução.

### Corrigido

- Corrigidos contraste, foco e cortes visuais em controles e cartões da
  interface.
- Corrigida a preservação da ordem da fila de telemetria após períodos sem
  conexão.
- Recursos Pro e Ralven AI agora permanecem claramente bloqueados enquanto
  estão em desenvolvimento, sem compra, ativação ou cobrança.

### Segurança

- Restringidas as ações de pacotes ao alias oficial do WinGet e reforçada a
  validação do ciclo de atualização e dos artefatos de release.

### Alterações técnicas

- Atualizados Worker, dashboard, dependências compatíveis e automações de CI
  para manter a publicação e a operação administrativa verificáveis.

## [1.6.1] - 2026-09-01

### Alterações técnicas

- Atualizados o Wrangler usado na publicação da infraestrutura Cloudflare e a
  base de compatibilidade de navegadores do site, com validação completa na CI.

## [1.6.0] - 2026-09-01

### Adicionado

- Ralven passa a oferecer Visão geral do sistema, inventário de aplicativos,
  biblioteca de jogos e um cartão dedicado ao FiveM, com diagnósticos locais
  para contextualizar o plano antes de qualquer alteração.
- Adicionados monitoramento local da sessão FiveM e controles opt-in de jogos
  do Windows, com detecção, verificação e reversão dentro dos limites de
  segurança do produto.
- A revisão do plano agora detalha ações, pré-condições, efeitos esperados e
  limitações para tornar a decisão do usuário mais clara.

### Melhorado

- A experiência pública e o aplicativo foram consolidados sob a identidade
  Ralven, incluindo recursos visuais, instalador, documentação e fluxos de
  atualização.
- Preferências iniciais foram ajustadas: iniciar com o Windows, minimizar para
  a bandeja, procurar atualizações e avisar sobre atualizações vêm ativados em
  novas instalações; iniciar minimizado continua opcional e depende da
  inicialização com o Windows.
- Relatórios opcionais, telemetria e crash reporting passaram a compartilhar
  uma escolha de privacidade clara e reversível.

### Corrigido

- Corrigida a preservação das escolhas de inicialização durante atualizações do
  instalador e mantido o opt-out explícito para o usuário.
- Corrigida a rota de distribuição: downloads e atualizações passam a usar
  `vemryx.com/Ralven/` e o feed assinado da Vemryx, sem depender do GitHub Pages.
- Corrigida a compatibilidade da telemetria legada e reforçadas validações de
  ações do otimizador para reportar estados reais com segurança.

### Segurança

- Reforçados consentimento para crash reports, idempotência de telemetria,
  proteção CSRF administrativa e isolamento das chaves de assinatura da release.
- A fundação de billing e entitlements foi preparada no backend com validação
  server-side e falha segura; ela não habilita checkout público nem cobrança
  automática nesta versão.

### Alterações técnicas

- O workflow estável publica instalador, runtime, hashes e manifestos assinados
  no armazenamento da Vemryx, conserva somente as 7 releases SemVer mais
  recentes e preserva os aliases estáveis.
- Atualizadas dependências de infraestrutura e do site, incluindo o patch de
  segurança do Next.js e o Wrangler do Worker.

## [1.5.1] - 2026-08-24

### Corrigido

- Corrigida a identidade pública na página de download, no instalador e nos
  links oficiais para Vemryx One.
- Corrigida a consulta de atualizações após a migração do repositório, mantendo
  a ponte estrita para instalações existentes.

### Alterações técnicas

- O repositório público passou a usar `marquezinii/VemryxOne`; o GitHub Pages
  passa a ser publicado em `marquezinii.github.io/VemryxOne`.

## [1.5.0] - 2026-08-24

### Adicionado

- Nova identidade pública Vemryx One no aplicativo, instalador, dashboard e
  site, preservando a atualização para instalações existentes do FiveMCleaner.
- Reforçado o fluxo de conta com perfil obrigatório, aceite de termos e suporte
  a senha para contas compatíveis autenticadas pelo Google.

### Melhorado

- Renovados os recursos visuais, ícone e textos públicos para a identidade
  Vemryx One.

### Corrigido

- Corrigidos bindings de textos localizados que podiam exibir conteúdo incorreto
  em partes da interface.

### Segurança

- A sessão do aplicativo agora exige e-mail verificado, perfil válido e termos
  aceitos; mutações administrativas remotas validam a origem do dashboard.

### Alterações técnicas

- Mantida a ponte de distribuição: os instaladores Vemryx One e FiveMCleaner
  são o mesmo binário assinado por hash, e o updater continua consumindo o
  runtime compatível.
- Atualizado o Wrangler usado pelo Worker de infraestrutura.

## [1.4.3] - 2026-08-20

### Corrigido

- Corrigida a abertura do Otimizador quando o plano está vazio; a tela não exibe mais um erro inesperado nesse cenário.
- Corrigida a formatação da mensagem de erro inesperado, que exibia marcadores de quebra de linha literalmente.

## [1.4.2] - 2026-08-20

### Corrigido

- Corrigido o estado vazio do plano de otimização, que não exibia ações, avisos e botão de ação sem ações disponíveis no FiveM Legacy.
- Corrigido erro de inicialização da tela de relato de bug causado por recurso de estilo inexistente no `BugReportWindow`.

## [1.4.0] - 2026-08-20

### Adicionado

- Avisos ao vivo publicados pelo painel administrativo e exibidos no aplicativo,
  com dispensa lembrada até a chegada de uma nova comunicação.
- Notas da versão após uma atualização concluída, apresentadas somente quando
  houver conteúdo novo para a versão instalada.
- Campos de diagnóstico e telemetria v5, com coleta essencial e opcional
  limitada pelo consentimento, suporte no Worker/D1 e novas métricas no painel.

### Melhorado

- Interface das telas Visão geral, Otimizador e Histórico renovada, com tema
  consistente, melhor responsividade, foco e progresso baseado em etapas reais.
- Fluxos de conta, relato de bugs, telemetria e atualização receberam cobertura
  adicional de contratos, falhas e privacidade.

### Corrigido

- Corrigida a ingestão dos campos de telemetria v5 no Worker, incluindo schema,
  migration aditiva e persistência dos dados validados.

### Alterações técnicas

- Endurecido o pipeline de distribuição: assemblies internas são ofuscadas antes
  de hash, assinatura e empacotamento, com validação fail-closed e smoke do
  runtime distribuído.
- Atualizadas as actions de atestação de proveniência do GitHub.

## [1.3.2] - 2026-08-06

### Corrigido

- O download de atualização de um clique falhava sempre, em qualquer
  máquina, com "Um arquivo necessário está indisponível ou em uso": o
  arquivo temporário do download era movido para o destino final antes de
  o `FileStream` de escrita ser fechado, e o Windows nunca libera esse
  handle a tempo. O download inteiro (133 MB) completava e validava o
  hash normalmente, só falhava no último passo. Corrigido fechando o
  stream antes do `File.Move`; cobertura de regressão adicionada.

## [1.3.1] - 2026-08-06

### Corrigido

- O botão "Continuar com o Google" nunca apareceu no instalador público da
  v1.3.0: a credencial OAuth vive apenas em um overlay local git-ignorado
  (por exigência do push protection do GitHub) e o pipeline de release nunca
  a injetava no pacote publicado. O workflow de release agora escreve esse
  overlay a partir de segredos do repositório antes de empacotar, e o login
  com Google passa a funcionar na build oficial.

## [1.3.0] - 2026-08-06

### Adicionado

- Contas de usuário completas: cadastro e login com Firebase Authentication
  (nome, sobrenome e usuário único reservado pelo backend), login com Google
  via OAuth2 + PKCE, e gerenciamento de conta (senha, e-mail, foto de perfil,
  exclusão) diretamente em Configurações.
- Monitor de desempenho ao vivo na Visão geral, com leituras reais de CPU,
  GPU, memória, disco e rede e histórico gráfico do último minuto.

### Corrigido

- Reforçada a segurança do Worker de telemetria: CORS restrito à origem do
  painel, limite de requisições na consulta de disponibilidade de usuário,
  hashing de sessão, proteção contra injeção de fórmula na exportação CSV e
  idempotência de ingestão.
- Eliminadas condições de corrida na detecção de software na inicialização,
  no encerramento do processo anterior do atualizador, no rollback elevado do
  broker (agora com timeout) e na restauração de sessão.
- Corrigida a leitura de contadores de desempenho que mantinha CPU e disco
  sempre em 0% no monitor ao vivo, e corrigida a política de senha para
  exigir apenas o mínimo de 12 caracteres.
- Corrigida a compatibilidade multiplataforma para Windows 10/11 em
  localidades diferentes de pt-BR e o carregamento tolerante de
  `settings.json` fora do schema atual.

### Melhorado

- Visão geral, Otimizador e o dashboard privado de telemetria redesenhados,
  com ícones vetoriais próprios, saudação por horário e link oficial do
  Discord.
- Reativação da instância em execução ao tentar abrir o aplicativo uma
  segunda vez, em vez de abrir uma nova janela.

## [1.2.0] - 2026-08-01

### Adicionado

- Atualizador transacional assinado: o runtime passa a ser baixado como pacote
  fechado e verificado, ativado por ponteiro atômico e iniciado pelo Launcher.
  A nova versão confirma a própria saúde com nonce; sem confirmação, o
  Launcher restaura automaticamente a última versão saudável.
- Telemetria sanitizada específica do atualizador e suporte no painel para
  acompanhar etapas de download, ativação, saúde e recuperação, sempre sujeito
  ao consentimento de telemetria.

### Corrigido

- Corrigidas corridas de encerramento do processo anterior e de locks
  transitórios nos arquivos de ativação, evitando que uma atualização válida
  fique presa ou que uma candidata nunca iniciada permaneça ativa.
- Corrigida a compatibilidade da telemetria legada sem campo de ambiente no
  Worker, preservando a rejeição de valores inválidos.

### Melhorado

- Renovada a interface Fluent, com navegação, telas secundárias, progresso do
  otimizador e estados de detecção mais claros, preservando o fluxo seguro de
  prévia e rollback.
- Reforçados contratos, testes e validações do instalador, do runtime e dos
  documentos de segurança para o fluxo de atualização pública.

## [1.1.3] - 2026-07-30

### Corrigido

- Eliminado o impasse entre o aplicativo e o instalador automático: depois
  que o setup verificado é iniciado pelo Windows, o aplicativo fecha
  imediatamente e libera os próprios arquivos para substituição. Antes, o
  app aguardava o setup enquanto o setup aguardava o app fechar, podendo
  encerrar com código 1 em alguns computadores.

## [1.1.2] - 2026-07-30

### Corrigido

- Corrigida a inicialização silenciosa do instalador de atualização em PCs
  onde a pasta de logs ainda não existe. A criação do log é preparada antes
  de iniciar o setup e, se o log não puder ser criado, não bloqueia a
  atualização verificada.

## [1.1.1] - 2026-07-30

### Corrigido

- Corrigido o contrato de telemetria de produção: o cliente agora envia o
  ambiente exigido pelo Worker e descarta rejeições permanentes em vez de
  manter lotes inválidos em fila.
- Corrigidos o consentimento e a fila local para impedir transmissão após a
  revogação da opção e envios duplicados por flushes concorrentes.
- Corrigido o filtro de data final do dashboard e dos relatos de bug, que
  agora inclui integralmente o dia selecionado.

### Melhorado

- Relatos de bug passam pelo Worker e D1 do FiveMCleaner e ficam disponíveis
  no painel administrativo autenticado; o FormSubmit não é mais usado.
- Atualizadas as instruções de operação e a documentação de segurança para
  refletir o fluxo de telemetria e relato de bugs validado em produção.

## [1.1.0] - 2026-07-27

### Adicionado

- Novo consentimento de privacidade versionado, telemetria técnica opcional
  via Worker Cloudflare e relato de bugs em texto com rota própria, validação
  no servidor e painel administrativo protegido.
- Atualização estável de um clique: após a confirmação, o instalador já
  verificado é executado silenciosamente, preserva a instalação e reabre o
  FiveMCleaner atualizado.
- Diagnósticos e ações adicionais para HAGS, Fullscreen Optimizations,
  G-SYNC/FreeSync, drivers, GPUs híbridas, bateria, PCIe ASPM e mouse polling;
  idioma Espanhol incluído na interface.

### Corrigido

- O broker administrativo não desfaz mais ações comuns já concluídas quando
  uma etapa elevada falha; reforçadas leituras de processo, escritas atômicas,
  launches restritos e a fila local de telemetria.
- Corrigidos instância única, detalhes de atualização, notificações de bandeja
  e campos de telemetria para manter os dados enviados válidos e limitados.

### Melhorado

- Barra inferior do plano, dica de privilégio administrativo e telas de
  consentimento ficaram mais claras, sem ampliar permissões do aplicativo.
- Documentação de segurança, privacidade, arquitetura, atualização e catálogo
  foi revisada para refletir os limites e o comportamento efetivamente entregue.
- Pipelines de CI, GitHub Pages e release passaram a usar as revisões atuais
  das ações oficiais do GitHub, mantendo checkout, setup, Pages e atestação
  de proveniência atualizados.

## [1.0.3] - 2026-07-25

### Corrigido

- Corrigidos fluxos transacionais de cancelamento e a persistência do journal
  para que uma execução interrompida não fique presa em estado intermediário.
- Corrigidos gates independentes para preferências gráficas de FiveM e GTA V,
  evitando que um ajuste planejasse indevidamente o outro.
- Corrigidos o diagnóstico de timeout do broker e a atualização do ledger de
  etapas administrativas durante a otimização.

### Melhorado

- Adicionada confirmação temática antes de cancelar ou fechar o aplicativo
  durante uma otimização; o fechamento confirmado aguarda o cancelamento seguro
  da etapa atual.
- O selo **Recomendado** agora segue o diagnóstico real do computador, e o
  plano atual ficou mais limpo ao ocultar metadados internos que poluíam a lista.
- Incluída notificação nativa do Windows quando uma atualização estável é
  encontrada.

### Atualizado

- Ampliados os diagnósticos e as opções opt-in para FiveM/GTA V Legacy,
  incluindo cache, instalação, gráficos, janela/VSync, commandline standalone,
  benchmark oficial do GTA V e comparação local antes/depois.
- Atualizadas as configurações, documentação de segurança, telemetria e a
  licença source-available do projeto para refletir o comportamento atual.

## [1.0.2] - 2026-07-23

### Corrigido

- Corrigido o fechamento inesperado na abertura causado pelo binding da versão
  no painel lateral.
- Corrigido o contraste do número da versão no tema escuro.
- Corrigido o enquadramento da janela maximizada para respeitar a área útil do
  monitor, sem faixas vazias nem rodapé oculto.

### Melhorado

- Refinados o selo de versão, o card de proteção e os seletores de idioma e
  aparência para melhorar legibilidade, alinhamento e consistência visual.
- O rodapé com relato de bug e copyright permanece acessível com a janela
  maximizada.

### Atualizado

- A página pública de download agora mostra a seção **Última versão pública**
  com mudanças verificáveis e link para o histórico completo.

## [1.0.1] - 2026-07-23

### Público

- Corrigida a proporção da arte lateral e o idioma inicial do instalador.
- Adicionados limites de tempo seguros para fases administrativas, evitando que uma etapa fique travada indefinidamente.
- Atualizada a publicação pública com landing no GitHub Pages e download direto do instalador.

## [1.0.0] - 2026-07-23

### Público

- Marco da primeira versão pública estável, mantendo toda a evolução técnica
  entregue antes desta numeração.
- Landing page própria para download, com visual do FiveMCleaner e acesso ao
  instalador oficial pelo GitHub Releases.

### Alterado

- Diagnóstico visual de FiveM Legacy e GTA V Legacy agora apresenta estados
  explícitos de detectado/não detectado; a identificação distingue corretamente
  Windows 11 de builds internos `10.0`.
- A interface recebeu modos com indicadores de intensidade, hardware mais claro
  e uma visão geral mais limpa.

### Política de versão

- As releases estáveis públicas avançam em sequência controlada: `1.0.0` até
  `1.0.99`, depois `1.1.0`; o mesmo padrão vale para cada minor seguinte. O
  workflow valida a próxima versão permitida antes de gerar uma release.

## [0.2.0] - 2026-07-22

### Adicionado

- Instalador `win-x64` autocontido com runtime .NET incluído, idiomas pt-BR e
  inglês, tema moderno, atalhos opcionais e atualização no mesmo diretório.
- Atualizador opt-in via GitHub Releases: valida versão estável, origem HTTPS,
  tamanho e SHA-256 antes de oferecer o instalador; a pessoa pode abrir as
  notas da release antes de baixar.
- Escolha explícita na desinstalação para preservar ou remover dados locais;
  instalações silenciosas preservam esses dados por padrão.
- Workflow manual de release com build, testes, smoke de instalação/upgrade/
  desinstalação, checksums, manifesto e atestação de proveniência.

### Alterado

- Progresso, relatório e apresentação dos perfis passaram a registrar o
  resultado de cada ação de maneira isolada e reversível.
- A interface passou a incluir preferências persistentes, tema, idioma,
  hardware detalhado, bandeja e prontidão local para criadores.

### Segurança

- O instalador não baixa runtimes nem executa PowerShell, CMD ou conteúdo
  remoto. O app não executa um pacote de atualização até a confirmação da
  pessoa e a validação do SHA-256.

## [0.1.0] - 2026-07-18

### Adicionado

- Fundação do diagnóstico, planos de otimização reversíveis, broker elevado
  restrito e documentação de segurança para FiveM Legacy.
