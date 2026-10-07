namespace Bloxstrap.Utility
{
    internal static class AtomicFile
    {
        private static InterProcessLock Lock(string path)
        {
            string identity = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())));
            var gate = new InterProcessLock("AtomicFile-" + identity, TimeSpan.FromSeconds(2));
            if (gate.IsAcquired) return gate;
            gate.Dispose();
            throw new IOException("The data file is busy; retry this operation.");
        }

        internal static string ReadText(string path)
        {
            using var gate = Lock(path);
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);
                    return reader.ReadToEnd();
                }
                catch (IOException ex) when (attempt < 3 && (ex is FileNotFoundException || (ex.HResult & 0xffff) is 32 or 33))
                { Thread.Sleep(10 << attempt); }
                catch (UnauthorizedAccessException) when (attempt < 3)
                { Thread.Sleep(10 << attempt); }
            }
        }

        internal static void WriteText(string path, string contents)
        {
            using var gate = Lock(path);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, contents);
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        if (File.Exists(path))
                        {
                            if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
                                throw new UnauthorizedAccessException("The destination is read-only.");
                            // ReplaceFile preserves the existing ACL and supports readers sharing deletion.
                            // MoveFileEx with overwrite can deny replacement while the destination is open.
                            File.Replace(temp, path, null);
                        }
                        else File.Move(temp, path);
                        break;
                    }
                    catch (IOException ex) when (attempt < 5 && (ex.HResult & 0xffff) is 32 or 33 or 80 or 183)
                    { Thread.Sleep(40 * (attempt + 1)); }
                    catch (FileNotFoundException) when (attempt < 5 && File.Exists(temp))
                    { /* A concurrent creator/deleter changed the destination; retry its current state. */ }
                    catch (UnauthorizedAccessException) when (attempt < 5 && File.Exists(path) &&
                        (File.GetAttributes(path) & FileAttributes.ReadOnly) == 0)
                    {
                        // Windows sync/antivirus readers can temporarily deny replacement.
                        // Never clear a user's read-only bit or bypass permissions; stop after bounded retries.
                        Thread.Sleep(40 * (attempt + 1));
                    }
                }
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}
