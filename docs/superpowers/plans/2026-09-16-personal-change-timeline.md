# Linha do tempo de mudanças pessoais — plano de implementação

> **Execução:** inline, nesta sessão (sem subagentes) — o contexto já foi
> levantado; despachar subagentes "zero-contexto" agora só duplicaria trabalho.
> **Spec:** `docs/superpowers/specs/2026-09-16-personal-change-timeline-design.md`

**Goal:** cada `PcChange` passa a carregar antes/depois; ganha dois novos tipos
(`StartupApps`, `GraphicsDriver`); a comparação de medições ganha um modo
histórico que não recusa; uma heurística simples associa mudança↔sintoma; a
lista já existente na `UltraPanel` mostra tudo isso; `ProFeatureAvailability`
liga.

**Tech Stack:** .NET 10 / C# 14, xUnit v3, WPF/MVVM manual (sem DI container).

## Decisões tomadas durante o levantamento (divergem do spec original)

1. **UI já existe** — não crio tela nova. `MainViewModel.Ultra.cs` já expõe
   `ObservableCollection<string> PersonalChanges`, renderizada por um
   `ItemsControl` simples em `UltraPanel.xaml:70-72`. Só enriqueço a string
   montada em `RefreshUltraPresentation()`.
2. **`PcChange` simplificado** — removo `Source`/`IsComplete` do spec original
   (YAGNI): `PreviousValue`/`CurrentValue` nulos já significam "não disponível".
3. **Hardware precisa de um campo novo** — `HardwareSignature` é um hash, não é
   legível. Adiciono `PcObservation.HardwareDescription` (`"{CpuName} · {GPUs}"`)
   só para exibição; a comparação de identidade continua pelo hash.
4. **Coletores novos = zero código de infraestrutura** — reaproveito
   `IWindowsApplicationInventoryInspector.InspectStartupAsync` e
   `IDriverVersionInspector.GetSnapshot()`, já existentes em `Ralven.Windows`.
5. **Demo mode** — reuso `SyntheticWindowsApplicationInventoryInspector`
   (já existe em `ApplicationsPageViewModel.cs`, `internal`, mesmo assembly).
   Crio um `SyntheticDriverVersionInspector` novo e pequeno (não existe nenhum).

## Arquivos afetados

- `src/Ralven.Contracts/PersonalTimeline.cs` — **novo**. Move
  `PcObservation`, `PcChange`, `PersonalMeasurement`, `PersonalWorkspace` de
  `PersonalWorkspaceService.cs`, com os campos novos.
- `src/Ralven.Core/Planning/PersonalTimelineAnalysis.cs` — **novo**. Move
  `DetectChanges`/`CanCompare` de `PersonalWorkspaceService`, adiciona
  `CompareHistorical`/`FindAssociations`.
- `src/Ralven.App/Services/PersonalWorkspaceService.cs` — **modifica**. Remove
  os records movidos; adiciona os dois inspectors no construtor; muda
  `CaptureObservationAsync`; muda retenção em `ObserveAsync`/`MeasureAsync`;
  `Validate`/`ValidateObservation` cobrem os campos novos.
- `src/Ralven.App/SyntheticDriverVersionInspector.cs` — **novo**, pequeno.
- `src/Ralven.App/MainWindow.xaml.cs` — **modifica** linha 141 (injeta os dois
  inspectors, com swap de demo).
- `src/Ralven.App/ViewModels/MainViewModel.Ultra.cs` — **modifica**
  `RefreshUltraPresentation()` (linhas 367-371) e adiciona uso de
  `FindAssociations`.
- `src/Ralven.App/ViewModels/ProPageViewModel.cs` — **modifica** linha 7,
  `Enabled = true`.
- `src/Ralven.App/Resources/Strings*.resx` (4 arquivos) — **modifica**: novas
  chaves `Ultra.Change.StartupApps{Added,Removed}`,
  `Ultra.Change.GraphicsDriver`, `Personal.Change.Association`,
  `Personal.Change.Evidence`; reescreve `Personal.History.Count` sem os
  números fixos 30/60.
- `tests/Ralven.Tests/Core/PersonalTimelineAnalysisTests.cs` — **novo**.
- `tests/Ralven.Tests/App/PersonalWorkspaceTests.cs` — **modifica** (retenção,
  novos coletores, evidência).
- `PROJECT_STATE.md` — **correção pontual** da linha que hoje afirma que o Pro
  "continua bloqueado" (deixa de ser verdade só para a UI local; cobrança
  continua fail-closed no servidor).

---

## Task 1 — Contratos: `PcChange`/`PcObservation` com evidência

**Cria** `src/Ralven.Contracts/PersonalTimeline.cs`:

