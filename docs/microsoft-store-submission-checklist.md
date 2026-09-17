# Primeira submissão MSIX — checklist manual do Partner Center

Objetivo: obter uma decisão de certificação sobre `allowElevation`. Não publicar
automaticamente, substituir Inno Setup nem promover este candidato para o canal Web.
Documentação Microsoft consultada em 17/09/2026; rótulos podem variar por idioma/conta.
Identidade reservada: `VemryxInc.Ralven`; Publisher `CN=1FB9E268-F80A-40DF-8E57-BBB7C7849ABD`; PublisherDisplayName `Vemryx Inc.`. PFN `VemryxInc.Ralven_vk02nsxq3ddr6`; Store ID `9N61X5M295M5`.

## 1. Conta e reserva

- [ ] Entrar em [Partner Center](https://partner.microsoft.com/) com a conta do publisher.
  Concluir/verificar o cadastro de desenvolvedor Windows e a verificação de identidade.
  Não criar outra conta se já existir uma adequada. Consultar as
  [instruções oficiais de conta](https://learn.microsoft.com/en-us/windows/apps/publish/partner-center/open-a-developer-account).
- [ ] Abrir **Apps and games**, criar o produto pela opção **New product** e reservar
  **Ralven**, se ainda não reservado. Se já existir, abrir o produto existente.
  Confirmar o fluxo **MSIX**, não o fluxo EXE/MSI.
- [ ] Na navegação do produto, expandir **Product management > Product identity**
  (a interface anterior pode chamar de App management > App identity).
  Copiar exatamente os três valores da tabela abaixo; não copiar o Store ID/PFN
  como se fosse o Package Name. [Fonte de identidade](https://learn.microsoft.com/en-us/windows/apps/publish/view-app-identity-details).

| Valor no portal | Parâmetro do build | Destino no manifesto gerado |
| --- | --- | --- |
| Package/Identity/Name | `-PackageName` | `Identity/@Name` |
| Package/Identity/Publisher | `-Publisher` | `Identity/@Publisher` |
| Package/Properties/PublisherDisplayName | `-PublisherDisplayName` | `Properties/PublisherDisplayName` |

O Publisher é o subject inteiro `CN=...` fornecido pelo portal, não um nome inventado.
PFN, Package SID e Store ID não precisam ser inseridos neste manifesto. O nome de
exibição Ralven deve corresponder ao nome reservado. Não alterar a identidade entre
o upload e a certificação. Não comprar certificado: a Store reassina o pacote aprovado.

## 2. Gerar o pacote com a identidade real

- [ ] No worktree desta tarefa, executar com os valores oficiais já reservados:

```powershell
./packaging/msix-store/Build-StoreMsix.ps1 `
  -PackageName 'VemryxInc.Ralven' `
  -Publisher 'CN=1FB9E268-F80A-40DF-8E57-BBB7C7849ABD' `
  -PublisherDisplayName 'Vemryx Inc.' `
  -Harden
```

- [ ] O build cria `artifacts/msix-store/Ralven-Store-1.7.1.0-x64.msix` e o manifesto
  resolvido em `artifacts/msix-store/layout/AppxManifest.xml`. Fonte parametrizada:
  `packaging/msix-store/AppxManifest.xml`. Não editar os binários após gerar o pacote.
- [ ] Executar `packaging/msix-store/Test-StoreLayout.ps1` contra o layout; conferir
  identidade, somente `runFullTrust`/`allowElevation`, ausência de parâmetros de demo
  e integridade do Broker. Preservar o SHA-256 do MSIX enviado.
- [ ] Para instalar/WACK localmente, assinar com certificado de desenvolvimento cujo
  subject corresponda ao Publisher e confiar nele somente no ambiente de teste.
  A assinatura de desenvolvimento não é a assinatura Microsoft nem precisa ser comprada.
  O upload MSIX não exige a compra de um certificado público; a Store reassina.
  [Requisitos de pacote e assinatura](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/app-package-requirements).
- [ ] Reexecutar WACK se identidade/assets/payload mudarem; guardar XML/HTML bruto e
  os achados, mesmo quando forem warnings. Não remover `allowElevation` para passar.
- [ ] Cumprir o [roteiro de testes](store-certification-test-plan.md), principalmente
  a operação de energia seguida de restauração, com UAC real.

## 3. Criar e completar a submissão

- [ ] Na página de overview do produto, **Product release > Start submission**.
  Salvar cada seção da submissão. O portal indica seções incompletas.
- [ ] **Pricing and availability**: escolher preço (Free para o produto gratuito atual),
  mercados e audience/discoverability conscientemente; não habilitar disponibilidade
  pública automaticamente. A retenção de publicação abaixo é obrigatória neste gate.
- [ ] **Properties**: selecionar categoria coerente com utilitário de sistema,
  não Games só por integrar FiveM. Usar uma categoria existente no dropdown; subcategoria
  e categoria secundária são opcionais. Declarar somente requisitos reais (desktop x64,
  Windows 10 2004+, AC para a ação de energia; GPU/driver e Legacy para os testes específicos).
- [ ] **Privacy policy URL** e **Support info**: inserir uma política pública HTTPS,
  acessível sem login, coerente com conta, telemetria/relatos opcionais e dados locais.
  Informar suporte/contact e site válidos do publisher. O aplicativo transmite dados
  em fluxos existentes; não declarar genericamente que não acessa/transmite informação
  pessoal. Não inventar e-mail de suporte nem publicar uma nova política sem revisão.
  [Properties, privacidade e suporte](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/enter-app-properties).
- [ ] **Product declarations**: responder as declarações realmente exibidas segundo
  o comportamento atual; não marcar serviços/drivers só porque existe um Broker.
  Não declarar integração de compras ativa: Pro/AI estão indisponíveis neste candidato.
- [ ] **Age ratings**: responder honestamente o questionário IARC e **Save and generate**.
  A classificação deve refletir o conteúdo do Ralven; não copiar automaticamente a
  classificação de GTA V. [Age ratings](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/age-ratings).
- [ ] **Packages**: enviar o MSIX de identidade real. A Store aceita `.msix`; não é
  obrigatório introduzir `.msixupload` nesta fase. Esperar a validação e corrigir
  erros de identidade/versão/manifesto. Conferir somente desktop x64 como destino.
  [Upload MSIX](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/upload-app-packages).
- [ ] **Store listings**: completar pelo menos English (en-US), com descrição precisa,
  screenshot real do candidato e os assets obrigatórios indicados pelo portal. Não usar
  screenshot sintético como evidência de operação real, nem afirmar FPS/ping garantidos.
  Descrever diagnóstico, preview, ações reversíveis e UAC sob demanda. Informar que
  Pro/AI estão indisponíveis, se aparecem nas imagens. Não anunciar compras futuras.
  Ao menos um screenshot é exigido; outros materiais só se o portal os exigir para
  o destino escolhido. [Checklist oficial da submissão](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/create-app-submission).

## 4. Capability e instruções privadas de certificação

- [ ] Abrir **Submission options** após upload. Na seção **Restricted capabilities**,
  preencher a nota de `allowElevation` com a versão principal de
  [Restricted Capabilities notes](store-restricted-capabilities-notes.md), ou a curta se
  o limite mostrado exigir. Preencher a justificativa separada de `runFullTrust` se solicitado.
- [ ] Em **Notes for certification**, colar
  [Notes for certification](store-notes-for-certification.md). Conferir que a URL do
  roteiro abre sem login e aponta para a revisão efetivamente enviada.
- [ ] O caminho documentado para instruções extensas é uma URL nas notas. Não existe
  na documentação consultada garantia de um campo genérico de anexo; se o portal oferecer
  anexo, pode incluir o roteiro como complemento, mantendo os passos principais nas notas.
  Fornecer credenciais de conta de teste apenas nesse campo privado, se necessárias
  para testar funções autenticadas; não são necessárias para energia/diagnóstico.
- [ ] Registrar a referência/data da resposta Microsoft que orientou certificação,
  se disponível, sem inventar número de caso ou reproduzir dados pessoais do e-mail.
- [ ] **Publishing hold options**: selecionar exatamente
  **Don't publish this submission until I select Publish now**. O padrão publica após
  aprovação; ele não é adequado a este gate. Não clicar posteriormente em Publish now.
- [ ] Verificar notificações/e-mail do responsável no Action Center; manter o publisher
  disponível para perguntas da certificação.
  [Notas, capabilities e retenção manual](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/manage-submission-options).

## 5. Submit for certification

- [ ] Conferir MSIX/hash, identidade, privacidade/suporte, listing, ratings, capabilities,
  instruções e retenção manual. Resolver todas as seções/erros pendentes no portal.
- [ ] Na **Application overview**, clicar **Submit for certification**. Esta ação é
  manual do proprietário; nenhum script desta tarefa faz upload/submissão/publicação.
- [ ] Acompanhar status de certificação/Action Center e guardar o relatório Microsoft.
  Se `allowElevation` for recusada, responder à razão com evidência e nova submissão;
  não retirar o Broker nem introduzir bypass. Se aprovada, manter publicação retida e
  abrir a segunda fase de implementação/validação Store.

## Individual versus Company — pendência específica

A página atual de capabilities descreve `allowElevation` para parceiros Microsoft
ou organizações enterprise, com aprovação estrita, mas não fornece uma regra
inequívoca que diga se uma conta Individual do Partner Center pode ou não obter a
exceção. **Não há aprovação presumida nem proibição individual confirmada aqui.**
Verificar eventuais mensagens em **Packages** após upload, **Submission options >
Restricted capabilities**, na validação final de submissão e no relatório de
certificação. Um erro de elegibilidade pode impedir a submissão; guardar a mensagem
literal e solicitar esclarecimento no caso existente. Não converter/comprar uma conta
por inferência. [Capability oficial](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/app-capability-declarations).

Separadamente, a política **10.14 Account Type** exige Company para organizações,
negócios/atividade profissional e nomes interpretáveis como entidade empresarial.
Essa regra geral pode se aplicar à identidade do publisher independentemente de
`allowElevation`; o proprietário deve conferir sua situação jurídica e a verificação
da conta. [Políticas atuais da Store](https://learn.microsoft.com/en-us/windows/apps/publish/store-policies#1014-account-type).

## Descrição inicial em inglês para Store listings

Texto coerente com o candidato; revisar no portal, sem anunciar AI/Pro como disponíveis:

> Ralven provides Windows diagnostics and user-reviewed maintenance and configuration
> plans. Inspect the proposed changes, requirements and risks before execution, check
> individual action results, and use transaction history to restore supported changes.
>
> The application runs as a standard user. Its predefined administrative actions use
> a separate helper and normal Windows UAC consent. These include an AC-power
> performance-plan action and an optional Hardware-Accelerated GPU Scheduling
> compatibility experiment in the specialized FiveM/GTAV Legacy workflow. HAGS
> changes require a Windows restart. Results depend on Windows, hardware and drivers;
> performance improvements are not guaranteed. FiveM and GTAV Legacy must already
> be installed for that specialized integration; Ralven supplies neither game nor
> entitlement. AI and Pro features are currently unavailable.

O screenshot obrigatório deve vir de uso normal do candidato instalado, sem
`--demo-synthetic`, sem dados pessoais/conta e sem simular uma operação administrativa.
