// Zero Unity dependencies. Pure .NET.
using System;
using System.Threading.Tasks;
using BurstStrike.Net.Client;
using BurstStrike.Net.Shared.Protocol;

namespace BurstStrike.Net.Session
{
    /// <summary>
    /// Network (remote server) game session.
    ///
    /// Data flow:
    ///   SubmitCommands → NetSyncClient.SendTickInput → [TCP] → Server →
    ///   Server merges → [TCP] → NetSyncClient.ReceivedTicks →
    ///   NetworkSession.Update() → OnCommandReady/OnTickReady → LogicWorld
    ///
    /// The server drives the tick clock. The client waits for TickCommands
    /// before advancing the simulation (strict lockstep).
    /// </summary>
    public sealed class NetworkSession : IGameSession
    {
        private readonly string _host;
        private readonly int _port;
        private readonly string _authToken;
        private readonly string _roomId;
        private readonly int _tickRate;

        private NetSyncClient _client;
        private NetSyncBridge _bridge;

        private SessionState _state = SessionState.None;
        private int _localPlayerId = -1;
        private int _playerCount;
        private int _randomSeed;

        // ── IGameSession ─────────────────────────────────────────────────

        public string ModeName => "Network";
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

        // ── Construction ─────────────────────────────────────────────────

        /// <param name="host">Server IP or hostname.</param>
        /// <param name="port">Server port.</param>
        /// <param name="authToken">Web-server auth token.</param>
        /// <param name="roomId">Desired room id (null = auto-match).</param>
        /// <param name="tickRate">Expected tick rate (for local init before server confirms).</param>
        public NetworkSession(string host, int port, string authToken, string roomId = null, int tickRate = 30)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _port = port;
            _authToken = authToken ?? "";
            _roomId = roomId;
            _tickRate = tickRate;
        }

        // ── Lifecycle ────────────────────────────────────────────────────

        public void Start()
        {
            if (_state != SessionState.None) return;

            SetState(SessionState.WaitingForPlayers);
            OnLog?.Invoke($"[Net] Connecting to {_host}:{_port}...");

            _client = new NetSyncClient { ProtocolVersion = 1 };
            _bridge = new NetSyncBridge(_client);

            // Wire bridge callbacks
            _bridge.InjectCommand = (bytes, tick, seq) => OnCommandReady?.Invoke(bytes, tick, seq);
            _bridge.OnTickReady = tick => OnTickReady?.Invoke(tick);

            // Fire-and-forget async connect (results are polled in Update)
            _ = ConnectAsync();
        }

        private async Task ConnectAsync()
        {
            try
            {
                bool ok = await _client.ConnectAndJoinAsync(_host, _port, _authToken, _roomId);
                if (!ok)
                {
                    EndReason = _client.JoinRejectReason ?? "Join rejected";
                    OnLog?.Invoke($"[Net] Join failed: {EndReason}");
                    SetState(SessionState.Ended);
                }
                else
                {
                    _localPlayerId = _client.LocalPlayerId;
                    OnLog?.Invoke($"[Net] Joined room '{_client.RoomId}' as player {_localPlayerId}. Waiting for game start...");
                    SetState(SessionState.WaitingForPlayers);
                }
            }
            catch (Exception ex)
            {
                EndReason = ex.Message;
                OnLog?.Invoke($"[Net] Connection error: {ex.Message}");
                SetState(SessionState.Ended);
            }
        }

        public void Stop()
        {
            if (_state == SessionState.Ended || _state == SessionState.None) return;
            EndReason = "Stopped";
            _client?.Disconnect();
            SetState(SessionState.Ended);
            OnLog?.Invoke("[Net] Session stopped.");
        }

        public void Update()
        {
            if (_client == null) return;

            // Drain log messages from network layer
            while (_client.LogMessages.TryDequeue(out var msg))
                OnLog?.Invoke($"[Net] {msg}");

            // Check for GameStart event
            while (_client.GameStartEvents.TryDequeue(out var gs))
            {
                _playerCount = gs.PlayerCount;
                _randomSeed = gs.RandomSeed;
                OnLog?.Invoke($"[Net] Game starting: {gs.PlayerCount} players, seed={gs.RandomSeed}");
                SetState(SessionState.Running);
            }

            // Process received ticks → inject commands into game
            if (_state == SessionState.Running)
            {
                _bridge.ProcessReceivedTicks();
            }

            // Check for disconnection events
            while (_client.PlayerDisconnectedEvents.TryDequeue(out var pid))
                OnLog?.Invoke($"[Net] Player {pid} disconnected.");

            while (_client.PlayerReconnectedEvents.TryDequeue(out var pid))
                OnLog?.Invoke($"[Net] Player {pid} reconnected.");

            // Check if client lost connection
            if (_state == SessionState.Running && _client.State == ClientState.Disconnected)
            {
                EndReason = "Connection lost";
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
