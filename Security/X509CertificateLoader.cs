using System;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.InteropServices;

namespace MP.Server
{
    /// <summary>
    /// Helper class for loading X509 certificates across different platforms
    /// </summary>
    public static class ServerCertificateLoader
    {
        /// <summary>
        /// Loads a PKCS#12 certificate from file with the given password
        /// </summary>
        public static X509Certificate2 LoadPkcs12FromFile(string filepath, string password)
        {
            // Read all bytes from the file
            var certificateBytes = File.ReadAllBytes(filepath);
            return LoadPkcs12(certificateBytes, password);
        }

        /// <summary>
        /// Loads a PKCS#12 certificate from byte array with the given password
        /// Uses platform-specific flags to ensure the certificate loads correctly
        /// </summary>
        public static X509Certificate2 LoadPkcs12(byte[] certificateBytes, string password)
        {
            X509KeyStorageFlags flags = GetPlatformSpecificFlags();
            return new X509Certificate2(certificateBytes, password, flags);
        }

        /// <summary>
        /// Gets platform-specific certificate storage flags
        /// Windows and Linux/macOS handle certificates differently
        /// </summary>
        private static X509KeyStorageFlags GetPlatformSpecificFlags()
        {
            // Base flags common to all platforms
            var flags = X509KeyStorageFlags.Exportable;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // On Windows, we want to use both user and machine stores for maximum compatibility
                flags |= X509KeyStorageFlags.MachineKeySet;
                flags |= X509KeyStorageFlags.PersistKeySet;
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || 
                     RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                // On Unix systems, the ephemeral key set is more reliable
                flags |= X509KeyStorageFlags.EphemeralKeySet;
            }

            return flags;
        }
    }
}