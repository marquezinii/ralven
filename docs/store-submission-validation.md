# Minimum Viable Store Submission — evidence and handoff

Date: 2026-09-17. Base: integrated MSIX POC #218, `dev/proxima-versao` at
`ecccda9`. This is a capability-certification gate, not a Store migration or release.

## Decision

**BLOQUEADO PARA SUBMISSÃO**

The owner confirmed that Ralven is not reserved in Partner Center. Consequently no
production Package Name, Publisher or PublisherDisplayName is available. The local
test MSIX is valid but **must not be uploaded as the reserved Store product**.
Reserve the product, copy the three identity values and rebuild following the
[manual checklist](microsoft-store-submission-checklist.md). Microsoft capability
approval is the purpose of this submission, not a prerequisite presumed satisfied.
Portal listing, declarations, ratings, support/privacy and publishing hold must also
be completed by the owner before Submit for certification.

## Engineering changes

- Separate `packaging/msix-store` entry point with mandatory identity parameters,
  Store revision zero, real launch, bundled App/Launcher/Broker/.NET/WPF runtime,
  only `runFullTrust` and `allowElevation`, and no startup/service/driver extension.
- Package identity detection gates Web updater creation, update-health writes and
  startup registration. Packaged Launcher reads the immutable bundled runtime
  pointer instead of activating/recovering/pruning Web versions. No second mutable
  Store runtime is created. Default unpackaged Web update paths remain active.
- Expose the existing HAGS opt-in only in the packaged FiveM Legacy optimizer.
  Aggressive profile and existing preconditions remain required. No new Broker
  action, command contract, registry scope or administrative privilege is added.
- Packaged Launcher configures its process through the WinForms SetHighDpiMode API
  before dialogs; elevation levels are unchanged. Manifest-based DPI was rejected
  by the .NET 10 WFO0003 compiler warning and replaced without suppression.
- Portable build can omit Web ZIP compression when MSIX consumes its directory;
  existing obfuscation and fail-closed integrity gates remain in use.
- Paste-ready restricted-capability notes, certification notes, reproducible tests
  and Partner Center checklist are included. The POC remains a historical artifact.

## Executed checks

| Check | Result |
| --- | --- |
| `dotnet restore Ralven.slnx` | Passed |
| `dotnet build Ralven.slnx --configuration Release --no-restore` | Passed, zero warnings/errors |
| Release test runner, `--minimum-expected-tests 1` | 1,610 passed; zero failures/skips |
| `dotnet format --verify-no-changes --no-restore --include ...` | Passed for all changed C# files |
| `Sync-Localization.ps1 -Mode Check` | Local PowerShell 7.6.5 attempts did not complete promptly (over 20 minutes); no local pass claimed. The same canonical check passed in PR CI in six seconds; local duplicate processes were then stopped. Catalog files themselves are unchanged; the UI reuses an existing localized action label. |
| PowerShell parser for packaging/build scripts | Passed |
| Existing release hardening (`-Harden`) | App/Broker Core/Windows obfuscated; embedded Launcher checked; fail-closed payload gate passed |
| MakeAppx schema/pack validation | Passed |
| `Test-StoreLayout.ps1` | Passed: runtime, minimal capabilities, mediumIL, no demo/startup, Broker hashes |
| Local signature verification | Passed, zero warnings/errors after trusting the dedicated development leaf |
| Local per-user installation | Passed, package status Ok |
| Packaged Launcher → App capture | Passed in package context; Settings startup/manual update disabled; HAGS opt-in visible only in specialized scope |
| Unpackaged Web optimizer capture | Passed; new HAGS opt-in absent |

Capture probes used existing `--demo-synthetic` explicitly, for layout inspection
without changing user settings. **The manifest has no demo argument.** These images
do not prove real diagnosis, administrative execution or serve as real Store listing
screenshots. The final listing needs a screenshot of normal use on a test machine.

## Real administrative test

**NÃO VALIDADO LOCALMENTE** — App → Broker apply, verification, rollback and
restoration on this candidate were not executed. The lower-risk power action was
investigated first. Read-only `powercfg /getactivescheme` showed High performance
already active (`8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c`). Changing the host first just
to manufacture an administrative mutation would be unnecessary. Profiles also
contain user-level maintenance; a disposable test host is preferable. HAGS has
greater host impact and requires real Legacy detection, so it was not forced.

