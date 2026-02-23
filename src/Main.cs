using MaxMind.GeoIP2;
using MaxMind.GeoIP2.Exceptions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.Plugins;
using System.Reflection;
using System.Text;

namespace ConnectMessage;

[PluginMetadata(
    Id = "ConnectMessage",
    Version = "1.0.5",
    Name = "ConnectMessage",
    Author = "verneri",
    Description = "Connect/disconnect messages (bots filtered)"
)]
public partial class ConnectMessage(ISwiftlyCore core) : BasePlugin(core)
{
    private PluginConfig _config = null!;

    private static readonly Dictionary<ulong, bool> LoopConnections = new();
    private static readonly HttpClient _httpClient = new();
    private static DatabaseReader? _geoReader;

    private const string Version = "v1.0.5";

    public override void Load(bool hotReload)
    {
        const string ConfigFileName = "config.jsonc";
        const string ConfigSection = "ConnectMessage";

        Core.Configuration
            .InitializeJsonWithModel<PluginConfig>(ConfigFileName, ConfigSection)
            .Configure(cfg => cfg.AddJsonFile(
                Core.Configuration.GetConfigPath(ConfigFileName),
                optional: false,
                reloadOnChange: true));

        var services = new ServiceCollection();
        services.AddSwiftly(Core)
            .AddOptionsWithValidateOnStart<PluginConfig>()
            .BindConfiguration(ConfigSection);

        var provider = services.BuildServiceProvider();
        _config = provider.GetRequiredService<IOptions<PluginConfig>>().Value;

        // Initialize GeoIP reader once
        var geoPath = Path.Combine(Core.PluginPath, "GeoLite2-Country.mmdb");
        if (File.Exists(geoPath))
            _geoReader = new DatabaseReader(geoPath);

        Core.GameEvent.HookPost<EventPlayerConnectFull>(OnPlayerConnectFull);
        Core.GameEvent.HookPost<EventPlayerDisconnect>(OnPlayerDisconnect);
    }

    public override void Unload()
    {
        _geoReader?.Dispose();
    }

    // =============================
    // CONNECT
    // =============================

    private SwiftlyS2.Shared.Misc.HookResult OnPlayerConnectFull(EventPlayerConnectFull @event)
    {
        if (@event == null)
            return SwiftlyS2.Shared.Misc.HookResult.Continue;

        // Disable default join broadcast if supported
        TrySetDontBroadcast(@event, true);

        var player = @event.Accessor.GetPlayer("userid");
        if (player == null || !player.IsValid)
            return SwiftlyS2.Shared.Misc.HookResult.Continue;

        // Skip bots completely
        if (IsBotPlayer(player))
            return SwiftlyS2.Shared.Misc.HookResult.Continue;

        var name = player.Controller.PlayerName;
        var ip = player.IPAddress?.Split(":")[0] ?? "Unknown";
        var country = GetCountry(ip);

        LoopConnections.Remove(player.SteamID);

        Core.PlayerManager.SendChat(
            Core.Localizer["player.connect", name, player.SteamID, country]);

        Core.ConsoleOutput.WriteToServerConsole(
            $"[ConnectMessage] {name} connected ({country}/{ip}/{player.SteamID})");

        if (_config.LogMessagesToDiscord)
            _ = WebhookConnected(name, player.SteamID, ip, country);

        if (_config.WelcomeMessage)
        {
            Core.Scheduler.DelayBySeconds(_config.MessageDelay, () =>
            {
                if (player != null && player.IsValid)
                    player.SendChat(Core.Localizer["welcome.message", name]);
            });
        }

        return SwiftlyS2.Shared.Misc.HookResult.Continue;
    }

    // =============================
    // DISCONNECT
    // =============================

