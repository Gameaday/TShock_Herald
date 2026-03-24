using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;

#nullable enable

namespace Herald;

[ApiVersion(2, 1)]
public class HeraldPlugin : TerrariaPlugin
{
    public override string Name => "The Herald";
    public override Version Version => new Version(1, 1, 0);
    public override string Author => "HistoryLabs";

    private string BasePath => Path.Combine(TShock.SavePath, "Herald");
    private string BroadcastsPath => Path.Combine(BasePath, "Broadcasts");
    private string ConfigPath => Path.Combine(BasePath, "HeraldConfig.json");

    private HeraldConfig _config = new();
    private FileSystemWatcher? _configWatcher;
    private DateTime _lastConfigReload = DateTime.UtcNow;
    private bool _wasDayTime;
    private int _tickCounter = 0;
    
    private static readonly HttpClient _httpClient = new();
    private readonly ConcurrentDictionary<string, DateTime> _webhookDebouncer = new(); // NEW: Prevents Discord rate limits

    // NEW: Pre-cached lists for zero-LINQ allocations in high-frequency hooks
    private List<Broadcast> _chatBroadcasts = new();
    private List<Broadcast> _npcBroadcasts = new();
    private List<Broadcast> _deathBroadcasts = new();
    private List<Broadcast> _timeBroadcasts = new();

    public HeraldPlugin(Main game) : base(game) { }

