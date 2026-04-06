// Zero Unity dependencies. Pure .NET.
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using BurstStrike.Net.Server.Auth;
using BurstStrike.Net.Server.Room;
using BurstStrike.Net.Shared.Protocol;
using BurstStrike.Net.Shared.Transport;

namespace BurstStrike.Net.Server
{
    /// <summary>
    /// Server configuration.
    /// </summary>
    public sealed class ServerConfig
    {
        public int Port { get; set; } = 9050;
        public int MaxPlayers { get; set; } = 2;
        public int TickRate { get; set; } = 30;
        public int CountdownTicks { get; set; } = 90;
        public int ProtocolVersion { get; set; } = 1;
    }

    /// <summary>
    /// Frame synchronization server.
    /// 
    /// Responsibilities:
    /// 1. Listen for TCP connections.
    /// 2. Authenticate clients via IAuthValidator.
    /// 3. Assign clients to rooms via RoomManager.
    /// 4. Each room runs its own tick loop on a dedicated thread.
    /// 5. Relay commands between players within a room.
    /// 
    /// This class is the top-level orchestrator. It does NOT simulate the game —
    /// it's a "dumb relay" like classic RTS lockstep servers (RA, SC:BW).
    /// </summary>
    public sealed class FrameSyncServer
    {
        private readonly ServerConfig _config;
        private readonly IAuthValidator _auth;
        private readonly RoomManager _roomManager;
        private TcpListener _listener;
        private CancellationTokenSource _cts;

        public Action<string> Log;

        public FrameSyncServer(ServerConfig config, IAuthValidator auth)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _auth = auth ?? throw new ArgumentNullException(nameof(auth));

            _roomManager = new RoomManager
            {
                DefaultMaxPlayers = config.MaxPlayers,
                DefaultTickRate = config.TickRate,
                DefaultCountdownTicks = config.CountdownTicks,
            };
        }

        /// <summary>Start listening for connections. Returns immediately.</summary>
        public void Start()
        {
            _cts = new CancellationTokenSource();
            _roomManager.Log = Log;

            _listener = new TcpListener(IPAddress.Any, _config.Port);
            _listener.Start();
            Log?.Invoke($"[Server] Listening on port {_config.Port} (maxPlayers={_config.MaxPlayers}, tickRate={_config.TickRate})");

            // Accept loop runs on a background task
            _ = AcceptLoop(_cts.Token);
        }

        /// <summary>Stop the server gracefully.</summary>
        public void Stop()
        {
            Log?.Invoke("[Server] Shutting down...");
            _cts?.Cancel();
            try { _listener?.Stop(); } catch { }
        }

        private async Task AcceptLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var tcpClient = await _listener.AcceptTcpClientAsync();
                    Log?.Invoke($"[Server] Incoming connection from {tcpClient.Client.RemoteEndPoint}");

