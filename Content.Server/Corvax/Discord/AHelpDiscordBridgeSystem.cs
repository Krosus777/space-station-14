using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Content.Server.Administration.Systems;
using Content.Server.Discord;
using Content.Server.Discord.DiscordLink;
using Content.Shared.CCVar;
using Content.Shared.GameTicking;
using NetCord;
using NetCord.Gateway;
using JetBrains.Annotations;
using Robust.Shared.Asynchronous;
using Robust.Shared.Configuration;
using Robust.Shared.Network;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Server.Corvax.Discord;

/// <summary>
/// Corvax: Bridges Discord ahelp threads with in-game bwoinks.
/// </summary>
[UsedImplicitly]
public sealed partial class AHelpDiscordBridgeSystem : EntitySystem, IPostInjectInit
{
    [Dependency] private readonly DiscordLink _discordLink = default!;
    [Dependency] private readonly DiscordWebhook _discordWebhook = default!;
    [Dependency] private readonly IConfigurationManager _config = default!;
    [Dependency] private readonly ILogManager _logManager = default!;
    [Dependency] private readonly ITaskManager _taskManager = default!;
    [Dependency] private readonly BwoinkSystem _bwoinkSystem = default!;

    private readonly Dictionary<NetUserId, AHelpDiscordThreadState> _byUser = new();
    private readonly Dictionary<ulong, NetUserId> _byThread = new();
    private readonly HashSet<ulong> _seenDiscordMessageIds = new();
    private readonly Queue<ulong> _seenDiscordMessageOrder = new();
    private readonly object _stateLock = new();
    private readonly object _webhookStateLock = new();

    private WebhookData? _ahelpWebhookData;
    private WebhookIdentifier? _ahelpWebhookIdentifier;
    private Task _ahelpWebhookLoadTask = Task.CompletedTask;

    private ISawmill _sawmill = default!;