Before-state observations are retained locally in `artifacts/store-validation`.
Read-only after-state observations confirmed the same active power GUID and HAGS
value still absent. This establishes no observed state change, not a rollback test.
No power/HAGS apply or rollback claim is made. The previous POC's Broker activation
and UAC observation are historical evidence, not a real administrative operation
in this final candidate. UAC used to trust the dedicated local test certificate is
also not a Broker action test. Complete tests 2 and 3 in the
[certification test plan](store-certification-test-plan.md) manually, recording
before/applied/restored state and action-specific outcomes.

## Windows 10

**NÃO VALIDADO EM HOST WINDOWS 10**. Host available: Windows 11 Pro x64,
10.0.26200.0. Manifest minimum is 10.0.19041.0, matching the existing Windows target;
the package is x64 and self-contained. Package identity detection uses the existing
Windows package API available before this minimum. Existing backdrop policy falls
back on Windows 10; Windows 11-only backdrop behavior is version gated.

The minimum install/API floor is not a statement that every old Windows 10 SKU is
still supported by Microsoft/.NET. The current .NET 10 support table lists supported
Windows 10 Enterprise/LTSC variants; old mainstream releases have lifecycle limits.
Test the actual advertised Windows 10 editions/builds before public Store rollout.
[Official .NET 10 OS support](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md).

## External pending items

1. Reserve Ralven and obtain the real Partner Center identity; regenerate and
   revalidate the exact upload package. No invented production identity is stored.
2. Complete account verification, listing with a real screenshot, actual support
   contact/public privacy URL, IARC and displayed declarations in Partner Center.
3. Microsoft decides `allowElevation`; retain optional WACK failures with
   evidence instead of treating local package validation as capability approval.
4. Individual versus Company eligibility for this particular capability has no
   unequivocal rule in the consulted official documentation. Inspect Packages,
   Restricted capabilities, final portal validation and certification feedback.
   The separate general Account Type policy may require Company for the publisher.
5. Manually validate real privileged apply/restore and Windows 10 on suitable hosts.

## Deliberately deferred until capability approval

- Full Store update UX and automated Store packaging/pipeline integration.
- Optional StartupTask experience; startup registration is disabled for this gate.
- Complete Web/Store data-coexistence/uninstall policy and long-duration update tests.
- Wider Windows 10/OEM/GPU matrix and final hardened release qualification.
- Public Store publication. Select manual publishing hold for this first submission.

Web retains its current signed updater and Inno distribution. No version/tag/public
release, certificate purchase, Store upload or Store publication is performed here.

## Remaining risks

Microsoft can refuse restricted elevation or request changes/additional tests.
HAGS is limited to the existing specialized Legacy workflow and depends on installed
game/hardware prerequisites. Power restoration is guarded and refuses to overwrite
a later independent scheme change. Store settings still contain Web preference
labels; the underlying updater services are disabled, with final UX deferred.
Data under the existing per-user Ralven location can coexist with Web; avoid running
both instances during certification tests. Local signing is development-only.

The development shortcut was not rebuilt: this updater/Store task uses the explicit
AI_RULES exception and isolated MSIX payload validation.

## Local candidate identity and fingerprint

`artifacts/msix-store/Ralven-Store-1.7.1.0-x64.msix`, 214,521,541 bytes.

SHA-256: `3472D2B72DCB5B85AA107BE4B8E1019983C4982F6114EEEF372EB33D5EB88C4B`.

- Name: `Ralven.StoreCertificationLocal`.
- Publisher: `CN=Ralven Store Certification Local`.
- PublisherDisplayName: `Ralven`.
- Version: `1.7.1.0`, x64.
- Resolved manifest: `artifacts/msix-store/layout/AppxManifest.xml`.
- Parametrized certification manifest: `packaging/msix-store/AppxManifest.xml`.

This identity is deliberately local, not a guessed Partner Center identity. The
certificate is a dedicated short-lived development certificate, not a Store or
public distribution certificate. Keep the public `.cer` with local test evidence;
there is no private certificate/key in Git. Final documentation/report resides
outside the already-built package; rebuilding with the real identity copies the
then-current documentation. Do not edit this signed candidate after WACK.

## WACK final — full results, not capability approval

