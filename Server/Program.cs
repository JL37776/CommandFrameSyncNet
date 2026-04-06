// Standalone server entry point. Zero Unity dependencies.
// Can be run as: dotnet run --project BurstStrike.Net.Server
// Or compiled to a standalone executable.
using System;
using System.Threading;
using BurstStrike.Net.Server;
using BurstStrike.Net.Server.Auth;

namespace BurstStrike.Net.Server
{
    /// <summary>
    /// Console entry point for the frame synchronization server.
    /// 
    /// Usage:
    ///   FrameSyncServer.exe [port] [maxPlayers] [tickRate]
    ///   
    /// Defaults:
    ///   port = 9050
    ///   maxPlayers = 2
    ///   tickRate = 30
    /// </summary>
    public static class Program
    {
        public static void Main(string[] args)
        {
            var config = new ServerConfig();

            // Parse CLI args
            if (args.Length > 0 && int.TryParse(args[0], out var port))
                config.Port = port;
            if (args.Length > 1 && int.TryParse(args[1], out var maxPlayers))
                config.MaxPlayers = maxPlayers;
            if (args.Length > 2 && int.TryParse(args[2], out var tickRate))
                config.TickRate = tickRate;

            Console.WriteLine("═══════════════════════════════════════════");
            Console.WriteLine("  Burst Strike — Frame Sync Server");
            Console.WriteLine("═══════════════════════════════════════════");
            Console.WriteLine($"  Port:       {config.Port}");
            Console.WriteLine($"  MaxPlayers: {config.MaxPlayers}");
            Console.WriteLine($"  TickRate:   {config.TickRate}");
            Console.WriteLine($"  Protocol:   v{config.ProtocolVersion}");
            Console.WriteLine("═══════════════════════════════════════════");

            // Use AllowAll auth for development. Replace with HttpAuthValidator in production.
            IAuthValidator auth = new AllowAllAuthValidator();

            var server = new FrameSyncServer(config, auth);
            server.Log = msg => Console.WriteLine($"  {msg}");

            server.Start();

            Console.WriteLine("  Server started. Press Ctrl+C to stop.");

            // Wait for Ctrl+C
            var exitEvent = new ManualResetEventSlim(false);
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                exitEvent.Set();
            };
            exitEvent.Wait();

            server.Stop();
            Console.WriteLine("  Server stopped.");
        }
    }
}
