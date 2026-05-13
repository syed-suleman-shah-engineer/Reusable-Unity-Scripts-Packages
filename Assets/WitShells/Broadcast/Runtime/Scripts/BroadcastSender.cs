using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using WitShells.ThreadingJob;
using WitShells.DesignPatterns;

namespace WitShells.Broadcast
{
    /// <summary>
    /// Sends UDP broadcast packets with optional periodic modes.
    /// Modes:
    /// - LoopSingle: repeat the same message at a fixed interval.
    /// - UntilResponse: repeat until a response is observed (subscribe to a BroadcastService instance).
    /// - RetryOnFailure: re-send only when local send throws; limited attempts.
    /// </summary>
    public class BroadcastSender : IDisposable
    {
        private Timer _timer;
        private bool _stopped;

        public enum PeriodicMode
        {
            LoopSingle,
            UntilResponse,
            RetryOnFailure,
        }

        /// <summary>
        /// Start periodic sending in the selected mode.
        /// For UntilResponse, pass a listening service; when it raises OnResponseReceived the sender stops.
        /// </summary>
        public void Start(string message, int port, PeriodicMode mode, int intervalMs = 1000, int maxRetries = 3, BroadcastService listenService = null)
        {
            Stop();
            _stopped = false;

            switch (mode)
            {
                case PeriodicMode.LoopSingle:
                {
                    _timer = new Timer(_ =>
                    {
                        ThreadManager.Instance.EnqueueJob(new BroadcastSendJob(message, port), _ => { }, ex => WitLogger.LogWarning($"BroadcastSender: send failed: {ex.Message}"));
                    }, null, 0, intervalMs);
                    break;
                }
                case PeriodicMode.UntilResponse:
                {
                    if (listenService == null)
                    {
                        WitLogger.LogWarning("BroadcastSender: UntilResponse mode requires a BroadcastService instance");
                        return;
                    }

                    void Handler(string payload, IPEndPoint ep)
                    {
                        try { _timer?.Dispose(); } catch { }
                        _timer = null;
                        listenService.OnResponseReceived -= Handler;
                        _stopped = true;
                    }
                    listenService.OnResponseReceived += Handler;

                    _timer = new Timer(_ =>
                    {
                        if (_stopped) return;
                        ThreadManager.Instance.EnqueueJob(new BroadcastSendJob(message, port), __ => { }, ex => WitLogger.LogWarning($"BroadcastSender: send failed: {ex.Message}"));
                    }, null, 0, intervalMs);
                    break;
                }
                case PeriodicMode.RetryOnFailure:
                {
                    int attempts = 0;
                    void TrySendOnce()
                    {
                        attempts++;
                        ThreadManager.Instance.EnqueueJob(new BroadcastSendJob(message, port),
                            onComplete: _ => { /* success: no repeat */ },
                            onError: ex =>
                            {
                                if (attempts >= maxRetries)
                                {
                                    WitLogger.LogWarning($"BroadcastSender: send failed after {attempts} attempts: {ex.Message}");
                                    return;
                                }
                                // schedule next attempt
                                try
                                {
                                    _timer?.Dispose();
                                    _timer = new Timer(__ => TrySendOnce(), null, intervalMs, Timeout.Infinite);
                                }
                                catch { }
                            });
                    }

                    TrySendOnce();
                    break;
                }
            }
        }

        public void Stop()
        {
            _stopped = true;
            try { _timer?.Dispose(); } catch { }
            _timer = null;
        }

        public void Dispose()
        {
            Stop();
        }
    }

    /// <summary>
    /// ThreadingJob to send one UDP broadcast packet.
    /// </summary>
    internal class BroadcastSendJob : ThreadJob<bool>
    {
        private readonly string _message;
        private readonly int _port;

        public BroadcastSendJob(string message, int port)
        {
            _message = message;
            _port = port;
        }

        public override bool Execute()
        {
            var bytes = Encoding.UTF8.GetBytes(_message ?? string.Empty);
            bool atLeastOne = false;

            foreach (var broadcastAddr in GetDirectedBroadcastAddresses())
            {
                try
                {
                    using (var client = new UdpClient())
                    {
                        client.EnableBroadcast = true;
                        var ep = new IPEndPoint(broadcastAddr, _port);
                        client.Send(bytes, bytes.Length, ep);
                        atLeastOne = true;
                    }
                }
                catch (Exception ex)
                {
                    Exception = ex; // keep last error; continue to remaining interfaces
                }
            }

            return atLeastOne;
        }

        /// <summary>
        /// Returns the subnet-directed broadcast address for every active IPv4 NIC
        /// (e.g. 192.168.1.255 instead of the limited 255.255.255.255).
        /// Directed broadcasts are forwarded within the local subnet by the OS,
        /// allowing all devices on the same router to receive the packet.
        /// Falls back to IPAddress.Broadcast only when no suitable NIC is found.
        /// </summary>
        private static List<IPAddress> GetDirectedBroadcastAddresses()
        {
            var result = new List<IPAddress>();

            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                foreach (var unicast in ni.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (unicast.IPv4Mask == null) continue;

                    byte[] ip   = unicast.Address.GetAddressBytes();
                    byte[] mask = unicast.IPv4Mask.GetAddressBytes();
                    var broadcast = new byte[4];
                    for (int i = 0; i < 4; i++)
                        broadcast[i] = (byte)(ip[i] | ~mask[i]);

                    result.Add(new IPAddress(broadcast));
                }
            }

            if (result.Count == 0)
                result.Add(IPAddress.Broadcast); // fallback

            return result;
        }
    }
}
