using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace ConnectMessage;
public class PluginConfig
{
    public bool WelcomeMessage { get; set; } = true;

    public int MessageDelay { get; set; } = 5;
    public bool LogMessagesToDiscord { get; set; } = true;
    public string DiscordWebhook { get; set; } = "";
}