    private SwiftlyS2.Shared.Misc.HookResult OnPlayerDisconnect(EventPlayerDisconnect @event)
    {
        if (@event == null)
            return SwiftlyS2.Shared.Misc.HookResult.Continue;

        @event.DontBroadcast = true;

        var player = @event.Accessor.GetPlayer("userid");
        if (player == null || !player.IsValid)
            return SwiftlyS2.Shared.Misc.HookResult.Continue;

        // Skip bots completely
        if (IsBotPlayer(player))
            return SwiftlyS2.Shared.Misc.HookResult.Continue;

        var name = player.Controller.PlayerName;
        var ip = player.IPAddress?.Split(":")[0] ?? "Unknown";
        var country = GetCountry(ip);

        // Prevent reconnect spam
        if (@event.Reason is 54 or 55 or 57)
        {
            LoopConnections[player.SteamID] = true;
            return SwiftlyS2.Shared.Misc.HookResult.Continue;
        }

        Core.PlayerManager.SendChat(
            Core.Localizer["player.disconnect", name, player.SteamID, country]);

        Core.ConsoleOutput.WriteToServerConsole(
            $"[ConnectMessage] {name} disconnected ({country}/{ip}/{player.SteamID})");

        if (_config.LogMessagesToDiscord)
            _ = WebhookDisconnected(name, player.SteamID, ip, country);

        return SwiftlyS2.Shared.Misc.HookResult.Continue;
    }

    // =============================
    // DISCORD WEBHOOKS
    // =============================

    private async Task WebhookConnected(string name, ulong steamId, string ip, string country)
    {
        var embed = new
        {
            title = $"{Core.Localizer["discord.connecttitle", name]}",
            url = $"https://steamcommunity.com/profiles/{steamId}",
            description = $"{Core.Localizer["discord.connectdescription", country, steamId, ip]}",
            color = 65280,
            footer = new { text = $"ConnectMessage {Version}" }
        };

        await SendWebhook(embed);
    }

    private async Task WebhookDisconnected(string name, ulong steamId, string ip, string country)
    {
        var embed = new
        {
            title = $"{Core.Localizer["discord.disconnecttitle", name]}",
            url = $"https://steamcommunity.com/profiles/{steamId}",
            description = $"{Core.Localizer["discord.disconnectdescription", country, steamId, ip]}",
            color = 16711680,
            footer = new { text = $"ConnectMessage {Version}" }
        };

        await SendWebhook(embed);
    }

    private async Task SendWebhook(object embed)
    {
        var payload = new { embeds = new[] { embed } };

        var json = Newtonsoft.Json.JsonConvert.SerializeObject(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync(_config.DiscordWebhook, content);

        if (!response.IsSuccessStatusCode)
            Core.Logger.LogError($"Discord webhook failed: {response.StatusCode}");
    }

    // =============================
    // GEOIP
    // =============================

    private string GetCountry(string ip)
    {
        try
        {
            if (_geoReader == null)
                return "Unknown";

            var response = _geoReader.Country(ip);
            return response?.Country?.IsoCode ?? "Unknown";
        }
        catch (AddressNotFoundException)
        {
            return "Unknown";
        }
        catch
        {
            return "Unknown";
        }
    }

    // =============================
    // BOT DETECTION (reflection safe)
    // =============================

    private static bool IsBotPlayer(object player)
    {
        try
        {
            if (TryBool(player, "IsBot")) return true;
            if (TryBool(player, "IsFakeClient")) return true;

            var controller = TryObj(player, "Controller");
            if (controller != null)
            {
                if (TryBool(controller, "IsBot")) return true;
                if (TryBool(controller, "IsFakeClient")) return true;
            }

            var steamProp = player.GetType().GetProperty("SteamID");
            if (steamProp?.GetValue(player) is ulong id && id == 0)
                return true;
        }
        catch { }

        return false;
    }

    private static bool TryBool(object obj, string name)
    {
        var p = obj.GetType().GetProperty(name);
        return p != null && p.PropertyType == typeof(bool) && (bool)p.GetValue(obj)!;
    }

    private static object? TryObj(object obj, string name)
    {
        return obj.GetType().GetProperty(name)?.GetValue(obj);
    }

    private static void TrySetDontBroadcast(object evt, bool value)
    {
        var p = evt.GetType().GetProperty("DontBroadcast");
        if (p != null && p.PropertyType == typeof(bool))
            p.SetValue(evt, value);
    }
}
