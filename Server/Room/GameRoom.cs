// Zero Unity dependencies. Pure .NET.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using BurstStrike.Net.Shared.Protocol;
using BurstStrike.Net.Server.Tick;

namespace BurstStrike.Net.Server.Room
{
    /// <summary>
    /// Room lifecycle states.
    /// </summary>
    public enum RoomState : byte
    {
        /// <summary>Just created, not accepting players yet.</summary>
        Created = 0,
        /// <summary>Waiting for enough players to join.</summary>
        Waiting = 1,
        /// <summary>Game is running — tick loop active.</summary>
        Running = 2,
        /// <summary>Room is closing, cleanup in progress.</summary>
        Closing = 3,
        /// <summary>Room fully destroyed, will be removed from manager.</summary>
        Destroyed = 4,
    }

    /// <summary>
    /// Room events that drive the state machine.
    /// </summary>
    public enum RoomEvent : byte
    {
        // ===== Player Events =====
        PlayerJoin = 0,
        PlayerLeave = 1,
        PlayerDisconnect = 2,
        PlayerReconnect = 3,

        // ===== Game Events =====
        GameStart = 4,
        GameEnd = 5,

        // ===== System Events =====
        Timeout = 6,
        Shutdown = 7,

        // ===== Error =====
        Error = 8,
    }

    /// <summary>
    /// One player slot within a room.
    /// </summary>
    public sealed class PlayerSlot
    {
        public readonly byte SlotIndex;
        public int AuthPlayerId;
        public string PlayerName;
        public IClientSession Session;
        public bool IsReady;
        public bool IsConnected;

        /// <summary>Pending input for the current server tick (null = no input yet).</summary>
        public Messages.TickInput? PendingInput;

        public PlayerSlot(byte index)
        {
            SlotIndex = index;
        }
    }

    /// <summary>
    /// Interface for a client session — Room only needs to send messages.
    /// This decouples Room from transport details.
    /// </summary>
    public interface IClientSession
    {
        int AuthPlayerId { get; }
        bool IsConnected { get; }
        Task SendAsync(byte[] payload, CancellationToken ct = default);
    }

    /// <summary>
    /// A single game room managing lockstep frame synchronization.
    /// 
    /// Event-driven state machine:
    ///   Created → Waiting → Running → Closing → Destroyed
    /// 
    /// During Running state, the room drives a tick clock:
    ///   1. Collect TickInput from all players (or timeout with empty input)
    ///   2. Merge into TickCommands
    ///   3. Broadcast to all players
    ///   4. Cache in CommandHistory
    ///   5. Advance tick
    /// 
    /// Reference: Red Alert / StarCraft lockstep — server is a dumb relay that
    /// merges and rebroadcasts commands without simulating the game.
    /// </summary>
    public sealed class GameRoom
    {
        public readonly string RoomId;
        public readonly int MaxPlayers;
        public readonly int TickRate;
        public readonly int CountdownTicks;

        private readonly PlayerSlot[] _slots;
        private readonly CommandHistory _history;
        private readonly object _lock = new object();

        private RoomState _state = RoomState.Created;
        private int _playerCount;
        private int _currentTick;
        private CancellationTokenSource _tickCts;
        private Task _tickLoopTask;

        /// <summary>Max ticks to wait for a player's input before advancing with empty input.</summary>
        public int InputTimeoutTicks { get; set; } = 10; // ~333ms at 30 tick/s

        /// <summary>How many ticks of no input before declaring a player disconnected.</summary>
        public int DisconnectTimeoutTicks { get; set; } = 300; // 10 seconds at 30 tick/s

        public RoomState State => _state;
        public int CurrentTick => _currentTick;
        public int PlayerCount => _playerCount;

        /// <summary>Callback for server logging.</summary>
        public Action<string> Log;

        /// <summary>Fired when room transitions to Destroyed state.</summary>
        public event Action<GameRoom> OnDestroyed;

