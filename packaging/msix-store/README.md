# Minimum Viable Store Submission

Reuses the integrated POC (#218) and existing portable payload/hardening. This is
a separate manual packaging entry point: no release workflow, Inno installer or Web
distribution is changed. Normal activation is real, not synthetic demo mode.

Use exact Partner Center identity values; see
[manual submission checklist](../../docs/microsoft-store-submission-checklist.md).

```powershell
./packaging/msix-store/Build-StoreMsix.ps1 `
  -PackageName '<reserved Package/Identity/Name>' `
  -Publisher '<reserved Package/Identity/Publisher>' `
  -PublisherDisplayName '<reserved Package/Properties/PublisherDisplayName>' `
  -Harden
./packaging/msix-store/Test-StoreLayout.ps1
```

`-CertificateThumbprint` optionally signs for local tests, using a development
certificate matching Publisher. `-SkipPortableBuild` reuses the exact already-built
payload; it does not prove hardening. Use it only when its provenance is known.
Outputs: `artifacts/msix-store/Ralven-Store-<version>-x64.msix` and resolved
`layout/AppxManifest.xml`. Template placeholders are deliberately invalid until
replaced; the build does not invent production identity. Package revision is zero.

Only `runFullTrust` and `allowElevation` are declared. No StartupTask, service,
driver or additional capability is requested. App/Launcher remain mediumIL. The
Launcher reads the bundled runtime pointer without invoking Web recovery/activation.
App does not create its Web updater services or write update-health receipts when
packaged. Startup registration is disabled and protected at the registry boundary.
The existing HAGS opt-in is exposed only in the packaged FiveM Legacy optimizer;
the Web plan remains unchanged. Administrative actions and Broker contracts do not expand.

Run WACK from a consenting administrator's test session:

```powershell
& 'C:/Program Files (x86)/Windows Kits/10/App Certification Kit/appcert.exe' test `
  -appxpackagepath '<signed local candidate MSIX>' `
  -reportoutputpath '<absolute report.xml>'
```

Keep raw XML/HTML outside Git (`artifacts`). No unattended UAC consent or workaround
is supplied. The validation report distinguishes a completed WACK result from an
infrastructure/permission failure. Real privileged tests are in the
[certification test plan](../../docs/store-certification-test-plan.md).

PowerShell can return before this native GUI-subsystem executable finishes. Wait
for the appcert process to exit and the report to exist; inspect `OVERALL_RESULT`,
`PARTIAL_RUN` and each test result. A launcher exit code alone is not a WACK pass.

After capability approval: implement Store-owned update UX, optional StartupTask,
distribution/data-coexistence policy, real Win10 testing and automated packaging
as a separate phase. Keep Web's signed updater intact. Do not create a second mutable
Store runtime. Do not publish this first submission automatically.
