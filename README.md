# 🌐 Net Module (Assets/Game/Net)

A lightweight, deterministic **frame-synchronization (lockstep)**
networking module for multiplayer games.

------------------------------------------------------------------------

## 🎯 Target Audience

This module is designed for:

-   Multiplayer game developers (frame-sync / lockstep)
-   Backend/server engineers
-   Networking/debugging engineers
-   Contributors extending transport or protocol layers

------------------------------------------------------------------------

## ⚡ One-Line Overview

> A tick-based frame synchronization system with client bridging, server
> simulation, session abstraction, and shared protocol/transport layers.

------------------------------------------------------------------------

# 🧱 Architecture Overview

                ┌───────────────┐
                │   Client      │
                │ NetSyncClient │
                └──────┬────────┘
                       │
                       ▼
            ┌─────────────────────┐
            │ Transport Layer     │
            │ (TcpTransport)      │
            └────────┬────────────┘
                     │
                     ▼
            ┌─────────────────────┐
            │   Server            │
            │ FrameSyncServer     │
            └────────┬────────────┘
                     │
                     ▼
            ┌─────────────────────┐
            │   GameRoom          │
            │ Tick Simulation     │
            └────────┬────────────┘
                     │
                     ▼
            ┌─────────────────────┐
            │ CommandHistory      │
            │ (Replay / Recovery) │
            └─────────────────────┘

------------------------------------------------------------------------

# 📁 Project Structure

    Assets/Game/Net/
    │
    ├── Client/                 
    │   ├── NetSyncClient.cs
    │   └── NetSyncBridge.cs
    │
    ├── Server/                 
    │   ├── Program.cs
    │   ├── FrameSyncServer.cs
    │   ├── Room/
    │   │   ├── GameRoom.cs
    │   │   └── RoomManager.cs
    │   └── Tick/
    │       └── CommandHistory.cs
    │
    ├── Session/                
    │   ├── LocalSession.cs
    │   ├── LanSession.cs
    │   └── NetworkSession.cs
    │
    ├── Shared/                 
    │   ├── Protocol/
    │   │   ├── Messages.cs
    │   │   └── MessageType.cs
    │   ├── Transport/
    │   │   └── TcpTransport.cs
    │   └── NetConstants.cs

------------------------------------------------------------------------

# 🔄 Core Flow

> Client sends input → Server collects → Executes → Broadcasts → Client
> applies

------------------------------------------------------------------------

# 📡 High-Level Data Flow

    Client (NetSyncClient)
        │
        │ send Input @ tick N
        ▼
    Transport (TCP)
        │
        ▼
    Server (FrameSyncServer / GameRoom)
        ├── Collect inputs
        ├── Store in CommandHistory
        ├── Execute tick
        └── Broadcast result
        ▲
        │
    Transport
        │
        ▼
    Client Receive
        → NetSyncBridge
        → Inject into Game Logic

------------------------------------------------------------------------

# ⏱️ Tick Timeline (Frame Sync)

    Time → →
    
    Tick:     0        1        2        3
    
    Client:
            send0    send1    send2
    
    Server:
            collect0
            execute0
            broadcast0
    
                     collect1
                     execute1
                     broadcast1
    
    Client:
            apply0   apply1   apply2

------------------------------------------------------------------------

# 🧩 Key Components

## 🟦 Client Layer

### NetSyncClient.cs

-   Handles connection lifecycle
-   Sends local player inputs
-   Receives server tick data

### NetSyncBridge.cs

-   Decouples networking from game logic
-   Injects received commands into simulation

``` csharp
bridge.InjectCommand = (bytes, tick, seq) =>
    world.ReceiveEncodedCommand(bytes, tick, seq);
```

------------------------------------------------------------------------

## 🟥 Server Layer

### FrameSyncServer.cs

-   Core tick loop
-   Collects inputs
-   Executes simulation
-   Broadcasts results

### GameRoom.cs

-   Manages match lifecycle
-   Player state & synchronization

### CommandHistory.cs

-   Ring buffer of past ticks
-   Supports reconnection replay & full match replay

------------------------------------------------------------------------

## 🟨 Shared Layer

### Messages.cs

-   Defines protocol messages

### MessageType.cs

-   Message types enum

### TcpTransport.cs

-   TCP communication layer

------------------------------------------------------------------------

## 🟩 Session Layer

  Type             Use Case

---------------- ---------------

  LocalSession     Single-player
  LanSession       LAN
  NetworkSession   Internet

------------------------------------------------------------------------

# 🚀 Quick Start

## Run Server

``` bash
dotnet run --project Server
```

## Build

``` bash
dotnet build
```

------------------------------------------------------------------------

# 🧪 Debugging Tips

-   Check server logs
-   Verify tick consistency
-   Ensure stable tick rate

------------------------------------------------------------------------

# ⚙️ Extension Guide

## Replace TCP with UDP

Create:

    Shared/Transport/UdpTransport.cs

------------------------------------------------------------------------

## Add Message Types

-   Update Messages.cs
-   Update MessageType.cs

------------------------------------------------------------------------

# 🔧 Build & Deployment

## Publish

``` bash
dotnet publish -c Release -r linux-x64 --self-contained true
```

------------------------------------------------------------------------

# 🤝 Contribution

Submit issues with logs and reproduction steps.

------------------------------------------------------------------------

# 🧠 Final Note

Deterministic, scalable multiplayer foundation.
