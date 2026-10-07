/*
 * Roblox Studio Mod Manager (ProjectSrc/Utility/PackageManifest.cs)
 * MIT License
 * Copyright (c) 2015-present MaximumADHD
*/

namespace Bloxstrap.Models.Manifest
{
    public class PackageManifest : List<Package>
    {
        public PackageManifest(string data)
        {
            using var reader = new StringReader(data);
            string? version = reader.ReadLine();

            if (version != "v0")
                throw new NotSupportedException($"Unexpected package manifest version: {version} (expected v0!)");

            while (true)
            {
                string? fileName = reader.ReadLine();
                if (fileName is null) break;
                if (fileName.Length == 0 && string.IsNullOrWhiteSpace(reader.ReadToEnd())) break;
                string? signature = reader.ReadLine();

                string? rawPackedSize = reader.ReadLine();
                string? rawSize = reader.ReadLine();

                if (string.IsNullOrEmpty(fileName) ||
                    string.IsNullOrEmpty(signature) ||
                    string.IsNullOrEmpty(rawPackedSize) ||
                    string.IsNullOrEmpty(rawSize))
                    throw new InvalidDataException("The Roblox package manifest is truncated.");

                if (!int.TryParse(rawPackedSize, out int packedSize) || !int.TryParse(rawSize, out int size))
                    throw new InvalidDataException("The Roblox package manifest contains an invalid size.");

                var package = new Package
                {
                    Name = fileName,
                    Signature = signature,
                    PackedSize = packedSize,
                    Size = size
                };
                package.Validate();
                // The launcher is not installed; later manifest entries still need processing.
                if (fileName == "RobloxPlayerLauncher.exe") continue;
                if (this.Any(x => string.Equals(x.Name, package.Name, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("The Roblox package manifest contains duplicate names.");
                Add(package);
            }
            if (Count == 0) throw new InvalidDataException("The Roblox package manifest contains no installable packages.");
        }
    }
}