        public GameRoom(string roomId, int maxPlayers = 2, int tickRate = 30, int countdownTicks = 90, int historyCapacity = 18000)
        {
            RoomId = roomId ?? throw new ArgumentNullException(nameof(roomId));
            MaxPlayers = maxPlayers;
            TickRate = tickRate;
            CountdownTicks = countdownTicks;

            _slots = new PlayerSlot[maxPlayers];
            for (int i = 0; i < maxPlayers; i++)
                _slots[i] = new PlayerSlot((byte)i);

            _history = new CommandHistory(historyCapacity);
        }

        // ── State Machine Entry Point ───────────────────────────────────

        /// <summary>
        /// Main event handler for the state machine.
        /// </summary>
        public void HandleEvent(RoomEvent evt)
        {
            lock (_lock)
            {
                if (_state == RoomState.Destroyed)
                    return;

                switch (_state)
                {
                    case RoomState.Created:
                        OnCreated(evt);
                        break;

                    case RoomState.Waiting:
                        OnWaiting(evt);
                        break;

                    case RoomState.Running:
                        OnRunning(evt);
                        break;

                    case RoomState.Closing:
                        OnClosing(evt);
                        break;
                }
            }
        }

        // ── State Handlers ───────────────────────────────────────────────

        private void OnCreated(RoomEvent evt)
        {
            switch (evt)
            {
                case RoomEvent.PlayerJoin:
                    TransitionTo(RoomState.Waiting);
                    break;

                case RoomEvent.Shutdown:
                case RoomEvent.Error:
                    TransitionTo(RoomState.Closing);
                    break;
            }
        }

        private void OnWaiting(RoomEvent evt)
        {
            switch (evt)
            {
                case RoomEvent.PlayerJoin:
                    if (IsFull())
                        HandleEvent(RoomEvent.GameStart);
                    break;

                case RoomEvent.PlayerLeave:
                    if (_playerCount == 0)
                        TransitionTo(RoomState.Closing);
                    break;

                case RoomEvent.GameStart:
                    TransitionTo(RoomState.Running);
                    break;

                case RoomEvent.Timeout:
                case RoomEvent.Error:
                case RoomEvent.Shutdown:
                    TransitionTo(RoomState.Closing);
                    break;
            }
        }

        private void OnRunning(RoomEvent evt)
        {
            switch (evt)
            {
                case RoomEvent.GameEnd:
                    TransitionTo(RoomState.Closing);
                    break;

                case RoomEvent.PlayerLeave:
                case RoomEvent.PlayerDisconnect:
                    if (_playerCount == 0)
                        TransitionTo(RoomState.Closing);
                    break;

                case RoomEvent.Timeout:
                case RoomEvent.Error:
                case RoomEvent.Shutdown:
                    TransitionTo(RoomState.Closing);
                    break;
            }
        }

        private void OnClosing(RoomEvent evt)
        {
            // Closing state doesn't handle new logic
        }

        // ── Transition Logic ─────────────────────────────────────────────

        private void TransitionTo(RoomState newState)
        {
            if (_state == newState) return;

            var oldState = _state;

            OnExit(oldState);

            _state = newState;

            OnEnter(newState);

            Log?.Invoke($"[Room {RoomId}] {oldState} → {newState}");
        }

        private void OnEnter(RoomState state)
        {
            switch (state)
            {
                case RoomState.Waiting:
                    StartWaitingTimer();
                    break;

                case RoomState.Running:
                    StartTick();
                    break;

                case RoomState.Closing:
                    StopTick();
                    Cleanup();
                    TransitionTo(RoomState.Destroyed);
                    break;

                case RoomState.Destroyed:
                    OnDestroyed?.Invoke(this);
                    break;
            }
        }

        private void OnExit(RoomState state)
        {
            switch (state)
            {
                case RoomState.Waiting:
                    StopWaitingTimer();
                    break;

                case RoomState.Running:
                    StopTick();
                    break;
            }
        }

