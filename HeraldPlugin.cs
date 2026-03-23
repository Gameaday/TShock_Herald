using System;
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
    public override Version Version => new Version(1, 0, 0);
    public override string Author => "HistoryLabs";

    private string BasePath => Path.Combine(TShock.SavePath, "Herald");
    private string BroadcastsPath => Path.Combine(BasePath, "Broadcasts");
    private string ConfigPath => Path.Combine(BasePath, "HeraldConfig.json");

    private HeraldConfig _config = new();
    private List<Broadcast> _allBroadcasts = new();
    private FileSystemWatcher? _configWatcher;
    private DateTime _lastConfigReload = DateTime.UtcNow;
    private bool _wasDayTime;
    private int _tickCounter = 0;
    private static readonly HttpClient _httpClient = new();

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

    // --- HOT RELOAD & CONFIG ---
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
                var tempConfig = JsonSerializer.Deserialize(File.ReadAllText(ConfigPath), HeraldJsonContext.Default.HeraldConfig);
                if (tempConfig != null) _config = tempConfig;
            }
            else File.WriteAllText(ConfigPath, JsonSerializer.Serialize(_config, HeraldJsonContext.Default.HeraldConfig));
        }
        catch (Exception ex) { TShock.Log.ConsoleError($"[Herald] Config Error: {ex.Message}"); }
    }

    private void LoadBroadcasts()
    {
        var safeTempList = new List<Broadcast>();
        try
        {
            var files = Directory.GetFiles(BroadcastsPath, "*.json");
            foreach (var file in files)
            {
                try
                {
                    var list = JsonSerializer.Deserialize(File.ReadAllText(file), HeraldJsonContext.Default.ListBroadcast);
                    if (list != null) safeTempList.AddRange(list);
                }
                catch (Exception ex) { TShock.Log.ConsoleError($"[Herald] Failed to load {Path.GetFileName(file)}: {ex.Message}"); }
            }
            _allBroadcasts = safeTempList;
            TShock.Log.ConsoleInfo($"[Herald] Indexed {_allBroadcasts.Count} broadcast triggers.");
        }
        catch (Exception ex) { TShock.Log.ConsoleError($"[Herald] Broadcast Library Error: {ex.Message}"); }
    }

    // --- EVENT HOOKS ---
    private void OnUpdate(EventArgs args)
    {
        if (++_tickCounter < 60) return;
        _tickCounter = 0;

        // Sleep if server is empty
        if (!_config.EnableBroadcaster || !TShock.Players.Any(p => p != null && p.Active)) return;

        if (Main.dayTime != _wasDayTime)
        {
            _wasDayTime = Main.dayTime;
            ProcessBroadcasts(Main.dayTime ? "Dawn" : "Dusk", "TimeTransition", null);
        }
    }

    private void OnChat(ServerChatEventArgs args)
    {
        if (args.Handled || !_config.EnableBroadcaster) return;
        string text = args.Text.ToLower();
        var player = TShock.Players[args.Who];
        if (player == null) return;

        foreach (var bc in _allBroadcasts.Where(b => b.Enabled && b.TriggerTypes.Contains("Chat")))
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
        foreach (var bc in _allBroadcasts.Where(b => b.Enabled && b.TriggerTypes.Contains("NPCKill")))
        {
            if (bc.TriggerNPCs.Count > 0 && !bc.TriggerNPCs.Any(n => n.Equals(npc.FullName, StringComparison.OrdinalIgnoreCase)))
                continue;

            if (CheckConditions(bc)) ExecuteBroadcast(bc, null);
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
        foreach (var bc in _allBroadcasts.Where(b => b.Enabled && b.TriggerTypes.Contains("Death")))
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

    // --- EVALUATION & EXECUTION ---
    private void ProcessBroadcasts(string trigger, string type, TSPlayer? target)
    {
        foreach (var bc in _allBroadcasts.Where(b => b.Enabled && b.TriggerTypes.Contains(type)))
        {
            if ((bc.TriggerWords.Count == 0 || bc.TriggerWords.Contains(trigger, StringComparer.OrdinalIgnoreCase)) 
                && CheckConditions(bc) && CheckAccess(bc, target) && CheckRegion(bc, target))
            {
                ExecuteBroadcast(bc, target);
            }
        }
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

        foreach (var cond in bc.Conditions.Select(c => c.ToLower()))
        {
            if (cond == "raining" && !Main.raining) return false;
            if (cond == "day" && !Main.dayTime) return false;
            if (cond == "night" && Main.dayTime) return false;
            if (cond == "bloodmoon" && !Main.bloodMoon) return false;
            if (cond == "streaming" && !_config.IsStreaming) return false;
        }
        return true;
    }

    private void ExecuteBroadcast(Broadcast bc, TSPlayer? target, string? specialContext = null)
    {
        // Execute Commands natively
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
                var payload = new { embeds = new[] { new { description = msg, color = (bc.TextColor.R << 16) | (bc.TextColor.G << 8) | bc.TextColor.B } } };
                _ = _httpClient.PostAsync(targetWebhook, new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
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
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(_config, HeraldJsonContext.Default.HeraldConfig));
            args.Player.SendSuccessMessage($"[Herald] Streamer Mode: {(_config.IsStreaming ? "ON" : "OFF")}");
        }
    }
}
