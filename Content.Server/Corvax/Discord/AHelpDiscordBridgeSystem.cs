using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Content.Server.Administration.Systems;
using Content.Server.Administration;
using Content.Server.Discord;
using Content.Server.Discord.DiscordLink;
using Content.Shared.CCVar;
using Content.Shared.Administration;
using Content.Shared.GameTicking;
using Content.Shared.Mind;
using Content.Server.GameTicking;
using NetCord;
using NetCord.Gateway;
using JetBrains.Annotations;
using Robust.Shared.Asynchronous;
using Robust.Shared.Configuration;
using Robust.Shared;
using Robust.Shared.Enums;
using Robust.Shared.Network;
using Robust.Server.Player;
using Robust.Shared.Timing;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Server.Corvax.Discord;

/// <summary>
/// Corvax: Bridges Discord ahelp threads with in-game bwoinks.
/// </summary>
[UsedImplicitly]
public sealed partial class AHelpDiscordBridgeSystem : EntitySystem, IPostInjectInit
{
    private const int MaxEmbedsPerWebhookMessage = 10;
    private const int MaxCharsPerEmbed = 3000;
    private static readonly Regex RoundTimeRegex = new(@"\*\*(?<time>\d{2}:\d{2}:\d{2})\*\*", RegexOptions.Compiled);

    [Dependency] private readonly DiscordLink _discordLink = default!;
    [Dependency] private readonly DiscordWebhook _discordWebhook = default!;
    [Dependency] private readonly AdminSystem _adminSystem = default!;
    [Dependency] private readonly IConfigurationManager _config = default!;
    [Dependency] private readonly ILogManager _logManager = default!;
    [Dependency] private readonly IPlayerManager _playerManager = default!;
    [Dependency] private readonly IPlayerLocator _playerLocator = default!;
    [Dependency] private readonly GameTicker _gameTicker = default!;
    [Dependency] private readonly ITaskManager _taskManager = default!;
    [Dependency] private readonly BwoinkSystem _bwoinkSystem = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;

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
        _discordLink.RegisterCommandCallback(ev => _ = OnCkeyCommandReceived(ev), "ckey");
        _discordLink.RegisterCommandCallback(ev => _ = OnAhCommandReceived(ev), "ah");
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
                var entries = new List<AHelpDiscordRelayEntry>(existing.Value.Entries);
                var nextSequence = existing.Value.NextSequence;
                var previousPayloadEntries = ExtractPayloadEntries(existing.Value.BasePayload);
                var currentPayloadEntries = ExtractPayloadEntries(request.Payload);

                for (var index = previousPayloadEntries.Count; index < currentPayloadEntries.Count; index++)
                    entries.Add(new AHelpDiscordRelayEntry(currentPayloadEntries[index].RoundTime, nextSequence++, currentPayloadEntries[index].Line));

                var updatedState = existing.Value with
                {
                    BasePayload = ClonePayload(request.Payload),
                    Entries = entries,
                    NextSequence = nextSequence,
                };
                var payload = BuildChronologicalPayload(updatedState.BasePayload, updatedState.Entries);
                await _discordWebhook.EditMessage(_ahelpWebhookIdentifier.Value, existing.Value.RootMessageId, payload);
                SetMapping(request.UserId, updatedState);