WACK 10.0.26100.7705, Windows 11 x64, completed 2026-09-17 at 16:44:51.
`PARTIAL_RUN=FALSE`, `OVERALL_RESULT=WARNING`: **21 PASS, 2 optional FAIL,
1 WARNING**. Raw XML/HTML is retained in `artifacts/store-validation/wack-api-dpi.*`.
No local WACK result represents Microsoft's approval of restricted capabilities.

Three passes are retained:
- Before DPI changes: WARNING, 21 PASS / 2 FAIL / 1 WARNING (`wack-before-dpi.xml`).
- Intermediate manifest-DPI candidate: overall PASS, 22 PASS / 2 optional FAIL
  (`wack-manifest-dpi.xml`), but adding those manifest settings produced the real
  WinForms compiler warning WFO0003. That approach was discarded, not suppressed.
- Final API-based candidate: WARNING, 21 PASS / 2 optional FAIL / 1 WARNING,
  with Release build zero warnings/errors. This is the fingerprinted candidate above.

| Finding | Classification and action |
| --- | --- |
| UAC execution level, index 11: Broker requireAdministrator | Expected due to requested allowElevation; potential certification blocker if Microsoft refuses the exception. Preserve UAC/manifest and submit justification. |
| Blocked executables, index 88: process creation APIs | Real static references in Launcher/App/Windows and self-contained .NET. Legitimate typed/native launch paths exist; not proof of arbitrary Broker execution. Potential blocker requiring desktop/full-trust certification review. Do not call all references false positives. |
| Blocked-name strings in .NET/WPF/resource binaries | Possible scanner/runtime effects; a string match alone does not establish invocation. Preserve each occurrence below; no blanket exemption or approval asserted. |
| Blocked-name strings in Markdown | Confirmed textual documentation matches, including this evidence report, not executable launch. False positives for these specific documentation occurrences. Documentation was not renamed or removed to evade the scan. |
| DPI warning, index 92: Launcher single-file | Potential blocker / possible .NET single-file scanner effect. Packaged Main calls the official WinForms SetHighDpiMode(PerMonitorV2) API before any dialog; WPF App already declares PerMonitorV2. Kit still reports missing native DPI API/manifest. Runtime DPI of Launcher dialogs was not measured; do not assert a proven blanket false positive. Manual monitor/DPI testing and certification review remain appropriate. |