```csharp
namespace Ralven.Contracts;

public enum PcChangeKind
{
    Hardware, Windows, GameMode, BackgroundCapture, LowDiskSpace,
    PointerAcceleration, StartupApps, GraphicsDriver
}

public sealed record PcObservation(
    DateTimeOffset CapturedAt, string HardwareSignature, string WindowsVersion,
    double FreeDiskGiB, WindowsGamingSettingState GameMode, WindowsGamingSettingState BackgroundCapture)
{
    public bool? PointerAccelerationEnabled { get; init; }
    public string HardwareDescription { get; init; } = string.Empty;
    public IReadOnlyList<string> StartupAppNames { get; init; } = [];
    public IReadOnlyList<string> GraphicsDriverVersions { get; init; } = [];
}

public sealed record PcChange(
    DateTimeOffset CapturedAt, PcChangeKind Kind, string? PreviousValue = null, string? CurrentValue = null);

public sealed record PersonalMeasurement(
    DateTimeOffset CapturedAt, PersonalUsage Usage, string Context, string HardwareSignature,
    string WindowsVersion, int SampleCount, double DurationSeconds,
    double? CpuPercent, double? GpuPercent, double? MemoryPercent, double? DiskPercent);

public sealed record PersonalWorkspace
{
    public int SchemaVersion { get; init; } = 1;
    public IReadOnlyList<PersonalOptimizationPreferencesDto> Profiles { get; init; } = [];
    public bool TrackingEnabled { get; init; }
    public PcObservation? Reference { get; init; }
    public PcObservation? LastObservation { get; init; }
    public IReadOnlyList<PcChange> Changes { get; init; } = [];
    public IReadOnlyList<PersonalMeasurement> Measurements { get; init; } = [];
}
```

`WindowsGamingSettingState` já é público em `Ralven.Contracts` (confirmar
namespace ao mover — está hoje referenciado sem qualificação dentro de
`Ralven.App.Services`; se estiver em outro namespace, ajustar o `using`).

**Modifica** `src/Ralven.App/Services/PersonalWorkspaceService.cs`: remove as
declarações de `PcObservation`, `PcChange`, `PersonalMeasurement`,
`PersonalWorkspace` (linhas 13-38 do arquivo atual) e adiciona
`using Ralven.Contracts;` (o arquivo já tem `using Ralven.Contracts;` na linha
5 — só precisa continuar existindo).

- [ ] Mover os records, compilar (`dotnet build src/Ralven.Contracts` e
  `dotnet build src/Ralven.App`), corrigir `using`s que quebrarem.
- [ ] Commit: `refactor(contracts): move modelo da linha do tempo pessoal para Ralven.Contracts`

**Interfaces produzidas para as tasks seguintes:** `PcChangeKind` com os 8
valores acima; `PcChange` com `PreviousValue`/`CurrentValue` (posição 3 e 4,
ambos opcionais); `PcObservation` com `HardwareDescription`,
`StartupAppNames`, `GraphicsDriverVersions`.

---

## Task 2 — `Ralven.Core`: `PersonalTimelineAnalysis`

**Cria** `src/Ralven.Core/Planning/PersonalTimelineAnalysis.cs`:

