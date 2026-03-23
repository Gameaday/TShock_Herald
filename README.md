# The Herald
### Event & Communication Engine for TShock

The Herald is a specialized TShock plugin designed to handle automated server announcements, event-based messaging, and Discord integrations. By isolating communication logic into a standalone module, it reduces CPU overhead and ensures that gameplay events—like boss kills or player deaths—are met with dynamic, configurable responses.

---

## Features

* **JSON Hot-Reloading**: Update your broadcast rules in real-time. The Herald monitors your configuration files and reloads them instantly without requiring a server restart.
* **Command Execution**: Separate visual messages from functional actions. Execute native TShock commands (e.g., `/time dawn`) silently as the Server while displaying custom text to players.
* **Dynamic Variable Parsing**: Support for `{player}`, `{world}`, `{context}`, and other tags to create immersive, automated responses.
* **Environmental Filtering**: Set broadcasts to trigger only during specific conditions, such as Blood Moons, rainy weather, or when "Streamer Mode" is active.
* **Performance Optimized**: Automatically "sleeps" and skips update logic when the server is empty to conserve resources.

---

## Installation

1. Download the `Herald.dll` from the repository releases.
2. Place the `.dll` into your TShock `ServerPlugins` folder.
3. Restart your server to generate the directory structure at `tshock/Herald/`.
4. Place your broadcast JSON files (e.g., `Admin.json`, `LFG.json`) into the `tshock/Herald/Broadcasts/` folder.

---

## Configuration

### HeraldConfig.json
This file manages global settings and streamer integration.

| Property | Description |
| :--- | :--- |
| `enableBroadcaster` | Master toggle for all broadcast logic. |
| `globalDiscordWebhookUrl` | Default webhook for all Discord announcements. |
| `streamerName` | The name used for the `{streamer}` variable. |
| `streamUrl` | The URL used for the `{streamUrl}` variable. |
| `isStreaming` | Boolean toggle for the `streaming` condition. |

### Broadcast JSONs
Each file in the `Broadcasts/` folder contains an array of broadcast objects.

| Field | Description |
| :--- | :--- |
| `name` | Unique identifier for the broadcast. |
| `triggerTypes` | Types: `Chat`, `Death`, `TimeTransition`, `NPCKill`, `Timed`. |
| `triggerWords` | Keywords that fire the event (e.g., `!lfg`, `lava`). |
| `commands` | List of TShock commands to execute as the Server. |
| `messages` | List of messages; one is chosen at random per trigger. |
| `conditions` | Logic checks: `bloodmoon`, `raining`, `day`, `night`, `streaming`. |

---

## Dynamic Variables

The following tags can be used in both `messages` and `commands`:

* **`{player}`**: Name of the player triggering the event.
* **`{world}`**: Current name of the world.
* **`{context}`**: Contextual data, such as a formatted death reason.
* **`{online}`**: Total number of active players.
* **`{streamer}`**: Streamer name from the global config.
* **`{streamUrl}`**: Twitch/Stream link from the global config.

---

## Commands

* **`/herald reload`**: Manually reloads all configurations and broadcast files.
* **`/herald live`**: Toggles "Streamer Mode" status in the config.

---

## License
MIT
