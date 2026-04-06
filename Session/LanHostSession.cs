// Zero Unity dependencies. Pure .NET.
using System;
using System.Threading;
using System.Threading.Tasks;
using BurstStrike.Net.Client;
using BurstStrike.Net.Server;
using BurstStrike.Net.Server.Auth;
using BurstStrike.Net.Shared.Protocol;

namespace BurstStrike.Net.Session
{
    /// <summary>
    /// LAN Host game session.
    ///
    /// The host machine runs:
    ///   1. An embedded FrameSyncServer on a background thread (TCP listener).
    ///   2. A local NetSyncClient that connects to the embedded server (loopback).
    ///
    /// From the game layer's perspective, LanHostSession behaves identically to
    /// NetworkSession — commands go through the server for deterministic ordering.
    ///
    /// Other players (LAN guests) connect to this machine's IP on the server port.
    ///
    /// Data flow (same as NetworkSession, but server is in-process):
    ///   SubmitCommands → NetSyncClient → TCP loopback → Embedded Server →
    ///   Server merges → TCP → NetSyncClient → OnCommandReady → LogicWorld
    /// </summary>
    public sealed class LanHostSession : IGameSession
    {
        private readonly int _port;
        private readonly int _maxPlayers;
        private readonly int _tickRate;
        private readonly int _countdownTicks;
        private readonly string _authToken;

        private FrameSyncServer _server;
        private NetSyncClient _client;
        private NetSyncBridge _bridge;

        private SessionState _state = SessionState.None;
        private int _localPlayerId = -1;
        private int _playerCount;
        private int _randomSeed;

        // ── IGameSession ─────────────────────────────────────────────────

        public string ModeName => "LAN-Host";
        public int LocalPlayerId => _localPlayerId;
        public int PlayerCount => _playerCount;
        public int TickRate => _tickRate;
        public int RandomSeed => _randomSeed;

        public SessionState State => _state;
        public string EndReason { get; private set; }

        public Action<byte[], int, int> OnCommandReady { get; set; }
        public Action<int> OnTickReady { get; set; }
        public Action<SessionState> OnStateChanged { get; set; }
        public Action<string> OnLog { get; set; }

        /// <summary>
        /// The port the embedded server is listening on.
        /// Guests need this to connect.
        /// </summary>
        public int ServerPort => _port;

        // ── Construction ─────────────────────────────────────────────────

        /// <param name="port">TCP port for the embedded server.</param>
        /// <param name="maxPlayers">Number of players for the room (default 2).</param>
        /// <param name="tickRate">Simulation tick rate.</param>
        /// <param name="countdownTicks">Countdown ticks after room is full.</param>
        public LanHostSession(
            int port = 9050,
            int maxPlayers = 2,
            int tickRate = 30,
            int countdownTicks = 90)
        {
            _port = port;
            _maxPlayers = maxPlayers;
            _tickRate = tickRate;
            _countdownTicks = countdownTicks;
            _authToken = $"host_{Environment.TickCount}";
        }

        // ── Lifecycle ────────────────────────────────────────────────────

        public void Start()
        {
            if (_state != SessionState.None) return;

            SetState(SessionState.WaitingForPlayers);

            // 1. Start embedded server
            var config = new ServerConfig
            {
                Port = _port,
                MaxPlayers = _maxPlayers,
                TickRate = _tickRate,
                CountdownTicks = _countdownTicks,
            };
            _server = new FrameSyncServer(config, new AllowAllAuthValidator());
            _server.Log = msg => OnLog?.Invoke($"[LAN-Server] {msg}");

            try
            {
                _server.Start();
                OnLog?.Invoke($"[LAN-Host] Embedded server started on port {_port} (maxPlayers={_maxPlayers}).");
            }
            catch (Exception ex)
            {
                EndReason = $"Failed to start server: {ex.Message}";
                OnLog?.Invoke($"[LAN-Host] {EndReason}");
                SetState(SessionState.Ended);
                return;
            }

            // 2. Connect local client to the embedded server (loopback)
            _client = new NetSyncClient { ProtocolVersion = 1, InputDelay = 0 }; // no input delay for host (server is local)
            _bridge = new NetSyncBridge(_client);
            _bridge.InjectCommand = (bytes, tick, seq) => OnCommandReady?.Invoke(bytes, tick, seq);
            _bridge.OnTickReady = tick => OnTickReady?.Invoke(tick);

            _ = ConnectLocalClient();
        }

        private async Task ConnectLocalClient()
        {
            try
            {
                // Small delay to let the server socket bind
                await Task.Delay(100);

                bool ok = await _client.ConnectAndJoinAsync("127.0.0.1", _port, _authToken);
                if (!ok)
                {
                    EndReason = _client.JoinRejectReason ?? "Local join failed";
                    OnLog?.Invoke($"[LAN-Host] {EndReason}");
                    SetState(SessionState.Ended);
                }
                else
                {
                    _localPlayerId = _client.LocalPlayerId;
                    OnLog?.Invoke($"[LAN-Host] Host connected as player {_localPlayerId}. Waiting for guests...");
                }
            }
            catch (Exception ex)
            {
                EndReason = ex.Message;
                OnLog?.Invoke($"[LAN-Host] Local connect error: {ex.Message}");
                SetState(SessionState.Ended);
            }
        }

        public void Stop()
        {
            if (_state == SessionState.Ended || _state == SessionState.None) return;
            EndReason = "Stopped";

            _client?.Disconnect();
            _server?.Stop();
            SetState(SessionState.Ended);
            OnLog?.Invoke("[LAN-Host] Session stopped.");
        }

        public void Update()
        {
            if (_client == null) return;

            // Drain log messages
            while (_client.LogMessages.TryDequeue(out var msg))
                OnLog?.Invoke($"[LAN-Host] {msg}");

            // Check for GameStart
            while (_client.GameStartEvents.TryDequeue(out var gs))
            {
                _playerCount = gs.PlayerCount;
                _randomSeed = gs.RandomSeed;
                OnLog?.Invoke($"[LAN-Host] Game starting: {gs.PlayerCount} players, seed={gs.RandomSeed}");
                SetState(SessionState.Running);
            }

            // Process ticks
            if (_state == SessionState.Running)
            {
                _bridge.ProcessReceivedTicks();
            }

            // Player events
            while (_client.PlayerDisconnectedEvents.TryDequeue(out var pid))
                OnLog?.Invoke($"[LAN-Host] Player {pid} disconnected.");

            while (_client.PlayerReconnectedEvents.TryDequeue(out var pid))
                OnLog?.Invoke($"[LAN-Host] Player {pid} reconnected.");

            // Connection lost check
            if (_state == SessionState.Running && _client.State == ClientState.Disconnected)
            {
                EndReason = "Connection to embedded server lost";
                SetState(SessionState.Ended);
            }
        }

        // ── Command pipeline ─────────────────────────────────────────────

        public void SubmitCommands(int tick, byte[][] encodedCommands)
        {
            if (_state != SessionState.Running) return;
            _bridge.SendLocalCommands(tick, encodedCommands);
        }

        public void Dispose()
        {
            Stop();
            _client?.Dispose();
            // Server cleanup
            _server?.Stop();
        }

        // ── Helpers ──────────────────────────────────────────────────────

        private void SetState(SessionState s)
        {
            if (_state == s) return;
            _state = s;
            OnStateChanged?.Invoke(s);
        }
    }
}
