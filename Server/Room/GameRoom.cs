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
        /// <summary>Waiting for enough players to join.</summary>
        Waiting,
        /// <summary>All slots filled, running countdown before game starts.</summary>
        Countdown,
        /// <summary>Game is running — tick loop active.</summary>
        Running,
        /// <summary>Game has ended.</summary>
        Ended,
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
    /// Lifecycle:
    ///   Waiting → (all slots filled) → Countdown → (timer expires) → Running → (game ends) → Ended
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

        private RoomState _state = RoomState.Waiting;
        private int _playerCount;
        private int _currentTick;
        private int _countdownRemaining;
        private int _randomSeed;

        /// <summary>Max ticks to wait for a player's input before advancing with empty input.</summary>
        public int InputTimeoutTicks { get; set; } = 10; // ~333ms at 30 tick/s

        /// <summary>How many ticks of no input before declaring a player disconnected.</summary>
        public int DisconnectTimeoutTicks { get; set; } = 300; // 10 seconds at 30 tick/s

        public RoomState State => _state;
        public int CurrentTick => _currentTick;
        public int PlayerCount => _playerCount;

        /// <summary>Callback for server logging.</summary>
        public Action<string> Log;

        public GameRoom(string roomId, int maxPlayers = 2, int tickRate = 30, int countdownTicks = 90, int historyCapacity = 18000)
        {
            RoomId = roomId ?? throw new ArgumentNullException(nameof(roomId));
            MaxPlayers = maxPlayers;
            TickRate = tickRate;
            CountdownTicks = countdownTicks;
            _countdownRemaining = countdownTicks;
            _randomSeed = Environment.TickCount ^ roomId.GetHashCode();

            _slots = new PlayerSlot[maxPlayers];
            for (int i = 0; i < maxPlayers; i++)
                _slots[i] = new PlayerSlot((byte)i);

            _history = new CommandHistory(historyCapacity);
        }

        // ── Player management ────────────────────────────────────────────

        /// <summary>
        /// Try to add a player to the room.
        /// Returns the assigned slot index, or -1 if the room is full.
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

                        // Check if room is full → start countdown
                        if (_playerCount >= MaxPlayers)
                        {
                            _state = RoomState.Countdown;
                            _countdownRemaining = CountdownTicks;
                            Log?.Invoke($"[Room {RoomId}] Room full. Countdown started ({CountdownTicks} ticks).");
                        }

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
                        }
                        else if (_state == RoomState.Running)
                        {
                            // Notify others
                            BroadcastExcept(new Messages.PlayerDisconnected { PlayerId = (byte)i }.Encode(), i);
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

        // ── Tick loop (called by TickDriver) ─────────────────────────────

        /// <summary>
        /// Run the room's tick loop. Called from a dedicated thread.
        /// </summary>
        public void Run(CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            int tickMs = 1000 / TickRate;
            long nextTickTime = sw.ElapsedMilliseconds;

            while (!ct.IsCancellationRequested && _state != RoomState.Ended)
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
                    switch (_state)
                    {
                        case RoomState.Countdown:
                            TickCountdown();
                            break;
                        case RoomState.Running:
                            TickRunning();
                            break;
                    }
                }
            }
        }

        private void TickCountdown()
        {
            _countdownRemaining--;
            if (_countdownRemaining <= 0)
            {
                // Transition to Running
                _state = RoomState.Running;
                _currentTick = 0;

                // Broadcast GameStart to all players
                var gameStart = new Messages.GameStart
                {
                    PlayerCount = (byte)MaxPlayers,
                    TickRate = TickRate,
                    CountdownTicks = 0,
                    RandomSeed = _randomSeed,
                };
                var payload = gameStart.Encode();
                BroadcastAll(payload);

                Log?.Invoke($"[Room {RoomId}] Game started! Tick rate={TickRate}");
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
