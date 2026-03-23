# TShock_Herald
Module: The Herald (Event &amp; Communication Engine) Objective: Act as the server's automated Town Crier. It monitors in-game triggers (chat, time, boss kills, deaths) and dispatches formatted broadcasts to the server and webhooks to Discord.


Module: The Herald (Event & Communication Engine)
Objective: Act as the server's automated Town Crier. It monitors in-game triggers (chat, time, boss kills, deaths) and dispatches formatted broadcasts to the server and webhooks to Discord.

Core Requirements
JSON Hot-Reloading: Must monitor the /Broadcasts folder and seamlessly update trigger conditions in memory without requiring a server restart.

Chat Interception: Must parse user chat for specific triggers (e.g., !lfg, !bright) and hide the trigger text from public chat if configured.

Native Command Execution: Must separate visual messages from functional commands, executing commands as the Server to bypass user permission checks.

State-Awareness: Must evaluate environmental conditions (Time of day, Blood Moon, Raining) before executing a broadcast payload.

Dynamic Variable Parsing: Must replace tags like {player}, {world}, and {context} dynamically based on the event source (e.g., mapping TShock's death reasons to {context}).

Technical Specifications
Dependencies: System.Text.Json, native HttpClient for Webhooks.

TShock Hooks: ServerChat, NpcKilled, NetGetData (Packet 118 - Player Death V2), GameUpdate (60-tick pulse for time-of-day transitions).

Commands: None natively. It relies entirely on chat parsing.

Data Structure: Requires mapping standard JSON arrays into strongly typed Broadcast C# objects.