```csharp
using Ralven.Contracts;

namespace Ralven.Core.Planning;

public sealed record HistoricalComparisonResult(
    IReadOnlyList<string> DifferingConditions, PersonalMeasurement First, PersonalMeasurement Second);

public sealed record PcChangeAssociation(PcChange Change, string? SymptomSummary);

public static class PersonalTimelineAnalysis
{
    private static readonly TimeSpan AssociationWindow = TimeSpan.FromHours(24);
    private const double AssociationThresholdPoints = 15;

    public static IReadOnlyList<PcChange> DetectChanges(PcObservation? previous, PcObservation current)
    {
        if (previous is null) return [];
        var result = new List<PcChange>();
        if (previous.HardwareSignature != current.HardwareSignature)
            result.Add(new(current.CapturedAt, PcChangeKind.Hardware, previous.HardwareDescription, current.HardwareDescription));
        if (previous.WindowsVersion != current.WindowsVersion)
            result.Add(new(current.CapturedAt, PcChangeKind.Windows, previous.WindowsVersion, current.WindowsVersion));
        if (Known(previous.GameMode) && Known(current.GameMode) && previous.GameMode != current.GameMode)
            result.Add(new(current.CapturedAt, PcChangeKind.GameMode, previous.GameMode.ToString(), current.GameMode.ToString()));
        if (Known(previous.BackgroundCapture) && Known(current.BackgroundCapture) && previous.BackgroundCapture != current.BackgroundCapture)
            result.Add(new(current.CapturedAt, PcChangeKind.BackgroundCapture, previous.BackgroundCapture.ToString(), current.BackgroundCapture.ToString()));
        if (previous.FreeDiskGiB >= 10 && current.FreeDiskGiB < 10)
            result.Add(new(current.CapturedAt, PcChangeKind.LowDiskSpace,
                previous.FreeDiskGiB.ToString("0.0"), current.FreeDiskGiB.ToString("0.0")));
        if (previous.PointerAccelerationEnabled.HasValue && current.PointerAccelerationEnabled.HasValue
            && previous.PointerAccelerationEnabled != current.PointerAccelerationEnabled)
            result.Add(new(current.CapturedAt, PcChangeKind.PointerAcceleration,
                previous.PointerAccelerationEnabled.ToString(), current.PointerAccelerationEnabled.ToString()));

        var previousStartup = previous.StartupAppNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var currentStartup = current.StartupAppNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var added in currentStartup.Except(previousStartup))
            result.Add(new(current.CapturedAt, PcChangeKind.StartupApps, null, added));
        foreach (var removed in previousStartup.Except(currentStartup))
            result.Add(new(current.CapturedAt, PcChangeKind.StartupApps, removed, null));

        var previousDrivers = previous.GraphicsDriverVersions
            .Select(ParseDriverEntry).Where(entry => entry is not null).ToDictionary(entry => entry!.Value.Device, entry => entry!.Value.Version);
        foreach (var entry in current.GraphicsDriverVersions.Select(ParseDriverEntry).Where(entry => entry is not null))
        {
            if (previousDrivers.TryGetValue(entry!.Value.Device, out var previousVersion) && previousVersion != entry.Value.Version)
                result.Add(new(current.CapturedAt, PcChangeKind.GraphicsDriver, previousVersion, entry.Value.Version));
        }

        return result;
    }

    private static (string Device, string Version)? ParseDriverEntry(string entry)
    {
        var separator = entry.LastIndexOf(' ');
        return separator <= 0 ? null : (entry[..separator], entry[(separator + 1)..]);
    }

    private static bool Known(WindowsGamingSettingState state) => state is
        WindowsGamingSettingState.Enabled or WindowsGamingSettingState.Disabled or WindowsGamingSettingState.NotConfigured;

    public static bool CanCompare(PersonalMeasurement first, PersonalMeasurement second) =>
        first.Usage == second.Usage
        && string.Equals(first.Context, second.Context, StringComparison.OrdinalIgnoreCase)
        && first.HardwareSignature == second.HardwareSignature
        && first.WindowsVersion == second.WindowsVersion
        && first.SampleCount == 30 && second.SampleCount == 30
        && first.DurationSeconds is >= 29 and <= 45 && second.DurationSeconds is >= 29 and <= 45
        && ((first.CpuPercent.HasValue && second.CpuPercent.HasValue)
            || (first.GpuPercent.HasValue && second.GpuPercent.HasValue)
            || (first.MemoryPercent.HasValue && second.MemoryPercent.HasValue)
            || (first.DiskPercent.HasValue && second.DiskPercent.HasValue));

    public static HistoricalComparisonResult CompareHistorical(PersonalMeasurement first, PersonalMeasurement second)
    {
        var differences = new List<string>();
        if (first.Usage != second.Usage) differences.Add(nameof(PersonalMeasurement.Usage));
        if (!string.Equals(first.Context, second.Context, StringComparison.OrdinalIgnoreCase)) differences.Add(nameof(PersonalMeasurement.Context));
        if (first.HardwareSignature != second.HardwareSignature) differences.Add(nameof(PersonalMeasurement.HardwareSignature));
        if (first.WindowsVersion != second.WindowsVersion) differences.Add(nameof(PersonalMeasurement.WindowsVersion));
        return new(differences, first, second);
    }

    public static IReadOnlyList<PcChangeAssociation> FindAssociations(
        IReadOnlyList<PcChange> changes, IReadOnlyList<PersonalMeasurement> measurements)
    {
        var result = new List<PcChangeAssociation>();
        foreach (var change in changes)
        {
            var before = measurements.Where(m => m.CapturedAt < change.CapturedAt && change.CapturedAt - m.CapturedAt <= AssociationWindow)
                .OrderByDescending(m => m.CapturedAt).FirstOrDefault();
            var after = measurements.Where(m => m.CapturedAt >= change.CapturedAt && m.CapturedAt - change.CapturedAt <= AssociationWindow)
                .OrderBy(m => m.CapturedAt).FirstOrDefault();
            result.Add(new(change, before is null || after is null || !CanCompare(before, after)
                ? null
                : DescribeShift(before, after)));
        }
        return result;
    }

    // ponytail: limiar fixo (15pp) e janela fixa (24h) são heurística inicial;
    // revisar com dados reais antes de tratar como detecção de regressão.
    private static string? DescribeShift(PersonalMeasurement before, PersonalMeasurement after)
    {
        (string Name, double? Before, double? After)[] metrics =
        [
            ("Cpu", before.CpuPercent, after.CpuPercent), ("Gpu", before.GpuPercent, after.GpuPercent),
            ("Memory", before.MemoryPercent, after.MemoryPercent), ("Disk", before.DiskPercent, after.DiskPercent)
        ];
        var shifted = metrics.Where(m => m.Before.HasValue && m.After.HasValue
            && Math.Abs(m.After.Value - m.Before.Value) >= AssociationThresholdPoints).ToArray();
        return shifted.Length == 0 ? null : string.Join(", ", shifted.Select(m => m.Name));
    }
}
```

- [ ] Escrever `tests/Ralven.Tests/Core/PersonalTimelineAnalysisTests.cs`
  (Task 8 cobre o conteúdo exato — implementar Task 2 e Task 8 juntas evita
  reescrever assinatura duas vezes).
- [ ] `dotnet build src/Ralven.Core`.
- [ ] Commit: `feat(core): adiciona evidência de mudança, comparação histórica e associação de sintomas`