[Official WFO0003 correction](https://learn.microsoft.com/en-us/dotnet/desktop/winforms/compiler-messages/wfo0003)
and [WinForms process DPI API](https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.application.sethighdpimode?view=windowsdesktop-10.0).
No diagnostic was disabled; no misleading manifest was added just to satisfy WACK.

Package/manifest/branding, debug configuration, private signing keys, services/drivers,
resource and special-capability static tests passed. Special-capability PASS only
validates this local test; it does not authorize allowElevation in Partner Center.
Every final failure/warning message follows. Additional Markdown string matches
are effects of shipping the normal portable documentation, not additional actions.

| Index | Test | Result | Optional |
| --- | --- | --- | --- |
| 2 | Instale drivers assinados e arquivos executáveis | PASS | TRUE |
| 11 | Nível de execução de controle de conta de usuário | FAIL | TRUE |
| 77 | Conta do aplicativo | PASS | TRUE |
| 31 | Manifesto do aplicativo | PASS | FALSE |
| 87 | Verbos da associação de arquivo | PASS | TRUE |
| 86 | Verificações do Registro | PASS | TRUE |
| 91 | Recursos Empresariais | PASS | FALSE |
| 90 | Pacotes de Recursos | PASS | FALSE |
| 70 | Analisador de arquivos banidos | PASS | FALSE |
| 62 | Assinatura de código privado | PASS | FALSE |
| 45 | Recursos do aplicativo | PASS | TRUE |
| 61 | Identidade visual | PASS | FALSE |
| 46 | Configuração de depuração | PASS | TRUE |
| 52 | Recursos de uso especial | PASS | FALSE |
| 56 | Atributo ExclusiveTo | PASS | FALSE |
| 57 | Localização de tipo | PASS | FALSE |
| 55 | Diferenciação de maiúsculas e minúsculas de nome de tipo | PASS | FALSE |
| 54 | Correção de nome de tipo | PASS | FALSE |
| 58 | Correção de metadados gerais | PASS | TRUE |
| 53 | Propriedades | PASS | FALSE |
| 89 | Uso de arquivo morto | PASS | TRUE |
| 88 | Executáveis bloqueados | FAIL | TRUE |
| 63 | Arquivos adequados da plataforma | PASS | TRUE |
| 92 | DPIAwarenessValidation | WARNING | FALSE |

### Index 11 — FAIL

- Ocorreu falha do arquivo executável Runtime\versions\1.7.1\broker\Ralven.Broker.exe porque ele requer permissões de segurança com privilégios elevados.

### Index 88 — FAIL

- O arquivo Runtime\versions\1.7.1\broker\System.Diagnostics.Process.dll contém uma referência a um "Processo de Inicialização" relacionado à API kernel32.dll!CreateProcessW
- O arquivo Runtime\versions\1.7.1\broker\System.Diagnostics.Process.dll contém uma referência a um "Processo de Inicialização" relacionado à API shell32.dll!ShellExecuteExW
- O arquivo Runtime\versions\1.7.1\System.Diagnostics.Process.dll contém uma referência a um "Processo de Inicialização" relacionado à API kernel32.dll!CreateProcessW
- O arquivo Runtime\versions\1.7.1\System.Diagnostics.Process.dll contém uma referência a um "Processo de Inicialização" relacionado à API shell32.dll!ShellExecuteExW
- O arquivo Runtime\versions\1.7.1\Microsoft.VisualBasic.Forms.dll contém uma referência a um "Processo de Inicialização" relacionado à API kernel32.dll!CreateProcessA
- O arquivo Runtime\versions\1.7.1\Ralven.dll contém uma referência a um "Processo de Inicialização" relacionado à API System.Diagnostics.Process.Start
- O arquivo Runtime\versions\1.7.1\broker\mscordbi.dll contém uma referência a um "Processo de Inicialização" relacionado à API kernel32.dll!CreateProcessW
- O arquivo Runtime\versions\1.7.1\mscordbi.dll contém uma referência a um "Processo de Inicialização" relacionado à API kernel32.dll!CreateProcessW
- O arquivo Runtime\versions\1.7.1\System.Diagnostics.PerformanceCounter.dll contém uma referência a um "Processo de Inicialização" relacionado à API System.Diagnostics.Process.Start
- O arquivo Runtime\versions\1.7.1\System.CodeDom.dll contém uma referência a um "Processo de Inicialização" relacionado à API System.Diagnostics.Process.Start
- O arquivo Runtime\versions\1.7.1\broker\coreclr.dll contém uma referência a um "Processo de Inicialização" relacionado à API kernel32.dll!CreateProcessW
- O arquivo Runtime\versions\1.7.1\coreclr.dll contém uma referência a um "Processo de Inicialização" relacionado à API kernel32.dll!CreateProcessW
- O arquivo Ralven.Launcher.exe contém uma referência a um "Processo de Inicialização" relacionado à API kernel32.dll!CreateProcessW
- O arquivo Ralven.Launcher.exe contém uma referência a um "Processo de Inicialização" relacionado à API shell32.dll!ShellExecuteW
- O arquivo Runtime\versions\1.7.1\System.Windows.Forms.dll contém uma referência a um "Processo de Inicialização" relacionado à API System.Diagnostics.Process.Start
- O arquivo Runtime\versions\1.7.1\broker\System.Diagnostics.PerformanceCounter.dll contém uma referência a um "Processo de Inicialização" relacionado à API System.Diagnostics.Process.Start
- O arquivo Runtime\versions\1.7.1\Ralven.exe contém uma referência a um "Processo de Inicialização" relacionado à API shell32.dll!ShellExecuteW
- O arquivo Runtime\versions\1.7.1\PresentationFramework.dll contém uma referência a um "Processo de Inicialização" relacionado à API shell32.dll!ShellExecuteEx
- O arquivo Runtime\versions\1.7.1\broker\System.CodeDom.dll contém uma referência a um "Processo de Inicialização" relacionado à API System.Diagnostics.Process.Start
- O arquivo Runtime\versions\1.7.1\UIAutomationTypes.dll contém uma referência a um "Processo de Inicialização" relacionado à API shell32.dll!ShellExecuteA
- O arquivo Runtime\versions\1.7.1\UIAutomationTypes.dll contém uma referência a um "Processo de Inicialização" relacionado à API shell32.dll!ShellExecuteEx
- O arquivo Runtime\versions\1.7.1\Ralven.Windows.dll contém uma referência a um "Processo de Inicialização" relacionado à API System.Diagnostics.Process.Start
- O arquivo Runtime\versions\1.7.1\Wpf.Ui.dll contém uma referência a um "Processo de Inicialização" relacionado à API System.Diagnostics.Process.Start
- O arquivo Runtime\versions\1.7.1\WindowsBase.dll contém uma referência a um "Processo de Inicialização" relacionado à API shell32.dll!ShellExecuteA
- O arquivo Runtime\versions\1.7.1\WindowsBase.dll contém uma referência a um "Processo de Inicialização" relacionado à API shell32.dll!ShellExecuteEx
- O arquivo Runtime\versions\1.7.1\PenImc_cor3.dll contém uma referência a um "Processo de Inicialização" relacionado à API shell32.dll!ShellExecuteExW
- O arquivo Runtime\versions\1.7.1\broker\Ralven.Windows.dll contém uma referência a um "Processo de Inicialização" relacionado à API System.Diagnostics.Process.Start
- O arquivo Runtime\versions\1.7.1\System.Windows.Forms.Primitives.dll contém uma referência a um "Processo de Inicialização" relacionado à API shell32.dll!ShellExecuteW
- O arquivo Runtime\versions\1.7.1\Sentry.dll contém uma referência a um "Processo de Inicialização" relacionado à API System.Diagnostics.Process.Start
- O arquivo Runtime\versions\1.7.1\broker\Ralven.Broker.exe contém uma referência a um "Processo de Inicialização" relacionado à API shell32.dll!ShellExecuteW
- O arquivo Runtime\versions\1.7.1\PresentationUI.dll contém uma referência a um "Processo de Inicialização" relacionado à API System.Diagnostics.Process.Start
- O arquivo Ralven.Launcher.exe contém uma referência de executável bloqueada para "CMD".
- O arquivo Ralven.Launcher.exe contém uma referência de executável bloqueada para "bash".
- O arquivo Ralven.Launcher.exe contém uma referência de executável bloqueada para "cdB".
- O arquivo Ralven.Launcher.exe contém uma referência de executável bloqueada para "MSBuild".
- O arquivo Ralven.Launcher.exe contém uma referência de executável bloqueada para "dnX".
- O arquivo Ralven.Launcher.exe contém uma referência de executável bloqueada para "CsI".
- O arquivo Ralven.Launcher.exe contém uma referência de executável bloqueada para "rEg".
- O arquivo clrjit.dll contém uma referência de executável bloqueada para "bash".
- O arquivo CONTRIBUTING.md contém uma referência de executável bloqueada para "PowerShell".
- O arquivo CONTRIBUTING.md contém uma referência de executável bloqueada para "CMD".
- O arquivo D3DCompiler_47_cor3.dll contém uma referência de executável bloqueada para "reg".
- O arquivo D3DCompiler_47_cor3.dll contém uma referência de executável bloqueada para "cmd".
- O arquivo Microsoft.DiaSymReader.Native.amd64.dll contém uma referência de executável bloqueada para "cmd".
- O arquivo Microsoft.VisualBasic.Core.dll contém uma referência de executável bloqueada para "cdB".
- O arquivo PresentationCore.dll contém uma referência de executável bloqueada para "csI".
- O arquivo PresentationCore.dll contém uma referência de executável bloqueada para "rEG".
- O arquivo PresentationCore.dll contém uma referência de executável bloqueada para "cmd".
- O arquivo PresentationFramework.dll contém uma referência de executável bloqueada para "CsI".
- O arquivo PresentationFramework.dll contém uma referência de executável bloqueada para "cmd".
- O arquivo PresentationFramework.dll contém uma referência de executável bloqueada para "reG".
- O arquivo PresentationFramework.dll contém uma referência de executável bloqueada para "DNX".
- O arquivo PresentationUI.dll contém uma referência de executável bloqueada para "dnX".
- O arquivo Ralven.dll contém uma referência de executável bloqueada para "reg".
- O arquivo Ralven.dll contém uma referência de executável bloqueada para "CMD".
- O arquivo System.CodeDom.dll contém uma referência de executável bloqueada para "cmd".
- O arquivo System.Collections.Concurrent.dll contém uma referência de executável bloqueada para "Cdb".
- O arquivo System.Diagnostics.EventLog.dll contém uma referência de executável bloqueada para "DNX".
- O arquivo System.Net.Sockets.dll contém uma referência de executável bloqueada para "cmd".
- O arquivo System.Printing.dll contém uma referência de executável bloqueada para "rEG".
- O arquivo System.Private.CoreLib.dll contém uma referência de executável bloqueada para "CMD".
- O arquivo System.Private.CoreLib.dll contém uma referência de executável bloqueada para "MSBuild".
- O arquivo System.Private.CoreLib.dll contém uma referência de executável bloqueada para "dnX".
- O arquivo System.Private.CoreLib.dll contém uma referência de executável bloqueada para "CsI".
- O arquivo System.Private.Xml.dll contém uma referência de executável bloqueada para "rEg".
- O arquivo System.Private.Xml.dll contém uma referência de executável bloqueada para "cDB".
- O arquivo System.Reflection.Metadata.dll contém uma referência de executável bloqueada para "Cdb".
- O arquivo System.Resources.Extensions.dll contém uma referência de executável bloqueada para "MSBuild".
- O arquivo System.Text.Json.dll contém uma referência de executável bloqueada para "CDb".
- O arquivo System.Text.RegularExpressions.dll contém uma referência de executável bloqueada para "dNX".
- O arquivo System.Windows.Forms.Design.dll contém uma referência de executável bloqueada para "cmd".
- O arquivo System.Windows.Forms.Design.dll contém uma referência de executável bloqueada para "CSI".
- O arquivo System.Windows.Forms.dll contém uma referência de executável bloqueada para "cMD".
- O arquivo System.Windows.Forms.dll contém uma referência de executável bloqueada para "reg".
- O arquivo System.Windows.Forms.Primitives.dll contém uma referência de executável bloqueada para "cmd".
- O arquivo UIAutomationClientSideProviders.dll contém uma referência de executável bloqueada para "cmd".
- O arquivo WinRT.Runtime.dll contém uma referência de executável bloqueada para "MSBuild".
- O arquivo architecture.md contém uma referência de executável bloqueada para "PowerShell".
- O arquivo release-hardening.md contém uma referência de executável bloqueada para "MSBuild".
- O arquivo release-preview.md contém uma referência de executável bloqueada para "PowerShell".
- O arquivo research.md contém uma referência de executável bloqueada para "powershell".
- O arquivo safety.md contém uma referência de executável bloqueada para "PowerShell".
- O arquivo safety.md contém uma referência de executável bloqueada para "CMD".
- O arquivo store-submission-validation.md contém uma referência de executável bloqueada para "PowerShell".
- O arquivo store-submission-validation.md contém uma referência de executável bloqueada para "CMD".
- O arquivo store-submission-validation.md contém uma referência de executável bloqueada para "bash".
- O arquivo store-submission-validation.md contém uma referência de executável bloqueada para "cdB".
- O arquivo store-submission-validation.md contém uma referência de executável bloqueada para "MSBuild".
- O arquivo store-submission-validation.md contém uma referência de executável bloqueada para "dnX".
- O arquivo store-submission-validation.md contém uma referência de executável bloqueada para "CsI".
- O arquivo store-submission-validation.md contém uma referência de executável bloqueada para "rEg".
- O arquivo CONVENTIONS.md contém uma referência de executável bloqueada para "MSBuild".
- O arquivo STACK.md contém uma referência de executável bloqueada para "MSBuild".
- O arquivo STACK.md contém uma referência de executável bloqueada para "PowerShell".
- O arquivo Ralven.resources.dll contém uma referência de executável bloqueada para "REG".
- O arquivo System.Windows.Forms.resources.dll contém uma referência de executável bloqueada para "CSi".

### Index 92 — WARNING

- O arquivo Ralven.Launcher.exe não tem PerMonitorV2 incluído no manifesto nem chamadas para APIs de Reconhecimento de DPI, por exemplo: user32!SetProcessDpiAwarenessContext ou user32!SetThreadDpiAwarenessContext.
- O app Ralven.StoreCertificationLocal_1.7.1.0_x64__r4pg94g74pms8 não tem Reconhecimento de DPI.

## Local test cleanup

The dedicated local test package was uninstalled after validation. Its exact development
certificate was removed from CurrentUser My/Root/TrustedPeople and LocalMachine
TrustedPeople; all four stores were queried and the leaf was absent. Existing Ralven
user data was not deleted. Local .cer/MSIX/reports remain for evidence. Cleanup of
aborted build temporaries was rejected by automatic approval policy; those ignored
artifacts remain outside Git and outside the final MSIX.

