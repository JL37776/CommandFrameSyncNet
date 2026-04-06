// Zero Unity dependencies. Pure .NET.
// This is the abstraction that World.cs programs against.
// All three modes (Local, LAN, Network) implement this interface.
using System;

namespace BurstStrike.Net.Session
{
    /// <summary>
    /// Game session lifecycle state.
    /// Identical across all modes (Local/LAN/Network).
    /// </summary>
    public enum SessionState
    {
        /// <summary>Not yet started or fully torn down.</summary>
        None,
        /// <summary>Connecting / waiting for players.</summary>
        WaitingForPlayers,
        /// <summary>All players ready, countdown running.</summary>
        Countdown,
        /// <summary>Game simulation is running.</summary>
        Running,
        /// <summary>Game has ended normally or abnormally.</summary>
        Ended,
    }

    /// <summary>
    /// Unified game session interface.
    ///
    /// The game layer (World.cs) programs against this interface and is completely
    /// agnostic about whether we're in Local, LAN-host, LAN-guest, or Network mode.
    ///
    /// Responsibilities:
    ///   - Start/stop the session
    ///   - Submit local commands each tick
    ///   - Receive merged commands for execution
    ///   - Report session state and errors
    ///
    /// Threading: all methods are called from the main thread unless noted otherwise.
    /// </summary>
    public interface IGameSession : IDisposable
    {
        // ── Identity ─────────────────────────────────────────────────────

        /// <summary>Human-readable mode name ("Local", "LAN-Host", "LAN-Guest", "Network").</summary>
        string ModeName { get; }

        /// <summary>This player's slot index (0-based). Assigned during session start.</summary>
        int LocalPlayerId { get; }

        /// <summary>Total number of players in the session.</summary>
        int PlayerCount { get; }

        /// <summary>Simulation tick rate (ticks per second).</summary>
        int TickRate { get; }

        /// <summary>Deterministic random seed shared by all participants.</summary>
        int RandomSeed { get; }

        // ── State ────────────────────────────────────────────────────────

        SessionState State { get; }

        /// <summary>If State == Ended, the reason string (or null).</summary>
        string EndReason { get; }

        // ── Lifecycle ────────────────────────────────────────────────────

        /// <summary>
        /// Begin the session asynchronously.
        /// For Local: returns immediately (always succeeds).
        /// For LAN-Host: starts embedded server, waits for guests.
        /// For LAN-Guest: connects to host.
        /// For Network: connects to remote server.
        /// </summary>
        void Start();

        /// <summary>Stop the session and release all resources.</summary>
        void Stop();

        // ── Per-frame update (called from main-thread Update) ────────────

        /// <summary>
        /// Main-thread per-frame poll.
        /// Drains network messages, advances state machine, delivers events.
        /// Must be called every frame from World.Update().
        /// </summary>
        void Update();

        // ── Command pipeline ─────────────────────────────────────────────

        /// <summary>
        /// Submit local encoded commands for the current logic tick.
        /// In Local mode: injected directly.
        /// In network modes: sent to server, which echoes them back merged with others.
        /// </summary>
        void SubmitCommands(int tick, byte[][] encodedCommands);

        /// <summary>
        /// Callback invoked (from Update) when a tick's merged commands are ready.
        /// The game layer decodes and injects them into LogicWorld.
        ///
        /// Signature: (byte[] encodedCommand, int tick, int sequence) → void
        /// </summary>
        Action<byte[], int, int> OnCommandReady { get; set; }

        /// <summary>
        /// Callback invoked when a tick is fully delivered (all players' commands received).
        /// LogicWorld should advance past this tick.
        ///
        /// Signature: (int tick) → void
        /// </summary>
        Action<int> OnTickReady { get; set; }

        /// <summary>
        /// Callback invoked when the session state changes.
        /// Useful for UI transitions (lobby → countdown → game → results).
        ///
        /// Signature: (SessionState newState) → void
        /// </summary>
        Action<SessionState> OnStateChanged { get; set; }

        /// <summary>
        /// Log messages from the session layer. Poll or subscribe.
        /// </summary>
        Action<string> OnLog { get; set; }
    }
}