    public override void Initialize()
    {
        base.Initialize();

        _bwoinkSystem.OnDiscordAHelpPublishRequested += OnDiscordAHelpPublishRequested;
        _discordLink.OnMessageReceived += OnDiscordMessageReceived;

        Subs.CVar(_config, CCVars.DiscordAHelpWebhook, OnAHelpWebhookChanged, true);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestartCleanup);
    }

    public override void Shutdown()
    {
        _bwoinkSystem.OnDiscordAHelpPublishRequested -= OnDiscordAHelpPublishRequested;
        _discordLink.OnMessageReceived -= OnDiscordMessageReceived;

        _config.UnsubValueChanged(CCVars.DiscordAHelpWebhook, OnAHelpWebhookChanged);

        base.Shutdown();
    }

    void IPostInjectInit.PostInject()
    {
        _sawmill = _logManager.GetSawmill("ahelp.discord");
    }

    private void OnAHelpWebhookChanged(string url)
    {
        lock (_webhookStateLock)
        {
            _ahelpWebhookLoadTask = ReloadAHelpWebhookAsync(url);
        }
    }

    private async Task ReloadAHelpWebhookAsync(string url)
    {
        _ahelpWebhookData = null;
        _ahelpWebhookIdentifier = null;

        if (string.IsNullOrWhiteSpace(url))
            return;

        var data = await _discordWebhook.GetWebhook(url);
        if (data == null)
        {
            _sawmill.Warning("Corvax ahelp webhook is configured but invalid.");
            return;
        }

        _ahelpWebhookData = data;
        _ahelpWebhookIdentifier = data.Value.ToIdentifier();
    }

    private async Task<AHelpDiscordPublishResult?> OnDiscordAHelpPublishRequested(AHelpDiscordPublishRequest request)
    {
        Task webhookLoadTask;
        lock (_webhookStateLock)
        {
            webhookLoadTask = _ahelpWebhookLoadTask;
        }

        await webhookLoadTask;

        if (_ahelpWebhookIdentifier == null || _ahelpWebhookData?.ChannelId == null)
            return default;

        try
        {
            var existing = GetMappingByUser(request.UserId);
            if (existing != null)
            {
                var payload = BuildThreadRelayPayload(request.Payload, existing.Value.DiscordRelayLines);
                await _discordWebhook.EditMessage(_ahelpWebhookIdentifier.Value, existing.Value.RootMessageId, payload);
                var updatedState = existing.Value with { LastPayload = ClonePayload(request.Payload) };
                SetMapping(request.UserId, updatedState);

                return new AHelpDiscordPublishResult(existing.Value.RootMessageId, existing.Value.ThreadId);
            }

            // Corvax: first ahelp message goes to the inbox channel, then the bot turns it into a dedicated thread.
            var response = await _discordWebhook.CreateMessage(_ahelpWebhookIdentifier.Value, request.Payload);
            var content = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                _sawmill.Error($"Failed to publish Corvax ahelp webhook message: {response.StatusCode}\n{content}");
                return default;
            }

            var messageId = ParseDiscordId(content);
            if (messageId == null)
            {
                _sawmill.Error($"Failed to extract message id from Corvax ahelp webhook response: {content}");
                return default;
            }

            var rootChannelId = checked(ulong.Parse(_ahelpWebhookData.Value.ChannelId!));
            var threadName = BuildThreadName(request.Payload.Username ?? "ahelp");
            var thread = await _discordLink.CreateThreadFromMessageAsync(rootChannelId, messageId.Value, threadName);
            if (thread == null)
            {
                _sawmill.Error($"Failed to create Discord thread for Corvax ahelp message {messageId.Value}; keeping the root message as the relay target.");
                var fallbackState = new AHelpDiscordThreadState(null, messageId.Value, ClonePayload(request.Payload), []);
                SetMapping(request.UserId, fallbackState);
                return new AHelpDiscordPublishResult(messageId.Value, null);
            }

            await _discordLink.JoinThreadAsync(thread.Id);

            var threadState = new AHelpDiscordThreadState(thread.Id, messageId.Value, ClonePayload(request.Payload), []);
            SetMapping(request.UserId, threadState);

            return new AHelpDiscordPublishResult(messageId.Value, thread.Id);
        }
        catch (Exception e)
        {
            _sawmill.Error($"Failed to publish Corvax ahelp webhook message: {e}");
            return default;
        }
    }

    private async void OnDiscordMessageReceived(Message message)
    {
        if (!TryGetUserByThread(message.ChannelId, out var userId))
        {
            _sawmill.Debug($"Ignoring Discord message in unmapped channel {message.ChannelId} from {message.Author.Username}.");
            return;
        }

        if (!TryMarkDiscordMessageSeen(message.Id))
        {
            _sawmill.Debug($"Ignoring duplicate Discord thread message {message.Id} in channel {message.ChannelId}.");
            return;
        }

        _sawmill.Info($"Discord thread message received id={message.Id} channel={message.ChannelId} author={message.Author.Username} bot={message.Author.IsBot} webhook={(message.WebhookId != null)} contentLength={message.Content.Length}");

        if (message.Author.IsBot || message.WebhookId != null)
            return;

        var senderName = message.Author.Username;

        var cleanText = message.Content.ReplaceLineEndings(" ");
        if (string.IsNullOrWhiteSpace(cleanText))
        {
            _sawmill.Warning($"Ignoring Discord thread message {message.Id} because it has no content. Check Discord Message Content intent.");
            return;
        }

        _sawmill.Debug($"Routing Discord thread message {message.Id} from {senderName} to NetUserId {userId}. ContentLength={message.Content.Length}");

        var relayLine = $"📤 **{senderName}:** {FormattedMessage.EscapeText(cleanText)}";
        await UpdateDiscordRelayHistoryAndMirrorAsync(userId, relayLine);

        _taskManager.RunOnMainThread(() =>
        {
            _bwoinkSystem.ReceiveExternalAHelpMessage(userId, cleanText, senderName);
        });
    }

    private AHelpDiscordThreadState? GetMappingByUser(NetUserId userId)
    {
        lock (_stateLock)
        {
            return _byUser.TryGetValue(userId, out var mapping) ? mapping : null;
        }
    }

    private static ulong? ParseDiscordId(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            if (!document.RootElement.TryGetProperty("id", out var id))
                return null;

            return id.ValueKind switch
            {
                JsonValueKind.String when ulong.TryParse(id.GetString(), out var stringParsed) => stringParsed,
                JsonValueKind.Number when id.TryGetUInt64(out var numberParsed) => numberParsed,
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private bool TryGetUserByThread(ulong threadId, out NetUserId userId)
    {
        lock (_stateLock)
        {
            return _byThread.TryGetValue(threadId, out userId);
        }
    }

    private void SetMapping(NetUserId userId, AHelpDiscordThreadState state)
    {
        lock (_stateLock)
        {
            _byUser[userId] = state;
            if (state.ThreadId is { } threadId)
                _byThread[threadId] = userId;
        }
    }

    private void OnRoundRestartCleanup(RoundRestartCleanupEvent _)
    {
        ClearState();
    }

    private void ClearState()
    {
        lock (_stateLock)
        {
            _byUser.Clear();
            _byThread.Clear();
            _seenDiscordMessageIds.Clear();
            _seenDiscordMessageOrder.Clear();
        }
    }

    private async Task UpdateDiscordRelayHistoryAndMirrorAsync(NetUserId userId, string relayLine)
    {
        AHelpDiscordThreadState state;
        WebhookIdentifier? webhookIdentifier;

        lock (_stateLock)
        {
            if (!_byUser.TryGetValue(userId, out state))
                return;

            state.DiscordRelayLines.Add(relayLine);
            _byUser[userId] = state;
            webhookIdentifier = _ahelpWebhookIdentifier;
        }

        if (webhookIdentifier == null)
            return;

        try
        {
            var payload = BuildThreadRelayPayload(state.LastPayload, state.DiscordRelayLines);
            await _discordWebhook.EditMessage(webhookIdentifier.Value, state.RootMessageId, payload);
        }
        catch (Exception e)
        {
            _sawmill.Warning($"Failed to mirror Discord reply into ahelp thread relay for user {userId}: {e}");
        }
    }

    private static WebhookPayload ClonePayload(WebhookPayload payload)
    {
        return new WebhookPayload
        {
            Username = payload.Username,
            AvatarUrl = payload.AvatarUrl,
            Content = payload.Content,
            AllowedMentions = payload.AllowedMentions,
            Embeds = CloneEmbeds(payload.Embeds),
        };
    }

    private static WebhookEmbed CloneEmbed(WebhookEmbed embed)
    {
        return new WebhookEmbed
        {
            Title = embed.Title,
            Description = embed.Description,
            Color = embed.Color,
            Footer = embed.Footer == null ? null : new WebhookEmbedFooter
            {
                Text = embed.Footer.Value.Text,
                IconUrl = embed.Footer.Value.IconUrl,
            },
            Fields = CloneFields(embed.Fields) ?? new List<WebhookEmbedField>(),
        };
    }

    private static WebhookPayload BuildThreadRelayPayload(WebhookPayload basePayload, IReadOnlyList<string> relayLines)
    {
        var payload = ClonePayload(basePayload);
        if (payload.Embeds is not { Count: > 0 })
            return payload;

        var embed = payload.Embeds[0];
        var description = embed.Description ?? string.Empty;
        if (relayLines.Count > 0)
        {
            if (description.Length > 0)
                description += "\n";

            description += string.Join("\n", relayLines);
        }

        embed.Description = description;
        payload.Embeds[0] = embed;
        return payload;
    }

    private static List<WebhookEmbed>? CloneEmbeds(List<WebhookEmbed>? embeds)
    {
        if (embeds == null)
            return null;

        var cloned = new List<WebhookEmbed>(embeds.Count);
        foreach (var embed in embeds)
            cloned.Add(CloneEmbed(embed));

        return cloned;
    }

    private static List<WebhookEmbedField>? CloneFields(List<WebhookEmbedField>? fields)
    {
        if (fields == null)
            return null;

        var cloned = new List<WebhookEmbedField>(fields.Count);
        foreach (var field in fields)
        {
            cloned.Add(new WebhookEmbedField
            {
                Name = field.Name,
                Value = field.Value,
                Inline = field.Inline,
            });
        }

        return cloned;
    }

    private bool TryMarkDiscordMessageSeen(ulong messageId)
    {
        lock (_stateLock)
        {
            if (!_seenDiscordMessageIds.Add(messageId))
                return false;

            _seenDiscordMessageOrder.Enqueue(messageId);
            while (_seenDiscordMessageOrder.Count > 256)
            {
                var removed = _seenDiscordMessageOrder.Dequeue();
                _seenDiscordMessageIds.Remove(removed);
            }

            return true;
        }
    }

    private static string BuildThreadName(string username)
    {
        var safe = username.Trim();
        if (safe.Length > 80)
            safe = safe[..80];

        return $"ahelp: {safe}";
    }

    private readonly record struct AHelpDiscordThreadState(ulong? ThreadId, ulong RootMessageId, WebhookPayload LastPayload, List<string> DiscordRelayLines);
}

public readonly record struct AHelpDiscordPublishRequest(NetUserId UserId, WebhookPayload Payload, bool NoReceivers);

public readonly record struct AHelpDiscordPublishResult(ulong RootMessageId, ulong? ThreadId);
