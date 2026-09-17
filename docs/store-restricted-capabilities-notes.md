# Restricted Capabilities notes — paste-ready English

Reserved product: VemryxInc.Ralven (Vemryx Inc.), Store ID 9N61X5M295M5. These notes describe the certification candidate, not an approval already received.
The official MSIX guidance does not document a character limit for this field.
Use the main version where it fits; otherwise use the short version. Do not truncate
security or scope statements. Paste the separate runFullTrust explanation if the
portal requests a justification for each capability.

## allowElevation — main version

Ralven is a Windows diagnostics and maintenance application that lets users review, apply, verify and undo predefined Windows configuration changes. We request allowElevation exclusively for Ralven.Broker.exe, an existing, short-lived administrative helper. Ralven.App (Ralven.exe) and Ralven.Launcher.exe have asInvoker manifests and run at medium integrity. They do not require administrator privileges for launch, navigation or diagnosis.

The application starts the Broker with the Windows runas verb only after the user explicitly confirms a reviewed optimization plan containing an administrative action, or requests restoration of such an action from History. Windows displays its normal UAC consent or credential prompt. Rejecting that prompt prevents the administrative phase. No service, driver, scheduled task, automatic elevation bypass or permanently elevated background process is installed.

The administrative optimization allowlist contains exactly two actions:
1. Activate a performance power scheme while connected to AC power. Ralven records the previously active scheme, uses predefined powercfg operations, verifies the resulting active scheme and can restore the previous scheme. The change remains active until restored; it is not automatically undone simply by closing Ralven. Restoration refuses to overwrite a newer power-scheme choice made after the action.
2. An explicit, optional Hardware-Accelerated GPU Scheduling compatibility experiment. This changes only the HwSchMode value under HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers. Ralven preserves the previous value, type and existence, rejects unsupported states, verifies the written state and supports restoration. Windows must be restarted for the scheduling change or its restoration to take effect. Ralven does not promise performance gains or restart Windows automatically.

These actions support the product's central workflow of applying controlled, reversible system settings rather than merely displaying advice. The power action provides a consistent, audited administrative transaction and authoritative restoration receipt, including on systems where permissions prevent a standard-user change. The HKLM HAGS experiment requires administrative access. Removing the Broker would eliminate these existing administrative apply/restore workflows, while elevating the entire application would violate least privilege.

The Broker independently revalidates typed, versioned requests and reconstructs the approved plan against the action catalog. Its session-bound IPC restricts access to the initiating user. It does not accept executable paths, command strings, scripts or arbitrary registry writes from the user. Executable integrity is checked before launch; administrative receipts and transaction snapshots support validated rollback. The helper exits after the requested phase completes. It also writes narrowly scoped administrative rollback authority under Ralven's own registry location; this is bookkeeping, not an additional optimization action.

There is no general-purpose shell, administrative scripting engine, downloaded executable payload, or arbitrary command execution through this helper. Ralven never disables or bypasses UAC, Defender, SmartScreen, firewall or anti-cheat protections. The MSIX candidate disables its own updater and startup registration; Microsoft Store owns package updates. We previously contacted Microsoft about this capability and were directed to submit the complete product for capability evaluation during certification. Please evaluate allowElevation for this narrowly scoped Broker usage.

## allowElevation — short version

Please approve allowElevation only for Ralven.Broker.exe, a short-lived, allowlisted administrative helper. Ralven.exe and Ralven.Launcher.exe remain asInvoker/mediumIL; normal launch and diagnosis request no UAC. Only a user-confirmed plan or History restoration starts the Broker through runas and normal Windows UAC. Denying UAC prevents the administrative phase.

There are exactly two administrative optimization actions: activating an AC-power performance scheme with recorded previous scheme, verification and guarded restoration; and an explicit HAGS experiment changing only HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\HwSchMode, preserving type/existence/value, verifying and restoring it. HAGS apply/restore requires a Windows restart; power remains changed until restored. Broker-owned registry receipts authorize rollback.

The Broker revalidates typed requests, session IPC and reconstructed plans; it accepts no arbitrary commands, paths, scripts or registry operations and exits after completion. Removing it removes these central controlled apply/restore workflows; elevating the whole App would violate least privilege. No UAC/security bypass, service, driver or scheduled task exists. The Store candidate disables its own updater/startup registration. Microsoft directed us to request the capability during product certification.

## runFullTrust

Ralven is an existing self-contained .NET/WPF Windows desktop application. Its Launcher and UI run as the current user at medium integrity, outside AppContainer, for desktop diagnostics and predefined user-confirmed maintenance. runFullTrust enables this packaged classic desktop architecture; it does not itself elevate the UI. Administrative execution is isolated to Ralven.Broker.exe and separately justified under allowElevation.

Source: [MSIX submission options and restricted capability review](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/manage-submission-options).