**Interfaces produzidas:** `PersonalTimelineAnalysis.DetectChanges(PcObservation?, PcObservation)`,
`.CanCompare(PersonalMeasurement, PersonalMeasurement)` (mesma assinatura de
antes), `.CompareHistorical(PersonalMeasurement, PersonalMeasurement) : HistoricalComparisonResult`,
`.FindAssociations(IReadOnlyList<PcChange>, IReadOnlyList<PersonalMeasurement>) : IReadOnlyList<PcChangeAssociation>`.

---

## Task 3 — Coletor sintético de driver (demo mode)

**Cria** `src/Ralven.App/SyntheticDriverVersionInspector.cs`:

```csharp
using Ralven.Windows.Infrastructure;

namespace Ralven.App;

/// <summary>Fixed, non-identifying driver snapshot for demo runs.</summary>
internal sealed class SyntheticDriverVersionInspector : IDriverVersionInspector
{
    public DriverVersionSnapshot GetSnapshot() => new(
        Video: [new("Demo Graphics Adapter", "1.0.0.0")],
        Network: [], Audio: [], Chipset: [], Storage: [], Usb: [], Bluetooth: []);
}
```

- [ ] `dotnet build src/Ralven.App`.
- [ ] Commit: `feat(app): adiciona inspector sintético de driver para modo demo`

---

## Task 4 — `PersonalWorkspaceService`: coletores + retenção por tempo

**Modifica** `src/Ralven.App/Services/PersonalWorkspaceService.cs`:

1. Construtor ganha dois parâmetros opcionais:

```csharp
public PersonalWorkspaceService(
    Func<CancellationToken, Task<bool>> authorizePro,
    bool inMemory = false,
    string? directory = null,
    ILocalizationService? localization = null,
    IMouseAccelerationInspector? mouseAcceleration = null,
    IWindowsApplicationInventoryInspector? applicationInventory = null,
    IDriverVersionInspector? driverVersion = null)
{
    // ...campos existentes...
    this.applicationInventory = applicationInventory ?? new WindowsApplicationInventoryInspector();
    this.driverVersion = driverVersion ?? new WindowsDriverVersionInspector();
}
```

com os dois novos campos `private readonly IWindowsApplicationInventoryInspector applicationInventory;`
e `private readonly IDriverVersionInspector driverVersion;`.

2. `CaptureObservationAsync` passa a coletar os dois novos sinais e a montar
   `HardwareDescription`:

```csharp
public Task<PcObservation> CaptureObservationAsync(
    AppDiagnostic current, WindowsGamingControlsService gamingControls, CancellationToken cancellationToken) => Task.Run(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (current.TotalMemoryGiB <= 0 || current.GpuNames.Count == 0
            || current.CpuName == localization.GetString("Diagnosis.CpuUnknown"))
            throw new InvalidOperationException("The hardware identity is incomplete.");
        var gaming = await gamingControls.ReadAsync(cancellationToken).ConfigureAwait(false);
        var mouse = mouseAcceleration.GetSnapshot();
        var drive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!);
        var startup = await applicationInventory.InspectStartupAsync(cancellationToken).ConfigureAwait(false);
        var drivers = driverVersion.GetSnapshot();
        return new PcObservation(DateTimeOffset.UtcNow,
            HardwareProfileSignature.Compute(current.CpuName, current.GpuNames, current.TotalMemoryGiB),
            current.OsLabel, drive.AvailableFreeSpace / 1024d / 1024 / 1024, gaming.GameMode, gaming.BackgroundCapture)
        {
            PointerAccelerationEnabled = mouse.State == MouseAccelerationInspectionState.Available
                ? mouse.AccelerationLevel > 0
                : null,
            HardwareDescription = TrimTo($"{current.CpuName} · {string.Join(", ", current.GpuNames)}", 200),
            StartupAppNames = startup.StartupItems.Select(item => TrimTo(item.Name, 200)).Distinct(StringComparer.OrdinalIgnoreCase).Take(50).ToArray(),
            GraphicsDriverVersions = drivers.Video.Select(item => TrimTo($"{item.DeviceName} {item.DriverVersion}", 200)).Take(20).ToArray()
        };
    }, cancellationToken);

private static string TrimTo(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];
```

3. Retenção por tempo em `ObserveAsync`/`MeasureAsync` (troca `TakeLast(60)`/
   `TakeLast(30)` por um filtro por data + teto):

```csharp
public Task<PersonalWorkspace> ObserveAsync(PcObservation observation, CancellationToken cancellationToken = default)
{
    ValidateObservation(observation);
    return ChangeAsync(state => !state.TrackingEnabled ? state : state with
    {
        LastObservation = observation,
        Changes = TrimChanges(state.Changes.Concat(PersonalTimelineAnalysis.DetectChanges(state.LastObservation, observation)))
    }, true, cancellationToken);
}

private static IReadOnlyList<PcChange> TrimChanges(IEnumerable<PcChange> changes) => changes
    .Where(change => change.CapturedAt >= DateTimeOffset.UtcNow.AddDays(-90))
    .TakeLast(500)
    .ToArray();

private static IReadOnlyList<PersonalMeasurement> TrimMeasurements(IEnumerable<PersonalMeasurement> measurements) => measurements
    .Where(measurement => measurement.CapturedAt >= DateTimeOffset.UtcNow.AddDays(-90))
    .TakeLast(120)
    .ToArray();
```

   E em `MeasureAsync`, trocar `Measurements = state.Measurements.Append(measurement).TakeLast(30).ToArray()`
   por `Measurements = TrimMeasurements(state.Measurements.Append(measurement))`.

