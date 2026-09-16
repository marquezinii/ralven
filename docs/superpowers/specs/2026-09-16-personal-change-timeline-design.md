# Linha do tempo de mudanças pessoais (Ralven Pro) — design

> Origem: análise de produto externa (ChatGPT) sobre o Ralven Pro, validada contra o
> código atual antes deste desenho. Primeira de várias iniciativas propostas; as
> demais (rotinas automáticas, laboratório de desempenho, backup versionado,
> atualização programada de apps) ficam fora de escopo aqui.

## Objetivo

Hoje `PersonalWorkspaceService` já detecta mudanças (`PcChange`), mas cada evento só
guarda `CapturedAt` + `Kind` — sem antes/depois, sem evidência, sem associação com
sintomas medidos. O objetivo é transformar isso numa linha do tempo que ajude a
responder "o que mudou no meu PC" com fato, associação e próximo teste — nunca
afirmando causa sem evidência.

## Fora de escopo

- Fluxos guiados de investigação ("[Comparar inicializações]", "[Abrir investigação
  guiada]") — só a lista read-only dos eventos.
- Cobrança/checkout (segue como está; `CheckoutAvailable` continua vindo do servidor).
- Reaplicar tweaks durante sessão FiveM ativa (pendência conhecida em `PROJECT_STATE.md`).

## Modelo (`Ralven.Contracts`)

Mover de `Ralven.App.Services.PersonalWorkspaceService.cs` para
`Ralven.Contracts` (novo arquivo `PersonalTimeline.cs` ou similar):
`PcObservation`, `PcChange`, `PersonalMeasurement`, `PersonalWorkspace`.

- `PcChange` ganha: `string? PreviousValue`, `string? CurrentValue`, `string Source`,
  `bool IsComplete`. Dado que não pôde ser coletado fica `null`/`IsComplete = false`
  — nunca inventado.
- `PcChangeKind` ganha `StartupApps` e `GraphicsDriver`.
- `PcObservation` ganha `IReadOnlyList<string> StartupAppNames` e
  `IReadOnlyList<string> GraphicsDriverVersions` (uma entrada por dispositivo de
  vídeo, formatada `"{DeviceName} {DriverVersion}"`).

## Regras puras (`Ralven.Core`)

Mover `DetectChanges` e `CanCompare` de `PersonalWorkspaceService` para
`Ralven.Core.Planning.PersonalTimelineAnalysis` (estático, sem I/O, testável
isoladamente — segue o padrão de `PersonalOptimizationPolicy`).

- `DetectChanges(PcObservation previous, PcObservation current, DateTimeOffset capturedAt)`
  passa a popular `PreviousValue`/`CurrentValue` em cada `PcChange` gerado.
- `CanCompare` (modo "comparação controlada") mantém as regras atuais
  (mesma versão do Windows etc.) sem alteração de comportamento.
- Novo `CompareHistorical(PersonalMeasurement first, PersonalMeasurement second)`:
  nunca recusa a comparação; retorna um `HistoricalComparisonResult` com a lista
  explícita de condições diferentes (hardware, Windows, uso, contexto) para que a
  UI/IA nunca apresente a comparação como equivalente a uma controlada.
- Novo `FindAssociations(IReadOnlyList<PcChange> changes, IReadOnlyList<PersonalMeasurement> measurements)`:
  heurística simples — para cada mudança, procura a medição mais próxima antes e
  depois (mesmo `Usage`/`Context`) dentro de uma janela fixa (24h) e verifica se
  algum percentual (`CpuPercent`/`GpuPercent`/`MemoryPercent`/`DiskPercent`) mudou
  mais que um limiar fixo (15 pontos percentuais). Retorna
  `PcChangeAssociation(PcChange Change, string? SymptomSummary)` — `SymptomSummary`
  descreve o que mudou, nunca afirma causalidade. Sem medições suficientes →
  `SymptomSummary = null`.
  <!-- ponytail: limiar fixo de 15pp e janela fixa de 24h são heurística inicial;
  revisar com dados reais antes de expor como "detecção de regressão" oficial. -->

## Coletores (`Ralven.Windows`, sem novo código de infraestrutura)

Reaproveitar o que já existe:
- `IWindowsApplicationInventoryInspector.InspectStartupAsync` → nomes dos itens de
  inicialização.
- `DriverVersionInspector` → versões de driver por dispositivo.

`PersonalWorkspaceService.CaptureObservationAsync` recebe as duas dependências por
construtor (mesmo padrão de swap para versão sintética em modo demo que
`ApplicationsPageViewModel`/`MainWindow.Navigation.xaml.cs` já usam).

## Persistência (`Ralven.App`)

`PersonalWorkspaceService` continua sendo o único dono do arquivo
`%LocalAppData%\Ralven\Personal\workspace.json` (mesmo write atômico). Só a política
de retenção muda:
- `Changes`: mantém eventos dos últimos 90 dias, com teto de 500 (o que vier primeiro).
- `Measurements`: mantém dos últimos 90 dias, com teto de 120.

Compatibilidade: campos novos são opcionais/anuláveis: um `workspace.json` já
existente (de builds internas com Pro ligado localmente) continua desserializando —
os novos campos vêm `null`/vazios até a próxima observação.

## UI (`Ralven.App`)

- `ProFeatureAvailability.Enabled = true` nesta branch. Efeito: destrava todos os
  recursos Pro já implementados (Ultra, workspace), não só a linha do tempo.
  Checkout continua gated por `CheckoutAvailable` do servidor (Asaas fail-closed) —
  nenhuma cobrança é habilitada por esta mudança.
- `ProPage`/`ProPageViewModel` ganham uma lista somente-leitura da linha do tempo:
  para cada `PcChange`, mostra tipo, quando, antes→depois (quando disponível) e a
  associação encontrada (quando existir), usando os textos localizados existentes
  como referência de estilo. Sem novos comandos/botões de investigação.

## Testes

- `Ralven.Core`: testes novos para `DetectChanges` (evidência preenchida),
  `CompareHistorical` (lista diferenças em vez de recusar) e `FindAssociations`
  (janela/limiar, dados incompletos).
- `Ralven.App`: `PersonalWorkspaceServiceTests` atualizado para a nova retenção e
  para os dois novos coletores (com fakes); teste de compatibilidade desserializando
  um `workspace.json` no formato antigo.
- Conferir se testes de `ProPageViewModel`/`MainViewModel` assumiam
  `ProFeatureAvailability.Enabled == false` e ajustar as asserções afetadas.
- Validação final: suíte `dotnet test` completa + `dotnet format --verify-no-changes`
  + `Verify-Safety.ps1`.

## Riscos conhecidos

- Ligar `ProFeatureAvailability.Enabled` afeta todo o Pro, não só esta feature —
  qualquer regressão em Ultra/checkout fica visível na próxima release. Mitigado por
  rodar a suíte completa antes do PR.
- Heurística de associação (`FindAssociations`) é deliberadamente simples; não deve
  ser vendida como "detecção de causa" — texto da UI precisa deixar isso explícito.
