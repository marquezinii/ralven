# Estado atual do projeto

> Documento canônico e deliberadamente curto. Ele descreve **o estado vigente**, não o histórico de implementação.
> Código, testes, Git e documentação especializada prevalecem se houver divergência. Para histórico detalhado, consulte `PROJECT_HISTORY.md` somente quando a tarefa realmente exigir contexto antigo.

## 1. Snapshot

- **Produto:** Ralven, plataforma de gerenciamento e otimização do Windows com IA, transparente, reversível e orientada por diagnóstico. FiveM para **GTAV Legacy** é a integração especializada atual da área de Jogos.
- **Integração:** `dev/proxima-versao` é a branch de integração da próxima versão; `main` representa a linha pública/estável. O fluxo de branches, worktrees, Pull Requests, integração e release é definido em `AI_RULES.md`.
- **Último estado consolidado:** 14/09/2026, após os PRs #206, #208–#210: analyzer atualizado, comunicação elevada do broker corrigida, anúncios de release do Discord reorganizados e vínculo seguro entre conta Ralven e cargos de plano no Discord preparado. Pro e Ralven AI continuam bloqueados, sem compra, ativação ou cobrança. Confirme o estado real com Git e testes atuais antes de trabalhar.
- **Release pública atual:** `v1.7.0`, publicada a partir de `main`. A próxima versão só é definida no fluxo oficial de release a partir das mudanças posteriores a essa tag.
- **Atalho de desenvolvimento:** `Ralven - Desenvolvimento` usa `scripts\Start-DevelopmentApp.ps1`. Conforme `AI_RULES.md`, deve ser reconstruído com `scripts\Install-DevelopmentShortcut.ps1 -Build` quando aplicável. O script espelha a árvore para a pasta irmã fixa `Ralven-dev-shortcut`, sem ficar órfão após a remoção de um worktree.

## 2. Objetivo e invariantes de segurança

- Priorizar mudanças pequenas, verificáveis, diagnosticáveis e reversíveis; nunca prometer ganho universal de FPS.
- A integração de **FiveM para GTAV Legacy** é a única integração de jogo com suporte operacional hoje. GTAV Enhanced deve ser detectado/bloqueado com segurança até existir suporte específico.
- Nunca desativar Defender, Firewall, SmartScreen, UAC, Windows Update ou serviços essenciais; nunca criar exclusões de antivírus.
- Nunca injetar código, alterar memória de processos, instalar driver de kernel, usar hook gráfico ou baixar/executar código arbitrário como mecanismo de otimização.
- Caches e arquivos sensíveis são tratados por allowlist. Autenticação, `game-storage`, NUI storage, configurações e plugins não são lixo automático.
- Perfis **Leve, Médio e Agressivo** e o plano pessoal **Ultra** são composições versionadas de ações; nunca uma lista arbitrária de tweaks.
- Cada ação deve ter escopo conhecido, pré-condições, validação, resultado tipado e rollback quando aplicável.
- O fluxo padrão é isolado por ação: verificar → aplicar → validar → registrar. Falha normal reverte somente a ação afetada; falha crítica pode abortar o restante. O broker elevado mantém contrato estrito e allowlisted. Cancelamentos preservam o histórico e os snapshots confirmados; a restauração desfaz a fase administrativa antes da local.
- Não medir FPS ao vivo dentro do FiveM por overlay/hook. O benchmark implementado é o benchmark **standalone oficial do GTA V**, opt-in e fora de uma sessão FiveM.
- Dados indisponíveis por limitações do Windows/driver devem aparecer como indisponíveis; nunca estimar ou inventar métricas.

Documentos normativos: `docs/safety.md` e `docs/architecture.md`.

## 3. Arquitetura atual

### Solução .NET

`Ralven.slnx` separa responsabilidades. A árvore de `src/` possui nove projetos principais:

