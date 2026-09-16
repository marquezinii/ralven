# Microsoft Store + MSIX — viabilidade para o Ralven

## Conclusão

**VIÁVEL COM RESTRIÇÕES**

O Ralven pode ser empacotado como aplicativo desktop MSIX sem remover o
diagnóstico, as ações de usuário padrão, o Launcher ou o Broker administrativo.
A POC local gerou, assinou, instalou, iniciou e atualizou o pacote; o Broker
empacotado também percorreu o caminho UAC imposto por seu manifesto
`requireAdministrator`.

A restrição decisiva é operacional, não técnica: o pacote precisa da capability
restrita `allowElevation`, e a Microsoft precisa aprová-la para a submissão na
Store. Até essa aprovação, a viabilidade de produção não está provada. Se a
capability for rejeitada, MSIX/Store deixa de ser um canal principal aceitável
para o produto completo; remover as ações administrativas violaria o critério
da investigação.

## 1. Estado atual

- O instalador público informado, `Ralven-Setup-latest-win-x64.exe`, está sem
  Authenticode (`NotSigned`). O SHA-256 informado é
  `409C92E70386A1C298E958EB51EAF5E7700C3E9925A082F34194B350C0D3C6A2` e a
  análise informada do VirusTotal é 0/66. Esses dados não foram revalidados
  nesta tarefa porque não foi feito download nem acesso à produção.
- O aviso do Edge é compatível com falta de reputação/assinatura e não constitui
  evidência de malware.
- O instalador Inno atual é per-user (`PrivilegesRequired=lowest`), instala sob
  `{autopf}\Ralven`, cria atalhos, registra startup em `HKCU\...\Run` quando
  solicitado e preserva o atualizador próprio.
- Nenhum pipeline, release, artefato público ou ambiente de produção foi
  alterado.

## 2. Arquitetura relevante do Ralven

| Componente | Estado atual | Consequência no MSIX |
| --- | --- | --- |
| `Ralven.App` | WPF, `asInvoker`, usuário padrão | Compatível como packaged classic desktop app `mediumIL` |
| `Ralven.Launcher` | `asInvoker`; lê `Runtime\active.json`, inicia a versão ativa e supervisiona health/rollback | Pode ser o executável de entrada, mas a parte de mutação do runtime não pode operar dentro do pacote |
| `Ralven.Broker` | `requireAdministrator`; executado com `Verb=runas`; IPC por named pipe `CurrentUserOnly`; contrato tipado e allowlisted | Preservável somente com `allowElevation` aprovada |
| `Ralven.Updater` | `asInvoker`; verifica handoff, hash e instalador | Deve ficar desativado/ausente no canal Store |
| `Ralven.UpdateRuntime` | staging, `Runtime\versions`, `active.json`, health receipt, rollback e anti-downgrade | Continua no canal Web; é incompatível com a imutabilidade do package root |

O Broker não é uma shell genérica. As operações administrativas encontradas são
a troca temporária/reversível do plano de energia e o opt-in de HAGS por
`HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\HwSchMode`, com
verificação e rollback. A separação App sem elevação/Broker elevado permanece a
arquitetura correta para MSIX.

Os dados da aplicação usam majoritariamente `%LOCALAPPDATA%\Ralven`. Um desktop
app full-trust empacotado não vira AppContainer; ainda assim, a identidade do
pacote cria também `%LOCALAPPDATA%\Packages\<PFN>`. A política de dados deve ser
deliberada antes da produção para evitar dois lugares sem necessidade.

## 3. Requisitos atuais da Microsoft Store/MSIX

- Desktop/Win32 pode ser publicado como MSIX. Para desktop full-trust, o
  manifesto usa `runFullTrust`; no baseline atual foi usado
  `uap10:RuntimeBehavior="packagedClassicApp"` e
  `uap10:TrustLevel="mediumIL"`.
- A Store exige identidade/publisher reservados no Partner Center, versão válida,
  assets, manifesto válido e aprovação de capabilities restritas declaradas.
- A Store reassina o MSIX após a certificação. Um certificado comercial não é
  necessário para o pacote adquirido pela Store; certificados self-signed são
  apenas para desenvolvimento local.
