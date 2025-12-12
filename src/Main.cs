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

namespace ConnectMessage;

[PluginMetadata(Id = "ConnectMessage", Version = "1.0.2", Name = "ConnectMessage", Author = "verneri", Description = "Connect/disconnect messages")]
public partial class ConnectMessage(ISwiftlyCore core) : BasePlugin(core) {

    private PluginConfig _config = null!;

    public static Dictionary<ulong, bool> LoopConnections = new Dictionary<ulong, bool>();

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

        if (_config.WelcomeMessage)
        {
            Core.Scheduler.DelayBySeconds(_config.MessageDelay, () => {

                player.SendChat(Core.Localizer["welcome.message", playername]);
            });
        }

        Core.PlayerManager.SendChat(Core.Localizer["player.connect", playername, country]);
        Core.Logger.LogInformation($"Player {playername} connected ({country}/{playerip})");

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

        Core.PlayerManager.SendChat(Core.Localizer["player.disconnect", playername, country]);
        Core.Logger.LogInformation($"Player {playername} disconnected ({country}/{playerip})");

        return HookResult.Continue;
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