                return new AHelpDiscordPublishResult(existing.Value.RootMessageId, existing.Value.ThreadId);
            }

            return await CreateAndMapThreadAsync(request.UserId, request.Payload, BuildThreadName(request.Payload.Username ?? "ahelp"));
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
            _sawmill.Debug($"Ignoring Discord message in unmapped channel {message.ChannelId} from {GetDiscordGuildName(message)}.");
            return;
        }

        if (IsCommandMessage(message))
        {
            _sawmill.Debug($"Ignoring Discord command message {message.Id} in thread channel {message.ChannelId}.");
            return;
        }

        if (!TryMarkDiscordMessageSeen(message.Id))
        {
            _sawmill.Debug($"Ignoring duplicate Discord thread message {message.Id} in channel {message.ChannelId}.");
            return;
        }

        _sawmill.Info($"Discord thread message received id={message.Id} channel={message.ChannelId} author={GetDiscordGuildName(message)} bot={message.Author.IsBot} webhook={(message.WebhookId != null)} contentLength={message.Content.Length}");

        if (message.Author.IsBot || message.WebhookId != null)
            return;

        var senderName = GetDiscordGuildName(message);

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

    private async Task OnCkeyCommandReceived(CommandReceivedEventArgs ev)
    {
        if (ev.Message.Author.IsBot || ev.Message.WebhookId != null)
            return;

        if (!TryGetUserByThread(ev.Message.ChannelId, out var userId))
            return;

        Task webhookLoadTask;
        lock (_webhookStateLock)
        {
            webhookLoadTask = _ahelpWebhookLoadTask;
        }

        await webhookLoadTask;

        if (_ahelpWebhookIdentifier == null)
            return;

        var state = GetMappingByUser(userId);
        if (state == null)
            return;

        var requestedServer = ev.RawArguments.Trim();
        var currentServer = _config.GetCVar(CVars.GameHostName);
        var serverMatches = string.IsNullOrWhiteSpace(requestedServer) ||
                            string.Equals(requestedServer, currentServer, StringComparison.OrdinalIgnoreCase);

        var payloads = await BuildCkeyLookupPayloads(requestedServer, currentServer, serverMatches);
        foreach (var payload in payloads)
            await SendThreadWebhookAsync(payload, state.Value.ThreadId);
    }

    private async Task OnAhCommandReceived(CommandReceivedEventArgs ev)
    {
        if (ev.Message.Author.IsBot || ev.Message.WebhookId != null)
            return;

        if (!TryGetUserByThread(ev.Message.ChannelId, out var currentThreadUserId))
            return;

        Task webhookLoadTask;
        lock (_webhookStateLock)
        {
            webhookLoadTask = _ahelpWebhookLoadTask;
        }

        await webhookLoadTask;

        if (_ahelpWebhookIdentifier == null || _ahelpWebhookData?.ChannelId == null)
            return;

        var currentThreadState = GetMappingByUser(currentThreadUserId);
        if (currentThreadState == null)
            return;

        if (ev.Arguments.Count < 2)
        {
            await SendThreadWebhookAsync(new WebhookPayload
            {
                Username = "ahelp branch",
                Content = "Usage: `!ah <servername> <ckey>`",
            }, currentThreadState.Value.ThreadId);
            return;
        }

        var requestedServer = ev.Arguments[0];
        var targetCkey = ev.Arguments[1];
        var currentServer = _config.GetCVar(CVars.GameHostName);

        if (!string.Equals(requestedServer, currentServer, StringComparison.OrdinalIgnoreCase))
        {
            await SendThreadWebhookAsync(new WebhookPayload
            {
                Username = "ahelp branch",
                Content = $"Requested server `{requestedServer}` does not match this instance `{currentServer}`.",
            }, currentThreadState.Value.ThreadId);
            return;
        }

        var targetPlayer = await _playerLocator.LookupIdByNameAsync(targetCkey);
        if (targetPlayer == null)
        {
            await SendThreadWebhookAsync(new WebhookPayload
            {
                Username = "ahelp branch",
                Content = $"Could not find player `{targetCkey}`.",
            }, currentThreadState.Value.ThreadId);
            return;
        }

        var targetName = string.IsNullOrWhiteSpace(targetPlayer.Username) ? targetCkey : targetPlayer.Username;
        var createdBy = GetDiscordGuildName(ev.Message);

        var payload = new WebhookPayload
        {
            Username = targetName,
            Content = $"AHELP branch opened for `{targetName}` on `{currentServer}` by `{createdBy}`.",
            Embeds = new List<WebhookEmbed>
            {
                new()
                {
                    Title = "AHELP",
                    Description = $"Branch opened from Discord for `{targetName}` on `{currentServer}`.\nWaiting for the in-game relay.",
                    Color = 0x3BA55C,
                    Footer = new WebhookEmbedFooter
                    {
                        Text = currentServer,
                    },
                },
            },
        };

        var result = await CreateAndMapThreadAsync(targetPlayer.UserId, payload, BuildThreadName(targetName));
        if (result == null)
        {
            await SendThreadWebhookAsync(new WebhookPayload
            {
                Username = "ahelp branch",
                Content = $"Failed to create ahelp branch for `{targetName}`.",
            }, currentThreadState.Value.ThreadId);
            return;
        }

        var threadLink = result.Value.ThreadId is { } threadId && _ahelpWebhookData.Value.GuildId is { } guildId
            ? $"https://discord.com/channels/{guildId}/{threadId}"
            : $"https://discord.com/channels/{_ahelpWebhookData.Value.ChannelId}/{result.Value.RootMessageId}";

        await SendThreadWebhookAsync(new WebhookPayload
        {
            Username = "ahelp branch",
            Content = $"Opened new ahelp branch for `{targetName}`: {threadLink}",
        }, currentThreadState.Value.ThreadId);
    }

    private AHelpDiscordThreadState? GetMappingByUser(NetUserId userId)
    {
        lock (_stateLock)
        {
            return _byUser.TryGetValue(userId, out var mapping) ? mapping : null;
        }
    }

    private static string GetDiscordGuildName(Message message)
    {
        if (message.Guild != null &&
            message.Guild.Users.TryGetValue(message.Author.Id, out var guildUser))
        {
            if (!string.IsNullOrWhiteSpace(guildUser.Nickname))
                return guildUser.Nickname;

            if (!string.IsNullOrWhiteSpace(guildUser.Username))
                return guildUser.Username;
        }

        return string.IsNullOrWhiteSpace(message.Author.GlobalName) ? message.Author.Username : message.Author.GlobalName;
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
            if (_byUser.TryGetValue(userId, out var existing) && existing.ThreadId is { } oldThreadId && oldThreadId != state.ThreadId)
                _byThread.Remove(oldThreadId);

            _byUser[userId] = state;
            if (state.ThreadId is { } threadId)
                _byThread[threadId] = userId;
        }
    }

    private async Task<AHelpDiscordPublishResult?> CreateAndMapThreadAsync(NetUserId userId, WebhookPayload payload, string threadName)
    {
        var response = await _discordWebhook.CreateMessage(_ahelpWebhookIdentifier!.Value, payload);
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

        var rootChannelId = checked(ulong.Parse(_ahelpWebhookData!.Value.ChannelId!));
        var thread = await _discordLink.CreateThreadFromMessageAsync(rootChannelId, messageId.Value, threadName);
        if (thread == null)
        {
            _sawmill.Error($"Failed to create Discord thread for Corvax ahelp message {messageId.Value}; keeping the root message as the relay target.");
            var fallbackEntries = ExtractPayloadEntries(payload);
            var fallbackNextSequence = fallbackEntries.Count;
            var fallbackState = new AHelpDiscordThreadState(null, messageId.Value, ClonePayload(payload), fallbackEntries, fallbackNextSequence);
            SetMapping(userId, fallbackState);
            return new AHelpDiscordPublishResult(messageId.Value, null);
        }

        await _discordLink.JoinThreadAsync(thread.Id);

        var initialEntries = ExtractPayloadEntries(payload);
        var nextSequence = initialEntries.Count;
        var threadState = new AHelpDiscordThreadState(thread.Id, messageId.Value, ClonePayload(payload), initialEntries, nextSequence);
        SetMapping(userId, threadState);

        return new AHelpDiscordPublishResult(messageId.Value, thread.Id);
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
        AHelpDiscordThreadState updatedState;
        WebhookIdentifier? webhookIdentifier;
        var roundTime = _gameTicker.RoundDuration();

        lock (_stateLock)
        {
            if (!_byUser.TryGetValue(userId, out state))
                return;

            var entries = new List<AHelpDiscordRelayEntry>(state.Entries)
            {
                new(roundTime, state.NextSequence, relayLine),
            };

            updatedState = state with
            {
                Entries = entries,
                NextSequence = state.NextSequence + 1,
            };
            _byUser[userId] = updatedState;
            webhookIdentifier = _ahelpWebhookIdentifier;
        }

        if (webhookIdentifier == null)
            return;

        try
        {
            var payload = BuildChronologicalPayload(updatedState.BasePayload, updatedState.Entries);
            await _discordWebhook.EditMessage(webhookIdentifier.Value, state.RootMessageId, payload);
        }
        catch (Exception e)
        {
            _sawmill.Warning($"Failed to mirror Discord reply into ahelp thread relay for user {userId}: {e}");
        }
    }

    private async Task SendThreadWebhookAsync(WebhookPayload payload, ulong? threadId)
    {
        try
        {
            var identifier = _ahelpWebhookIdentifier;
            if (identifier == null)
                return;

            var response = await _discordWebhook.CreateMessage(identifier.Value, payload, threadId);
            if (!response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                _sawmill.Warning($"Failed to send ckey lookup webhook to Discord: {response.StatusCode}\n{content}");
            }
        }
        catch (Exception e)
        {
            _sawmill.Error($"Failed to send ckey lookup webhook to Discord: {e}");
        }
    }

    private Task<List<WebhookPayload>> BuildCkeyLookupPayloads(string requestedServer, string currentServer, bool serverMatches)
    {
        var rows = new List<string>();
        foreach (var session in _playerManager.Sessions
                     .Where(session => session.Status is SessionStatus.Connected or SessionStatus.InGame)
                     .OrderBy(session => session.Name, StringComparer.OrdinalIgnoreCase))
        {
            var playerInfo = _adminSystem.GetCachedPlayerInfo(session.UserId);
            if (playerInfo == null)
                continue;

            rows.Add(FormatCkeyLookupRow(playerInfo));
        }

        var description = serverMatches
            ? $"Current player list for `{currentServer}`."
            : $"Requested server `{requestedServer}` does not match this instance `{currentServer}`. Showing local data.";

        var payloads = new List<WebhookPayload>();
        if (rows.Count == 0)
        {
            payloads.Add(new WebhookPayload
            {
                Username = "ckey lookup",
                Embeds = new List<WebhookEmbed> { BuildCkeyLookupEmbed(description, currentServer, []) },
            });
            return Task.FromResult(payloads);
        }

        var currentEmbeds = new List<WebhookEmbed>();
        var currentRows = new List<string>();
        var currentRowLength = 0;

        void FlushEmbed()
        {
            if (currentRows.Count == 0)
                return;

            currentEmbeds.Add(BuildCkeyLookupEmbed(description, currentServer, currentRows));
            currentRows = [];
            currentRowLength = 0;
        }

        void FlushMessage()
        {
            if (currentEmbeds.Count == 0)
                return;

            payloads.Add(new WebhookPayload
            {
                Username = "ckey lookup",
                Embeds = currentEmbeds.ToList(),
            });

            currentEmbeds = [];
        }

        foreach (var row in rows)
        {
            var nextLength = currentRowLength + row.Length + "\n".Length;
            if (currentRows.Count > 0 && nextLength > MaxCharsPerEmbed)
            {
                FlushEmbed();
                if (currentEmbeds.Count == MaxEmbedsPerWebhookMessage)
                    FlushMessage();
            }

            currentRows.Add(row);
            currentRowLength += row.Length + "\n".Length;
        }

        FlushEmbed();
        FlushMessage();

        if (payloads.Count == 0)
        {
            payloads.Add(new WebhookPayload
            {
                Username = "ckey lookup",
                Embeds = new List<WebhookEmbed> { BuildCkeyLookupEmbed(description, currentServer, []) },
            });
        }

        if (payloads.Count > 1)
        {
            for (var index = 0; index < payloads.Count; index++)
            {
                var payload = payloads[index];
                if (payload.Embeds is not { Count: > 0 })
                    continue;

                for (var embedIndex = 0; embedIndex < payload.Embeds.Count; embedIndex++)
                {
                    var embed = payload.Embeds[embedIndex];
                    embed.Title = $"CKEY lookup (part {index + 1}/{payloads.Count})";
                    payload.Embeds[embedIndex] = embed;
                }

                payloads[index] = payload;
            }
        }

        return Task.FromResult(payloads);
    }

    private static WebhookEmbed BuildCkeyLookupEmbed(string description, string currentServer, IReadOnlyList<string> rows)
    {
        var embedDescription = description;
        if (rows.Count > 0)
        {
            embedDescription += "\n```\n";
            embedDescription += string.Join("\n", rows);
            embedDescription += "\n```";
        }
        else
        {
            embedDescription += "\nNo player data available.";
        }

        return new WebhookEmbed
        {
            Title = "CKEY lookup",
            Description = embedDescription,
            Color = 0x3BA55C,
            Footer = new WebhookEmbedFooter
            {
                Text = currentServer,
            },
        };
    }

    private string FormatCkeyLookupRow(PlayerInfo playerInfo)
    {
        var roleName = Loc.GetString(RoleTypePrototype.FallbackName);
        var subtypeName = string.Empty;

        if (playerInfo.RoleProto is { } roleProto && _prototypeManager.TryIndex(roleProto, out RoleTypePrototype? roleType))
        {
            roleName = Loc.GetString(roleType.Name);
            if (playerInfo.Subtype is { } subtype)
                subtypeName = Loc.GetString(subtype);
        }

        var roleDisplay = string.IsNullOrWhiteSpace(subtypeName)
            ? roleName
            : $"{roleName} ({subtypeName})";

        var ckey = string.IsNullOrWhiteSpace(playerInfo.Username) ? "unknown" : playerInfo.Username;
        var characterName = string.IsNullOrWhiteSpace(playerInfo.CharacterName) ? "unknown" : playerInfo.CharacterName;
        var job = string.IsNullOrWhiteSpace(playerInfo.StartingJob) ? Loc.GetString("generic-unknown-title") : playerInfo.StartingJob;
        var antag = playerInfo.Antag ? "yes" : "no";

        return $"{ckey} | char={characterName} | job={job} | role={roleDisplay} | antag={antag}";
    }

    private bool IsCommandMessage(Message message)
    {
        return !string.IsNullOrWhiteSpace(_discordLink.BotPrefix) &&
               message.Content.StartsWith(_discordLink.BotPrefix, StringComparison.Ordinal);
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

    private static WebhookPayload BuildChronologicalPayload(WebhookPayload basePayload, IReadOnlyList<AHelpDiscordRelayEntry> entries)
    {
        var payload = ClonePayload(basePayload);
        if (payload.Embeds is not { Count: > 0 })
            return payload;

        var embed = payload.Embeds[0];
        var description = string.Join("\n",
            entries.OrderBy(entry => entry.RoundTime ?? TimeSpan.MaxValue)
                .ThenBy(entry => entry.Sequence)
                .Select(entry => entry.Line));

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

    private static List<AHelpDiscordRelayEntry> ExtractPayloadEntries(WebhookPayload payload)
    {
        var entries = new List<AHelpDiscordRelayEntry>();
        var description = payload.Embeds is { Count: > 0 } ? payload.Embeds[0].Description : null;
        if (string.IsNullOrWhiteSpace(description))
            return entries;

        foreach (var rawLine in description.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line))
                continue;

            entries.Add(new AHelpDiscordRelayEntry(TryParseRoundTime(line), entries.Count, line));
        }

        return entries;
    }

    private static TimeSpan? TryParseRoundTime(string line)
    {
        var match = RoundTimeRegex.Match(line);
        if (!match.Success)
            return null;

        return TimeSpan.TryParseExact(match.Groups["time"].Value, @"hh\:mm\:ss", null, out var time)
            ? time
            : null;
    }

    private readonly record struct AHelpDiscordThreadState(ulong? ThreadId, ulong RootMessageId, WebhookPayload BasePayload, List<AHelpDiscordRelayEntry> Entries, long NextSequence);
    private readonly record struct AHelpDiscordRelayEntry(TimeSpan? RoundTime, long Sequence, string Line);
}

public readonly record struct AHelpDiscordPublishRequest(NetUserId UserId, WebhookPayload Payload, bool NoReceivers);

public readonly record struct AHelpDiscordPublishResult(ulong RootMessageId, ulong? ThreadId);