- `Ralven.App` — WPF, navegação, localização, tema, conta, apresentação, progresso e interação.
- `Ralven.Contracts` — DTOs, IDs, enums e contratos compartilhados; os estados persistidos de transação e journal são contratos duráveis append-only.
- `Ralven.Core` — catálogo de ações, perfis, planejamento e regras independentes de Windows/UI; o planejamento é puro e recebe explicitamente suas entradas variáveis.
- `Ralven.Windows` — descoberta e adaptadores Windows, filesystem, registro, diagnósticos e ações permitidas.
- `Ralven.Broker` — processo administrativo efêmero e allowlisted; sem shell/comandos arbitrários.
- `Ralven.Launcher` — inicialização/ativação do runtime e coordenação do fluxo de atualização.
- `Ralven.Updater` — atualização independente e staging/aplicação da atualização.
- `Ralven.UpdateRuntime` — contratos/estado durável usados pela cadeia de atualização e recuperação.
- `Ralven.ReleaseTool` — suporte à preparação/validação de artefatos de release.

Testes .NET ficam em `tests/Ralven.Tests/`.

A toolchain integrada usa .NET 10 LTS com SDK 10.0.303, C# 14 fixo e NuGet Central Package Management em `Directory.Packages.props`. Os testes usam xUnit v3 sobre Microsoft Testing Platform, com cobertura via `coverlet.MTP`.

### Infraestrutura e web

- `infra/cloudflare-worker/` — backend Cloudflare Worker + D1 para telemetria, relatos de bug e perfil de conta.
- `infra/dashboard/` — painel administrativo privado da telemetria/bugs.
- `https://vemryx.com/Ralven/` — página pública e origem dos downloads; manifestos assinados e artefatos versionados são servidos pelo Worker do site a partir de R2 privado.
- `installer/` — Inno Setup 7 em arquitetura x64.
- `scripts/` — build, validação, release, smoke tests e launcher de desenvolvimento.
- `.github/workflows/` — CI de .NET/Worker/dashboard, SBOM e release. O site público vive no repositório Vemryx; Dependabot cobre NuGet, npm da infraestrutura e Actions.

Node 24.19 LTS é o baseline versionado para site, Worker e dashboard.

### Persistência local

Preferências, journals, solicitações efêmeras, filas e logs locais ficam sob `%LOCALAPPDATA%\Ralven`; não gravar dados mutáveis na pasta de instalação. Na primeira abertura, o importador allowlisted pode copiar dados pessoais compatíveis de gerações sem suporte, sem sobrescrever nem alterar a origem.

## 4. Estado funcional relevante

### Interface e produto

- Aplicação WPF com WPF-UI/Fluent, Mica, tema claro/escuro/sistema, fallback para Windows 10 e localização declarativa em inglês, português do Brasil, espanhol e francês.
- Configurações reúne conta, aparência/idioma, inicialização/bandeja, privacidade, atualizações, restauração de preferências e limpeza opt-in somente do cache descartável do Ralven.
- Visão geral apresenta diagnóstico e métricas locais de CPU, GPU, memória, disco e rede. A coleta visual pausa fora do primeiro plano; o monitor FiveM iniciado explicitamente continua somente leitura na bandeja.
- Sistema mostra hardware, saúde somente leitura de antivírus/firewall/atualizações automáticas e os dois controles HKCU allowlisted de jogos do Windows, com confirmação, journal e rollback.
- Aplicativos instala, atualiza e desinstala somente pacotes identificados das origens `winget` e `msstore`; inventário e inicialização permanecem leituras que nunca executam `UninstallString`.
- Jogos abre o hub FiveM/GTAV Legacy, com otimizador especializado, histórico, monitor local e acesso ao download oficial do ReShade. GTAV Enhanced continua bloqueado.
- O Otimizador compartilha a trilha Preparar → Executar → Resultado entre `GeneralWindows` e `FiveMLegacy`; detalhes técnicos expõem risco, acesso, verificação e rollback. Cache/reparo continua opt-in e fora dos perfis padrão.
- Ralven Pro e Ralven AI permanecem visíveis, localizados e bloqueados como recursos em desenvolvimento. Não há compra, ativação, cobrança ou chamada ao modelo enquanto as flags continuarem desativadas.
- Notas da Versão aparecem após update quando existe versão ainda não vista. Avisos ao vivo vêm de `GET /live-alert` e podem ser dispensados até o próximo ID.
- `MainWindow` e `MainViewModel` são `partial class` por responsabilidade; edite o arquivo parcial da área correspondente.

### Motor e diagnóstico

