using System;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MP.Server.Diagnostics;

namespace MP.Server.Testing
{
    /// <summary>
    /// Connectivity test tool — LAN uses the auto-detected default gateway,
    /// WAN uses ICMP pings to well-known public DNS servers.
    /// </summary>
    public class ConnectionTester
    {
        private readonly ILogger<ConnectionTester> _logger;

        // Public IPs used only for outbound ping reachability checks.
        // Using well-known stable DNS resolvers, not the server's own public IP.
        private static readonly string[] WanPingTargets = { "8.8.8.8", "1.1.1.1", "9.9.9.9" };
        private const int PingTimeoutMs = 2000;

        public ConnectionTester(ILogger<ConnectionTester> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// WAN test: pings three reliable public DNS resolvers.
        /// Returns true if at least one responds.
        /// </summary>
        public async Task<bool> TestWanConnectivity()
        {
            _logger.LogInformation("🌍 Testing WAN connectivity (pinging public DNS resolvers)...");

            var tasks = WanPingTargets.Select(async ip =>
            {
                try
                {
                    using var ping = new Ping();
                    var reply = await ping.SendPingAsync(ip, PingTimeoutMs);
                    bool ok = reply.Status == IPStatus.Success;
                    if (ok)
                        _logger.LogInformation("  ✅ {IP}: {Rtt}ms", ip, reply.RoundtripTime);
                    else
                        _logger.LogWarning("  ❌ {IP}: {Status}", ip, reply.Status);
                    return (IP: ip, Ok: ok, Rtt: ok ? reply.RoundtripTime : -1L);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("  ❌ {IP}: {Error}", ip, ex.Message);
                    return (IP: ip, Ok: false, Rtt: -1L);
                }
            }).ToArray();

            var results = await Task.WhenAll(tasks);
            var successful = results.Where(r => r.Ok).ToArray();

            if (successful.Length == 0)
            {
                _logger.LogError("❌ WAN unreachable — all {Count} ping targets failed", WanPingTargets.Length);
                return false;
            }

            var avg = successful.Average(r => r.Rtt);
            _logger.LogInformation("✅ WAN reachable ({Hit}/{Total} hosts, avg {Avg:F0}ms)",
                successful.Length, WanPingTargets.Length, avg);
            return true;
        }

        /// <summary>
        /// LAN test: auto-detects the default gateway and pings it.
        /// Returns true if the gateway responds.
        /// </summary>
        public async Task<bool> TestLanConnectivity()
        {
            var gateway = GetDefaultGateway();
            if (gateway == null)
            {
                _logger.LogError("❌ LAN test failed — could not detect a default gateway");
                return false;
            }

            _logger.LogInformation("🏠 Testing LAN connectivity — pinging gateway {Gateway}...", gateway);

            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(gateway.ToString(), PingTimeoutMs);
                if (reply.Status == IPStatus.Success)
                {
                    _logger.LogInformation("✅ Gateway {Gateway} responded in {Rtt}ms", gateway, reply.RoundtripTime);
                    return true;
                }

                _logger.LogError("❌ Gateway {Gateway} did not respond: {Status}", gateway, reply.Status);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Ping to gateway {Gateway} failed", gateway);
                return false;
            }
        }

        /// <summary>
        /// Returns the IPv4 default gateway of the first active non-loopback interface that has one.
        /// </summary>
        public static IPAddress? GetDefaultGateway()
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                         && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().GatewayAddresses)
                .Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork
                                  && !IPAddress.IsLoopback(a));
        }
    }
}

