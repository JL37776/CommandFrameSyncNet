// Zero Unity dependencies. Pure .NET.
// This is the client-side networking component that connects to the frame sync server.
// It can be used from Unity or any .NET application.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BurstStrike.Net.Shared.Protocol;
using BurstStrike.Net.Shared.Transport;

namespace BurstStrike.Net.Client
{
    /// <summary>
    /// Client connection state.
    /// </summary>
    public enum ClientState
    {
        Disconnected,
        Connecting,
        Joining,
        InRoom,
        InGame,
        Reconnecting,
    }

    /// <summary>
    /// Network synchronization client for lockstep frame sync.
    /// 
    /// Lifecycle:
    ///   Disconnected → ConnectAsync() → Connecting → JoinRequest → Joining →
    ///   JoinResult(ok) → InRoom → GameStart → InGame → (disconnect) → Reconnecting
    /// 
    /// Usage (from game code):
    ///   1. Create NetSyncClient
    ///   2. Call ConnectAndJoinAsync(host, port, authToken)
    ///   3. Wait for OnGameStart event
    ///   4. Each local tick: call SendTickInput(tick, commands)
    ///   5. Poll OnTickCommands to receive merged commands
    ///   6. Feed commands into LogicWorld
    /// 
    /// This class is thread-safe. Send/Receive run on background tasks.
    /// Events are enqueued into concurrent collections for main-thread consumption.
    /// </summary>
    public sealed class NetSyncClient : IDisposable
    {
        private ITransport _transport;
        private CancellationTokenSource _cts;
        private volatile ClientState _state = ClientState.Disconnected;

        // ── Public state ─────────────────────────────────────────────────

        public ClientState State => _state;

        /// <summary>Assigned player slot index (0-based) after successful join.</summary>
        public int LocalPlayerId { get; private set; } = -1;

        /// <summary>Room id assigned by the server.</summary>
        public string RoomId { get; private set; }

        /// <summary>Last received server tick.</summary>
        public int LastReceivedTick { get; private set; } = -1;

        /// <summary>Protocol version (must match server).</summary>
        public int ProtocolVersion { get; set; } = 1;

        /// <summary>
        /// Input delay in ticks. Commands generated at local tick T are stamped as tick T + InputDelay.
        /// This gives the network time to deliver commands before they must be executed.
        /// Typical values: 2-4 at 30 tick/s.
        /// </summary>
        public int InputDelay { get; set; } = 2;

        // ── Events (thread-safe queues for main-thread polling) ──────────

        /// <summary>Merged tick commands received from server. Poll this every frame.</summary>
        public ConcurrentQueue<Messages.TickCommands> ReceivedTicks { get; } = new ConcurrentQueue<Messages.TickCommands>();

        /// <summary>GameStart event (fires once when game begins).</summary>
        public ConcurrentQueue<Messages.GameStart> GameStartEvents { get; } = new ConcurrentQueue<Messages.GameStart>();

        /// <summary>Player disconnect/reconnect events.</summary>
        public ConcurrentQueue<byte> PlayerDisconnectedEvents { get; } = new ConcurrentQueue<byte>();
        public ConcurrentQueue<byte> PlayerReconnectedEvents { get; } = new ConcurrentQueue<byte>();

        /// <summary>Error/info log messages from the network layer.</summary>
        public ConcurrentQueue<string> LogMessages { get; } = new ConcurrentQueue<string>();

        /// <summary>Join rejection reason (if join failed).</summary>
        public string JoinRejectReason { get; private set; }

        // ── Connection / Join ────────────────────────────────────────────

        /// <summary>
        /// Connect to the frame sync server and request to join a room.
        /// Returns true if join was accepted.
        /// </summary>
        public async Task<bool> ConnectAndJoinAsync(string host, int port, string authToken, string roomId = null)
        {
            if (_state != ClientState.Disconnected)
                throw new InvalidOperationException($"Cannot connect in state {_state}");

            _cts = new CancellationTokenSource();
            _state = ClientState.Connecting;
            JoinRejectReason = null;

            try
            {
                _transport = await TcpTransport.ConnectAsync(host, port, _cts.Token);
                _state = ClientState.Joining;

                // Send JoinRequest
                var joinReq = new Messages.JoinRequest
                {
                    AuthToken = authToken ?? "",
                    RoomId = roomId ?? "",
                    ProtocolVersion = ProtocolVersion,
                };
                await _transport.SendAsync(joinReq.Encode(), _cts.Token);

                // Wait for JoinResult
                var response = await _transport.ReceiveAsync(_cts.Token);
                var msgType = Messages.GetType(response);
                if (msgType != MessageType.JoinResult)
                {
                    JoinRejectReason = $"Unexpected response: {msgType}";
                    Disconnect();
                    return false;
                }

                var joinResult = Messages.JoinResult.Decode(response, 1);
                if (!joinResult.Success)
                {
                    JoinRejectReason = joinResult.Reason;
                    LogMessages.Enqueue($"Join rejected: {joinResult.Reason}");
                    Disconnect();
                    return false;
                }

                LocalPlayerId = joinResult.PlayerId;
                RoomId = joinResult.RoomId;
                _state = ClientState.InRoom;

                LogMessages.Enqueue($"Joined room '{RoomId}' as player {LocalPlayerId}");

                // Start background receive loop
                _ = Task.Run(() => ReceiveLoop(_cts.Token));

                return true;
            }
            catch (Exception ex)
            {
                JoinRejectReason = ex.Message;
                LogMessages.Enqueue($"Connect failed: {ex.Message}");
                _state = ClientState.Disconnected;
                return false;
            }
        }

