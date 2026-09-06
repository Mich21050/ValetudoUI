namespace ValetudoUI.Options;

/// <summary>Telegram bot notifications (plain Bot API calls, no library). Disabled unless token and chat id are set.</summary>
public sealed class TelegramOptions
{
    public const string Section = "Telegram";

    /// <summary>Bot token from @BotFather. Keep it out of appsettings.json (use appsettings.Development.json or user secrets).</summary>
    public string? BotToken { get; set; }

    /// <summary>Chat to post to. A string because group chat ids are negative and channels use "@name".</summary>
    public string? ChatId { get; set; }

    public bool NotifyRunCompleted { get; set; } = true;
    public bool NotifyErrors { get; set; } = true;

    /// <summary>Send without sound (Telegram's disable_notification).</summary>
    public bool Silent { get; set; }

    /// <summary>Bot API host; only changed for testing against a stand-in server.</summary>
    public string ApiBaseUrl { get; set; } = "https://api.telegram.org";

    public bool Enabled => !string.IsNullOrWhiteSpace(BotToken) && !string.IsNullOrWhiteSpace(ChatId);
}
