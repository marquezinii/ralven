# Real Microsoft Store certification candidate — evidence

Date: 2026-09-17. Source base: origin/dev/proxima-versao at ecccda9 plus the prerequisite minimum-submission commits from open PR #221. No production release or automatic Partner Center upload/publication.

## Decision

**PRONTO PARA SUBMISSÃO** — the production-identity hardened MSIX is valid and available for manual upload. This means ready to request Microsoft's certification decision, not WACK PASS, capability approval, public rollout qualification or a completed portal submission. The owner must complete listing/declarations/ratings and retain manual publishing hold.

## Upload artifact

- Path relative to this worktree: artifacts/msix-store/Ralven-Store-1.7.1.0-x64.msix.
- Version: 1.7.1.0; architecture: x64; minimum Windows: 10.0.19041.0.
- Size: 214,509,020 bytes.
- Name: VemryxInc.Ralven.
- Publisher: CN=1FB9E268-F80A-40DF-8E57-BBB7C7849ABD.
- PublisherDisplayName: Vemryx Inc.
- PFN verified by installed Windows package: VemryxInc.Ralven_vk02nsxq3ddr6.
- Store ID (reference only): 9N61X5M295M5.
- Only capabilities: rescap:runFullTrust and rescap:allowElevation.
- Upload artifact is unsigned locally. Microsoft Store applies its distribution signature after certification. No public certificate purchase or production signing identity is implied.
- SHA-256: 4FB229739B23FFD93D760CA63F394A31D3E4845BC758A5CA2E2C7527A7E812A7.

## Executed validations

| Check | Result |
| --- | --- |
| Build-StoreMsix.ps1 -Harden | Passed; existing portable release hardening, build zero warnings/errors |
| MakeAppx pack and unpack of the upload bytes | Passed, schema validation enabled |
| Test-StoreLayout.ps1 on layout and extracted upload | Passed: exact production identity, mediumIL, runtime, minimal capabilities, Broker hashes |
| Wrong-identity negative probe | Passed: mismatched identity rejected |
| Test-NoUnobfuscatedAssemblies.ps1 on extracted upload | Passed: App/Broker Core and Windows plus Launcher single-file bundle; no PDB/maps leaked |
| PowerShell parser for changed scripts | Passed |
| Existing Release tests | 1,610 passed, zero failed/skipped (20.972 seconds) |
| Verify-Safety.ps1 | Passed; zero-warning Release build, 1,610 tests passed again |
| Development copy SignTool verify /pa /v | Passed, zero warnings/errors; temporary leaf matching Publisher |
| Add-AppxPackage and OS identity | Passed, status Ok, exact expected PFN |
| Normal packaged Launcher → App | Passed: Ralven.exe from WindowsApps, no demo arguments, responsive Ralven window |
| Packaged synthetic Settings/FiveMOptimizer captures | Passed; manually inspected images; startup/manual Web update disabled and specialized HAGS opt-in visible |
| Broker activation/runas probe | Passed: started from package-context harness, no request supplied, exit 2; broker-started/invalid-arguments logged |
| Read-only power/HAGS before and after | Unchanged; not an apply/rollback test |

The Broker probe used an external manual test harness, not a new product entry point or a real App transaction. It exercised the normal Windows runas elevation path and safe missing-argument rejection. Certificate trust UAC is separate evidence. Synthetic images do not prove real diagnostics, administrative execution or qualify as real Store listing screenshots. No unattended UAC consent was supplied.

## Full WACK

Windows App Certification Kit 10.0.26100.7705, Windows 11 Pro 10.0.26200.0 x64. Completed appcert process; OVERALL_RESULT=WARNING; PARTIAL_RUN=FALSE. Exact production identity and version in report. 24 tests: 21 PASS, 2 optional FAIL, 1 WARNING. Tested the separately development-signed copy of the same MSIX; payload/manifest were not rebuilt or modified when signing.