- `ActionCatalog.CurrentVersion` atual: **22**.
- Diagnósticos locais cobrem FiveM/GTA, hardware, armazenamento/TRIM, processos, rede, pagefile/commit, drivers, tela, energia, WHEA, aceleração do mouse e sinais de throttling. Dados indisponíveis permanecem indisponíveis; ver `docs/research.md`.
- Planos são reconstituídos e comparados integralmente no runtime e no broker. Journal, snapshots e receipts administrativos em HKLM/Registry64 preservam rastreabilidade e fazem rollback elevado falhar fechado quando a autoridade está ausente ou corrompida.
- A execução isola falhas por ação, respeita pré-requisitos e criticidade e nunca relata sucesso parcial como total. A restauração desfaz primeiro a fase administrativa e depois a local.
- A recomendação considera hardware, pressão de recursos, uso pretendido e software de transmissão. A resposta consistente do ponteiro é exclusiva do Ultra, opt-in e reversível.
- Relatórios estruturados e técnicos sanitizados só saem do aplicativo por ação explícita do usuário.

### Conta e autenticação

- Firebase Authentication REST sustenta cadastro, login, verificação, recuperação, reautenticação, Google OAuth2 + PKCE, TOTP e exclusão. ID tokens ficam em memória; refresh token opcional usa DPAPI; senha e tokens não são registrados.
- O Worker valida RS256/JWKS, `aud`, `iss`, expiração e `sub`; o Firebase UID é a identidade permanente. Perfil complementar e username único ficam em D1.
- Sessão completa exige e-mail verificado, perfil e termos vigentes. Mudanças sensíveis exigem reautenticação; a última forma de acesso não pode ser removida.
- Códigos TOTP de recuperação são exibidos uma vez e armazenados somente como HMAC; seu uso remove o fator perdido e revoga sessões.
- A conta pode gerar um código Discord de dez minutos e uso único. O Worker persiste somente o HMAC e o ID numérico vinculado; o bot oficial consulta cargos Free/Pro/Max derivados dos entitlements por rotas com segredo de serviço.
- O card de conta lê Free/Pro de `GET /account/entitlements`, autenticado pelo mesmo ID token, sem expor dados do provedor de pagamento. Avatar permanece somente local.

### Telemetria, backend e operações

- `/telemetry`, `POST /bugs`, `GET /api/bugs` e o aviso ao vivo estão ativos. Bug reports são texto, e-mail/log opcionais e não usam anexo/R2.
- Privacidade está na versão **9**: diagnósticos essenciais são allowlisted; dados detalhados e crash reports sanitizados compartilham a opção Relatórios opcionais. Falhas remotas nunca alteram a otimização.
- As migrations `0008`–`0015` — alertas, 2FA, correlação v9 e vínculo Discord — e seus consumidores ainda exigem um próximo deploy controlado conjunto.
- O dashboard privado usa sessão opaca revogável, CSRF/origem exata para mutações, CSP/anti-frame e consultas agregadas sem conteúdo interativo ou identificadores de conta/provedor.
- Cobrança Asaas permanece fail-closed e desativada (`ASAAS_BILLING_ENABLED = "false"`). Preço vem do servidor e pagamentos só concedem entitlement após revalidação canônica; ver `docs/billing.md`.
- `POST /ai/message` exige Pro + `ralven_ai`, e-mail verificado, rate limit, idempotência e orçamento D1. A rota e a UI continuam desativadas; o modelo não recebe ferramentas nem acesso ao Windows.

### Atualização e distribuição

- Launcher/Updater usam staging, origem, versão, tamanho, SHA-256, ativação atômica, health-check, recuperação e rollback; caminhos mutáveis recusam reparse points e hashing/extração não bloqueia a UI.
- Instalador Inno Setup 7 é self-contained `win-x64`, acompanha o tema, mostra progresso real e preserva documentos/localização nos quatro idiomas.
- Releases protegidas são reconstruídas de fonte limpa, ofuscam Core/Windows antes da assinatura e validam determinismo e ausência de assemblies não ofuscados; mapas cifrados ficam fora dos assets públicos.
- A CI seleciona escopo e cobre política, localização, .NET, Worker, dashboard, site, SBOM e instalador. Dependabot direciona atualizações à `dev/proxima-versao`.
- Site, README, instalador, manifestos/checksums e release devem permanecer coerentes com a versão realmente publicada.