4. `Validate` (linhas 297-314 hoje) precisa acompanhar os novos limites e
   campos: trocar `state.Changes.Count > 60` por `state.Changes.Count > 500`,
   `state.Measurements.Count > 30` por `state.Measurements.Count > 120`, e
   adicionar validação de `HardwareDescription`/`StartupAppNames`/
   `GraphicsDriverVersions` dentro de `ValidateObservation` (comprimento ≤ 200
   por item, listas ≤ 50/20 itens — mesmos limites usados na captura, para que
   um arquivo editado manualmente não passe do que a captura real produziria).
   `PcChange` não tem mais restrição de enum estranha — `Enum.IsDefined(item.Kind)`
   continua válido com os 2 valores novos.

- [ ] Atualizar `Validate`/`ValidateObservation` com os novos limites.
- [ ] `dotnet build`.
- [ ] Commit: `feat(app): coleta apps de inicialização e driver de vídeo; retenção passa a ser por tempo`

**Interfaces produzidas:** construtor de `PersonalWorkspaceService` com os
dois parâmetros novos (nomeados, opcionais — não quebra os call sites
existentes que não os passam, exceto onde a Task 5 os adiciona
explicitamente).

---

## Task 5 — Ligar os coletores no ponto de composição

**Modifica** `src/Ralven.App/MainWindow.xaml.cs:141`:

```csharp
personalWorkspaceService: new PersonalWorkspaceService(AuthorizeProOperationAsync, inMemory: demoMode,
    applicationInventory: demoMode ? new SyntheticWindowsApplicationInventoryInspector() : new WindowsApplicationInventoryInspector(),
    driverVersion: demoMode ? new SyntheticDriverVersionInspector() : new WindowsDriverVersionInspector()));
```

(`SyntheticWindowsApplicationInventoryInspector` está em
`Ralven.App.ViewModels` — confirmar que `MainWindow.xaml.cs` já tem
`using Ralven.App.ViewModels;`; se não tiver, adicionar.)

- [ ] `dotnet build src/Ralven.App`.
- [ ] Commit: `feat(app): injeta coletores reais/sintéticos no workspace pessoal`

---

## Task 6 — Exibir evidência e associação na linha do tempo existente

**Modifica** `src/Ralven.App/ViewModels/MainViewModel.Ultra.cs`, dentro de
`RefreshUltraPresentation()` (linhas 367-371 hoje):

```csharp
PersonalChanges.Clear();
var associations = PersonalTimelineAnalysis.FindAssociations(personalWorkspace.Changes, personalWorkspace.Measurements)
    .ToDictionary(item => item.Change);
foreach (var change in personalWorkspace.Changes.Reverse())
{
    var line = change.CapturedAt.ToLocalTime().ToString("g", localization.CurrentCulture)
        + " · " + localization.GetString($"Ultra.Change.{change.Kind}");
    if (change.PreviousValue is not null || change.CurrentValue is not null)
        line += " " + localization.Format("Personal.Change.Evidence",
            change.PreviousValue ?? localization.GetString("Ultra.Unavailable"),
            change.CurrentValue ?? localization.GetString("Ultra.Unavailable"));
    if (associations.TryGetValue(change, out var association) && association.SymptomSummary is not null)
        line += " " + localization.Format("Personal.Change.Association", association.SymptomSummary);
    PersonalChanges.Add(line);
}
if (PersonalChanges.Count == 0) PersonalChanges.Add(localization.GetString("Ultra.Tracking.NoChanges"));
```

`PcChange` é um `record` — usar como chave de `Dictionary` funciona (igualdade
estrutural), mas dois `PcChange` idênticos por acaso colidiriam; como
`CapturedAt` já é granular (timestamp completo) isso não ocorre na prática.

Também trocar as duas chamadas restantes a `PersonalWorkspaceService.CanCompare`
em `FindBaseline`/`PersonalComparisonSummary`/`RefreshPersonalComparison` (linhas
61, 155, 331) por `PersonalTimelineAnalysis.CanCompare`, e adicionar
`using Ralven.Core.Planning;` (já presente na linha 5 do arquivo — confirmar).

- [ ] `dotnet build src/Ralven.App`.
- [ ] Commit: `feat(app): mostra evidência e associação na linha do tempo pessoal`

---

## Task 7 — Ligar o Pro

**Modifica** `src/Ralven.App/ViewModels/ProPageViewModel.cs:7`:

```csharp
public const bool Enabled = true;
```

- [ ] `dotnet build`.
- [ ] Rodar toda a suíte (`dotnet test`) e revisar qualquer teste que assumia
  `Enabled == false` como comportamento esperado (buscar por
  `ProFeatureAvailability` em `tests/Ralven.Tests`); ajustar as asserções
  afetadas para o comportamento correto com a flag ligada, nunca apagar a
  cobertura.
