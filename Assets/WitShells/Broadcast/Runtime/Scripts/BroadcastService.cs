using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using WitShells.DesignPatterns;
using WitShells.ThreadingJob;

namespace WitShells.Broadcast
{
    /// <summary>
    /// Listens for UDP responses on a given port and raises events.
    /// Usage:
    /// var svc = new BroadcastService();
    /// svc.OnResponseReceived += (msg, remote) => { ... };
    /// svc.StartListening(port: 7777, singleResponse: false, durationMs: 5000);
    /// svc.Stop();
    /// svc.Dispose();
    /// </summary>
    public class BroadcastService : IDisposable
    {
        /// <summary>Raised when a response packet is received. Message is UTF8-decoded.</summary>
        public event Action<string, IPEndPoint> OnResponseReceived;
        public event Action OnListeningStopped;
        public event Action<Exception> OnError;

        private BroadcastListenJob _currentJob;

        /// <summary>
        /// Start listening for UDP responses on a port.
        /// </summary>
        /// <param name="port">UDP port to listen on.</param>
        /// <param name="singleResponse">If true, stops after the first received message.</param>
        /// <param name="durationMs">How long to listen in milliseconds. 0 means infinite.</param>
        public void StartListening(int port, bool singleResponse = true, int durationMs = 0)
        {
            Stop();

            _currentJob = new BroadcastListenJob(port, singleResponse, durationMs);

            ThreadManager.Instance.EnqueueStreamingJob<string>(_currentJob,
                onProgress: (msg) =>
                {
                    var parts = msg.Split('|');
                    if (parts.Length >= 3 && int.TryParse(parts[1], out var p))
                    {
                        var ep = new IPEndPoint(IPAddress.Parse(parts[0]), p);
                        OnResponseReceived?.Invoke(parts[2], ep);
                    }
                    else
                    {
                        OnResponseReceived?.Invoke(msg, new IPEndPoint(IPAddress.None, 0));
                    }
                },
                onComplete: () => OnListeningStopped?.Invoke(),
                onError: ex => OnError?.Invoke(ex));
        }

        /// <summary>Stops listening immediately by closing the underlying UDP socket.</summary>
        public void Stop()
        {
            _currentJob?.Cancel();
            _currentJob = null;
        }

        public void Dispose()
        {
            Stop();
        }
    }

    // Note: sending is handled by a separate sender class.

    /// <summary>
    /// Streaming job that listens on UDP and reports each received message via onProgress.
    /// Reports combined string "ip|port|payload" to avoid custom structs across job boundary.
    /// </summary>
    internal class BroadcastListenJob : ThreadJob<string>
    {
        private readonly int _port;
        private readonly bool _singleResponse;
        private readonly int _durationMs;
        private UdpClient _udpClient;
        private volatile bool _cancelled;

        // How long Receive() blocks before re-checking the loop condition (ms).
        private const int ReceivePollIntervalMs = 200;

        public override bool IsStreaming { get; protected set; } = true;

        public BroadcastListenJob(int port, bool singleResponse, int durationMs = 0)
        {
            _port = port;
            _singleResponse = singleResponse;
            _durationMs = durationMs;
        }

        /// <summary>Cancels the listening loop by closing the UDP socket.</summary>
        public void Cancel()
        {
            _cancelled = true;
            try { _udpClient?.Close(); } catch { }
        }

        public override void ExecuteStreaming(Action<string> onProgress, Action onComplete = null)
        {
            long startMs = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
            try
            {
                _udpClient = new UdpClient(_port);
                // Short timeout so the loop can re-check cancellation and duration
                // even when no packets arrive.
                _udpClient.Client.ReceiveTimeout = ReceivePollIntervalMs;

                while (!_cancelled)
                {
                    if (_durationMs > 0 && DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond - startMs >= _durationMs)
                        break;

                    IPEndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
                    byte[] data;
                    try
                    {
                        data = _udpClient.Receive(ref remoteEP);
                    }
                    catch (SocketException sx) when (sx.SocketErrorCode == SocketError.TimedOut)
                    {
                        // Poll timeout — loop back and re-check conditions.
                        continue;
                    }

                    string message = Encoding.UTF8.GetString(data);
                    onProgress?.Invoke($"{remoteEP.Address}|{remoteEP.Port}|{message}");
                    if (_singleResponse) break;
                }
            }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                if (!_cancelled) Exception = ex;
            }
            finally
            {
                try { _udpClient?.Close(); } catch { }
                onComplete?.Invoke();
            }
        }
    }
}
