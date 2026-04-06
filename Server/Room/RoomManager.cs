// Zero Unity dependencies. Pure .NET.
using System;
using System.Collections.Generic;
using System.Threading;

namespace BurstStrike.Net.Server.Room
{
    /// <summary>
    /// Server-wide room manager. Creates, finds, and manages game rooms.
    /// 
    /// Default behavior (matching the requirement):
    /// - Empty roomId in JoinRequest → auto-match to the first waiting room, or create a new one.
    /// - Specific roomId → join that room, or create it if it doesn't exist.
    /// - Room full → reject.
    /// </summary>
    public sealed class RoomManager
    {
        private readonly Dictionary<string, GameRoom> _rooms = new Dictionary<string, GameRoom>();
        private readonly object _lock = new object();
        private int _roomCounter;

        // ── Configuration ────────────────────────────────────────────────

        /// <summary>Default max players per room.</summary>
        public int DefaultMaxPlayers { get; set; } = 2;

        /// <summary>Tick rate for new rooms.</summary>
        public int DefaultTickRate { get; set; } = 30;

        /// <summary>Countdown ticks before game starts (after room is full).</summary>
        public int DefaultCountdownTicks { get; set; } = 90; // 3 seconds at 30 tick/s

        /// <summary>Command history capacity (ticks to retain for reconnection).</summary>
        public int DefaultHistoryCapacity { get; set; } = 18000;

        /// <summary>Server-wide logger.</summary>
        public Action<string> Log;

        // ── Room operations ──────────────────────────────────────────────

        /// <summary>
        /// Find or create a room for a player.
        /// Returns the GameRoom and the assigned slot index.
        /// If slotIndex == -1, the join was rejected.
        /// </summary>
        public (GameRoom room, int slotIndex) JoinOrCreate(
            IClientSession session,
            int authPlayerId,
            string playerName,
            string requestedRoomId)
        {
            lock (_lock)
            {
                // 1) Try reconnect to existing room
                foreach (var kv in _rooms)
                {
                    int reconnSlot = kv.Value.TryReconnect(session, authPlayerId);
                    if (reconnSlot >= 0)
                        return (kv.Value, reconnSlot);
                }

                // 2) Specific room requested
                if (!string.IsNullOrEmpty(requestedRoomId))
                {
                    if (_rooms.TryGetValue(requestedRoomId, out var existing))
                    {
                        int slot = existing.TryAddPlayer(session, authPlayerId, playerName);
                        return (existing, slot);
                    }
                    else
                    {
                        // Create room with the requested id
                        var room = CreateRoom(requestedRoomId);
                        int slot = room.TryAddPlayer(session, authPlayerId, playerName);
                        return (room, slot);
                    }
                }

                // 3) Auto-match: find first waiting room with space
                foreach (var kv in _rooms)
                {
                    if (kv.Value.State == RoomState.Waiting && kv.Value.PlayerCount < kv.Value.MaxPlayers)
                    {
                        int slot = kv.Value.TryAddPlayer(session, authPlayerId, playerName);
                        if (slot >= 0)
                            return (kv.Value, slot);
                    }
                }

                // 4) No suitable room → create a new one
                {
                    var room = CreateRoom(null);
                    int slot = room.TryAddPlayer(session, authPlayerId, playerName);
                    return (room, slot);
                }
            }
        }

        private GameRoom CreateRoom(string roomId)
        {
            if (string.IsNullOrEmpty(roomId))
            {
                int num = Interlocked.Increment(ref _roomCounter);
                roomId = $"room_{num:D4}";
            }

            var room = new GameRoom(roomId, DefaultMaxPlayers, DefaultTickRate, DefaultCountdownTicks, DefaultHistoryCapacity);
            room.Log = Log;
            _rooms[roomId] = room;

            Log?.Invoke($"[RoomManager] Created room '{roomId}' (maxPlayers={DefaultMaxPlayers}, tickRate={DefaultTickRate})");
            return room;
        }

        /// <summary>Get a room by id.</summary>
        public GameRoom GetRoom(string roomId)
        {
            lock (_lock)
            {
                return _rooms.TryGetValue(roomId, out var r) ? r : null;
            }
        }

        /// <summary>Remove an ended room from the manager.</summary>
        public void RemoveRoom(string roomId)
        {
            lock (_lock)
            {
                _rooms.Remove(roomId);
                Log?.Invoke($"[RoomManager] Removed room '{roomId}'");
            }
        }

        /// <summary>Get all active room ids (for admin/debug).</summary>
        public string[] GetRoomIds()
        {
            lock (_lock)
            {
                var ids = new string[_rooms.Count];
                int i = 0;
                foreach (var kv in _rooms)
                    ids[i++] = kv.Key;
                return ids;
            }
        }
    }
}
