namespace Bloxstrap
{
    internal static class MultiInstanceWatcher
    {
        internal const string ReadyEventName = "DepthStrap-MultiInstanceReady";
        internal const string FailedEventName = "DepthStrap-MultiInstanceFailed";

        internal sealed class Reservation : IDisposable
        {
            private readonly Mutex _mutex;
            internal bool Owned { get; private set; }
            internal Reservation(string name)
            {
                _mutex = new Mutex(false, name);
                try { TryOwn(); }
                catch { _mutex.Dispose(); throw; }
            }
            internal void TryOwn()
            {
                if (Owned) return;
                try { Owned = _mutex.WaitOne(0); }
                catch (AbandonedMutexException) { Owned = true; }
            }
            public void Dispose()
            {
                if (Owned) _mutex.ReleaseMutex();
                _mutex.Dispose();
            }
        }

        internal static async Task<bool> WaitForReadyAsync(Func<bool> ready, Func<bool> failed, Func<bool> exited,
            TimeSpan timeout, CancellationToken token)
        {
            var elapsed = Stopwatch.StartNew();
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (ready()) return true;
                if (failed() || exited()) return ready();
                if (elapsed.Elapsed >= timeout) return false;
                await Task.Delay(100, token);
            }
        }
        public static bool IsReady()
        {
            try { using var ready = EventWaitHandle.OpenExisting(ReadyEventName); return ready.WaitOne(0); }
            catch (WaitHandleCannotBeOpenedException) { return false; }
        }

        internal static Mutex Acquire(string name)
        {
            var mutex = new Mutex(false, name);
            bool acquired;
            try { acquired = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (acquired) return mutex;
            mutex.Dispose();
            throw new InvalidOperationException("Close all Roblox clients before enabling multi-client launching.");
        }

        public static void Run()
            => RunForObjects(ReadyEventName, FailedEventName, "DepthStrap-MultiInstanceWatcher", "ROBLOX_singletonMutex", "ROBLOX_singletonEvent",
                CountPlayers, () => Utilities.DoesMutexExist("Bloxstrap-Bootstrapper"));

        private static int CountPlayers()
        {
            try
            {
                var players = Process.GetProcessesByName("RobloxPlayerBeta");
                try { return players.Length; }
                finally { foreach (var player in players) player.Dispose(); }
            }
            catch (Exception ex) { App.Logger.WriteException("MultiInstanceWatcher", ex); return -1; }
        }

        internal static void RunForObjects(string readyName, string failedName, string guardName, string singletonName, string singletonEventName,
            Func<int> playerCount, Func<bool> launching)
        {
            // Ownership stays on this thread for the lifetime of every launched Player.
            Reservation? singleton = null, singletonEvent = null;
            using var guard = new Mutex(false, guardName);
            bool ownsGuard;
            try { ownsGuard = guard.WaitOne(0); }
            catch (AbandonedMutexException) { ownsGuard = true; }
            if (!ownsGuard) return;
            using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, readyName);
            using var failed = new EventWaitHandle(false, EventResetMode.ManualReset, failedName);
            ready.Reset(); failed.Reset();
            try
            {
                singleton = new Reservation(singletonName);
                singletonEvent = new Reservation(singletonEventName);
                App.Logger.WriteLine("MultiInstanceWatcher", singleton.Owned && singletonEvent.Owned
                    ? "Reserved both singleton mutexes."
                    : "Sharing existing singleton mutex reservations with another launcher; retaining handles and taking ownership when released.");
                ready.Set();
                var elapsed = Stopwatch.StartNew(); bool seenPlayer = false;
                while (true)
                {
                    singleton.TryOwn(); singletonEvent.TryOwn();
                    int count = playerCount();
                    seenPlayer |= count > 0;
                    if (Roblox.MultiInstanceLifetime.ShouldStop(elapsed.Elapsed, seenPlayer, count,
                        launching())) break;
                    Thread.Sleep(500);
                }
            }
            catch (Exception ex) { failed.Set(); App.Logger.WriteException("MultiInstanceWatcher", ex); }
            finally
            {
                ready.Reset();
                singletonEvent?.Dispose();
                singleton?.Dispose();
                guard.ReleaseMutex();
            }
        }
    }
}