        // ── Player management ────────────────────────────────────────────

        /// <summary>
        /// Try to add a player to the room.
        /// Returns the assigned slot index, or -1 if the room is full or not in Waiting state.
        /// </summary>
        public int TryAddPlayer(IClientSession session, int authPlayerId, string playerName)
        {
            lock (_lock)
            {
                if (_state != RoomState.Waiting)
                    return -1;

                // Find empty slot
                for (int i = 0; i < _slots.Length; i++)
                {
                    if (_slots[i].Session == null)
                    {
                        _slots[i].Session = session;
                        _slots[i].AuthPlayerId = authPlayerId;
                        _slots[i].PlayerName = playerName ?? $"Player{i}";
                        _slots[i].IsConnected = true;
                        _playerCount++;

                        Log?.Invoke($"[Room {RoomId}] Player '{_slots[i].PlayerName}' joined slot {i} ({_playerCount}/{MaxPlayers})");

                        // Trigger PlayerJoin event
                        HandleEvent(RoomEvent.PlayerJoin);

                        return i;
                    }
                }

                return -1; // full
            }
        }

        /// <summary>Handle player disconnection. During Running, keep slot but mark disconnected.</summary>
        public void PlayerDisconnected(IClientSession session)
        {
            lock (_lock)
            {
                for (int i = 0; i < _slots.Length; i++)
                {
                    if (_slots[i].Session == session)
                    {
                        _slots[i].IsConnected = false;
                        Log?.Invoke($"[Room {RoomId}] Player '{_slots[i].PlayerName}' (slot {i}) disconnected.");

                        if (_state == RoomState.Waiting)
                        {
                            // Remove player from slot
                            _slots[i].Session = null;
                            _slots[i].AuthPlayerId = 0;
                            _playerCount--;
                            HandleEvent(RoomEvent.PlayerLeave);
                        }
                        else if (_state == RoomState.Running)
                        {
                            // Notify others
                            BroadcastExcept(new Messages.PlayerDisconnected { PlayerId = (byte)i }.Encode(), i);
                            HandleEvent(RoomEvent.PlayerDisconnect);
                        }
                        break;
                    }
                }
            }
        }

        /// <summary>Handle player reconnection.</summary>
        public int TryReconnect(IClientSession session, int authPlayerId)
        {
            lock (_lock)
            {
                for (int i = 0; i < _slots.Length; i++)
                {
                    if (_slots[i].AuthPlayerId == authPlayerId && !_slots[i].IsConnected)
                    {
                        _slots[i].Session = session;
                        _slots[i].IsConnected = true;
                        Log?.Invoke($"[Room {RoomId}] Player '{_slots[i].PlayerName}' (slot {i}) reconnected.");
                        BroadcastExcept(new Messages.PlayerReconnected { PlayerId = (byte)i }.Encode(), i);
                        HandleEvent(RoomEvent.PlayerReconnect);
                        return i;
                    }
                }
                return -1;
            }
        }

        // ── Input collection ─────────────────────────────────────────────

        /// <summary>Called by ClientSession when a TickInput message is received.</summary>
        public void ReceiveInput(int slotIndex, Messages.TickInput input)
        {
            lock (_lock)
            {
                if (slotIndex < 0 || slotIndex >= _slots.Length) return;
                if (_state != RoomState.Running) return;
                _slots[slotIndex].PendingInput = input;
            }
        }

        /// <summary>Handle sync request for reconnection.</summary>
        public Messages.TickCommands[] HandleSyncRequest(int fromTick, int toTick)
        {
            lock (_lock)
            {
                return _history.GetRange(fromTick, toTick);
            }
        }

        // ── Tick loop management ─────────────────────────────────────────

        private CancellationTokenSource _waitingTimerCts;
        private Task _waitingTimerTask;

