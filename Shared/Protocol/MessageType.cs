// Zero Unity dependencies. Pure .NET.
namespace BurstStrike.Net.Shared.Protocol
{
    /// <summary>
    /// All message types exchanged between client and frame-sync server.
    /// Wire format: first byte of every message payload is the MessageType.
    /// </summary>
    public enum MessageType : byte
    {
        // ── Client → Server ──────────────────────────────────────────────
        /// <summary>Client requests to join a room. Carries auth token.</summary>
        JoinRequest = 0x01,

        /// <summary>Client signals it has loaded and is ready to start.</summary>
        ClientReady = 0x02,

        /// <summary>Client sends its commands for a specific tick.</summary>
        TickInput = 0x03,

        /// <summary>Client requests historical ticks for reconnection.</summary>
        SyncRequest = 0x04,

        // ── Server → Client ──────────────────────────────────────────────
        /// <summary>Server responds to JoinRequest (accept/reject).</summary>
        JoinResult = 0x10,

        /// <summary>Server notifies all players that the game is starting.</summary>
        GameStart = 0x11,

        /// <summary>Server broadcasts merged commands for a tick to all players.</summary>
        TickCommands = 0x12,

        /// <summary>Server sends historical ticks in response to SyncRequest.</summary>
        SyncResponse = 0x13,

        /// <summary>Server notifies that a player disconnected.</summary>
        PlayerDisconnected = 0x14,

        /// <summary>Server notifies that a player reconnected.</summary>
        PlayerReconnected = 0x15,

        /// <summary>Server notifies game has ended.</summary>
        GameEnd = 0x16,

        // ── Bidirectional ────────────────────────────────────────────────
        Ping = 0xF0,
        Pong = 0xF1,
        Disconnect = 0xFE,
        Error = 0xFF,
    }
}
