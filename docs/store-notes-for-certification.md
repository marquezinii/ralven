# Notes for certification — paste-ready English

Paste the text below in Submission options > Notes for certification. The MSIX
documentation consulted does not specify a limit; this text is deliberately compact.
If the portal imposes a limit, retain the test-plan URL and the two action paths.
Provide test-account credentials only in the private Partner Center field if testing
account-specific features; never put credentials in this repository.

---

First MSIX submission, 2026-09-17. See allowElevation in Restricted capabilities notes. Ralven.Launcher.exe starts the bundled Ralven.exe at medium integrity; only the short-lived Ralven.Broker.exe elevates. Launch and diagnosis must not request UAC. Complete the first-run privacy choice; local optimization does not require a Ralven account.

Select English in Settings. On an AC-powered test PC, open Optimize, select Balanced, inspect the plan/details and confirm execution. The performance power-plan action records the previous scheme and requests normal Windows UAC when it needs the Broker. Accept UAC, check the action result and active scheme, then use History > Undo on that transaction; restoration also requests UAC. Already active/unavailable schemes or battery power may produce no change/Skipped; use a PC with an available performance scheme for the changed-state test.

For HAGS (requires detected FiveM/GTAV Legacy, games stopped): Games > FiveM > specialized optimizer > Aggressive > check Test Hardware-Accelerated GPU Scheduling (HAGS), review the plan and confirm execution. Normal UAC precedes administrative execution. Check the action result and HwSchMode under HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers. Use History > Undo to restore its previous value/type/existence. Restart is needed for HAGS apply/restore to affect the GPU scheduler; no automatic restart occurs. Review all profile actions on a disposable PC.

Broker validates typed/session-bound requests, verifies outcomes, records rollback authority and exits. No free-form shell, script or arbitrary command interface exists. Denying UAC prevents administrative changes. The candidate disables Web self-update and startup registration; Store owns updates. Pro/AI are marked unavailable and have no purchase flow.

Detailed reproducible tests and limitations:
https://github.com/marquezinii/ralven/blob/084424a81b6f92313399b88f16eae1cccd6f8b16/docs/store-certification-test-plan.md
