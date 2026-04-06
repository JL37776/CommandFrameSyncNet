// Zero Unity dependencies. Pure .NET.
using System;
using System.Threading.Tasks;
using BurstStrike.Net.Client;
using BurstStrike.Net.Shared.Protocol;

namespace BurstStrike.Net.Session
{
    /// <summary>
    /// LAN Guest game session.
    ///
    /// Connects to a LAN host's embedded server.
    /// Functionally identical to NetworkSession but with a different ModeName
    /// and slightly different default settings (lower input delay, no web auth).
    ///
    /// Data flow:
    ///   SubmitCommands → NetSyncClient → [TCP to host IP] → Host's Server →
    ///   Server merges → [TCP] → NetSyncClient → OnCommandReady → LogicWorld
    /// </summary>
    public sealed class LanGuestSession : IGameSession
    {
        private readonly string _hostIp;
        private readonly int _port;
        private readonly int _tickRate;

        private NetSyncClient _client;
        private NetSyncBridge _bridge;

        private SessionState _state = SessionState.None;
        private int _localPlayerId = -1;
        private int _playerCount;
        private int _randomSeed;

        // ── IGameSession ─────────────────────────────────────────────────

        public string ModeName => "LAN-Guest";
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

        /// <param name="hostIp">IP address of the LAN host machine.</param>
        /// <param name="port">Server port on the host (default 9050).</param>
        /// <param name="tickRate">Expected tick rate.</param>
        public LanGuestSession(string hostIp, int port = 9050, int tickRate = 30)
        {
            _hostIp = hostIp ?? throw new ArgumentNullException(nameof(hostIp));
            _port = port;
            _tickRate = tickRate;
        }

        // ── Lifecycle ────────────────────────────────────────────────────

        public void Start()
        {
            if (_state != SessionState.None) return;

            SetState(SessionState.WaitingForPlayers);
            OnLog?.Invoke($"[LAN-Guest] Connecting to {_hostIp}:{_port}...");

            _client = new NetSyncClient
            {
                ProtocolVersion = 1,
                InputDelay = 1, // low delay for LAN
            };
            _bridge = new NetSyncBridge(_client);
            _bridge.InjectCommand = (bytes, tick, seq) => OnCommandReady?.Invoke(bytes, tick, seq);
            _bridge.OnTickReady = tick => OnTickReady?.Invoke(tick);

            _ = ConnectAsync();
        }

        private async Task ConnectAsync()
        {
            try
            {
                // LAN: use a simple auth token (AllowAllAuth on host)
                string token = $"guest_{Environment.TickCount}";
                bool ok = await _client.ConnectAndJoinAsync(_hostIp, _port, token);
                if (!ok)
                {
                    EndReason = _client.JoinRejectReason ?? "Join rejected";
                    OnLog?.Invoke($"[LAN-Guest] {EndReason}");
                    SetState(SessionState.Ended);
                }
                else
                {
                    _localPlayerId = _client.LocalPlayerId;
                    OnLog?.Invoke($"[LAN-Guest] Joined room as player {_localPlayerId}. Waiting for game start...");
                }
            }
            catch (Exception ex)
            {
                EndReason = ex.Message;
                OnLog?.Invoke($"[LAN-Guest] Connection error: {ex.Message}");
                SetState(SessionState.Ended);
            }
        }

        public void Stop()
        {
            if (_state == SessionState.Ended || _state == SessionState.None) return;
            EndReason = "Stopped";
            _client?.Disconnect();
            SetState(SessionState.Ended);
            OnLog?.Invoke("[LAN-Guest] Session stopped.");
        }

        public void Update()
        {
            if (_client == null) return;

            while (_client.LogMessages.TryDequeue(out var msg))
                OnLog?.Invoke($"[LAN-Guest] {msg}");

            while (_client.GameStartEvents.TryDequeue(out var gs))
            {
                _playerCount = gs.PlayerCount;
                _randomSeed = gs.RandomSeed;
                OnLog?.Invoke($"[LAN-Guest] Game starting: {gs.PlayerCount} players, seed={gs.RandomSeed}");
                SetState(SessionState.Running);
            }

            if (_state == SessionState.Running)
            {
                _bridge.ProcessReceivedTicks();
            }

            while (_client.PlayerDisconnectedEvents.TryDequeue(out var pid))
                OnLog?.Invoke($"[LAN-Guest] Player {pid} disconnected.");

            while (_client.PlayerReconnectedEvents.TryDequeue(out var pid))
                OnLog?.Invoke($"[LAN-Guest] Player {pid} reconnected.");

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
