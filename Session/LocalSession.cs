// Zero Unity dependencies. Pure .NET.
using System;

namespace BurstStrike.Net.Session
{
    /// <summary>
    /// Local (single-player / offline) game session.
    ///
    /// Commands bypass the network entirely:
    ///   SubmitCommands → OnCommandReady → LogicWorld (same tick, zero latency).
    ///
    /// The LogicWorld tick loop is self-driven (its own Stopwatch-based loop).
    /// This session just acts as a passthrough adapter so World.cs doesn't need
    /// special-case code for offline play.
    /// </summary>
    public sealed class LocalSession : IGameSession
    {
        private SessionState _state = SessionState.None;
        private int _currentTick;
        private readonly int _tickRate;
        private readonly int _randomSeed;

        // ── IGameSession ─────────────────────────────────────────────────

        public string ModeName => "Local";
        public int LocalPlayerId => 0;
        public int PlayerCount => 1;
        public int TickRate => _tickRate;
        public int RandomSeed => _randomSeed;

        public SessionState State => _state;
        public string EndReason { get; private set; }

        public Action<byte[], int, int> OnCommandReady { get; set; }
        public Action<int> OnTickReady { get; set; }
        public Action<SessionState> OnStateChanged { get; set; }
        public Action<string> OnLog { get; set; }

        // ── Construction ─────────────────────────────────────────────────

        public LocalSession(int tickRate = 30, int randomSeed = 0)
        {
            _tickRate = tickRate;
            _randomSeed = randomSeed != 0 ? randomSeed : Environment.TickCount;
        }

        // ── Lifecycle ────────────────────────────────────────────────────

        public void Start()
        {
            if (_state != SessionState.None) return;

            _currentTick = 0;
            SetState(SessionState.Running);
            OnLog?.Invoke("[Local] Session started (single-player).");
        }

        public void Stop()
        {
            if (_state == SessionState.Ended || _state == SessionState.None) return;
            EndReason = "Stopped";
            SetState(SessionState.Ended);
            OnLog?.Invoke("[Local] Session stopped.");
        }

        public void Update()
        {
            // Nothing to poll in local mode — everything is synchronous.
        }

        // ── Command pipeline ─────────────────────────────────────────────

        /// <summary>
        /// In local mode, commands are injected directly (zero latency).
        /// Each command is passed through OnCommandReady immediately.
        /// Then OnTickReady is called so LogicWorld can advance.
        /// </summary>
        public void SubmitCommands(int tick, byte[][] encodedCommands)
        {
            if (_state != SessionState.Running) return;

            var inject = OnCommandReady;
            if (inject != null && encodedCommands != null)
            {
                for (int i = 0; i < encodedCommands.Length; i++)
                {
                    var cmd = encodedCommands[i];
                    if (cmd == null || cmd.Length == 0) continue;
                    // Local player = 0, sequence = command index
                    inject(cmd, tick, i);
                }
            }

            _currentTick = tick;
            OnTickReady?.Invoke(tick);
        }

        public void Dispose()
        {
            Stop();
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
