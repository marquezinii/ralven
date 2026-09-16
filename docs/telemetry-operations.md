# Operação da telemetria em produção

## Contrato e fluxo

O aplicativo envia lotes JSON por `POST` para a rota HTTPS `/telemetry` do
Worker Cloudflare. Cada evento deve conter `eventName`, `executionTimeMs`,
`appVersion` e `environment` (`Development` ou `Production`), além dos campos
opcionais documentados em `telemetry.md`. O Worker valida o lote inteiro,
persiste-o no D1 de produção e retorna `202 Accepted`.

O dashboard usa a mesma origem do Worker e filtra `Production` por padrão.
Assim, um evento marcado como `Development` não aparece no painel padrão até
que o filtro de ambiente seja alterado.

## Incidente de 30/07/2026

A versão pública `1.1.0` construía o transporte Cloudflare sem incluir o
campo obrigatório `environment`. O Worker corretamente recusava todos esses
lotes com HTTP `400`; como o cliente tratava qualquer resposta não-2xx como
falha transitória, os eventos permaneciam na fila local e nenhum aparecia no
dashboard. A correção inclui o ambiente resolvido pelo executável em todo
payload e rejeita configurações de produção que não apontem para o host e a
rota autorizados.

## Checklist obrigatório antes de uma release

- confirme que `Config/appsettings.Production.json` está no publish e aponta
  para `https://api.vemryx.com/telemetry`;
- valide a serialização do cliente, incluindo `environment: Production`;
- execute `npm.cmd test` em `infra/cloudflare-worker` e `infra/dashboard`;
- execute o build e os testes Release do .NET;
- usando credenciais Cloudflare, aplique as migrations com
  `wrangler d1 migrations apply TELEMETRY_DB --remote` antes do
  deploy do Worker e antes de criar a GitHub Release; o Wrangler captura o
  backup D1 dessa operação;
- execute `scripts/Test-ProductionDiagnostics.ps1`: o gate envia um evento
  anônimo sintético, confirma o contrato completo no D1 usado pelo dashboard e
  remove a linha; cria também uma conta Firebase sintética, valida login,
  renovação de sessão, bloqueio do perfil antes da confirmação do e-mail,
  revogação do token e exclusão coordenada pelo Worker (com limpeza direta de
  contingência se o fluxo falhar antes da exclusão); também envia um crash sintético marcado
  `ralven.release_smoke=true` e exige o aceite do Sentry;
- antes das migrations e do deploy, confirme pelo preflight do workflow que os
  quatro secrets obrigatórios de contas (`FIREBASE_WEB_API_KEY`,
  `FIREBASE_ADMIN_CLIENT_EMAIL`, `FIREBASE_ADMIN_PRIVATE_KEY` e
  `MFA_RECOVERY_CODE_HMAC_SECRET`) existem no Worker;
- instale o artefato Release, dê consentimento e execute uma otimização
  controlada; registre versão, horário UTC e identificador de smoke test;
- confirme o `202` no Worker, a linha no D1 e a visibilidade no dashboard;
- confirme que o artefato do instalador contém a build recém-validada.

## Diagnóstico seguro

Não envie telemetria de otimização ao Sentry e não use FormSubmit como fallback.
Diagnósticos de otimização usam apenas Worker + D1; crashes usam Sentry dentro
dos relatórios opcionais, ativados por padrão e desativáveis pelo usuário;
relatos manuais usam `/bugs`. Uma resposta 2xx, incluindo 202, remove o lote da
fila. Falhas de rede, 429 e 5xx ficam na fila para nova tentativa; rejeições 4xx
permanentes são descartadas para não criar retry infinito.

Como compatibilidade de migração, o Worker classifica qualquer payload sem
`environment` como `Production`. Clientes atuais devem continuar enviando o
campo explicitamente; `null` ou valores desconhecidos continuam rejeitados.

## Comandos remotos (exigem autenticação Cloudflare)

```powershell
npx.cmd wrangler d1 execute TELEMETRY_DB --remote --command "SELECT COUNT(*) AS total, MAX(received_at) AS last_received FROM telemetry_events"
npx.cmd wrangler d1 execute TELEMETRY_DB --remote --command "SELECT five_m_install_detected, operation_id FROM telemetry_events LIMIT 1; SELECT terms_version FROM account_profiles LIMIT 1; SELECT message, active, updated_at FROM live_alert WHERE id = 1"
npx.cmd wrangler deployments list
```

Nunca coloque `CLOUDFLARE_API_TOKEN`, segredos do Worker ou credenciais D1 no
aplicativo, em arquivos versionados ou em logs.
