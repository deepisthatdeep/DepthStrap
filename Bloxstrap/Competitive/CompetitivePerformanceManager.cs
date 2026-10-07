using Bloxstrap.Roblox;
using Bloxstrap.Integrations;
using System.Runtime.InteropServices;
using Bloxstrap.Enums;

namespace Bloxstrap.Competitive
{
    /// <summary>
    /// Applies competitive performance configuration to the Roblox process:
    /// CPU priority, execution-speed power throttling (HighQoS intent) and optional CPU affinity.
    ///
    /// Design rules:
    /// - Windows-only; every operation is best-effort (attempt, log result, continue).
    /// - Never holds a Roblox process handle open for long periods (Byfron/Hyperion concern):
    ///   short-lived Process objects / manual handles are disposed immediately.
    /// - A failure here must never prevent Roblox from launching.
    /// </summary>
    internal static class CompetitivePerformanceManager
    {
        private const string LOG_IDENT = "CompetitivePerformance";

        #region Power throttling P/Invoke (Windows-only)

        // Matches the Windows SDK (processthreadsapi.h) exactly: THREE ULONGs, 12 bytes.
        // Passing a 4-field/16-byte struct makes SetProcessInformation fail with ERROR_INVALID_PARAMETER (87).
        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_POWER_THROTTLING_STATE
        {
            public uint Version;      // PROCESS_POWER_THROTTLING_CURRENT_VERSION (1)
            public uint ControlMask;  // mechanisms the caller takes control of
            public uint StateMask;    // on/off state for each controlled mechanism
        }

        private const int ProcessPowerThrottling = 4; // PROCESS_INFORMATION_CLASS.ProcessPowerThrottling

        private const uint PROCESS_POWER_THROTTLING_VERSION_1 = 1;

        // Control/State mask bits (winnt.h):
        private const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

