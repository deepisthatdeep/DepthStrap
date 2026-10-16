# Public recovery checks

These tests reference the actual DepthStrap application assembly. They create isolated synthetic file and registry fixtures and simulated adapters. They do not delete real Roblox data or change network hardware.

From the repository root:

```powershell
dotnet build Tests/DepthStrap.RecoveryTests/DepthStrap.RecoveryTests.csproj -c Release
dotnet Tests/DepthStrap.RecoveryTests/bin/Release/net10.0-windows/DepthStrap.RecoveryTests.dll --self-test
dotnet Tests/DepthStrap.RecoveryTests/bin/Release/net10.0-windows/DepthStrap.RecoveryTests.dll --ui artifacts/recovery-ui
dotnet Tests/DepthStrap.RecoveryTests/bin/Release/net10.0-windows/DepthStrap.RecoveryTests.dll --socket
powershell -NoProfile -ExecutionPolicy Bypass -File Tests/DepthStrap.RecoveryTests/AdapterDriverFixture.ps1 -Source Bloxstrap/Recovery/AdapterDriver.ps1
```

The UI check renders navigation, reset confirmations and adapter dialogs using fixtures. No UAC action is invoked. A live adapter change, reboot, or real signed-in reset is a separate manual validation task.

The current Anti Api menu has one complete-reset action for files, registry and Roblox browser cookies. UI fixtures cover its `runas` request and UAC-cancellation handling without launching a real reset. Browser tests create synthetic Chromium/Firefox databases and Firefox session recovery files. They preserve unrelated website cookies and saved tabs.

Additional focused checks:

```powershell
dotnet Tests/DepthStrap.RecoveryTests/bin/Release/net10.0-windows/DepthStrap.RecoveryTests.dll --store-servicing-fixture
dotnet Tests/DepthStrap.RecoveryTests/bin/Release/net10.0-windows/DepthStrap.RecoveryTests.dll --large-uninstall-fixture artifacts/large-reset-fixture
```

`--admin-reset-fixture <fresh-admin-reset-fixture-GUID-directory>` is an optional elevated integration fixture. It saves only newly created synthetic registry keys to disposable hives and never invokes the production all-user inventory or a real uninstall. `--offline-hive-fixture <saved-synthetic-admin-fixture> <new-report-directory>` reuses copies of those saved synthetic hives to test production loading, deletion, flushing and persistence without another UAC request. Neither mode is part of normal app startup.
