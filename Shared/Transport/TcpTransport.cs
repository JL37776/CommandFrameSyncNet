// Zero Unity dependencies. Pure .NET.
using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace BurstStrike.Net.Shared.Transport
{
    /// <summary>
    /// Abstract transport interface for sending/receiving length-prefixed messages.
    /// Implementations: TcpTransport (production), MemoryTransport (tests).
    /// </summary>
    public interface ITransport : IDisposable
    {
        bool IsConnected { get; }
        Task SendAsync(byte[] payload, CancellationToken ct = default);
        Task<byte[]> ReceiveAsync(CancellationToken ct = default);
        void Disconnect();
    }

    /// <summary>
    /// TCP transport with length-prefixed framing.
    /// Wire: [4-byte LE payload length][payload bytes]
    /// Thread-safe for concurrent send/receive (one sender, one receiver).
    /// </summary>
    public sealed class TcpTransport : ITransport
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private volatile bool _disposed;

        /// <summary>Max message size to prevent abuse (default 1 MB).</summary>
        public int MaxMessageSize { get; set; } = 1024 * 1024;

        public bool IsConnected => !_disposed && _client.Connected;

        /// <summary>Wrap an existing connected TcpClient (used by server accept).</summary>
        public TcpTransport(TcpClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _client.NoDelay = true; // Nagle off for low-latency lockstep
            _stream = _client.GetStream();
        }

        /// <summary>Connect to a remote server.</summary>
        public static async Task<TcpTransport> ConnectAsync(string host, int port, CancellationToken ct = default)
        {
            var client = new TcpClient();
            client.NoDelay = true;
            await client.ConnectAsync(host, port);
            ct.ThrowIfCancellationRequested();
            return new TcpTransport(client);
        }

        public async Task SendAsync(byte[] payload, CancellationToken ct = default)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(TcpTransport));
            if (payload == null) throw new ArgumentNullException(nameof(payload));

            // Length prefix (4 bytes LE)
            var header = new byte[4];
            header[0] = (byte)(payload.Length);
            header[1] = (byte)(payload.Length >> 8);
            header[2] = (byte)(payload.Length >> 16);
            header[3] = (byte)(payload.Length >> 24);

            await _sendLock.WaitAsync(ct);
            try
            {
                await _stream.WriteAsync(header, 0, 4, ct);
                if (payload.Length > 0)
                    await _stream.WriteAsync(payload, 0, payload.Length, ct);
                await _stream.FlushAsync(ct);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public async Task<byte[]> ReceiveAsync(CancellationToken ct = default)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(TcpTransport));

            // Read 4-byte length header
            var header = await ReadExactAsync(4, ct);
            int length = header[0] | (header[1] << 8) | (header[2] << 16) | (header[3] << 24);

            if (length < 0 || length > MaxMessageSize)
                throw new ProtocolViolationException($"Invalid message length: {length}");

            if (length == 0)
                return Array.Empty<byte>();

            return await ReadExactAsync(length, ct);
        }

        private async Task<byte[]> ReadExactAsync(int count, CancellationToken ct)
        {
            var buf = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = await _stream.ReadAsync(buf, offset, count - offset, ct);
                if (read == 0)
                    throw new EndOfStreamException("Remote closed connection");
                offset += read;
            }
            return buf;
        }

        public void Disconnect()
        {
            if (_disposed) return;
            _disposed = true;
            try { _stream?.Close(); } catch { }
            try { _client?.Close(); } catch { }
        }

        public void Dispose() => Disconnect();
    }

    /// <summary>Protocol-level error (bad framing, oversized message, etc.).</summary>
    public class ProtocolViolationException : Exception
    {
        public ProtocolViolationException(string message) : base(message) { }
    }
}