        [Flags]
        private enum ProcessAccess : uint
        {
            PROCESS_SET_INFORMATION = 0x0200,
            PROCESS_QUERY_LIMITED_INFORMATION = 0x1000,
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetProcessInformation(
            IntPtr hProcess,
            int processInformationClass,
            ref PROCESS_POWER_THROTTLING_STATE state,
            uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessInformation(
            IntPtr hProcess,
            int processInformationClass,
            ref PROCESS_POWER_THROTTLING_STATE state,
            uint size);

        #endregion

        /// <summary>
        /// Competitive performance is active when both the master switch and the performance section are on.
        /// </summary>
        public static bool IsEffective =>
            App.Settings.Prop.CompetitiveModeEnabled &&
            App.Settings.Prop.CompetitivePerformanceEnabled;

        public static ProcessPriorityOption GetEffectivePriority() =>
            IsEffective ? App.Settings.Prop.CompetitiveProcessPriority : App.Settings.Prop.SelectedProcessPriority;

        /// <summary>
        /// Applies the full competitive configuration to a running Roblox process.
        /// Safe to call repeatedly (idempotent). Never throws.
        /// </summary>
        public static void ApplyToProcess(int processId)
        {
            if (!OperatingSystem.IsWindows())
                return;

            try
            {
                using var process = Process.GetProcessById(processId);

                if (process.HasExited)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"PID {processId} already exited, skipping");
                    CompetitiveSessionLogger.Write($"Roblox PID={processId} exited before performance apply");
                    return;
                }

                // Never apply player optimizations to Studio or an unrelated/reused PID.
                if (!IsEffective || !process.ProcessName.Equals("RobloxPlayerBeta", StringComparison.OrdinalIgnoreCase))
                    return;

                ApplyPriority(process);
                ApplyPowerThrottling(process);
                ApplyAffinity(process);
            }
            catch (Exception ex)
            {
                // never let a performance failure take Roblox down with it
                App.Logger.WriteException($"{LOG_IDENT}::ApplyToProcess", ex);
                CompetitiveSessionLogger.Write($"Performance apply failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Sets the process priority class. Logs "from -> to" transitions.
        /// </summary>
        public static bool ApplyPriority(Process process)
        {
            try
            {
                var option = GetEffectivePriority();
                if (option == ProcessPriorityOption.Normal && !IsEffective)
                    return true; // legacy default: leave alone

                ProcessPriorityClass target = option switch
                {
                    ProcessPriorityOption.Low => ProcessPriorityClass.Idle,
                    ProcessPriorityOption.BelowNormal => ProcessPriorityClass.BelowNormal,
                    ProcessPriorityOption.Normal => ProcessPriorityClass.Normal,
                    ProcessPriorityOption.AboveNormal => ProcessPriorityClass.AboveNormal,
                    ProcessPriorityOption.High => ProcessPriorityClass.High,
                    ProcessPriorityOption.RealTime => ProcessPriorityClass.RealTime,
                    _ => ProcessPriorityClass.Normal
                };

                ProcessPriorityClass? current = null;
                try { current = process.PriorityClass; }
                catch { /* access denied reading state - keep going */ }

                if (current == target)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"PID {process.Id} priority already {target}, no change");
                    CompetitiveSessionLogger.Write($"Process priority: {target} (unchanged)");
                    return true;
                }

                process.PriorityClass = target;

                string from = current?.ToString() ?? "Unknown";
                App.Logger.WriteLine(LOG_IDENT, $"Roblox PID {process.Id} priority {from} -> {target}");
                CompetitiveSessionLogger.Write($"Process priority: {from} -> {target}");

                if (option == ProcessPriorityOption.RealTime)
                    App.Logger.WriteLine(LOG_IDENT, "NOTE: RealTime priority selected. This can interfere with Windows scheduling, audio and input.");

                return true;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException($"{LOG_IDENT}::ApplyPriority", ex);
                CompetitiveSessionLogger.Write($"Process priority apply failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Requests performance-oriented execution-speed power throttling (HighQoS intent) on the Roblox process.
        /// This is separate from CPU priority and can coexist with it.
        /// </summary>
        public static bool ApplyPowerThrottling(Process process)
        {
            if (!App.Settings.Prop.DisableRobloxPowerThrottling || !IsEffective)
                return true;

            IntPtr handle = IntPtr.Zero;
            try
            {
                handle = OpenProcess(
                    (uint)(ProcessAccess.PROCESS_SET_INFORMATION |
                           ProcessAccess.PROCESS_QUERY_LIMITED_INFORMATION),
                    false,
                    process.Id);

                if (handle == IntPtr.Zero)
                {
                    int err = Marshal.GetLastWin32Error();
                    App.Logger.WriteLine(LOG_IDENT, $"Power throttling: OpenProcess failed (Win32 error {err}), skipping");
                    CompetitiveSessionLogger.Write($"Power throttling: skipped (OpenProcess Win32 error {err})");
                    return false;
                }

                // read current state for diagnostics (best effort)
                string before = ProbeCurrentState(handle);

                // HighQoS intent: take control of the execution-speed mechanism and turn it OFF.
                // (StateMask bit off = high execution speed; on = EcoQoS-style throttling.)
                var state = new PROCESS_POWER_THROTTLING_STATE
                {
                    Version = PROCESS_POWER_THROTTLING_VERSION_1,
                    ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                    StateMask = 0
                };

                if (!SetProcessInformation(handle, ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>()))
                {
                    int err = Marshal.GetLastWin32Error();
                    string nowState = ProbeCurrentState(handle);
                    App.Logger.WriteLine(LOG_IDENT, $"Power throttling: SetProcessInformation failed (Win32 error {err}); current state: {nowState}");
                    CompetitiveSessionLogger.Write($"Power throttling: request failed (Win32 error {err}), state now: {nowState}");
                    return false;
                }

                App.Logger.WriteLine(LOG_IDENT, $"PID {process.Id} execution-speed throttling {before} -> high (performance)");
                CompetitiveSessionLogger.Write($"Power throttling: HighQoS requested ({before} -> high)");
                return true;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException($"{LOG_IDENT}::ApplyPowerThrottling", ex);
                CompetitiveSessionLogger.Write($"Power throttling apply failed: {ex.Message}");
                return false;
            }
            finally
            {
                if (handle != IntPtr.Zero)
                    CloseHandle(handle);
            }
        }

        /// <summary>
        /// Best-effort read of the process power-throttling state for diagnostics.
        /// Uses the documented, versioned 12-byte structure.
        /// </summary>
        private static string ProbeCurrentState(IntPtr handle)
        {
            try
            {
                var cur = new PROCESS_POWER_THROTTLING_STATE { Version = PROCESS_POWER_THROTTLING_VERSION_1 };
                if (GetProcessInformation(handle, ProcessPowerThrottling, ref cur, (uint)Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>()) &&
                    cur.Version == PROCESS_POWER_THROTTLING_VERSION_1)
                {
                    if ((cur.ControlMask & PROCESS_POWER_THROTTLING_EXECUTION_SPEED) != 0)
                        return (cur.StateMask & PROCESS_POWER_THROTTLING_EXECUTION_SPEED) != 0 ? "throttled" : "high";
                    return "system-managed";
                }

                return $"unknown (GET Win32 error {Marshal.GetLastWin32Error()})";
            }
            catch
            {
                return "unknown";
            }
        }

        /// <summary>
        /// Applies custom CPU affinity only when explicitly enabled. Default is system-managed:
        /// Windows knows the machine topology (P/E cores, CCDs, processor groups) better than we do.
        /// </summary>
        public static void ApplyAffinity(Process process)
        {
            try
            {
                if (!App.Settings.Prop.CustomCpuAffinityEnabled || App.Settings.Prop.CustomCpuAffinityMask == 0)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"PID {process.Id} CPU affinity left system-managed");
                    CompetitiveSessionLogger.Write("CPU affinity: automatic (system-managed)");
                    return;
                }

                if (Environment.ProcessorCount > 64)
                    App.Logger.WriteLine(LOG_IDENT, "NOTE: more than 64 logical processors detected; the affinity mask only covers processor group 0.");

                nint mask = unchecked((nint)App.Settings.Prop.CustomCpuAffinityMask);
                process.ProcessorAffinity = mask;

                App.Logger.WriteLine(LOG_IDENT, $"PID {process.Id} CPU affinity set to mask 0x{mask:X}");
                CompetitiveSessionLogger.Write($"CPU affinity: custom mask 0x{(ulong)mask:X}");
            }
            catch (Exception ex)
            {
                App.Logger.WriteException($"{LOG_IDENT}::ApplyAffinity", ex);
                CompetitiveSessionLogger.Write($"CPU affinity apply failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Single delayed verification pass (~PerformanceReapplyDelayMs after launch).
        /// Roblox startup or another system component could alter process state after initial launch.
        /// Not an infinite polling loop by design.
        /// </summary>
        public static async Task ReapplyAfterDelayAsync(int processId, CancellationToken token)
        {
            // Bounded schedule of short-lived passes (absolute ms after launch):
            // the user-configured delay plus ~20s and ~60s. Roblox can re-assert its own
            // priority class during startup AND later when entering a game session.
            // Still not a polling loop: three passes, then done.
            int first = Math.Clamp(App.Settings.Prop.PerformanceReapplyDelayMs, 2000, 5000);
            var times = new[] { first, 20_000, 60_000 }.Distinct().OrderBy(ms => ms).ToArray();

            long elapsed = 0;
            int pass = 0;
            try
            {
                DateTime started;
                using (var initial = Process.GetProcessById(processId)) started = initial.StartTime;
                foreach (int t in times)
                {
                    long waitMs = t - elapsed;
                    if (waitMs > 0)
                        await Task.Delay((int)waitMs, token);
                    elapsed = t;
                    pass++;

                    using var process = Process.GetProcessById(processId);
                    if (process.HasExited || process.StartTime != started || !IsEffective)
                    {
                        CompetitiveSessionLogger.Write($"Priority re-apply: PID exited before pass {pass}");
                        return;
                    }

                    ApplyToProcess(processId);

                    // explicit verification line for the session log
                    try
                    {
                        string current = process.PriorityClass.ToString();
                        CompetitiveSessionLogger.Write(pass == 1
                            ? $"Priority verification: {current}"
                            : $"Priority re-check (pass {pass}): {current}");
                    }
                    catch { /* best effort */ }
                }
            }
            catch (OperationCanceledException)
            {
                // normal shutdown path, nothing to do
            }
            catch (Exception ex)
            {
                App.Logger.WriteException($"{LOG_IDENT}::Reapply", ex);
                CompetitiveSessionLogger.Write($"Priority re-apply failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Pre-launch application of competitive FPS cap + graphics preset via Roblox GlobalBasicSettings,
        /// plus the best-effort FastFlag rendering layer. Called right before Roblox starts.
        /// Never throws; a failure here must not block launch.
        /// </summary>
        public static void ApplyPreLaunchSettings()
        {
            if (!IsEffective)
            {
                CompetitiveSettingsBackup.Restore();
                return;
            }

            if (!CompetitiveSettingsBackup.Restore()) return;
            MonitorRefreshRateService.ApplyDetectedCap();
            bool globalApplied = ApplyGlobalBasicSettings();
            ApplyFastFlagLayer();
            if (App.Settings.Prop.CompetitiveAggressiveRendering && globalApplied)
                CompetitiveSettingsBackup.LockQuality();
        }

        private static bool ApplyGlobalBasicSettings()
        {
            const string LOG_IDENT2 = "CompetitivePerformance.GBS";

            try
            {
                if (CompetitiveSettingsBackup.IsQualityLockInUse())
                {
                    App.Logger.WriteLine(LOG_IDENT2, "Shared graphics settings remain locked for active clients; changes apply after all Players close.");
                    return false;
                }
                App.GlobalSettings.Load();
                if (App.GlobalSettings.LastLoadFailed) return false;

                if (!App.GlobalSettings.Loaded)
                {
                    // Seed only the two supported settings; Roblox supplies its other defaults.
                    Directory.CreateDirectory(Paths.Roblox);
                    App.GlobalSettings.Document = System.Xml.Linq.XDocument.Parse(
                        "<roblox version=\"4\"><External>null</External><External>nil</External><Item class=\"UserGameSettings\" referent=\"RBX0\"><Properties><int name=\"FramerateCap\">0</int><token name=\"SavedQualityLevel\">3</token></Properties></Item></roblox>");
                    App.GlobalSettings.Loaded = true;
                    App.GlobalSettings.Save();
                    if (!App.GlobalSettings.LastSaveSucceeded) return false;
                }

                bool changed = false;

                // --- FPS cap (the real modern setting, not DFIntTaskSchedulerTargetFps) ---
                int fpsCap = Math.Clamp(App.Settings.Prop.CompetitiveFpsCap, 0, 1000);
                string? currentCap = App.GlobalSettings.GetPreset("Rendering.FramerateCap");

                if (!int.TryParse(currentCap, out int parsedCap) || parsedCap != fpsCap)
                {
                    CompetitiveSettingsBackup.SetGlobal("Rendering.FramerateCap", fpsCap.ToString());
                    changed = true;
                    CompetitiveSessionLogger.Write($"FPS cap: {currentCap ?? "unset"} -> {fpsCap}");
                }

                // --- Low graphics preset (lower only, never raise) ---
                if (App.Settings.Prop.CompetitiveLowGraphicsPreset || App.Settings.Prop.CompetitiveAggressiveRendering)
                {
                    int target = App.Settings.Prop.CompetitiveAggressiveRendering ? 3 : Math.Clamp(App.Settings.Prop.CompetitiveGraphicsQualityLevel, 0, 21);
                    string? currentQ = App.GlobalSettings.GetPreset("Rendering.SavedQualityLevel");

                    if (!int.TryParse(currentQ, out int parsedQ) || parsedQ > target || (App.Settings.Prop.CompetitiveAggressiveRendering && parsedQ != target))
                    {
                        CompetitiveSettingsBackup.SetGlobal("Rendering.SavedQualityLevel", target.ToString());
                        changed = true;
                        CompetitiveSessionLogger.Write($"Graphics quality: {currentQ ?? "unset"} -> {target}");
                    }
                }

                if (changed)
                {
                    App.GlobalSettings.Save();
                    if (!App.GlobalSettings.LastSaveSucceeded) return false;
                    App.Logger.WriteLine(LOG_IDENT2, "GlobalBasicSettings updated and saved");
                }
                return true;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException($"{LOG_IDENT}::ApplyGlobalBasicSettings", ex);
                CompetitiveSessionLogger.Write($"GBS apply failed: {ex.Message}");
                return false;
            }
        }

        private static void ApplyFastFlagLayer()
        {
            const string LOG_IDENT2 = "CompetitivePerformance.FF";

            try
            {
                // FastFlags are a supplemental best-effort layer. If the user disabled the FF manager,
                // everything else (priority, region monitor, GBS FPS cap) still works.
                if (!App.Settings.Prop.UseFastFlagManager)
                {
                    App.Logger.WriteLine(LOG_IDENT2, "FastFlag manager disabled; skipping rendering flag layer");
                    return;
                }

                // MSAA 1x (best effort - depends on current Roblox allowlist)
                bool aggressive = App.Settings.Prop.CompetitiveAggressiveRendering;
                CompetitiveSettingsBackup.SetFlag("Rendering.MSAA1", aggressive || App.Settings.Prop.CompetitiveMSAA1x ? "1" : null);
                CompetitiveSettingsBackup.SetFlag("Rendering.TextureQuality.OverrideEnabled", aggressive ? "True" : null);
                CompetitiveSettingsBackup.SetFlag("Rendering.TextureQuality.Level", aggressive ? "0" : null);
                CompetitiveSettingsBackup.SetFlag("Rendering.PauseVoxerlizer", aggressive ? "True" : null);
                CompetitiveSettingsBackup.SetFlag("Graphic.GraySky", aggressive ? "True" : null);
                CompetitiveSettingsBackup.SetFlag("Rendering.DisableScaling", aggressive ? "False" : null);
                CompetitiveSettingsBackup.SetFlag("Rendering.ManualFullscreen", aggressive ? "False" : null);
                // Quality stays at the requested GBS level; FRM=1 would override the quality-3 profile.
                CompetitiveSettingsBackup.SetFlag("Rendering.FrmQuality", null);

                // Remove grass (same values the FastFlags page uses)
                string? grass = aggressive || App.Settings.Prop.CompetitiveDisableGrass ? "0" : null;
                CompetitiveSettingsBackup.SetFlag("Rendering.RemoveGrass1", grass);
                CompetitiveSettingsBackup.SetFlag("Rendering.RemoveGrass2", grass);
                CompetitiveSettingsBackup.SetFlag("Rendering.RemoveGrass3", grass);

                // Low-poly meshes (optional, more aggressive)
                if (aggressive || App.Settings.Prop.CompetitiveLowPolyMeshes)
                {
                    int level = aggressive ? 0 : 5;
                    int[] baseValues = { 2000, 1500, 1000, 500 };
                    string[] levels = new string[4];
                    for (int i = 0; i < 4; i++)
                        levels[i] = ((baseValues[i] * level) / 9).ToString();

                    CompetitiveSettingsBackup.SetFlag("Rendering.LowPolyMeshes1", levels[0]);
                    CompetitiveSettingsBackup.SetFlag("Rendering.LowPolyMeshes2", levels[1]);
                    CompetitiveSettingsBackup.SetFlag("Rendering.LowPolyMeshes3", levels[2]);
                    CompetitiveSettingsBackup.SetFlag("Rendering.LowPolyMeshes4", levels[3]);
                }
                else
                {
                    for (int i = 1; i <= 4; i++)
                        CompetitiveSettingsBackup.SetFlag($"Rendering.LowPolyMeshes{i}", null);
                }

                // Renderer preference (best effort - flags may no longer be honored by Roblox)
                CompetitiveSettingsBackup.SetFlag("Rendering.Mode.OpenGL", null);
                switch (App.Settings.Prop.CompetitiveRenderer)
                {
                    case CompetitiveRendererOption.Direct3D11:
                        CompetitiveSettingsBackup.SetFlag("Rendering.Mode.D3D11", "True");
                        CompetitiveSettingsBackup.SetFlag("Rendering.Mode.Vulkan", null);
                        break;

                    case CompetitiveRendererOption.Vulkan:
                        CompetitiveSettingsBackup.SetFlag("Rendering.Mode.Vulkan", "True");
                        CompetitiveSettingsBackup.SetFlag("Rendering.Mode.D3D11", null);
                        break;

                    default: // Automatic - let Roblox decide
                        CompetitiveSettingsBackup.SetFlag("Rendering.Mode.D3D11", null);
                        CompetitiveSettingsBackup.SetFlag("Rendering.Mode.Vulkan", null);
                        break;
                }

                App.FastFlags.Save();
                App.Logger.WriteLine(LOG_IDENT2, "FastFlag rendering layer applied");
            }
            catch (Exception ex)
            {
                App.Logger.WriteException($"{LOG_IDENT}::ApplyFastFlagLayer", ex);
                CompetitiveSessionLogger.Write($"FastFlag layer apply failed: {ex.Message}");
            }
        }
    }
}
