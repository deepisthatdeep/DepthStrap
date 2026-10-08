using System.ComponentModel;

namespace Bloxstrap.Networking
{
    internal interface IWarpClient
    {
        Task EnsureReadyAsync(bool termsAccepted, IProgress<string>? progress, CancellationToken token);
        Task SetConnectedAsync(bool connected, CancellationToken token);
    }

    internal sealed class WarpClient : IWarpClient
    {
        // Official consumer download linked by Cloudflare's Windows installation documentation.
        internal const string DownloadUrl = "https://downloads.cloudflareclient.com/v1/download/windows/ga";
        internal static string CliPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Cloudflare", "Cloudflare WARP", "warp-cli.exe");
        public async Task EnsureReadyAsync(bool termsAccepted, IProgress<string>? progress, CancellationToken token)
        {
            if (!termsAccepted) throw new InvalidOperationException("Accept Cloudflare's terms and privacy policy in network setup to install and configure WARP.");
            if (!File.Exists(CliPath))
            {
                progress?.Report("Downloading Cloudflare's official WARP installation package…");
                string directory = Path.Combine(Paths.Base, "Packages", "Cloudflare");
                Directory.CreateDirectory(directory);
                string package = Path.Combine(directory, "Cloudflare-WARP.msi");
                string pending = package + ".download.msi";
                using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
                try
                {
                    using var response = await client.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token);
                    response.EnsureSuccessStatusCode();
                    if (response.RequestMessage?.RequestUri?.Scheme != "https") throw new InvalidDataException("WARP download was redirected to an insecure connection.");
                    await using (var input = await response.Content.ReadAsStreamAsync(token))
                    await using (var output = new FileStream(pending, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                    {
                        var buffer = new byte[81920]; long total = 0; int read;
                        while ((read = await input.ReadAsync(buffer, token)) > 0)
                        {
                            total += read;
                            if (total > 250_000_000) throw new InvalidDataException("WARP installation package exceeds the download limit.");
                            await output.WriteAsync(buffer.AsMemory(0, read), token);
                        }
                    }
                    await VerifyPublisherAsync(pending, token);
                    File.Move(pending, package, true);
                }
                finally { if (File.Exists(pending)) File.Delete(pending); }
                progress?.Report("Installing Cloudflare WARP. Complete the Windows permission and installer prompts…");
                using var sealedPackage = new FileStream(package, FileMode.Open, FileAccess.Read, FileShare.Read);
                await VerifyPublisherAsync(package, token);
                var install = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe")) { UseShellExecute = true, Verb = "runas" };
                install.ArgumentList.Add("/i"); install.ArgumentList.Add(package); install.ArgumentList.Add("/passive"); install.ArgumentList.Add("/norestart");
                // MSI is a Windows installer UI; it owns elevation, progress and cancellation.
                using var process = Process.Start(install) ?? throw new IOException("Windows Installer could not be started.");
                // Once MSI has started, keep its verified package and the operation gate
                // alive until Windows Installer finishes, even if the dialog is closed.
                await process.WaitForExitAsync();
                token.ThrowIfCancellationRequested();
                if (process.ExitCode is not (0 or 3010)) throw new IOException($"WARP installation did not complete (Windows Installer code {process.ExitCode}).");
                for (int i = 0; i < 20 && !File.Exists(CliPath); i++) await Task.Delay(500, token);
            }
            if (!File.Exists(CliPath)) throw new FileNotFoundException("Cloudflare WARP is not installed.");
            await VerifyPublisherAsync(CliPath, token);
            progress?.Report("Waiting for the Cloudflare WARP service…");
            bool serviceReady = false;
            for (int attempt = 0; attempt < 20; attempt++)
            {
                if ((await RunCliAsync(token, "status")).Code == 0) { serviceReady = true; break; }
                await Task.Delay(500, token);
            }
            if (!serviceReady) throw new IOException("Cloudflare WARP's service did not become ready. Open WARP and retry network setup.");
            progress?.Report("Preparing Cloudflare WARP registration…");
            var registration = await RunCliAsync(token, "registration", "show");
            if (registration.Code != 0) await RequireSuccessAsync(token, "registration", "new");
        }
        public async Task SetConnectedAsync(bool connected, CancellationToken token)
        {
            if (!App.Settings.Prop.CloudflareTermsAccepted) throw new InvalidOperationException("Complete Cloudflare consent in network setup first.");
            if (!File.Exists(CliPath)) throw new FileNotFoundException("Run network setup to install Cloudflare WARP first.");
            await VerifyPublisherAsync(CliPath, token);
            if (connected) await RequireSuccessAsync(token, "mode", "warp+doh");
            await RequireSuccessAsync(token, connected ? "connect" : "disconnect");
        }
        private static async Task RequireSuccessAsync(CancellationToken token, params string[] args)
        {
            var result = await RunCliAsync(token, args);
            if (result.Code != 0) throw new IOException("Cloudflare WARP could not complete " + string.Join(' ', args) + ". Open Cloudflare WARP to check registration or device policy.");
        }
        private static Task<(int Code, string Output)> RunCliAsync(CancellationToken token, params string[] args)
        {
            var start = new ProcessStartInfo(CliPath);
            // This is used only after the user accepts Cloudflare's terms in the setup dialog.
            start.ArgumentList.Add("--accept-tos");
            foreach (string arg in args) start.ArgumentList.Add(arg);
            return RunAsync(start, token);
        }
        internal static Task VerifyPublisherAsync(string path, CancellationToken token) => Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            CloudflareSignature.Verify(path);
        }, token);
        private static async Task<(int Code, string Output)> RunAsync(ProcessStartInfo start, CancellationToken token)
        {
            start.UseShellExecute = false; start.CreateNoWindow = true;
            start.RedirectStandardOutput = true; start.RedirectStandardError = true;
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
            budget.CancelAfter(TimeSpan.FromSeconds(25));
            using var process = Process.Start(start) ?? throw new IOException("Cloudflare command could not be started.");
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync(budget.Token); }
            catch { try { process.Kill(true); } catch { } throw; }
            // Output may contain registration identifiers; do not log or persist it.
            return (process.ExitCode, (await output) + (await error));
        }
    }
}
