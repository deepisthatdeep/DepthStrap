/*
 * Roblox Studio Mod Manager (ProjectSrc/Utility/Package.cs)
 * MIT License
 * Copyright (c) 2015-present MaximumADHD
*/

namespace Bloxstrap.Models.Manifest
{
    public class Package
    {
        public string Name { get; set; } = "";

        public string Signature { get; set; } = "";

        public int PackedSize { get; set; }

        public int Size { get; set; }

        internal void Validate()
        {
            if (string.IsNullOrEmpty(Name) || string.IsNullOrEmpty(Signature) ||
                !Regex.IsMatch(Name, @"\A[A-Za-z0-9][A-Za-z0-9_.-]*\z") ||
                !Regex.IsMatch(Signature, @"\A[a-fA-F0-9]{32}\z") || PackedSize <= 0 || Size < 0)
                throw new InvalidDataException("The Roblox package manifest contains an invalid entry.");
        }

        public string DownloadPath
        {
            get { Validate(); return Path.Combine(Paths.Downloads, Signature.ToLowerInvariant()); }
        }

        public override string ToString()
        {
            return $"[{Signature}] {Name}";
        }
    }
}