                    // Handle each client on its own task
                    _ = Task.Run(() => HandleClient(tcpClient, ct), ct);
                }
                catch (ObjectDisposedException) { break; } // listener stopped
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Log?.Invoke($"[Server] Accept error: {ex.Message}");
                }
            }
        }

        private async Task HandleClient(TcpClient tcpClient, CancellationToken ct)
        {
            ITransport transport = null;
            ClientSession session = null;

            try
            {
                transport = new TcpTransport(tcpClient);

                // 1) Wait for JoinRequest (first message must be JoinRequest)
                var firstMsg = await transport.ReceiveAsync(ct);
                var msgType = Messages.GetType(firstMsg);

                if (msgType != MessageType.JoinRequest)
                {
                    Log?.Invoke($"[Server] Expected JoinRequest, got {msgType}. Disconnecting.");
                    transport.Disconnect();
                    return;
                }

                var joinReq = Messages.JoinRequest.Decode(firstMsg, 1); // skip message type byte

                // 2) Validate protocol version
                if (joinReq.ProtocolVersion != _config.ProtocolVersion)
                {
                    var reject = new Messages.JoinResult
                    {
                        Success = false,
                        Reason = $"Protocol version mismatch: server={_config.ProtocolVersion}, client={joinReq.ProtocolVersion}",
                    };
                    await transport.SendAsync(reject.Encode(), ct);
                    transport.Disconnect();
                    return;
                }

                // 3) Authenticate via web server
                var authResult = await _auth.ValidateAsync(joinReq.AuthToken);
                if (!authResult.IsValid)
                {
                    Log?.Invoke($"[Server] Auth failed for token '{joinReq.AuthToken}': {authResult.DenyReason}");
                    var reject = new Messages.JoinResult { Success = false, Reason = authResult.DenyReason };
                    await transport.SendAsync(reject.Encode(), ct);
                    transport.Disconnect();
                    return;
                }

                Log?.Invoke($"[Server] Auth OK: playerId={authResult.PlayerId}, name={authResult.PlayerName}");

                // 4) Create session and join room
                session = new ClientSession(transport, authResult.PlayerId, authResult.PlayerName);

                var (room, slotIndex) = _roomManager.JoinOrCreate(
                    session, authResult.PlayerId, authResult.PlayerName, joinReq.RoomId);

                if (slotIndex < 0)
                {
                    var reject = new Messages.JoinResult { Success = false, Reason = "Room is full or unavailable" };
                    await transport.SendAsync(reject.Encode(), ct);
                    transport.Disconnect();
                    return;
                }

                session.Room = room;
                session.SlotIndex = slotIndex;

                // 5) Send JoinResult (success)
                var joinResult = new Messages.JoinResult
                {
                    Success = true,
                    PlayerId = (byte)slotIndex,
                    RoomId = room.RoomId,
                };
                await transport.SendAsync(joinResult.Encode(), ct);

                // 6) If room transitioned to Countdown, start the room tick thread
                if (room.State == RoomState.Countdown || room.State == RoomState.Running)
                {
                    StartRoomIfNeeded(room, ct);
                }

                // 7) Enter message receive loop
                await session.ReceiveLoop(ct);
            }
            catch (EndOfStreamException)
            {
                Log?.Invoke($"[Server] Client disconnected cleanly.");
            }
            catch (Exception ex)
            {
                Log?.Invoke($"[Server] Client error: {ex.Message}");
            }
            finally
            {
                if (session?.Room != null)
                    session.Room.PlayerDisconnected(session);
                transport?.Disconnect();
            }
        }

        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _roomThreads
            = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>();

        private void StartRoomIfNeeded(GameRoom room, CancellationToken ct)
        {
            if (!_roomThreads.TryAdd(room.RoomId, true))
                return; // already running

            var thread = new Thread(() =>
            {
                try
                {
                    room.Run(ct);
                }
                catch (Exception ex)
                {
                    Log?.Invoke($"[Room {room.RoomId}] Tick thread error: {ex.Message}");
                }
                finally
                {
                    _roomThreads.TryRemove(room.RoomId, out _);
                }
            })
            {
                IsBackground = true,
                Name = $"Room_{room.RoomId}",
            };
            thread.Start();
        }
    }

    /// <summary>
    /// Per-client session on the server side.
    /// Receives messages and dispatches them to the appropriate room.
    /// </summary>
    internal sealed class ClientSession : IClientSession
    {
        private readonly ITransport _transport;

        public int AuthPlayerId { get; }
        public string PlayerName { get; }
        public GameRoom Room { get; set; }
        public int SlotIndex { get; set; } = -1;
        public bool IsConnected => _transport.IsConnected;

        public ClientSession(ITransport transport, int authPlayerId, string playerName)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            AuthPlayerId = authPlayerId;
            PlayerName = playerName;
        }

        public Task SendAsync(byte[] payload, CancellationToken ct = default)
        {
            return _transport.SendAsync(payload, ct);
        }

        /// <summary>Main receive loop — runs until disconnection.</summary>
        public async Task ReceiveLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _transport.IsConnected)
            {
                var data = await _transport.ReceiveAsync(ct);
                var msgType = Messages.GetType(data);

                switch (msgType)
                {
                    case MessageType.ClientReady:
                        // Could be used for ready-check; for now, no-op.
                        break;

                    case MessageType.TickInput:
                    {
                        var input = Messages.TickInput.Decode(data, 1);
                        Room?.ReceiveInput(SlotIndex, input);
                        break;
                    }

                    case MessageType.SyncRequest:
                    {
                        var req = Messages.SyncRequest.Decode(data, 1);
                        var ticks = Room?.HandleSyncRequest(req.FromTick, req.ToTick);
                        if (ticks != null)
                        {
                            var resp = new Messages.SyncResponse { Ticks = ticks };
                            await _transport.SendAsync(resp.Encode(), ct);
                        }
                        break;
                    }

                    case MessageType.Ping:
                    {
                        var ping = Messages.Ping.Decode(data, 1);
                        var pong = new Messages.Pong { TimestampMs = ping.TimestampMs };
                        await _transport.SendAsync(pong.Encode(), ct);
                        break;
                    }

                    case MessageType.Disconnect:
                        return; // clean disconnect

                    default:
                        // Unknown message — ignore
                        break;
                }
            }
        }
    }
}