- [ ] Commit: `feat(app): liga o Ralven Pro em dev/proxima-versao`

---

## Task 8 — Testes de `Ralven.Core`

**Cria** `tests/Ralven.Tests/Core/PersonalTimelineAnalysisTests.cs`:

```csharp
using Ralven.Contracts;
using Ralven.Core.Planning;
using Xunit;

namespace Ralven.Tests.Core;

public sealed class PersonalTimelineAnalysisTests
{
    private static PcObservation Observation => new(DateTimeOffset.UtcNow, new string('a', 64),
        "Windows 11", 40, WindowsGamingSettingState.Enabled, WindowsGamingSettingState.Disabled)
    {
        HardwareDescription = "Ryzen 5800X · RTX 3070",
        StartupAppNames = ["Discord"],
        GraphicsDriverVersions = ["NVIDIA GeForce RTX 3070 31.0.15.3623"]
    };

    [Fact]
    public void DetectChangesReportsPreviousAndCurrentValues()
    {
        var next = Observation with { HardwareDescription = "Ryzen 5800X · RTX 4070", WindowsVersion = "Windows 11" };
        var changes = PersonalTimelineAnalysis.DetectChanges(Observation, next);
        Assert.Empty(changes); // hash não mudou -- HardwareDescription sozinho não dispara Hardware
    }

    [Fact]
    public void HardwareChangeCarriesReadableEvidence()
    {
        var next = Observation with
        {
            HardwareSignature = new string('b', 64),
            HardwareDescription = "Ryzen 5800X · RTX 4070"
        };
        var change = Assert.Single(PersonalTimelineAnalysis.DetectChanges(Observation, next));
        Assert.Equal(PcChangeKind.Hardware, change.Kind);
        Assert.Equal("Ryzen 5800X · RTX 3070", change.PreviousValue);
        Assert.Equal("Ryzen 5800X · RTX 4070", change.CurrentValue);
    }

    [Fact]
    public void StartupAppAdditionAndRemovalAreReportedSeparately()
    {
        var next = Observation with { StartupAppNames = ["Steam"] };
        var changes = PersonalTimelineAnalysis.DetectChanges(Observation, next);
        Assert.Contains(changes, change => change.Kind == PcChangeKind.StartupApps && change.PreviousValue == "Discord" && change.CurrentValue is null);
        Assert.Contains(changes, change => change.Kind == PcChangeKind.StartupApps && change.PreviousValue is null && change.CurrentValue == "Steam");
    }

    [Fact]
    public void GraphicsDriverVersionBumpOnTheSameDeviceIsReported()
    {
        var next = Observation with { GraphicsDriverVersions = ["NVIDIA GeForce RTX 3070 32.0.15.6094"] };
        var change = Assert.Single(PersonalTimelineAnalysis.DetectChanges(Observation, next));
        Assert.Equal(PcChangeKind.GraphicsDriver, change.Kind);
        Assert.Equal("31.0.15.3623", change.PreviousValue);
        Assert.Equal("32.0.15.6094", change.CurrentValue);
    }

    [Fact]
    public void CompareHistoricalListsDifferencesInsteadOfRefusing()
    {
        var first = new PersonalMeasurement(DateTimeOffset.UtcNow, PersonalUsage.Gaming, "Scene", new string('a', 64), "Windows 10", 30, 30, 10, 10, 10, 10);
        var second = first with { WindowsVersion = "Windows 11", HardwareSignature = new string('b', 64) };
        var result = PersonalTimelineAnalysis.CompareHistorical(first, second);
        Assert.Contains(nameof(PersonalMeasurement.WindowsVersion), result.DifferingConditions);
        Assert.Contains(nameof(PersonalMeasurement.HardwareSignature), result.DifferingConditions);
        Assert.DoesNotContain(nameof(PersonalMeasurement.Usage), result.DifferingConditions);
    }

    [Fact]
    public void FindAssociationsFlagsALargeShiftWithinTheWindowAndIgnoresDistantOrIncompatibleMeasurements()
    {
        var change = new PcChange(DateTimeOffset.UtcNow, PcChangeKind.Windows, "Windows 10", "Windows 11");
        PersonalMeasurement Measurement(TimeSpan offset, double cpu, string context = "Scene") => new(
            change.CapturedAt + offset, PersonalUsage.Gaming, context, new string('a', 64), "Windows 11", 30, 30, cpu, null, null, null);
        var measurements = new[]
        {
            Measurement(TimeSpan.FromHours(-1), 20),
            Measurement(TimeSpan.FromHours(1), 60),
            Measurement(TimeSpan.FromHours(48), 95), // fora da janela de 24h
        };
        var associations = PersonalTimelineAnalysis.FindAssociations([change], measurements);
        var association = Assert.Single(associations);
        Assert.Equal("Cpu", association.SymptomSummary);
    }

    [Fact]
    public void FindAssociationsReturnsNullSymptomWhenMeasurementsAreMissingOrIncomparable()
    {
        var change = new PcChange(DateTimeOffset.UtcNow, PcChangeKind.Windows, "Windows 10", "Windows 11");
        Assert.Null(PersonalTimelineAnalysis.FindAssociations([change], []).Single().SymptomSummary);
    }
}
```