## 5. Pendências e decisões abertas

Somente itens ainda relevantes devem permanecer aqui. Quando resolvidos e integrados, remova-os em vez de criar uma cronologia.

1. **Ideia futura — reaplicar tweaks durante a sessão FiveM/GTA** (backlog de funcionalidade, não decisão bloqueada): o monitor local de sessão (§4) só observa presença/ausência; ajustes que precisariam ser aplicados/restaurados durante o ciclo de vida do jogo (prioridade, afinidade, core parking, timer resolution e semelhantes) continuam fora do catálogo até existir arquitetura segura de reversão mesmo se o Ralven for encerrado. Ver `docs/graphics-optimizations-backlog.md`.
2. **GTAV Enhanced** — sem suporte operacional; requer adaptador/projeto específico antes de habilitar qualquer ação.
3. **Authenticode público** — executáveis e instalador ainda não possuem assinatura de publisher confiável; a implementação depende de certificado/conta externa e deve assinar antes dos hashes e manifestos finais.
4. **Próximas majors do frontend** — TypeScript 7 ainda excede o peer range suportado pelo `typescript-eslint` vigente, e ESLint 10 ainda não é aceito por plugins do stack Next. O estado suportado permanece TypeScript 6 e ESLint 9 até os peers oficiais convergirem.
5. **Vulnerabilidades do Dependabot** — zeradas; avaliar novas atualizações pelo CI e pelo limite de compatibilidade do item 4.
6. **Campos de bug-report v5 não enviados** — `reproducibility`, `severity` e `gtaEdition` foram cogitados para o relato de bug (`BugReportWindow`) junto da telemetria v5, mas ficaram fora da integração: o Worker não tem schema/validação para eles em `bug_reports`, e a UI não os preenche hoje. Requer trabalho conjunto de UI + backend antes de existir.

## 6. Baseline de validação registrada

Estes números são **referência do último estado validado**, não substituem testes da branch atual.

- **14/09/2026 — `dev/proxima-versao`:** build Release sem avisos; **1.599 testes .NET**, **296 testes do Worker/D1**, **63 do dashboard** e **3 do site** aprovados, além de format, segurança, lint, typecheck e audits sem falhas. Build portátil, instalador e smoke são validados separadamente antes da conclusão da integração.

## 7. Comandos essenciais

Na raiz:

```powershell
dotnet restore Ralven.slnx
dotnet build Ralven.slnx --configuration Release --no-restore
dotnet run --project tests/Ralven.Tests/Ralven.Tests.csproj --configuration Release --no-build -- --minimum-expected-tests 1
dotnet format Ralven.slnx --verify-no-changes
.\scripts\Verify-Safety.ps1
git diff --check
.\scripts\Start-DevelopmentApp.ps1
```

Worker:

```powershell
Set-Location infra\cloudflare-worker
npm test
npm audit
```

Dashboard/site: execute testes, lint, typecheck e build definidos nos respectivos `package.json` quando a superfície for alterada.

Build/distribuição, quando aplicável:

```powershell
.\scripts\Build-Portable.ps1
.\scripts\Build-Installer.ps1 -Version <versão>
```

## 8. Release e operações remotas

- `main` não recebe desenvolvimento normal. Integração ocorre em `dev/proxima-versao`; publicação oficial segue `AI_RULES.md`.
- Não inferir autorização de push/deploy/release a partir de um commit local ou de uma validação bem-sucedida.
- Antes de calcular versão ou publicar, confirme tags/releases reais e o diff desde a última tag pública confirmada neste snapshot.
- Deploy do Worker, Pages, release, tags, assets e demais operações remotas devem seguir as permissões e gatilhos definidos em `AI_RULES.md`.
- Release pública exige coerência entre código, versão, `CHANGELOG.md`, GitHub Release, instalador, updater, site e artefatos.

## 9. Referências por domínio

Use `AI_RULES.md` para Git/integração/release; `docs/safety.md` e `docs/architecture.md` para invariantes; `docs/telemetry.md`, `docs/installer.md`, `docs/graphics-optimizations-backlog.md` e `infra/cloudflare-worker/README.md` para seus domínios. `PROJECT_HISTORY.md` é histórico e não leitura padrão.
