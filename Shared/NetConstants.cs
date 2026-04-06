// Zero Unity dependencies. Pure .NET.
namespace BurstStrike.Net.Shared
{
    /// <summary>
    /// Shared constants for the network protocol.
    /// </summary>
    public static class NetConstants
    {
        /// <summary>Current protocol version. Client and server must match.</summary>
        public const int ProtocolVersion = 1;

        /// <summary>Default server port.</summary>
        public const int DefaultPort = 9050;

        /// <summary>Default tick rate (ticks per second).</summary>
        public const int DefaultTickRate = 30;

        /// <summary>Default max players per room.</summary>
        public const int DefaultMaxPlayers = 2;

        /// <summary>Default countdown before game starts (ticks).</summary>
        public const int DefaultCountdownTicks = 90; // 3 seconds at 30 tick/s

        /// <summary>Max message payload size (1 MB).</summary>
        public const int MaxMessageSize = 1024 * 1024;

        /// <summary>Command history capacity (ticks). 18000 = 10 minutes at 30 tick/s.</summary>
        public const int DefaultHistoryCapacity = 18000;

        /// <summary>Default client input delay (ticks). Commands are sent for tick T + InputDelay.</summary>
        public const int DefaultInputDelay = 2;
    }
}