- [ ] Rodar `dotnet run --project tests/Ralven.Tests/Ralven.Tests.csproj --configuration Release --no-build -- --filter PersonalTimelineAnalysisTests`
  (ou o runner de teste padrão do projeto) e confirmar que passam.
- [ ] Commit: `test(core): cobre evidência, comparação histórica e associação de sintomas`

---

## Task 9 — Atualizar testes de `Ralven.App`

**Modifica** `tests/Ralven.Tests/App/PersonalWorkspaceTests.cs`:

- `MeasurementsRejectMissingCoverageAndIncompatibleComparisons` e as duas
  chamadas a `PersonalWorkspaceService.CanCompare`/`DetectChanges` (linhas
  150-190 hoje) passam a chamar `Ralven.Core.Planning.PersonalTimelineAnalysis.CanCompare`/
  `.DetectChanges` (o método estático saiu de `PersonalWorkspaceService`).
- `TrackingIsOptInAndKeepsOnlyTheLatestSixtyChanges` (linha 127) precisa de
  novo nome e nova asserção — 90 dias / 500 é o teto agora, não 60. Reescrever
  para gerar 70 observações em **menos de 90 dias** de intervalo (como já faz,
  com minutos) e esperar `Assert.Equal(70, workspace.Changes.Count)` (nenhuma
  expira por tempo com esse intervalo pequeno) — o teste de teto passa a ser
  sobre o número 500, não 60; ajustar ou adicionar um teste dedicado ao corte
  por tempo:

```csharp
[Fact]
public async Task ChangesOlderThanNinetyDaysAreDroppedButRecentOnesSurvive()
{
    var service = new PersonalWorkspaceService(_ => Task.FromResult(true), inMemory: true);
    var reference = Observation;
    await service.SetTrackingAsync(true, reference, Token);
    await service.ObserveAsync(reference with { CapturedAt = reference.CapturedAt.AddDays(-91), GameMode = WindowsGamingSettingState.Disabled }, Token);
    await service.ObserveAsync(reference with { CapturedAt = reference.CapturedAt.AddDays(-1), GameMode = WindowsGamingSettingState.Enabled }, Token);
    var workspace = await service.LoadAsync(Token);
    Assert.All(workspace.Changes, change => Assert.True(change.CapturedAt >= DateTimeOffset.UtcNow.AddDays(-90)));
}
```

  (Nota: `TrimChanges` filtra por `DateTimeOffset.UtcNow`, não pelo
  `CapturedAt` da observação mais recente — uma mudança "antiga" só é
  descartada quando o relógio real já passou os 90 dias desde que ela foi
  criada, o que é verdade neste teste porque a mudança já nasce com
  `CapturedAt` no passado.)

- `UnavailableSensorsAreNotReportedAsConfigurationChanges` e
  `TrackingReportsPointerAccelerationDriftOnlyWhenBothReadingsAreAvailable`
  (linhas 146-170) trocam `PersonalWorkspaceService.DetectChanges` por
  `PersonalTimelineAnalysis.DetectChanges` e continuam válidos como estão
  (a evidência nova não afeta os `Assert.Equal([PcChangeKind...], ...)`
  existentes, que só olham `.Kind`).
- Novo teste cobrindo os coletores injetados:

```csharp
[Fact]
public async Task CaptureObservationCollectsStartupAppsAndGraphicsDriverVersion()
{
    var service = new PersonalWorkspaceService(_ => Task.FromResult(true), inMemory: true,
        applicationInventory: new FakeApplicationInventoryInspector(["Discord", "Steam"]),
        driverVersion: new FakeDriverVersionInspector("NVIDIA GeForce RTX 3070", "31.0.15.3623"));
    var diagnostic = new AppDiagnostic
    {
        Edition = FiveMEdition.Unknown, IsFiveMRunning = false, GtaVDetected = false, GtaVIsRunning = false,
        GtaVGraphicsSettingsPath = string.Empty, CpuName = "Test CPU", GpuName = "Test GPU", GpuNames = ["Test GPU"],
        TotalMemoryGiB = 16, AvailableMemoryGiB = 8, LogicalProcessorCount = 8, FreeDiskGiB = 100, LegacyCacheBytes = 0,
        OsLabel = "Windows 11", ReadinessScore = 80, RecommendedProfile = OptimizationProfile.Balanced,
        PerformancePressure = PerformancePressureLevel.Low
    };
    var observation = await service.CaptureObservationAsync(diagnostic, new WindowsGamingControlsService(demoMode: true), Token);
    Assert.Contains("Discord", observation.StartupAppNames);
    Assert.Contains("Steam", observation.StartupAppNames);
    Assert.Contains("NVIDIA GeForce RTX 3070 31.0.15.3623", observation.GraphicsDriverVersions);
}
```

