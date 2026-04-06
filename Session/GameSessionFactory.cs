// Zero Unity dependencies. Pure .NET.
namespace BurstStrike.Net.Session
{
    /// <summary>
    /// Game mode identifier.
    /// </summary>
    public enum GameMode
    {
        /// <summary>Single-player, no network. Commands go directly to LogicWorld.</summary>
        Local,
        /// <summary>LAN host: embedded server + local client. Others connect to this machine.</summary>
        LanHost,
        /// <summary>LAN guest: connect to another machine on the local network.</summary>
        LanGuest,
        /// <summary>Remote network: connect to a dedicated server.</summary>
        Network,
    }

    /// <summary>
    /// Configuration for creating a game session.
    /// </summary>
    public sealed class SessionConfig
    {
        // ── Common ───────────────────────────────────────────────────────
        public GameMode Mode { get; set; } = GameMode.Local;
        public int TickRate { get; set; } = 30;

        // ── Network / LAN Guest ──────────────────────────────────────────
        /// <summary>Server host IP (for Network and LAN-Guest modes).</summary>
        public string Host { get; set; } = "127.0.0.1";

        /// <summary>Server port.</summary>
        public int Port { get; set; } = 9050;

        /// <summary>Auth token from web server (for Network mode).</summary>
        public string AuthToken { get; set; } = "";

        /// <summary>Desired room id (null = auto-match).</summary>
        public string RoomId { get; set; }

        // ── LAN Host ─────────────────────────────────────────────────────
        /// <summary>Max players for hosted room.</summary>
        public int MaxPlayers { get; set; } = 2;

        /// <summary>Countdown ticks after room is full.</summary>
        public int CountdownTicks { get; set; } = 90;

        // ── Local ────────────────────────────────────────────────────────
        /// <summary>Random seed (0 = auto-generate).</summary>
        public int RandomSeed { get; set; }
    }

    /// <summary>
    /// Factory for creating IGameSession instances from configuration.
    /// </summary>
    public static class GameSessionFactory
    {
        /// <summary>
        /// Create a game session for the specified mode.
        /// </summary>
        public static IGameSession Create(SessionConfig config)
        {
            if (config == null) config = new SessionConfig();

            switch (config.Mode)
            {
                case GameMode.Local:
                    return new LocalSession(config.TickRate, config.RandomSeed);

                case GameMode.LanHost:
                    return new LanHostSession(
                        port: config.Port,
                        maxPlayers: config.MaxPlayers,
                        tickRate: config.TickRate,
                        countdownTicks: config.CountdownTicks);

                case GameMode.LanGuest:
                    return new LanGuestSession(
                        hostIp: config.Host,
                        port: config.Port,
                        tickRate: config.TickRate);

                case GameMode.Network:
                    return new NetworkSession(
                        host: config.Host,
                        port: config.Port,
                        authToken: config.AuthToken,
                        roomId: config.RoomId,
                        tickRate: config.TickRate);

                default:
                    return new LocalSession(config.TickRate, config.RandomSeed);
            }
        }

        /// <summary>Shorthand: create a local session.</summary>
        public static IGameSession CreateLocal(int tickRate = 30)
            => new LocalSession(tickRate);

        /// <summary>Shorthand: create a LAN host session.</summary>
        public static IGameSession CreateLanHost(int port = 9050, int maxPlayers = 2, int tickRate = 30)
            => new LanHostSession(port, maxPlayers, tickRate);

        /// <summary>Shorthand: create a LAN guest session.</summary>
        public static IGameSession CreateLanGuest(string hostIp, int port = 9050, int tickRate = 30)
            => new LanGuestSession(hostIp, port, tickRate);

        /// <summary>Shorthand: create a network session.</summary>
        public static IGameSession CreateNetwork(string host, int port, string authToken, string roomId = null, int tickRate = 30)
            => new NetworkSession(host, port, authToken, roomId, tickRate);
    }
}
