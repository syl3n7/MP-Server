using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MP.Server.Security
{
    /// <summary>
    /// Provides AES encryption/decryption for UDP packets
    /// Each session gets a unique encryption key after TCP authentication
    /// </summary>
    public class UdpEncryption
    {
        private readonly byte[] _key;
        private readonly byte[] _iv;
        
        public UdpEncryption(string sessionId, string sharedSecret = "RacingServerUDP2024!")
        {
            // Generate deterministic key and IV from session ID and shared secret
            using var sha256 = SHA256.Create();
            var keySource = Encoding.UTF8.GetBytes(sessionId + sharedSecret);
            var keyHash = sha256.ComputeHash(keySource);
            
            _key = new byte[32]; // AES-256 key
            _iv = new byte[16];   // AES IV
            
            Array.Copy(keyHash, 0, _key, 0, 32);
            Array.Copy(keyHash, 16, _iv, 0, 16);
        }
        
        public byte[] Encrypt(string jsonData)
        {
            using var aes = Aes.Create();
            aes.Key = _key;
            aes.IV = _iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            
            using var encryptor = aes.CreateEncryptor();
            var dataBytes = Encoding.UTF8.GetBytes(jsonData);
            return encryptor.TransformFinalBlock(dataBytes, 0, dataBytes.Length);
        }
        
        public string? Decrypt(byte[] encryptedData)
        {
            try
            {
                using var aes = Aes.Create();
                aes.Key = _key;
                aes.IV = _iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                
                using var decryptor = aes.CreateDecryptor();
                var decryptedBytes = decryptor.TransformFinalBlock(encryptedData, 0, encryptedData.Length);
                return Encoding.UTF8.GetString(decryptedBytes);
            }
            catch
            {
                return null; // Invalid or corrupted data
            }
        }
        
        /// <summary>
        /// Creates an encrypted UDP packet with a simple header for integrity
        /// Format: [4 bytes length][encrypted data]
        /// </summary>
        public byte[] CreatePacket(object data)
        {
            var json = JsonSerializer.Serialize(data);
            var encrypted = Encrypt(json);
            var packet = new byte[4 + encrypted.Length];
            
            // Add length header (little endian)
            BitConverter.GetBytes(encrypted.Length).CopyTo(packet, 0);
            encrypted.CopyTo(packet, 4);
            
            return packet;
        }
        
        /// <summary>
        /// Parses an encrypted UDP packet
        /// </summary>
        public T? ParsePacket<T>(byte[] packetData)
        {
            if (packetData.Length < 4)
                return default;
                
            var length = BitConverter.ToInt32(packetData, 0);
            if (length != packetData.Length - 4 || length <= 0)
                return default;
                
            var encryptedData = new byte[length];
            Array.Copy(packetData, 4, encryptedData, 0, length);
            
            var json = Decrypt(encryptedData);
            if (string.IsNullOrEmpty(json))
                return default;
                
            try
            {
                return JsonSerializer.Deserialize<T>(json);
            }
            catch
            {
                return default;
            }
        }
    }
}