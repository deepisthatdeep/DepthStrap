using Bloxstrap.Models.Manifest;

namespace Bloxstrap.Utility
{
    internal static class VerifiedPackageCache
    {
        internal static void Verify(Stream stream, Package package)
        {
            package.Validate();
            if (stream.Length != package.PackedSize ||
                !string.Equals(MD5Hash.FromStream(stream), package.Signature, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The package does not match the Roblox manifest.");
        }

        internal static async Task<bool> TryCopyAsync(string source, Package package, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!File.Exists(source)) return false;
            Directory.CreateDirectory(Paths.Downloads);
            string temp = package.DownloadPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                {
                    if (input.Length != package.PackedSize)
                        throw new InvalidDataException("The cached package does not match its manifest size.");
                    await input.CopyToAsync(output, token);
                    token.ThrowIfCancellationRequested();
                    Verify(output, package);
                }
                token.ThrowIfCancellationRequested();
                File.Move(temp, package.DownloadPath, true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            { App.Logger.WriteException("VerifiedPackageCache::Copy", ex); return false; }
            finally { Cleanup(temp); }
        }

        internal static async Task DownloadAsync(System.Net.Http.HttpClient client, string url, Package package,
            CancellationToken token, Action<int>? progress = null)
        {
            token.ThrowIfCancellationRequested();
            string destination = package.DownloadPath;
            Directory.CreateDirectory(Paths.Downloads);
            string temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using var response = await client.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(token);
                await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                {
                    byte[] buffer = new byte[81920];
                    int total = 0;
                    while (true)
                    {
                        int count = await input.ReadAsync(buffer, token);
                        if (count == 0) break;
                        if (count > package.PackedSize - total)
                            throw new InvalidDataException("The download exceeded its manifest size.");
                        await output.WriteAsync(buffer.AsMemory(0, count), token);
                        total += count;
                        progress?.Invoke(count);
                    }
                    Verify(output, package);
                }
                token.ThrowIfCancellationRequested();
                File.Move(temp, destination, true);
            }
            finally { Cleanup(temp); }
        }

        private static void Cleanup(string temp)
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { App.Logger.WriteException("VerifiedPackageCache::Cleanup", ex); }
        }
    }
}