Raw report: artifacts/store-validation/wack-production-identity.xml. HTML: artifacts/store-validation/wack-production-identity.htm. Raw reports may include host paths and remain outside Git. All failure/warning messages are preserved below; nothing was suppressed or weakened.

| Finding | Classification / action |
| --- | --- |
| Index 11, Broker requireAdministrator, one message | Expected due to allowElevation and potential Microsoft approval blocker. Do not remove Broker or weaken UAC. Explicitly disclose in capability and certification notes. |
| Index 88, process APIs in product and .NET runtime, 96 messages total including documentation | Potential blocker requiring review. Product process-launch calls are real typed/limited desktop operations. Framework references may be scanner effects; no blanket false-positive assertion. Literal blocked-executable strings in Markdown are proven documentation matches, not executable payloads. Do not rename/remove documentation or weaken the kit to evade detection. |
| Index 92, DPI, two warnings | Potential blocker / possible single-file scanner effect. Packaged Launcher calls WinForms SetHighDpiMode(PerMonitorV2); WPF App has its DPI manifest. Static kit did not recognize Launcher awareness. No WFO0003 suppression or misleading manifest was introduced. Runtime Launcher-dialog multi-monitor DPI behavior remains unmeasured. |

No new manifest/layout/integrity error remained to correct before this capability gate. Optional FAIL is still FAIL. The Microsoft certification team decides whether the disclosed desktop architecture findings are acceptable or need corrections.