        private void StartWaitingTimer()
        {
            _waitingTimerCts = new CancellationTokenSource();
            _waitingTimerTask = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(30000, _waitingTimerCts.Token); // 30s timeout
                    lock (_lock)
                    {
                        if (_state == RoomState.Waiting && _playerCount > 0)
                        {
                            HandleEvent(RoomEvent.Timeout);
                        }
                    }
                }
                catch (OperationCanceledException) { }
            });
        }

        private void StopWaitingTimer()
        {
            _waitingTimerCts?.Cancel();
            try { _waitingTimerTask?.Wait(1000); }
            catch (Exception) { /* Task cancellation or timeout */ }
        }

        private void StartTick()
        {
            _tickCts = new CancellationTokenSource();
            _tickLoopTask = Task.Run(() => TickLoopProc(_tickCts.Token), _tickCts.Token);
        }

        private void StopTick()
        {
            _tickCts?.Cancel();
            try { _tickLoopTask?.Wait(1000); }
            catch (Exception) { /* Task cancellation or timeout */ }
        }

        private void Cleanup()
        {
            // Clean up resources
            _tickCts?.Dispose();
            _waitingTimerCts?.Dispose();
        }

        private bool IsFull() => _playerCount >= MaxPlayers;

        /// <summary>
        /// Run the room's tick loop. Called from a dedicated thread.
        /// </summary>
        private void TickLoopProc(CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            int tickMs = 1000 / TickRate;
            long nextTickTime = sw.ElapsedMilliseconds;

            while (!ct.IsCancellationRequested && _state != RoomState.Destroyed)
            {
                long now = sw.ElapsedMilliseconds;
                if (now < nextTickTime)
                {
                    Thread.Sleep(1);
                    continue;
                }
                nextTickTime += tickMs;

                // Anti-spiral: if too far behind, resync
                if (sw.ElapsedMilliseconds - nextTickTime > tickMs * 4)
                    nextTickTime = sw.ElapsedMilliseconds;

                lock (_lock)
                {
                    if (_state == RoomState.Running)
                    {
                        TickRunning();
                    }
                }
            }
        }

        private void TickRunning()
        {
            // Collect inputs from all players
            var inputs = new List<Messages.PlayerInput>(MaxPlayers);
            for (int i = 0; i < _slots.Length; i++)
            {
                var slot = _slots[i];
                if (slot.Session == null) continue;

                var input = slot.PendingInput;
                slot.PendingInput = null; // consume

                if (input.HasValue && input.Value.Commands != null && input.Value.Commands.Length > 0)
                {
                    inputs.Add(new Messages.PlayerInput
                    {
                        PlayerId = (byte)i,
                        Commands = input.Value.Commands,
                    });
                }
                // If no input, the player is idle this tick — that's fine for lockstep.
                // (An empty TickCommands still advances the tick.)
            }

            // Build merged TickCommands
            var tickCmd = new Messages.TickCommands
            {
                Tick = _currentTick,
                Inputs = inputs.ToArray(),
            };

            // Cache in history (for reconnection / replay)
            _history.Store(tickCmd);

            // Broadcast to all connected players
            var payload = tickCmd.Encode();
            BroadcastAll(payload);

            _currentTick++;
        }

        // ── Broadcast helpers ────────────────────────────────────────────

        private void BroadcastAll(byte[] payload)
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                var slot = _slots[i];
                if (slot.Session != null && slot.IsConnected)
                {
                    try
                    {
                        // Fire-and-forget: the tick loop must not wait for slow clients.
                        // If the send buffer fills up, that client will be disconnected.
                        _ = slot.Session.SendAsync(payload, CancellationToken.None);
                    }
                    catch
                    {
                        slot.IsConnected = false;
                    }
                }
            }
        }

        private void BroadcastExcept(byte[] payload, int exceptSlot)
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                if (i == exceptSlot) continue;
                var slot = _slots[i];
                if (slot.Session != null && slot.IsConnected)
                {
                    try { _ = slot.Session.SendAsync(payload, CancellationToken.None); }
                    catch { slot.IsConnected = false; }
                }
            }
        }
    }
}
