using MaxMind.GeoIP2;
using MaxMind.GeoIP2.Exceptions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Plugins;
using System.Text;

namespace ConnectMessage;

[PluginMetadata(Id = "ConnectMessage", Version = "1.0.4", Name = "ConnectMessage", Author = "verneri", Description = "Connect/disconnect messages")]
public partial class ConnectMessage(ISwiftlyCore core) : BasePlugin(core) {

    private PluginConfig _config = null!;

    private static Dictionary<ulong, bool> LoopConnections = new Dictionary<ulong, bool>();
    private static readonly HttpClient _httpClient = new HttpClient();
    private static readonly string _version = "v1.0.4";

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

        ServiceCollection services = new();
        services.AddSwiftly(Core)
            .AddOptionsWithValidateOnStart<PluginConfig>()
            .BindConfiguration(ConfigSection);
        var provider = services.BuildServiceProvider();
        _config = provider.GetRequiredService<IOptions<PluginConfig>>().Value;

        Core.GameEvent.HookPost<EventPlayerConnectFull>(OnPlayerConnectFull);
        Core.GameEvent.HookPost<EventPlayerDisconnect>(OnPlayerDisconnect);
    }

    public override void Unload() {

    }

    private HookResult OnPlayerConnectFull(EventPlayerConnectFull @event)
    {
        if (@event == null)
            return HookResult.Continue;
        var player = @event.Accessor.GetPlayer("userid");
        if (player == null || !player.IsValid)
            return HookResult.Continue;
        var playername = player.Controller.PlayerName;
        string country = GetCountry(player.IPAddress?.Split(":")[0] ?? "Unknown");
        string playerip = player.IPAddress?.Split(":")[0] ?? "Unknown";

        if (LoopConnections.ContainsKey(player.SteamID))
        {
            LoopConnections.Remove(player.SteamID);
        }

        Core.PlayerManager.SendChat(Core.Localizer["player.connect", playername, player.SteamID, country]);
        Core.ConsoleOutput.WriteToServerConsole($"[ConnectMessage] Player {playername} connected ({country}/{playerip}/{player.SteamID})");

        if (_config.LogMessagesToDiscord)
        {
            Task.Run(async () =>
            {
                await WebhookConnected(playername, player.SteamID, playerip, country);
            });
        }

        if (_config.WelcomeMessage)
        {
            Core.Scheduler.DelayBySeconds(_config.MessageDelay, () => {

                player.SendChat(Core.Localizer["welcome.message", playername]);
            });
        }

        return HookResult.Continue;
    }
    private HookResult OnPlayerDisconnect(EventPlayerDisconnect @event)
    {
        if (@event == null)
            return HookResult.Continue;

        var player = @event.Accessor.GetPlayer("userid");
        if (player == null || !player.IsValid)
            return HookResult.Continue;
        var playername = player.Controller.PlayerName;
        string country = GetCountry(player.IPAddress?.Split(":")[0] ?? "Unknown");
        string playerip = player.IPAddress?.Split(":")[0] ?? "Unknown";

        @event.DontBroadcast = true;

        if (@event.Reason == 54 || @event.Reason == 55 || @event.Reason == 57)
        {
            if (!LoopConnections.ContainsKey(player.SteamID))
            {
                LoopConnections.Add(player.SteamID, true);
            }
            if (LoopConnections.ContainsKey(player.SteamID))
            {
                return HookResult.Continue;
            }
        }

        Core.PlayerManager.SendChat(Core.Localizer["player.disconnect", playername, player.SteamID, country]);
        Core.ConsoleOutput.WriteToServerConsole($"[ConnectMessage] Player {playername} disconnected ({country}/{playerip}/{player.SteamID})");

        if (_config.LogMessagesToDiscord)
        {
            Task.Run(async () =>
            {
                await WebhookDisconnected(playername, player.SteamID, playerip, country);
            });
        }
        return HookResult.Continue;
    }

    public async Task WebhookConnected(string playerName, ulong steamID, string playerip, string country)
    {
        var embed = new
        {
            title = $"{Core.Localizer["discord.connecttitle", playerName]}",
            url = $"https://steamcommunity.com/profiles/{steamID}",
            description = $"{Core.Localizer["discord.connectdescription", country, steamID, playerip]}",
            color = 65280,
            footer = new
            {
                text = $"{Core.Localizer["discord.footer", _version]}"
            }
        };

        var payload = new
        {
            embeds = new[] { embed }
        };

        var jsonPayload = Newtonsoft.Json.JsonConvert.SerializeObject(payload);
        var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync(_config.DiscordWebhook, content);

        if (!response.IsSuccessStatusCode)
        {
            Core.Logger.LogError($"Failed to send message to Discord! code: {response.StatusCode}");
        }
    }
    public async Task WebhookDisconnected(string playerName, ulong steamID, string playerip, string country)
    {
        var embed = new
        {
            title = $"{Core.Localizer["discord.disconnecttitle", playerName]}",
            url = $"https://steamcommunity.com/profiles/{steamID}",
            description = $"{Core.Localizer["discord.disconnectdescription", country, steamID, playerip]}",
            color = 16711680,
            footer = new
            {
                text = $"{Core.Localizer["discord.footer", _version]}"
            }
        };

        var payload = new
        {
            embeds = new[] { embed }
        };

        var jsonPayload = Newtonsoft.Json.JsonConvert.SerializeObject(payload);
        var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync(_config.DiscordWebhook, content);

        if (!response.IsSuccessStatusCode)
        {
            Core.Logger.LogError($"Failed to send message to Discord! code: {response.StatusCode}");
        }
    }

    private string GetCountry(string ipAddress)
    {
        try
        {
            using var reader = new DatabaseReader(Path.Combine(Core.PluginPath, "GeoLite2-Country.mmdb"));
            var response = reader.Country(ipAddress);
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
} 