- `allowElevation` é restrita. A documentação atual pede justificativa detalhada
  e recomenda contato prévio com `reportapp@microsoft.com`; a análise pode levar
  pelo menos cinco dias úteis e não há garantia de aprovação.
- MSIX core é suportado em Windows 10 e 11. O Ralven já mira Windows 10 2004
  (`10.0.19041`), um baseline conservador para esse manifesto.
- Instalação é per-user, o package root fica sob `WindowsApps` e é somente
  leitura. Arquivos mutáveis devem ficar em locais de dados.
- Serviços desktop empacotados existem em versões modernas do Windows, mas têm
  requisitos próprios de manifesto/instalação e não são necessários pelo
  Ralven atual. Drivers, serviços e tarefas agendadas não devem ser adicionados
  como mecanismo alternativo de elevação.
- A Store gerencia aquisição, updates diferenciais e desinstalação limpa. A
  disponibilidade dos updates ainda depende da Store e pode ser afetada por
  configuração do usuário/GPO.

Fontes Microsoft: [capabilities de aplicativos empacotados](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/app-capability-declarations),
[requisitos do pacote MSIX](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/app-package-requirements),
[opções de assinatura](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options),
[containerização MSIX](https://learn.microsoft.com/en-us/windows/msix/msix-containerization-overview),
[compatibilidade Windows 10/11](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/msix-windows10-windows11) e
[políticas da Microsoft Store](https://learn.microsoft.com/en-us/windows/apps/publish/store-policies).

### SmartScreen e a diferença entre os dois modelos da Store

**MSIX efetivamente distribuído pela Store:** a Microsoft reassina o pacote e a
documentação afirma que aplicativos distribuídos pela Store não ficam sujeitos
aos avisos de download do SmartScreen. O download deixa de ser um EXE avulso do
site, eliminando especificamente a etapa do Edge que hoje diz “normalmente não é
baixado”. Isso não elimina o UAC legítimo quando o Broker for usado.

**EXE/MSI apenas listado na Store:** é outro fluxo. O publisher continua
responsável por assinar o instalador e todos os PEs com certificado de uma CA,
fornecer URL versionada imutável e instalação silenciosa. A Store não reassina
esse EXE/MSI e não entrega automaticamente updates aos usuários existentes; o
atualizador do produto continua responsável. A aquisição pela Store reduz a
fricção naquele fluxo, mas o download direto do mesmo EXE pelo site continua
sujeito à assinatura e reputação próprias.

Fontes: [reputação do SmartScreen](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/smartscreen-reputation),
[escolha do caminho de distribuição](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/packaging/),
[requisitos de EXE/MSI na Store](https://learn.microsoft.com/en-us/windows/apps/publish/store-policies#102-security) e
[updates de EXE/MSI](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msi/publish-update-to-your-app-on-store).

## 4. Compatibilidades

- WPF/.NET self-contained x64 e executáveis Win32 auxiliares.
- Execução normal sem elevação por App e Launcher.
- Elevação sob demanda do Broker, condicionada a `allowElevation`.
- IPC atual por arquivo controlado + named pipe do usuário.
- Leitura e escrita em `%LOCALAPPDATA%\Ralven`.
- Operações HKCU e acesso desktop full-trust compatíveis com o token do usuário.
- Operações HKLM do Broker após UAC; MSIX não remove permissões de um processo
  realmente elevado.
- Ícones, identidade, Menu Iniciar, instalação per-user e uninstall gerenciado.
- Payload já endurecido/obfuscado pode ser empacotado depois do hardening; a
  assinatura externa da Store não substitui o manifesto de integridade interno
  do Broker.
- Serviços, drivers e tarefas agendadas não são necessários pelo núcleo atual.

## 5. Incompatibilidades

- O package root é imutável. O atualizador atual não pode criar
  `Runtime\versions\<version>`, trocar `active.json` ou fazer rollback dentro de
  `WindowsApps`.
- O registro de startup atual via `HKCU\...\Run` deve ser substituído, no canal
  Store, por `desktop:StartupTask` e sua API. O manifesto da POC contém a task
  desabilitada, mas a UI atual ainda não a controla.
- Atalhos são diferentes: MSIX registra o Menu Iniciar; o atalho de Desktop do
  Inno não deve ser recriado por escrita ad hoc.
- O `Ralven.Updater`/instalador Inno não deve coexistir ativamente dentro da
  variante Store.
- A POC usa identidade de teste. A identidade final deve vir do Partner Center.

## 6. Restricted capabilities necessárias

| Capability/declaração | Necessidade | Motivo |
| --- | --- | --- |
| `rescap:runFullTrust` | Sim | App WPF e Launcher são desktop full-trust `mediumIL` |
| `rescap:allowElevation` | Sim | Broker possui `requireAdministrator` e é elevado sob demanda |
| `desktop:FullTrustProcess` | Não no desenho atual | Essa extensão serve ao `FullTrustProcessLauncher`; o Ralven já é desktop full-trust e inicia diretamente seu auxiliar empacotado |
| extensões `desktop6` | Não demonstradas | Nenhuma extensão desktop6 é necessária para App, Launcher, Broker ou startup atuais |
| `unvirtualizedResources` | Não demonstrada | Os paths explícitos atuais e o processo elevado atendem ao escopo; pedir capability adicional ampliaria revisão sem necessidade |

O processo que precisa de elevação é somente `Ralven.Broker.exe`. A capability é
declarada no pacote, mas a justificativa ao Partner Center deve delimitar o
executável e as operações.

## 7. Situação específica do Ralven.Broker

Tecnicamente, a separação foi preservada na POC: Launcher/App continuam
`mediumIL`; o Broker mantém seu manifesto `requireAdministrator` e somente ele
abre UAC. A ativação local do probe iniciou o Broker empacotado e registrou
`broker-started` seguido de `invalid-arguments`, exatamente o resultado seguro
esperado sem um plano assinado/pipe.

Para a aprovação, a submissão deve explicar e demonstrar:

1. lista fechada das operações (`powercfg` para plano temporário e HAGS em HKLM);
2. consentimento explícito e preview antes da alteração;
3. detecção, pós-condição e rollback;
4. ausência de shell/script/comando livre, bypass de segurança ou modificação
   silenciosa;
5. Broker separado, execução somente sob demanda e revalidação privilegiada;
6. instruções reproduzíveis para a equipe de certificação testar cada ação.

O caso parece compatível com as políticas atuais, especialmente a regra 10.2.8
que permite modificar configurações/preferências/experiência do Windows por
métodos suportados e com consentimento. Isso é uma avaliação técnica, não uma
pré-aprovação da Microsoft. Há ainda [orientação antiga de preparação Desktop
Bridge](https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-prepare)
que diz que apps que exigem elevação não seriam aceitos; a documentação atual e
específica de `allowElevation` criou o caminho de exceção, mas reforça que a
decisão de certificação é o risco principal.

## 8. Impacto no updater

O desenho recomendado é explicitamente por canal:

- **Store:** Store controla versões, download, update, atomicidade e rollback da
  implantação. O aplicativo não consulta nem executa o updater próprio.
- **Web/Inno:** mantém o sistema atual de manifest assinado, SHA-256, staging,
  `Runtime/versions`, health receipt, rollback e anti-downgrade.

Não se deve redirecionar o updater atual para escrever fora do package root ou
simular um segundo runtime mutável. Isso duplicaria duas cadeias de confiança e
criaria estados que a Store não conhece. O Launcher pode continuar como entrada
no pacote, mas no canal Store deve apenas iniciar o runtime que veio no pacote,
sem realizar ativação/mutação própria.

A POC atualizou localmente o pacote de `1.7.1.0` para `1.7.1.1`, alterou o
InstallLocation para a nova versão e preservou `LocalState`. Isso valida a
mecânica MSIX local, não o serviço de update da Store.

Fontes: [como updates MSIX funcionam](https://learn.microsoft.com/en-us/windows/msix/app-package-updates),
[updates App Installer fora da Store](https://learn.microsoft.com/en-us/windows/msix/app-installer/auto-update-and-repair--overview) e
[políticas de MSIX por GPO](https://learn.microsoft.com/mt-mt/windows/msix/group-policy-msix).

## 9. Impacto no instalador atual

Nenhum. Inno Setup, `Build-Installer.ps1`, `release.yml` e o canal Web continuam
como estão. MSIX deve ser um artefato paralelo, produzido a partir do mesmo
payload compilado, com gates próprios. Não se recomenda converter o Inno; a POC
empacota diretamente o layout conhecido e evita capturar efeitos de uma
instalação.

Para o canal Web, Authenticode OV continua sendo necessário mesmo que a Store
seja adotada, porque visitantes que escolherem o EXE direto continuam fora da
cadeia de confiança da Store.

## 10. POC realizada

Arquivos isolados:

- `packaging/msix-poc/AppxManifest.xml`;
- `packaging/msix-poc/Build-MsixPoc.ps1`;
- `packaging/msix-poc/README.md`.

O script chama o build portátil existente, copia o payload completo, adiciona
manifesto/assets, executa `makeappx.exe` e opcionalmente assina/verifica com
`signtool.exe`. Não adiciona dependências e não toca no pipeline de release.

A POC preserva App, Broker, Launcher, runtime, ícone e identidade experimental.
`--demo-synthetic` evita alterações reais. Para a validação do UAC foi usado
temporariamente um segundo Application ID oculto, removido do manifesto final da
POC porque a Store aceita somente um aplicativo por pacote nesse fluxo.

## 11. Testes executados

| Teste | Resultado |
| --- | --- |
| `Build-Portable.ps1 -Runtime win-x64 -Configuration Release` | Passou; build Release, 0 warnings/erros |
| `dotnet restore Ralven.slnx` | Passou |
| `dotnet build Ralven.slnx --configuration Release --no-restore` | Passou; 0 warnings/erros |
| suíte `Ralven.Tests` Release | Passou; 1.599/1.599 testes, 0 falhas |
| `makeappx pack` | Passou; 752 arquivos, pacote de 214.314.786 bytes |
| Assinatura dev + `signtool verify /pa /v` | Passou, 0 warnings/erros |
| `Add-AppxPackage` com certificado dev confiado localmente | Passou; package status `Ok` |
| Instalação per-user/Package Identity | Passou; PFN `Ralven.MsixPoc_szcdhz64h0y1w` |
| Menu Iniciar/AUMID | Passou; `Ralven.MsixPoc_szcdhz64h0y1w!Ralven` |
| Launcher -> App | Passou; `Ralven.exe` executado de `WindowsApps`, janela responsiva |
| Coexistência com instalação Web | Passou na POC; processo empacotado e processo Web permaneceram separados |
| Escrita no package root | Bloqueada com `UnauthorizedAccessException`, como esperado |
| Criação da área de dados do pacote | Passou; estrutura `%LOCALAPPDATA%\Packages\<PFN>` criada |
| Broker/UAC | Passou para ativação; log registrou `broker-started` e rejeição segura de argumentos ausentes |
| Update MSIX local 1.7.1.0 -> 1.7.1.1 | Passou; nova versão registrada e `LocalState` preservado |
| Uninstall per-user | Passou; registro e package data removidos, `%LOCALAPPDATA%\Ralven` compartilhado preservado |
| WACK 10.0.26100.7705 sobre a POC final | Concluiu com `OVERALL_RESULT=WARNING`; assets e conformidade do pacote passaram |

## 12. Itens que não puderam ser testados

- **NÃO VALIDADO LOCALMENTE:** aprovação de `allowElevation` no Partner Center.
- **NÃO VALIDADO LOCALMENTE:** assinatura final feita pela Store e comportamento
  real do Edge/SmartScreen na aquisição publicada.
- O WACK local foi executado, mas não representa a certificação da Store. O
  passe final terminou com `OVERALL_RESULT=WARNING`: o teste de UAC marcou o
  Broker `requireAdministrator` como falha; o teste de executáveis bloqueados
  marcou referências a criação de processos no runtime .NET self-contained e
  no próprio Ralven; e houve warning de DPI awareness no Launcher/Broker. O
  pacote/assets passaram depois das correções. Os dois `FAIL` restantes exigem
  avaliação junto com `allowElevation` e o modelo desktop full-trust; não devem
  ser ocultados nem tratados como aprovação. Consulte o [fluxo oficial do
  WACK](https://learn.microsoft.com/en-us/windows/uwp/debug-test-perf/windows-app-certification-kit).
- **NÃO VALIDADO LOCALMENTE:** comunicação App -> Broker e uma operação
  administrativa real dentro do pacote. A ativação/UAC foi validada, mas uma
  ação real alteraria o estado da máquina durante uma POC.
- **NÃO VALIDADO LOCALMENTE:** habilitar/desabilitar startup pela UI via
  `StartupTask`.
- **NÃO VALIDADO LOCALMENTE:** update servido pela infraestrutura da Store,
  pausa/retomada, GPO e rollback gerenciado pela Store.
- **NÃO VALIDADO LOCALMENTE:** build MSIX com obfuscação/hardening habilitados;
  apenas o payload Release normal foi usado.
- **NÃO VALIDADO LOCALMENTE:** Windows 10 real; o teste foi feito no host atual.

## 13. Alterações necessárias para produção

1. Pedir avaliação prévia de `allowElevation` com a justificativa e roteiro do
   Broker; não iniciar migração antes desse gate.
2. Reservar identidade no Partner Center e substituir nome/publisher/assets de
   teste.
3. Introduzir detecção de canal/package identity em um único ponto.
4. No canal Store, ocultar/desabilitar a UX e serviços do updater próprio; fazer
   o Launcher apenas iniciar o runtime empacotado.
5. Trocar o serviço de startup HKCU por `StartupTask` no canal Store.
6. Definir a política de dados: preferencialmente manter `%LOCALAPPDATA%\Ralven`
   para continuidade entre canais, documentando uninstall e privacidade.
7. Gerar MSIX após hardening e verificações de integridade, então rodar WACK,
   testes Win10/Win11 e os cenários administrativos completos.
8. Adicionar artefato/gate paralelo ao workflow somente depois da prova no
   Partner Center; não alterar o artefato Inno existente.

## 14. Riscos

- **Alto:** rejeição de `allowElevation`; é o único bloqueador externo capaz de
  inviabilizar o produto completo na Store.
- **Médio:** políticas/certificação interpretarem operações de otimização como
  amplas demais. Mitigação: descrição precisa, preview, consentimento, rollback
  e allowlist já existentes.
- **Médio:** dois canais com comportamento de update divergente. Mitigação:
  channel flag explícita e testes de cada distribuição.
- **Médio:** dados compartilhados entre instalações Web e Store podem causar
  concorrência ou downgrade lógico. Mitigação: contrato de versão dos dados e
  mutex/locking consciente da identidade.
- **Baixo:** diferenças de startup/atalhos/uninstall em relação ao Inno; são
  mudanças de UX, não perda do núcleo.
- **Baixo:** crescimento do MSIX (~215 MB); a Store usa updates diferenciais,
  mas o primeiro download permanece grande.

## 15. Trabalho estimado para implementação

Após aprovação prévia da capability:

- 2–4 dias de engenharia: canal Store, updater/Launcher, startup e packaging;
- 1–2 dias: testes Win10/Win11, WACK, assets e instruções de certificação;
- revisão da capability: pelo menos 5 dias úteis segundo a orientação atual,
  potencialmente mais se houver perguntas ou rejeição.

Estimativa: aproximadamente uma semana de engenharia, mais o prazo externo de
certificação. Sem aprovação prévia, o trabalho deve parar na POC.

## 16. Fluxo recomendado de distribuição

```text
vemryx.com/Ralven
  -> botão principal: Microsoft Store
  -> MSIX assinado/distribuído pela Microsoft
  -> Store controla updates e uninstall

vemryx.com/Ralven/download
  -> canal alternativo: Inno Setup com Authenticode OV
  -> atualizador assinado atual controla updates e rollback
```

Primeiro gate: submeter a justificativa de `allowElevation`, idealmente com um
pacote de teste e roteiro das duas operações administrativas. Se aprovada, a
Store deve se tornar o canal principal. Se rejeitada, não mutilar o produto:
manter o canal Web e priorizar Authenticode OV para reduzir SmartScreen/Edge.
