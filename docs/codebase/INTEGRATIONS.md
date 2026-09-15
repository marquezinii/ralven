# Integrações externas

## 1) Inventário

| Sistema | Tipo | Finalidade | Autenticação | Criticidade | Evidência |
| --- | --- | --- | --- | --- | --- |
| Windows/FiveM/GTAV Legacy | APIs, arquivos, registro e processos locais | Diagnóstico e ações reversíveis | Usuário atual; broker UAC para allowlist administrativa | alta | `src/Ralven.Windows/`, `src/Ralven.Broker/` |
| Cloudflare Worker | API HTTPS | Telemetria, bugs, perfis, alerta, stats e manifest de update | pública limitada; Firebase Bearer; ou sessão admin + CSRF | alta | `infra/cloudflare-worker/src/index.js` |
| Cloudflare D1 | banco SQL | Eventos, ações, perfis, bugs, sessões e alerta | binding `TELEMETRY_DB` | alta | `wrangler.toml`, `migrations/` |
| Firebase Authentication | REST/JWT/JWKS | Conta, e-mail/senha e identidade do perfil | API key pública no cliente; ID token RS256 validado no Worker | alta | `FirebaseAuthService.cs`, `firebaseIdToken.js` |
| Google OAuth 2.0 | OAuth + OIDC | Login Google com Authorization Code + PKCE | client ID/credencial desktop em overlay local | média | `src/Ralven.App/Services/GoogleOAuthClient.cs` |
| Vemryx / Cloudflare R2 | manifestos/download | Consulta assinada, download e página oficial de versões | público via `vemryx.com/Ralven/`; publicação usa GitHub Actions | alta | `SignedManifestUpdateService.cs`, `.github/workflows/release.yml` |
| Sentry | observabilidade | Crash reporting sanitizado e opt-in | DSN de projeto na configuração | média | `SentryCrashReportingService.cs` |
| Cloudflare Pages / Workers | hosting | Dashboard privado / site institucional Vemryx | deploy operacional | média | `infra/dashboard/README.md` |
| Discord | Bot oficial | Avisos públicos de release e roadmap | Bot consulta os dados públicos do repositório e publica como o bot | baixa | `Discord-Bot-Ralven/bot.py` |

## 2) Armazenamento

| Store | Papel | Camada de acesso | Risco principal | Evidência |
| --- | --- | --- | --- | --- |
| D1 | Dados remotos do Worker | módulos `queries.js` e stores por domínio | migrations e filtros devem permanecer alinhados | `infra/cloudflare-worker/migrations/`, `src/stats/queries.js` |
| `%LOCALAPPDATA%/Ralven` | settings, journals, fila, histórico e logs | serviços App/Windows/UpdateRuntime | atomicidade, sanitização e não seguir reparse points | `AppOptimizationService.cs`, `TransactionJournal.cs` |
| DPAPI CurrentUser | refresh token e piso de versão | `SecureFirebaseSessionStore`, `VersionFloorStore` | corrupção deve falhar fechada sem expor segredo | respectivos arquivos em `App/Services` e `UpdateRuntime` |
| Runtime de atualização | active pointer, staging, receipt e recovery journal | `Ralven.UpdateRuntime` | troca de artefato/downgrade/ativação parcial | `RuntimePackageStager.cs`, `RecoveryCoordinator.cs` |

## 3) Secrets e credenciais

- Worker: `ADMIN_PASSWORD_HASH`, `IP_HASH_SECRET` e `ADMIN_CSRF_SECRET` são secrets do Wrangler; não são commitados.
- App: endpoints, Firebase API key pública e DSN ficam em `appsettings.<Environment>.json`; valores locais sensíveis/opcionais usam `appsettings.<Environment>.local.json`, ignorado pelo Git.
- Sessão: senha e ID token não são persistidos; somente refresh token protegido por DPAPI CurrentUser.
- CI/release: chaves de assinatura e webhooks entram como GitHub Secrets; workflows não devem imprimi-los.
- Ciclo de rotação dos secrets operacionais: `[TODO]` não há periodicidade versionada no repositório.

## 4) Confiabilidade e falhas

- Transportes Cloudflare exigem HTTPS, recusam redirect e usam timeout; telemetria mantém fila local limitada e retry em abertura/chamada posterior.
- Firebase valida assinatura, `aud`, `iss`, expiração, `sub` e cacheia JWKS com limites para `kid` desconhecido.
- Worker limita corpo, schema, CORS, CSRF e rate limits; writes públicos relevantes falham fechados sem binding.
- Update valida origem, tamanho, SHA-256 e assinatura antes de staging; ativação usa health receipt e rollback.
- Telemetria/Sentry/bug report não podem quebrar a otimização; não há fallback para outro backend quando o Worker falha.
- `TransientRetry` cobre locks locais curtos no runtime de atualização; não existe circuit breaker geral.

## 5) Observabilidade

- Sentry recebe apenas exceção sanitizada quando **Relatórios opcionais** está ativo; PII, sessões, tracing e captura HTTP automática estão desativados.
- Telemetria usa evento allowlisted e idempotente; dashboard lê métricas e erros operacionais via Worker.
- Broker/updater mantêm logs/journals locais com contexto de transação.
- `[TODO]` não há SLO, tracing distribuído ou alerta automatizado do Worker/D1 documentado no código.

## 6) Evidências

- `src/Ralven.App/Services/RemoteServicesOptions.cs`
- `src/Ralven.App/Services/CloudflareTransportDefaults.cs`
- `src/Ralven.App/Services/SecureFirebaseSessionStore.cs`
- `src/Ralven.App/Services/SignedManifestUpdateService.cs`
- `infra/cloudflare-worker/wrangler.toml`
- `infra/cloudflare-worker/src/auth/firebaseIdToken.js`
- `infra/cloudflare-worker/src/requestSecurity.js`
- `docs/telemetry.md`
