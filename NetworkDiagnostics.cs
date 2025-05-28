using System;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace MP.Server.Diagnostics
{
    /// <summary>
    /// Network diagnostic utilities to help identify WAN vs LAN connection issues
    /// </summary>
    public static class NetworkDiagnostics
    {
        public static async Task<bool> TestPortConnectivity(string host, int port, ILogger? logger = null, int timeoutMs = 5000)
        {
            try
            {
                using var client = new TcpClient();
                var connectTask = client.ConnectAsync(host, port);
                var timeoutTask = Task.Delay(timeoutMs);
                
                var completedTask = await Task.WhenAny(connectTask, timeoutTask);
                
                if (completedTask == timeoutTask)
                {
                    logger?.LogWarning("⏰ Connection to {Host}:{Port} timed out after {Timeout}ms", host, port, timeoutMs);
                    return false;
                }
                
                if (connectTask.IsFaulted)
                {
                    logger?.LogError("❌ Connection to {Host}:{Port} failed: {Error}", host, port, connectTask.Exception?.GetBaseException().Message);
                    return false;
                }
                
                logger?.LogInformation("✅ Successfully connected to {Host}:{Port}", host, port);
                return true;
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "❌ Error testing connection to {Host}:{Port}", host, port);
                return false;
            }
        }
        
        public static async Task<bool> TestUdpConnectivity(string host, int port, ILogger? logger = null, int timeoutMs = 5000)
        {
            try
            {
                using var client = new UdpClient();
                client.Connect(host, port);
                
                // Send a test packet
                var testData = Encoding.UTF8.GetBytes("PING");
                await client.SendAsync(testData, testData.Length);
                
                // Try to receive response (with timeout)
                var receiveTask = client.ReceiveAsync();
                var timeoutTask = Task.Delay(timeoutMs);
                
                var completedTask = await Task.WhenAny(receiveTask, timeoutTask);
                
                if (completedTask == timeoutTask)
                {
                    logger?.LogWarning("⏰ UDP response from {Host}:{Port} timed out after {Timeout}ms", host, port, timeoutMs);
                    return false;
                }
                
                logger?.LogInformation("✅ UDP connectivity to {Host}:{Port} confirmed", host, port);
                return true;
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "❌ Error testing UDP connectivity to {Host}:{Port}", host, port);
                return false;
            }
        }
        
        public static void PrintNetworkInfo(ILogger? logger = null)
        {
            try
            {
                logger?.LogInformation("🌐 Network Interface Information:");
                
                var interfaces = NetworkInterface.GetAllNetworkInterfaces();
                foreach (var netInterface in interfaces)
                {
                    if (netInterface.OperationalStatus == OperationalStatus.Up)
                    {
                        logger?.LogInformation("  Interface: {Name} ({Type})", netInterface.Name, netInterface.NetworkInterfaceType);
                        
                        var properties = netInterface.GetIPProperties();
                        foreach (var ipInfo in properties.UnicastAddresses)
                        {
                            if (ipInfo.Address.AddressFamily == AddressFamily.InterNetwork)
                            {
                                var isPrivate = IsPrivateIP(ipInfo.Address);
                                logger?.LogInformation("    IPv4: {IP} ({Type})", ipInfo.Address, isPrivate ? "Private" : "Public");
                            }
                        }
                    }
                }
                
                // Check external IP
                logger?.LogInformation("🔍 Attempting to determine external IP...");
                _ = Task.Run(async () => {
                    try
                    {
                        using var client = new HttpClient();
                        client.Timeout = TimeSpan.FromSeconds(10);
                        var externalIP = await client.GetStringAsync("https://api.ipify.org");
                        logger?.LogInformation("🌍 External IP: {ExternalIP}", externalIP.Trim());
                    }
                    catch (Exception ex)
                    {
                        logger?.LogWarning("⚠️ Could not determine external IP: {Error}", ex.Message);
                    }
                });
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "❌ Error gathering network information");
            }
        }
        
        public static void PrintCertificateInfo(X509Certificate2? certificate, ILogger? logger = null)
        {
            if (certificate == null)
            {
                logger?.LogWarning("⚠️ No certificate provided");
                return;
            }
            
            try
            {
                logger?.LogInformation("🔐 Certificate Information:");
                logger?.LogInformation("  Subject: {Subject}", certificate.Subject);
                logger?.LogInformation("  Issuer: {Issuer}", certificate.Issuer);
                logger?.LogInformation("  Thumbprint: {Thumbprint}", certificate.Thumbprint);
                logger?.LogInformation("  Valid From: {NotBefore}", certificate.NotBefore);
                logger?.LogInformation("  Valid To: {NotAfter}", certificate.NotAfter);
                logger?.LogInformation("  Has Private Key: {HasPrivateKey}", certificate.HasPrivateKey);
                
                // Check Subject Alternative Names
                foreach (var extension in certificate.Extensions)
                {
                    if (extension.Oid?.Value == "2.5.29.17") // Subject Alternative Name
                    {
                        logger?.LogInformation("  SAN Extension found (check for your public IP in certificate)");
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "❌ Error reading certificate information");
            }
        }
        
        private static bool IsPrivateIP(IPAddress ip)
        {
            var bytes = ip.GetAddressBytes();
            
            // 127.x.x.x (loopback)
            if (bytes[0] == 127) return true;
            
            // 10.x.x.x
            if (bytes[0] == 10) return true;
            
            // 172.16.x.x - 172.31.x.x
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
            
            // 192.168.x.x
            if (bytes[0] == 192 && bytes[1] == 168) return true;
            
            return false;
        }
    }
}
