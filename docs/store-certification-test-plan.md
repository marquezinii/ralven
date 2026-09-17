# Ralven MSIX certification test plan

English instructions for Microsoft certification engineers. This is the first
submission to request `allowElevation`, not a claim that Microsoft approved it.
Use VemryxInc.Ralven version 1.7.1.0 uploaded in Partner Center (PFN VemryxInc.Ralven_vk02nsxq3ddr6; Store ID 9N61X5M295M5). Record package identity/version/hash, Windows build,
hardware, action outcomes and before/after/restored state with each run.

## Prerequisites and safety

- Windows desktop x64, Windows 10 2004/build 19041 or newer; AC power for the power test.
- A consenting administrator who can approve standard Windows UAC prompts.
- A disposable test PC or VM. Profiles include additional listed user-level
  actions (including age-scoped temporary-file maintenance without full rollback).
  Review the entire preview before confirming; do not test on personal data.
- For a changed-state power test: a supported performance scheme, initially a
  different active scheme. Modern Standby/OEM policy can make performance schemes
  unavailable. Do not defeat an OEM policy to manufacture a test result.
- For HAGS: a real detected FiveM installation using GTAV **Legacy**, with both
  games stopped, and suitable GPU/driver hardware if verifying scheduler behavior.
  GTAV Enhanced is intentionally blocked. No game/account entitlement is supplied
  by Ralven. The registry post-condition and runtime GPU behavior are different tests.
- Complete first-run privacy consent. To match labels, Settings > Language > English.
  Local diagnosis/optimization requires no Ralven login. Account-specific flows
  require a test account, supplied privately in Partner Center if included in testing.

## Test 1 — normal launch and diagnosis

1. Install the submitted MSIX through the certification deployment flow.
2. Start **Ralven** from Start. Launcher starts the immutable bundled runtime.
3. Complete the privacy choice and open Overview, System and Optimize.
4. Expect no UAC for launch, navigation or diagnosis. Inspect process tokens:
   `Ralven.Launcher.exe` and `Ralven.exe` should run at medium integrity.
5. Expect detected data or an explicit unavailable state, never invented metrics.
6. In Settings, startup registration and the manual Web update command are disabled.
   No `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` entry should be created
   by this MSIX. There is no second mutable runtime or self-update staging in WindowsApps.

## Test 2 — performance power scheme, App → Broker → restore

1. Close competing Ralven Web/Store instances. Connect AC power.
2. Record `powercfg /getactivescheme` and `powercfg /list` before opening Ralven.
   These commands are observer tools, not commands accepted by the Broker.
3. Open **Optimize**, choose **Balanced**, inspect **Plan** and technical details.
   Locate **Activate high-performance power plan**. Record the complete action list.
4. Continue through the prepare/execute confirmation, approving the displayed plan.
5. Standard-user preflight is read-only. When a change is required, Windows displays
   UAC for `Ralven.Broker.exe`; accept it. The App itself does not elevate.
6. Wait for real progress and the result. Require the power action to be Applied/Changed
   (not just aggregate success), then record `powercfg /getactivescheme` again.
   Expect the selected performance scheme to differ from the recorded previous scheme.
7. Confirm Broker exits after its requested phase and the UI remains medium integrity.
8. Open **History**, locate this exact transaction and select **Undo**. Confirm the
   restoration and accept its UAC prompt. Administrative restore precedes local restore.
9. Require a successful power rollback outcome. Record `powercfg /getactivescheme`;
   its GUID must exactly match the initial GUID. Confirm Broker has exited again.
10. If battery, scheme unavailable, or already active caused Skipped/NoChange, record
    that result honestly; it does not establish an executed mutation/rollback test.

Power is not automatically restored on App exit. Do not switch schemes between apply
and restore: rollback intentionally preserves a newer user choice and will refuse to
overwrite it. If testing that guard, use a disposable PC and document manual cleanup
separately; do not call a refusal a successful restoration.

## Test 3 — explicit HAGS experiment, App → Broker → restore

1. Meet the Legacy prerequisites above. Record the Registry64 state of
   `HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\HwSchMode`: parent/value
   existence, type and exact value. Use Registry Editor or a read-only registry query.
2. Open **Games > FiveM**, then its specialized optimizer. Choose **Aggressive**.
3. Explicitly check **Test Hardware-Accelerated GPU Scheduling (HAGS)** in Plan.
   It defaults off for every new process and is not a stored startup preference.
4. Require that the HAGS action appears in the preview. Inspect its restart, risk,
   administrator and undo details and the other actions in the profile. Do not execute
   if detection reports Enhanced/unknown edition or if the plan is blocked.
5. Confirm the displayed plan. Accept normal Windows UAC for the Broker.
6. Require a changed HAGS per-action result and verify the registry post-condition:
   supported DWORD 2 becomes DWORD 1; DWORD 1 or an absent value becomes DWORD 2.
   Unsupported types/values must fail safely rather than be replaced.
7. Confirm Broker exits. Do not infer a real scheduler/FPS change from a registry write.
8. **History > Undo** for this transaction, confirm and accept UAC. Verify exact prior
   type/value/existence restoration, including deletion if originally absent.
9. For hardware-effect testing, reboot only by an explicit manual tester action after
   apply, observe Windows Graphics settings, then restore and reboot again. No automatic
   reboot is performed by Ralven. No performance gain is guaranteed.

## Test 4 — UAC denial

1. On a fresh eligible transaction, repeat Test 2, but deny the UAC prompt.
2. Require an unsuccessful/cancelled administrative phase and no administrative mutation.
   Already confirmed independent user-level actions may remain; inspect their outcomes
   and use the transaction history as needed. Do not expect a total transaction rollback.
3. No elevated Broker should remain alive and the application must remain usable.

## Test 5 — trust boundaries and blocked cases

1. There is no UI field accepting administrative command text, scripts, executable paths
   or arbitrary registry keys. Do not supply such data via a fabricated privileged tool.
2. Request normal restore only from the corresponding transaction in History. Missing
   or invalid privileged authority must fail closed; do not edit production receipts.
3. On battery power the performance action must not blindly apply. On unsupported OEM
   configurations require a truthful skipped/unavailable outcome.
4. Enhanced discovery must block the FiveM Legacy flow; it must never fall back to Legacy.
5. Ensure Defender, SmartScreen, UAC and firewall settings are unchanged by Ralven.

## Local evidence and limits

See [candidate validation report](store-submission-validation.md) for checks actually run.
An observer changing the power scheme directly is not an App → Broker test.
Secure-desktop UAC cannot be accepted autonomously using the available native UI tooling.
Unless the validation report records a full before/apply/verify/restore cycle,
the real privileged operation is **NÃO VALIDADO LOCALMENTE** and these are manual instructions.
Windows 10 host behavior is **NÃO VALIDADO EM HOST WINDOWS 10** unless recorded separately.
