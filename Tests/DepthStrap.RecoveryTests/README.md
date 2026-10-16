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

The current Anti Api menu opens Complete wipe. Confirm with its checkbox and `WIPE ROBLOX`, then one action performs internal preflight and deletion; there is no create/save-preview control. UI fixtures cover `runas`, UAC cancellation, actual combined fixture deletion, duplicate clicks, consent consumption on hive-load failure and explicit retries. Browser tests create synthetic Chromium/Firefox databases and Firefox session recovery files. They preserve unrelated website cookies and saved tabs.

Elevated profile loading now prefers `RegLoadKey` with backup/restore privileges, because `RegLoadAppKey` requires uniform security descriptors across the hive and is unsuitable as the first choice for normal mixed-ACL Windows profiles. Application loading remains a fallback with READ/WRITE/DELETE access rather than ACL/owner modification rights. Loader-policy fixtures cover fallback, mount races, sharing retries, status diagnostics and identity-check failures. Native offline-copy tests exercise the handle loader; elevated real profile-loader behavior needs the separate administrator fixture.

Additional focused checks:

```powershell
dotnet Tests/DepthStrap.RecoveryTests/bin/Release/net10.0-windows/DepthStrap.RecoveryTests.dll --store-servicing-fixture
dotnet Tests/DepthStrap.RecoveryTests/bin/Release/net10.0-windows/DepthStrap.RecoveryTests.dll --large-uninstall-fixture artifacts/large-reset-fixture
```

`--admin-reset-fixture <fresh-admin-reset-fixture-GUID-directory>` is an optional elevated integration fixture. It saves only newly created synthetic registry keys to disposable hives and never invokes the production all-user inventory or a real uninstall. `--offline-hive-fixture <saved-synthetic-admin-fixture> <new-report-directory>` reuses copies of those saved synthetic hives to test production loading, deletion, flushing and persistence without another UAC request. Neither mode is part of normal app startup.

The administrator fixture also runs the production combined wipe against its disposable offline hives, a GUID-named machine-registry subtree, synthetic Roblox/DepthStrap files, a synthetic Studio project and a synthetic Chrome database. It checks original-executable retention, unrelated-data retention and persistence after reopening the hives. `selected-loader.json` records which loader actually worked; success through the application fallback does not verify global `RegLoadKey` support. Real application data and physical adapters are never reset by this fixture.