Sources: [WACK](https://learn.microsoft.com/en-us/windows/uwp/debug-test-perf/windows-app-certification-kit), [WinForms WFO0003](https://learn.microsoft.com/en-us/dotnet/desktop/winforms/compiler-messages/wfo0003), [SetHighDpiMode](https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.application.sethighdpimode?view=windowsdesktop-10.0).

## Remaining local coverage and external steps

**NÃO VALIDADO LOCALMENTE** — a real App → Broker administrative change, verified post-condition and rollback/restoration were not executed. The least-risk power action was investigated; this host already has High performance active (8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c). Profiles also include user maintenance. HAGS needs actual Legacy detection and has greater host impact. Do not manufacture a mutation on personal data; run tests 2/3 of the certification test plan on a suitable disposable host. Before/after state stayed unchanged; this is not rollback proof.

**NÃO VALIDADO EM HOST WINDOWS 10** — static minimum/target floor 19041 and existing version-gated Windows UI fallback are retained. No Windows 10 host is available. Verify actually advertised editions/builds, GPU/OEM behavior and .NET OS support before public Store rollout.

Complete the real listing screenshot, privacy URL, support contact, IARC/age/category/declarations, restricted capability notes and certification notes in Partner Center. The Individual-versus-Company allowance for allowElevation remains without an unequivocal official answer; inspect Packages, Restricted capabilities, final validation and feedback. General Company-account policy is separate and may apply to Vemryx Inc. No capability approval is presumed.

Deliberately deferred: final Store updater UX/pipeline and Microsoft-owned update experience, optional StartupTask (disabled now), full Web/Store data-coexistence policy, wider Windows 10/OEM/GPU tests and public publication. Web updater and Inno Setup stay unchanged. No mutable second Store runtime, service, driver, scheduled task or security bypass is added.

The development shortcut was not rebuilt under the AI_RULES updater/Store exception. Package validation used this isolated worktree. Final notes and manual checklist are adjacent documents. The payload includes the portable documentation snapshot at build time; this final evidence/hash report lives in the source handoff and is not a self-referential payload hash.

## Every WACK test

| Index | Name | Result | Optional |
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

## Every failure/warning message

### Index 11: FAIL

- Ocorreu falha do arquivo executável Runtime\versions\1.7.1\broker\Ralven.Broker.exe porque ele requer permissões de segurança com privilégios elevados.

### Index 88: FAIL

- O arquivo Runtime\versions\1.7.1\broker\System.Diagnostics.Process.dll contém uma referência a um "Processo de Inicialização" relacionado à API kernel32.dll!CreateProcessW
- O arquivo Runtime\versions\1.7.1\broker\System.Diagnostics.Process.dll contém uma referência a um "Processo de Inicialização" relacionado à API shell32.dll!ShellExecuteExW
- O arquivo Runtime\versions\1.7.1\System.Diagnostics.Process.dll contém uma referência a um "Processo de Inicialização" relacionado à API kernel32.dll!CreateProcessW
- O arquivo Runtime\versions\1.7.1\System.Diagnostics.Process.dll contém uma referência a um "Processo de Inicialização" relacionado à API shell32.dll!ShellExecuteExW
- O arquivo Runtime\versions\1.7.1\Microsoft.VisualBasic.Forms.dll contém uma referência a um "Processo de Inicialização" relacionado à API kernel32.dll!CreateProcessA
- O arquivo Runtime\versions\1.7.1\broker\mscordbi.dll contém uma referência a um "Processo de Inicialização" relacionado à API kernel32.dll!CreateProcessW
- O arquivo Runtime\versions\1.7.1\mscordbi.dll contém uma referência a um "Processo de Inicialização" relacionado à API kernel32.dll!CreateProcessW
- O arquivo Runtime\versions\1.7.1\Ralven.Windows.dll contém uma referência a um "Processo de Inicialização" relacionado à API System.Diagnostics.Process.Start
- O arquivo Runtime\versions\1.7.1\System.Diagnostics.PerformanceCounter.dll contém uma referência a um "Processo de Inicialização" relacionado à API System.Diagnostics.Process.Start
- O arquivo Runtime\versions\1.7.1\System.CodeDom.dll contém uma referência a um "Processo de Inicialização" relacionado à API System.Diagnostics.Process.Start
- O arquivo Runtime\versions\1.7.1\Ralven.dll contém uma referência a um "Processo de Inicialização" relacionado à API System.Diagnostics.Process.Start
- O arquivo Ralven.Launcher.exe contém uma referência a um "Processo de Inicialização" relacionado à API kernel32.dll!CreateProcessW
- O arquivo Ralven.Launcher.exe contém uma referência a um "Processo de Inicialização" relacionado à API shell32.dll!ShellExecuteW
- O arquivo Runtime\versions\1.7.1\broker\coreclr.dll contém uma referência a um "Processo de Inicialização" relacionado à API kernel32.dll!CreateProcessW
- O arquivo Runtime\versions\1.7.1\coreclr.dll contém uma referência a um "Processo de Inicialização" relacionado à API kernel32.dll!CreateProcessW
- O arquivo Runtime\versions\1.7.1\System.Windows.Forms.dll contém uma referência a um "Processo de Inicialização" relacionado à API System.Diagnostics.Process.Start
- O arquivo Runtime\versions\1.7.1\broker\System.Diagnostics.PerformanceCounter.dll contém uma referência a um "Processo de Inicialização" relacionado à API System.Diagnostics.Process.Start
- O arquivo Runtime\versions\1.7.1\PresentationFramework.dll contém uma referência a um "Processo de Inicialização" relacionado à API shell32.dll!ShellExecuteEx
- O arquivo Runtime\versions\1.7.1\broker\System.CodeDom.dll contém uma referência a um "Processo de Inicialização" relacionado à API System.Diagnostics.Process.Start
- O arquivo Runtime\versions\1.7.1\UIAutomationTypes.dll contém uma referência a um "Processo de Inicialização" relacionado à API shell32.dll!ShellExecuteA
- O arquivo Runtime\versions\1.7.1\UIAutomationTypes.dll contém uma referência a um "Processo de Inicialização" relacionado à API shell32.dll!ShellExecuteEx
- O arquivo Runtime\versions\1.7.1\broker\Ralven.Broker.exe contém uma referência a um "Processo de Inicialização" relacionado à API shell32.dll!ShellExecuteW
- O arquivo Runtime\versions\1.7.1\Ralven.exe contém uma referência a um "Processo de Inicialização" relacionado à API shell32.dll!ShellExecuteW
- O arquivo Runtime\versions\1.7.1\Wpf.Ui.dll contém uma referência a um "Processo de Inicialização" relacionado à API System.Diagnostics.Process.Start
- O arquivo Runtime\versions\1.7.1\WindowsBase.dll contém uma referência a um "Processo de Inicialização" relacionado à API shell32.dll!ShellExecuteA
- O arquivo Runtime\versions\1.7.1\WindowsBase.dll contém uma referência a um "Processo de Inicialização" relacionado à API shell32.dll!ShellExecuteEx
- O arquivo Runtime\versions\1.7.1\PenImc_cor3.dll contém uma referência a um "Processo de Inicialização" relacionado à API shell32.dll!ShellExecuteExW
- O arquivo Runtime\versions\1.7.1\System.Windows.Forms.Primitives.dll contém uma referência a um "Processo de Inicialização" relacionado à API shell32.dll!ShellExecuteW
- O arquivo Runtime\versions\1.7.1\Sentry.dll contém uma referência a um "Processo de Inicialização" relacionado à API System.Diagnostics.Process.Start
- O arquivo Runtime\versions\1.7.1\PresentationUI.dll contém uma referência a um "Processo de Inicialização" relacionado à API System.Diagnostics.Process.Start
- O arquivo Runtime\versions\1.7.1\broker\Ralven.Windows.dll contém uma referência a um "Processo de Inicialização" relacionado à API System.Diagnostics.Process.Start
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

### Index 92: WARNING

- O arquivo Ralven.Launcher.exe não tem PerMonitorV2 incluído no manifesto nem chamadas para APIs de Reconhecimento de DPI, por exemplo: user32!SetProcessDpiAwarenessContext ou user32!SetThreadDpiAwarenessContext.
- O app VemryxInc.Ralven_1.7.1.0_x64__vk02nsxq3ddr6 não tem Reconhecimento de DPI.

## Local development signature and manual cleanup

Separate signed test copy: artifacts/store-validation/Ralven-Store-1.7.1.0-x64-development-signed.msix. SHA-256: 8D0E27526BE594C5FF01CDF8BC2DDE5FA9D9A28CFC6B39470F10424CA83C7E2D. This is not the upload file and not Microsoft Store's signature. Only the public CER was exported; no PFX/private-key file was generated or committed.

The temporary self-signed leaf expires 2026-10-01; thumbprint 17EC3FF5CA7AE60061BD9B137E9FD4A6FE0C5800. The own test package and development certificate remain installed/trusted locally. Automatic approval review rejected the combined uninstall/certificate cleanup operation with "blocked by policy" before execution. It was not retried. This does not affect the separate unsigned upload artifact.

Manual cleanup, after any further local testing:
1. Windows Settings > Apps > Installed apps > Ralven > Uninstall: identify this development package (VemryxInc.Ralven_1.7.1.0_x64__vk02nsxq3ddr6), not a separate Web installation. Preserve shared per-user Ralven data.
2. Run certmgr.msc. Under Personal > Certificates and Trusted Root Certification Authorities > Certificates, locate the exact development thumbprint above using Certificate > Details > Thumbprint; delete only that generated leaf/key/trust as offered by Windows. No other certificate is in scope.
3. Run certlm.msc with normal UAC. Under Trusted People > Certificates, locate the same exact thumbprint and delete only that leaf. Do not change system security settings or other trusted certificates.
4. Before installing the final Store-distributed product, remove this development installation/trust; the Microsoft distribution signature is separate.

## Source handoff

Branch: chore/store-production-identity-20260917; PR #223 supersedes #222. Initial CI rejected the former build/ branch prefix; the branch was replaced with an accepted chore/ prefix without changing policy, commit history or the validated upload bytes. PR #221 remains an overlapping prerequisite; resolve that integration dependency before merging. Source handoff documentation updates do not regenerate or change the MSIX.