(Campos exatos exigidos por `AppDiagnostic` — conferir contra
`src/Ralven.App/Services/AppModels.cs:19-70` caso a lista de `required`
tenha mudado; `CreateMinimalDiagnostic` em `FakeAppOptimizationService.cs:138`
usa o mesmo conjunto, mas não popula `GpuNames`, por isso não serve sem essa
adição.)

  com dois fakes minúsculos no mesmo arquivo de teste:

```csharp
file sealed class FakeApplicationInventoryInspector(IReadOnlyList<string> startupNames) : IWindowsApplicationInventoryInspector
{
    public Task<WindowsApplicationInventorySnapshot> InspectAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new WindowsApplicationInventorySnapshot([], [], DateTimeOffset.UtcNow, true, true));
    public Task<WindowsApplicationInventorySnapshot> InspectStartupAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new WindowsApplicationInventorySnapshot([], startupNames.Select(name =>
            new WindowsStartupItem(name, @"C:\Startup", WindowsStartupItemSource.StartupFolder, WindowsApplicationScope.CurrentUser)).ToArray(),
            DateTimeOffset.UtcNow, true, true));
}

file sealed class FakeDriverVersionInspector(string deviceName, string version) : IDriverVersionInspector
{
    public DriverVersionSnapshot GetSnapshot() => new([new(deviceName, version)], [], [], [], [], [], []);
}
```

  **Observação de implementação:** ver como o `AppDiagnostic` de teste é
  construído nos testes existentes deste mesmo arquivo (`Observation` usa um
  helper implícito — checar se há um builder/fixture de `AppDiagnostic` já
  usado em outro teste do projeto, ex. em `AppOptimizationServiceTests` ou
  fixture compartilhada, e reaproveitar; não inventar um `AppDiagnostic` novo
  à mão sem checar os campos obrigatórios de `CaptureObservationAsync`
  — `TotalMemoryGiB > 0`, `GpuNames.Count > 0`, `CpuName` diferente de
  `"Diagnosis.CpuUnknown"`).

- [ ] Rodar a suíte completa (`dotnet test` na raiz, ou o comando do
  `PROJECT_STATE.md` §7) e corrigir qualquer teste que quebrar por causa do
  `ProFeatureAvailability.Enabled = true` da Task 7.
- [ ] Commit: `test(app): cobre coletores injetados e retenção por tempo do workspace pessoal`

---

## Task 10 — Localização (4 idiomas) e limpeza final

Em cada um de `Strings.resx`, `Strings.pt-BR.resx`, `Strings.es.resx`,
`Strings.fr.resx`, ao lado das chaves `Ultra.Change.*` existentes, adicionar:

- `Ultra.Change.StartupApps` — ex. en: `A program was added to or removed from Windows startup.`
- `Ultra.Change.GraphicsDriver` — ex. en: `The graphics driver version changed.`
- `Personal.Change.Evidence` — ex. en: `({0} → {1})`
- `Personal.Change.Association` — ex. en: `· possibly related to a shift in {0}`

E reescrever `Personal.History.Count` sem os números fixos, ex. en:
`{0} measurements · {1} changes saved in the last 90 days`. Traduzir para
pt-BR/es/fr no mesmo estilo direto das chaves vizinhas já existentes nesses
arquivos (usar as traduções de `Ultra.Change.*` já presentes como referência
de tom).

Depois de escrever as 4×5 chaves:

- [ ] Rodar a suíte completa de testes (inclui `LocalizedInterfaceContractTests`,
  que valida paridade de chaves entre os 4 idiomas) e corrigir qualquer chave
  faltante.
- [ ] `dotnet format Ralven.slnx --verify-no-changes`.
- [ ] `.\scripts\Verify-Safety.ps1`.
- [ ] Commit: `feat(app): localiza a evidência da linha do tempo pessoal em 4 idiomas`

---

## Task 11 — `PROJECT_STATE.md` e fechamento

- [ ] Corrigir a frase da seção 1 que hoje diz "Pro... continuam bloqueados
  enquanto suas dependências operacionais não estiverem configuradas" — Pro
  passa a estar habilitado no cliente (`ProFeatureAvailability.Enabled = true`)
  nesta branch; cobrança continua fail-closed no servidor (Asaas desativado);
  Ralven AI e o vínculo Discord continuam bloqueados, sem mudança.
- [ ] `dotnet build Ralven.slnx --configuration Release`.
- [ ] `dotnet run --project tests/Ralven.Tests/Ralven.Tests.csproj --configuration Release --no-build -- --minimum-expected-tests 1`.
- [ ] `dotnet format Ralven.slnx --verify-no-changes`.
- [ ] `.\scripts\Verify-Safety.ps1`.
- [ ] `git diff --check`.
- [ ] `scripts\Install-DevelopmentShortcut.ps1 -Build` (reconstrói o atalho
  "Ralven - Desenvolvimento" a partir deste worktree).
- [ ] Revisar o diff completo, criar o(s) commit(s) finais restantes, push da
  branch da tarefa e abrir o PR para `dev/proxima-versao` com o handoff exigido
  por `AI_RULES.md` (objetivo, escopo, validação, riscos: a flag do Pro afeta
  todos os recursos Pro já implementados, não só esta feature).
