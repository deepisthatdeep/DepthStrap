using Bloxstrap.Models.Manifest;
using System.Runtime.ExceptionServices;

namespace Bloxstrap.Utility
{
    internal static class InstallPackagePipeline
    {
        internal static long PackedBytes(IEnumerable<Package> packages) => packages.Sum(x => (long)x.PackedSize);
        internal static long RequiredBytes(IEnumerable<Package> packages) => packages.Sum(x => checked((long)x.Size + x.PackedSize));
        internal static long RequiredFreeBytes(IEnumerable<Package> packages, Func<Package, long> cachedSize)
            => packages.Sum(x => checked((long)x.Size + Math.Max(0, x.PackedSize - Math.Max(0, cachedSize(x)))));
        internal static long CachedSize(Package package)
        {
            try { var file = new FileInfo(package.DownloadPath); return file.Exists ? file.Length : 0; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
        }
        internal static int EstimatedKilobytes(IEnumerable<Package> packages) => (int)Math.Min(int.MaxValue, RequiredBytes(packages) / 1024);

        internal static void BeginRecovery(JsonManager<Bloxstrap.Models.Persistable.DistributionState> distribution)
        {
            bool previous = distribution.Prop.InstallationPending;
            distribution.Prop.InstallationPending = true;
            if (distribution.TrySave()) return;
            distribution.Prop.InstallationPending = previous;
            throw new IOException("Could not save installation recovery state. The existing build was preserved.");
        }

        internal static void CompleteRecovery(JsonManager<Bloxstrap.Models.Persistable.DistributionState> distribution)
        {
            bool previous = App.State.Prop.ForceReinstall;
            App.State.Prop.ForceReinstall = false;
            if (!App.State.TrySave())
            {
                App.State.Prop.ForceReinstall = previous;
                throw new IOException("Could not finish saving installation state. Installation remains eligible for retry.");
            }
            distribution.Prop.InstallationPending = false;
            if (distribution.TrySave()) return;
            distribution.Prop.InstallationPending = true;
            throw new IOException("Could not save the installed Roblox build. Installation remains eligible for retry.");
        }

        internal static async Task RunAsync(IEnumerable<Package> packages, Func<Package, Task> download,
            Action<Package> extract, CancellationToken token, Action? downloadsFinished = null)
        {
            using var slots = new SemaphoreSlim(2);
            var extractions = new List<Task>();
            Exception? failure = null;
            try
            {
                foreach (var package in packages)
                {
                    token.ThrowIfCancellationRequested();
                    // Surface extraction failures before starting another transfer.
                    foreach (var completed in extractions.Where(x => x.IsCompleted)) await completed;
                    await download(package);
                    token.ThrowIfCancellationRequested();
                    if (package.Name == "WebView2RuntimeInstaller.zip") continue;
                    await slots.WaitAsync(token);
                    extractions.Add(Task.Run(() =>
                    {
                        try { token.ThrowIfCancellationRequested(); extract(package); }
                        finally { slots.Release(); }
                    }));
                }
                downloadsFinished?.Invoke();
            }
            catch (Exception ex) { failure = ex; }
            try { await Task.WhenAll(extractions); }
            catch (Exception ex)
            {
                if (failure is null) failure = ex;
                else App.Logger.WriteException("InstallPackagePipeline::Extraction", ex);
            }
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
            token.ThrowIfCancellationRequested();
        }
    }
}
