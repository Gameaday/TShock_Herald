using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Microsoft.Xna.Framework;

namespace Herald;

public class HeraldConfig
{
    public bool EnableBroadcaster { get; set; } = true;
    public string GlobalDiscordWebhookUrl { get; set; } = "";
    public string StreamerName { get; set; } = "Albire0";
    public string StreamUrl { get; set; } = "https://twitch.tv/albire0";
    public bool IsStreaming { get; set; } = false; 
}

public class Broadcast
{
    public string Name { get; set; } = "";
    public List<string> TriggerTypes { get; set; } = new();
    public bool Enabled { get; set; }
    
    public List<string> Commands { get; set; } = new(); 
    public List<string> Messages { get; set; } = new();
    
    public List<string> TriggerWords { get; set; } = new();
    public List<string> TriggerNPCs { get; set; } = new();
    public List<string> TriggerRegions { get; set; } = new();
    public List<string> Groups { get; set; } = new();
    public string Permission { get; set; } = "";
    public List<string> AllowedDays { get; set; } = new();
    public List<string> Conditions { get; set; } = new();
    
    public string DiscordWebhookUrl { get; set; } = "";
    public string DiscordTitle { get; set; } = "";
    public string DiscordUsername { get; set; } = "";
    public string DiscordPingRole { get; set; } = "";
    
    public string ColorHex { get; set; } = "#FFFFFF";
    public bool HideTriggerText { get; set; } = false;
    public bool TriggerToWholeGroup { get; set; } = true;

    [JsonIgnore]
    public Color TextColor
    {
        get
        {
            try { return new Color(Convert.ToInt32(ColorHex.Substring(1, 2), 16), Convert.ToInt32(ColorHex.Substring(3, 2), 16), Convert.ToInt32(ColorHex.Substring(5, 2), 16)); }
            catch { return Color.White; }
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(HeraldConfig))]
[JsonSerializable(typeof(List<Broadcast>))]
internal partial class HeraldJsonContext : JsonSerializerContext { }
