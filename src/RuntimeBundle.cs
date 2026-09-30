using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;

namespace RelayBalanceDesktop
{
    public static class RuntimeBundle
    {
        public static string Prepare()
        {
            string directory = Path.Combine(Program.DataDirectory, "runtime", "1.1.0-" + RuntimeManifest.PayloadId);
            Directory.CreateDirectory(directory);
            Extract(directory, "node.exe", "Payload.Node", RuntimeManifest.NodeHash, true);
            Extract(directory, "core.mjs", "Payload.Core", RuntimeManifest.CoreHash, false);
            Extract(directory, "desktop-worker.mjs", "Payload.Worker", RuntimeManifest.WorkerHash, false);
            Extract(directory, "NODE-LICENSE.txt", "Payload.License", RuntimeManifest.LicenseHash, false);
            return directory;
        }
        private static string Hash(string file)
        {
            using (FileStream stream = File.OpenRead(file))
            using (SHA256 hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
        private static void Extract(string directory, string name, string resource, string expected, bool compressed)
        {
            string target = Path.Combine(directory, name);
            if (File.Exists(target) && Hash(target) == expected) return;
            string temporary = target + ".new-" + System.Diagnostics.Process.GetCurrentProcess().Id.ToString();
            try
            {
                using (Stream input = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
                {
                    if (input == null) throw new InvalidDataException("Resource is missing");
                    using (FileStream output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        if (compressed) { using (GZipStream unpack = new GZipStream(input, CompressionMode.Decompress)) unpack.CopyTo(output); }
                        else input.CopyTo(output);
                        output.Flush(true);
                    }
                }
                if (Hash(temporary) != expected) throw new InvalidDataException("Resource integrity check failed");
                if (File.Exists(target)) File.Delete(target);
                File.Move(temporary, target);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