    public override void Initialize()
    {
        Directory.CreateDirectory(BasePath);
        Directory.CreateDirectory(BroadcastsPath);

        LoadConfig();
        LoadBroadcasts();
        StartHotReloader();

        _wasDayTime = Main.dayTime;

        ServerApi.Hooks.ServerChat.Register(this, OnChat);
        ServerApi.Hooks.NetGetData.Register(this, OnGetData);
        ServerApi.Hooks.NpcKilled.Register(this, OnNpcKilled);
        ServerApi.Hooks.GameUpdate.Register(this, OnUpdate);

        Commands.ChatCommands.Add(new Command("herald.admin", AdminCommand, "herald"));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _configWatcher?.Dispose();
            ServerApi.Hooks.ServerChat.Deregister(this, OnChat);
            ServerApi.Hooks.NetGetData.Deregister(this, OnGetData);
            ServerApi.Hooks.NpcKilled.Deregister(this, OnNpcKilled);
            ServerApi.Hooks.GameUpdate.Deregister(this, OnUpdate);
        }
        base.Dispose(disposing);
    }

    private void StartHotReloader()
    {
        try
        {
            _configWatcher = new FileSystemWatcher(BasePath)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                IncludeSubdirectories = true,
                EnableRaisingEvents = true
            };
            _configWatcher.Changed += (s, e) => 
            {
                if ((DateTime.UtcNow - _lastConfigReload).TotalSeconds > 1.5)
                {
                    _lastConfigReload = DateTime.UtcNow;
                    if (e.FullPath.Contains("HeraldConfig.json")) Task.Delay(500).ContinueWith(_ => LoadConfig());
                    else if (e.FullPath.Contains("Broadcasts")) Task.Delay(500).ContinueWith(_ => LoadBroadcasts());
                }
            };
        }
        catch (Exception ex) { TShock.Log.ConsoleError($"[Herald] FileWatcher failed: {ex.Message}"); }
    }

    private void LoadConfig()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var text = File.ReadAllText(ConfigPath);
                _config = JsonSerializer.Deserialize<HeraldConfig>(text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new HeraldConfig();
            }
            else
            {
                var options = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
                File.WriteAllText(ConfigPath, JsonSerializer.Serialize(_config, options));
            }
        }
        catch (Exception ex) { TShock.Log.ConsoleError($"[Herald] Config Error: {ex.Message}"); }
    }

    private void LoadBroadcasts()
    {
        var tempChat = new List<Broadcast>();
        var tempNpc = new List<Broadcast>();
        var tempDeath = new List<Broadcast>();
        var tempTime = new List<Broadcast>();
        int total = 0;

        try
        {
            var files = Directory.GetFiles(BroadcastsPath, "*.json");
            foreach (var file in files)
            {
                try
                {
                    var text = File.ReadAllText(file);
                    var list = JsonSerializer.Deserialize<List<Broadcast>>(text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    
                    if (list != null)
                    {
                        foreach (var bc in list.Where(b => b.Enabled))
                        {
                            if (bc.TriggerTypes.Contains("Chat", StringComparer.OrdinalIgnoreCase)) tempChat.Add(bc);
                            if (bc.TriggerTypes.Contains("NPCKill", StringComparer.OrdinalIgnoreCase)) tempNpc.Add(bc);
                            if (bc.TriggerTypes.Contains("Death", StringComparer.OrdinalIgnoreCase)) tempDeath.Add(bc);
                            if (bc.TriggerTypes.Contains("TimeTransition", StringComparer.OrdinalIgnoreCase)) tempTime.Add(bc);
                            total++;
                        }
                    }
                }
                catch (Exception ex) { TShock.Log.ConsoleError($"[Herald] Failed to load {Path.GetFileName(file)}: {ex.Message}"); }
            }
            
            // Swap references atomically
            _chatBroadcasts = tempChat;
            _npcBroadcasts = tempNpc;
            _deathBroadcasts = tempDeath;
            _timeBroadcasts = tempTime;
            
            TShock.Log.ConsoleInfo($"[Herald] Indexed {total} active broadcast triggers.");
        }
        catch (Exception ex) { TShock.Log.ConsoleError($"[Herald] Broadcast Library Error: {ex.Message}"); }
    }

    private void OnUpdate(EventArgs args)
    {
        if (++_tickCounter < 60) return;
        _tickCounter = 0;

        if (!_config.EnableBroadcaster || !TShock.Players.Any(p => p != null && p.Active)) return;

        if (Main.dayTime != _wasDayTime)
        {
            _wasDayTime = Main.dayTime;
            string trigger = Main.dayTime ? "Dawn" : "Dusk";
            
            foreach (var bc in _timeBroadcasts)
            {
                if ((bc.TriggerWords.Count == 0 || bc.TriggerWords.Contains(trigger, StringComparer.OrdinalIgnoreCase)) && CheckConditions(bc))
                {
                    ExecuteBroadcast(bc, null);
                }
            }
        }
    }

    private void OnChat(ServerChatEventArgs args)
    {
        if (args.Handled || !_config.EnableBroadcaster) return;
        string text = args.Text.ToLower();
        var player = TShock.Players[args.Who];
        if (player == null) return;

        // ZERO LINQ OVERHEAD
        foreach (var bc in _chatBroadcasts)
        {
            if (bc.TriggerWords.Any(tw => text.Contains(tw.ToLower())))
            {
                if (CheckRegion(bc, player) && CheckAccess(bc, player) && CheckConditions(bc))
                {
                    ExecuteBroadcast(bc, player);
                    if (bc.HideTriggerText) args.Handled = true;
                }
            }
        }
    }

    private void OnNpcKilled(NpcKilledEventArgs args)
    {
        if (!_config.EnableBroadcaster) return;
        var npc = args.npc;
        
        // ZERO LINQ OVERHEAD
        foreach (var bc in _npcBroadcasts)
        {
            bool nameMatch = bc.TriggerNPCs.Count == 0 || bc.TriggerNPCs.Any(n => n.Equals(npc.FullName, StringComparison.OrdinalIgnoreCase));
            bool tagMatch = CheckSemanticTags(bc, npc);

            if (nameMatch && tagMatch && CheckConditions(bc)) ExecuteBroadcast(bc, null, npc.FullName);
        }
    }

    private void OnGetData(GetDataEventArgs args)
    {
        if (args.Handled || !_config.EnableBroadcaster || args.MsgID != PacketTypes.PlayerDeathV2) return;
        
        using var reader = new BinaryReader(new MemoryStream(args.Msg.readBuffer, args.Index, args.Length));
        int playerId = reader.ReadByte();
        var reason = Terraria.DataStructures.PlayerDeathReason.FromReader(reader);
        var player = TShock.Players[playerId];
        if (player == null || !player.Active) return;

        string deathMessage = reason.GetDeathText(player.Name).ToString();
        
        // ZERO LINQ OVERHEAD
        foreach (var bc in _deathBroadcasts)
        {
            if (bc.TriggerWords.Count == 0 || bc.TriggerWords.Any(tw => deathMessage.Contains(tw, StringComparison.OrdinalIgnoreCase)))
            {
                if (CheckRegion(bc, player) && CheckAccess(bc, player) && CheckConditions(bc))
                {
                    ExecuteBroadcast(bc, player, deathMessage);
                }
            }
        }
    }

    private bool CheckSemanticTags(Broadcast bc, NPC npc)
    {
        if (bc.TriggerTags.Count == 0) return true;

        foreach(var tag in bc.TriggerTags)
        {
            string t = tag.ToLower();
            if (t == "boss" && !npc.boss) return false;
            if (t == "flying" && !npc.noGravity) return false;
            if (t == "aquatic" && npc.waterMovementSpeed <= 0) return false;
            if (t == "slime" && npc.aiStyle != 1 && npc.aiStyle != 15) return false;
            if (t == "townnpc" && !npc.townNPC) return false;
            if (t == "enemy" && (npc.friendly || npc.townNPC)) return false;
            if (t == "elite" && npc.value < 1000) return false; 
        }
        return true; 
    }

    private bool CheckAccess(Broadcast bc, TSPlayer? player)
    {
        if (player == null) return true;
        if (!string.IsNullOrWhiteSpace(bc.Permission) && !player.HasPermission(bc.Permission)) return false;
        return bc.Groups.Count == 0 || bc.Groups.Contains("*") || bc.Groups.Any(g => player.Group.Name == g);
    }

    private bool CheckRegion(Broadcast bc, TSPlayer? player)
    {
        if (player == null || bc.TriggerRegions.Count == 0) return true;
        var reg = TShock.Regions.GetTopRegion(TShock.Regions.InAreaRegion(player.TileX, player.TileY));
        return reg != null && bc.TriggerRegions.Contains(reg.Name);
    }

    private bool CheckConditions(Broadcast bc)
    {
        if (bc.AllowedDays.Count > 0 && !bc.AllowedDays.Contains(DateTime.Now.DayOfWeek.ToString(), StringComparer.OrdinalIgnoreCase)) return false;
        if (bc.Conditions.Count == 0) return true;

        foreach (var cond in bc.Conditions)
        {
            string c = cond.ToLower();
            if (c == "raining" && !Main.raining) return false;
            if (c == "day" && !Main.dayTime) return false;
            if (c == "night" && Main.dayTime) return false;
            if (c == "bloodmoon" && !Main.bloodMoon) return false;
            if (c == "streaming" && !_config.IsStreaming) return false;
        }
        return true;
    }

    private void ExecuteBroadcast(Broadcast bc, TSPlayer? target, string? specialContext = null)
    {
        foreach (var cmd in bc.Commands)
        {
            string formattedCmd = cmd.Replace("{player}", target?.Name ?? "Server")
                                     .Replace("{world}", Main.worldName)
                                     .Replace("{context}", specialContext ?? "");
            Commands.HandleCommand(TSPlayer.Server, formattedCmd.TrimStart('/'));
        }

        if (bc.Messages.Count > 0)
        {
            string msg = bc.Messages[Random.Shared.Next(bc.Messages.Count)]
                .Replace("{player}", target?.Name ?? "Server")
                .Replace("{world}", Main.worldName)
                .Replace("{context}", specialContext ?? "")
                .Replace("{streamer}", _config.StreamerName)
                .Replace("{streamUrl}", _config.StreamUrl)
                .Replace("{online}", TShock.Players.Count(p => p != null && p.Active).ToString());

            if (bc.TriggerToWholeGroup) TSPlayer.All.SendMessage(msg, bc.TextColor);
            else target?.SendMessage(msg, bc.TextColor);

            string targetWebhook = !string.IsNullOrWhiteSpace(bc.DiscordWebhookUrl) ? bc.DiscordWebhookUrl : _config.GlobalDiscordWebhookUrl;
            if (!string.IsNullOrWhiteSpace(targetWebhook))
            {
                // DEBOUNCER: Prevent Discord Rate-Limit Bans
                string debounceKey = $"{targetWebhook}_{msg}";
                if (_webhookDebouncer.TryGetValue(debounceKey, out DateTime lastSent) && (DateTime.UtcNow - lastSent).TotalSeconds < 5) return;
                
                _webhookDebouncer[debounceKey] = DateTime.UtcNow;

                var payload = new { embeds = new[] { new { description = msg, color = (bc.TextColor.R << 16) | (bc.TextColor.G << 8) | bc.TextColor.B } } };
                _ = _httpClient.PostAsync(targetWebhook, new StringContent(JsonSerializer.Serialize(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }), Encoding.UTF8, "application/json"));
            }
        }
    }

    private void AdminCommand(CommandArgs args)
    {
        if (args.Parameters.Count == 0)
        {
            args.Player.SendErrorMessage("Usage: /herald <reload | live>");
            return;
        }

        string cmd = args.Parameters[0].ToLower();
        if (cmd == "reload")
        {
            LoadConfig();
            LoadBroadcasts();
            args.Player.SendSuccessMessage("[Herald] Configs reloaded.");
        }
        else if (cmd == "live")
        {
            _config.IsStreaming = !_config.IsStreaming;
            
            // Disable watcher temporarily to prevent collision IOException
            if (_configWatcher != null) _configWatcher.EnableRaisingEvents = false;
            
            var options = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(_config, options));
            
            if (_configWatcher != null) _configWatcher.EnableRaisingEvents = true;

            args.Player.SendSuccessMessage($"[Herald] Streamer Mode: {(_config.IsStreaming ? "ON" : "OFF")}");
        }
    }
}
