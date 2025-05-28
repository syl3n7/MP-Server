using System;
using System.Net.Sockets;
using System.Net.Security;
using System.Text;
using System.Threading.Tasks;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using MP.Server.Diagnostics;

namespace MP.Server.Testing
{
    /// <summary>
    /// Simple connection test tool to diagnose WAN connectivity issues
    /// </summary>
    public class ConnectionTester
    {
        private readonly ILogger<ConnectionTester> _logger;
        
        public ConnectionTester(ILogger<ConnectionTester> logger)
        {
            _logger = logger;
        }
        
        public async Task<bool> TestWanConnectivity(string publicIP = "89.114.116.19", int port = 443)
        {
            _logger.LogInformation("🧪 Testing WAN connectivity to {PublicIP}:{Port}", publicIP, port);
            
            // Test basic TCP connectivity
            var tcpResult = await NetworkDiagnostics.TestPortConnectivity(publicIP, port, _logger);
            if (!tcpResult)
            {
                _logger.LogError("❌ TCP connectivity test failed - Check NAT/Firewall configuration");
                return false;
            }
            
            // Test TLS handshake
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(publicIP, port);
                
                var stream = client.GetStream();
                var sslStream = new SslStream(stream, false, ValidateServerCertificate);
                
                await sslStream.AuthenticateAsClientAsync(publicIP);
                
                _logger.LogInformation("✅ TLS handshake successful");
                
                // Test basic protocol
                var welcomeBuffer = new byte[1024];
                var bytesRead = await sslStream.ReadAsync(welcomeBuffer, 0, welcomeBuffer.Length);
                var welcome = Encoding.UTF8.GetString(welcomeBuffer, 0, bytesRead);
                
                _logger.LogInformation("📨 Received welcome: {Welcome}", welcome.Trim());
                
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ TLS connection test failed");
                return false;
            }
        }
        
        public async Task<bool> TestLanConnectivity(string lanIP = "192.168.3.123", int port = 443)
        {
            _logger.LogInformation("🧪 Testing LAN connectivity to {LanIP}:{Port}", lanIP, port);
            
            return await NetworkDiagnostics.TestPortConnectivity(lanIP, port, _logger);
        }
        
        private bool ValidateServerCertificate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors sslPolicyErrors)
        {
            // Accept self-signed certificates for testing
            if (sslPolicyErrors == SslPolicyErrors.RemoteCertificateChainErrors ||
                sslPolicyErrors == SslPolicyErrors.RemoteCertificateNameMismatch)
            {
                _logger.LogWarning("⚠️ Accepting certificate with errors: {Errors}", sslPolicyErrors);
                return true;
            }
            
            _logger.LogInformation("✅ Certificate validation successful");
            return sslPolicyErrors == SslPolicyErrors.None;
        }
    }
}