        // ── Send ─────────────────────────────────────────────────────────

        /// <summary>
        /// Send this client's commands for a specific tick.
        /// Call this once per local logic tick.
        /// Pass empty array if no commands this tick.
        /// </summary>
        public void SendTickInput(int tick, byte[][] encodedCommands)
        {
            if (_state != ClientState.InGame || _transport == null || !_transport.IsConnected)
                return;

            var input = new Messages.TickInput
            {
                Tick = tick + InputDelay, // stamp with input delay
                Commands = encodedCommands ?? Array.Empty<byte[]>(),
            };

            try
            {
                // Fire-and-forget send (non-blocking for the game loop)
                _ = _transport.SendAsync(input.Encode(), _cts?.Token ?? CancellationToken.None);
            }
            catch (Exception ex)
            {
                LogMessages.Enqueue($"Send error: {ex.Message}");
            }
        }

        /// <summary>Send a ping to measure round-trip time.</summary>
        public void SendPing()
        {
            if (_transport == null || !_transport.IsConnected) return;
            var ping = new Messages.Ping
            {
                TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            try { _ = _transport.SendAsync(ping.Encode()); } catch { }
        }

        // ── Reconnection ─────────────────────────────────────────────────

        /// <summary>
        /// Request historical ticks from the server for reconnection catch-up.
        /// Results arrive via ReceivedTicks queue.
        /// </summary>
        public async Task RequestSyncAsync(int fromTick, int toTick)
        {
            if (_transport == null || !_transport.IsConnected) return;
            var req = new Messages.SyncRequest { FromTick = fromTick, ToTick = toTick };
            await _transport.SendAsync(req.Encode(), _cts?.Token ?? CancellationToken.None);
        }

        // ── Disconnect ───────────────────────────────────────────────────

        public void Disconnect()
        {
            _state = ClientState.Disconnected;
            _cts?.Cancel();
            _transport?.Disconnect();
        }

        public void Dispose() => Disconnect();

        // ── Background receive loop ──────────────────────────────────────

        private async Task ReceiveLoop(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested && _transport.IsConnected)
                {
                    var data = await _transport.ReceiveAsync(ct);
                    var msgType = Messages.GetType(data);

                    switch (msgType)
                    {
                        case MessageType.GameStart:
                        {
                            var gs = Messages.GameStart.Decode(data, 1);
                            _state = ClientState.InGame;
                            GameStartEvents.Enqueue(gs);
                            LogMessages.Enqueue($"Game starting! Players={gs.PlayerCount}, TickRate={gs.TickRate}, Seed={gs.RandomSeed}");
                            break;
                        }

                        case MessageType.TickCommands:
                        {
                            var tc = Messages.TickCommands.Decode(data, 1);
                            LastReceivedTick = tc.Tick;
                            ReceivedTicks.Enqueue(tc);
                            break;
                        }

                        case MessageType.SyncResponse:
                        {
                            var sr = Messages.SyncResponse.Decode(data, 1);
                            if (sr.Ticks != null)
                            {
                                for (int i = 0; i < sr.Ticks.Length; i++)
                                {
                                    ReceivedTicks.Enqueue(sr.Ticks[i]);
                                    if (sr.Ticks[i].Tick > LastReceivedTick)
                                        LastReceivedTick = sr.Ticks[i].Tick;
                                }
                                LogMessages.Enqueue($"Sync received: {sr.Ticks.Length} ticks ({sr.Ticks[0].Tick}-{sr.Ticks[sr.Ticks.Length - 1].Tick})");
                            }
                            break;
                        }

                        case MessageType.PlayerDisconnected:
                        {
                            var pd = Messages.PlayerDisconnected.Decode(data, 1);
                            PlayerDisconnectedEvents.Enqueue(pd.PlayerId);
                            LogMessages.Enqueue($"Player {pd.PlayerId} disconnected.");
                            break;
                        }

                        case MessageType.PlayerReconnected:
                        {
                            var pr = Messages.PlayerReconnected.Decode(data, 1);
                            PlayerReconnectedEvents.Enqueue(pr.PlayerId);
                            LogMessages.Enqueue($"Player {pr.PlayerId} reconnected.");
                            break;
                        }

                        case MessageType.Pong:
                        {
                            var pong = Messages.Pong.Decode(data, 1);
                            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                            long rtt = now - pong.TimestampMs;
                            LogMessages.Enqueue($"Pong: RTT={rtt}ms");
                            break;
                        }

                        case MessageType.GameEnd:
                        {
                            var ge = Messages.GameEnd.Decode(data, 1);
                            _state = ClientState.Disconnected;
                            LogMessages.Enqueue($"Game ended. Reason={ge.Reason}");
                            return;
                        }

                        case MessageType.Error:
                            LogMessages.Enqueue("Server error received.");
                            break;
                    }
                }
            }
            catch (EndOfStreamException)
            {
                LogMessages.Enqueue("Server connection lost.");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                LogMessages.Enqueue($"Receive error: {ex.Message}");
            }
            finally
            {
                if (_state == ClientState.InGame)
                {
                    _state = ClientState.Reconnecting;
                    LogMessages.Enqueue("Connection lost during game. Ready for reconnect.");
                }
                else
                {
                    _state = ClientState.Disconnected;
                }
            }
        }
    }
}
