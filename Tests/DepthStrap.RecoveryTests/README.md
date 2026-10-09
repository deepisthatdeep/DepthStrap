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
