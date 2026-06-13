using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Content.Server.Corvax.Discord;
using Content.Server.Discord;
using Content.Shared.CCVar;
using Robust.Shared.Network;
using Robust.Shared.Utility;

namespace Content.Server.Administration.Systems
{
    public sealed partial class BwoinkSystem
    {
        public event Func<AHelpDiscordPublishRequest, Task<AHelpDiscordPublishResult?>>? OnDiscordAHelpPublishRequested; // Corvax: external ahelp bridge publishes and mirrors Discord relay state.

        private async Task<AHelpDiscordPublishResult?> PublishDiscordAHelpAsync(AHelpDiscordPublishRequest request) // Corvax: allow the bridge to own Discord delivery while the base bwoink flow stays unchanged.
        {
            var handlers = OnDiscordAHelpPublishRequested;
            if (handlers == null)
                return null;

            AHelpDiscordPublishResult? result = null;
            foreach (Func<AHelpDiscordPublishRequest, Task<AHelpDiscordPublishResult?>> handler in handlers.GetInvocationList())
            {
                result = await handler(request);
            }

            return result;
        }

        private async Task<bool> TryPublishDiscordAHelpBridgeAsync(NetUserId userId, DiscordRelayInteraction existingEmbed, WebhookPayload payload, bool onCallRelay) // Corvax: keep the bridge logic additive so the base queue flow can stay master-shaped.
        {
            var bridgeResult = await PublishDiscordAHelpAsync(new AHelpDiscordPublishRequest(userId, payload, onCallRelay));
            if (bridgeResult != null)
            {
                existingEmbed.Id = bridgeResult.Value.RootMessageId.ToString();
                _relayMessages[userId] = existingEmbed;
            }
            else if (OnDiscordAHelpPublishRequested != null)
            {
                _sawmill.Log(LogLevel.Error,
                    $"Discord ahelp bridge is installed but failed to publish message for user {userId}; skipping direct webhook fallback to avoid duplicate posts.");
                _relayMessages.Remove(userId);
                return true;
            }
            else
            {
                return false;
            }

            // Actually do the on call relay last, we just need to grab it before we dequeue every message above.
            if (onCallRelay &&
                _onCallData != null)
            {
                existingEmbed.OnCall = true;
                var roleMention = _config.GetCVar(CCVars.DiscordAhelpMention);

                if (!string.IsNullOrEmpty(roleMention))
                {
                    var message = new StringBuilder();
                    message.AppendLine($"<@&{roleMention}>");
                    message.AppendLine("Unanswered SOS");

                    // Need webhook data to get the correct link for that channel rather than on-call data.
                    if (_webhookData is { GuildId: { } guildId, ChannelId: { } channelId })
                    {
                        message.AppendLine(
                            $"**[Go to ahelp](https://discord.com/channels/{guildId}/{channelId}/{existingEmbed.Id})**");
                    }

                    payload = GeneratePayload(message.ToString(), existingEmbed.Username, existingEmbed.CharacterName);

                    var request = await _httpClient.PostAsync($"{_onCallUrl}?wait=true",
                        new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

                    var content = await request.Content.ReadAsStringAsync();
                    if (!request.IsSuccessStatusCode)
                    {
                        _sawmill.Log(LogLevel.Error, $"Discord returned bad status code when posting relay message (perhaps the message is too long?): {request.StatusCode}\nResponse: {content}");
                    }
                }
            }
            else
            {
                existingEmbed.OnCall = false;
            }

            return true;
        }

        public void ReceiveExternalAHelpMessage(NetUserId userId, string text, string senderName) // Corvax: import Discord thread replies back into in-game bwoinks.
        {
            _activeConversations[userId] = DateTime.Now;

            var escapedText = FormattedMessage.EscapeText(text);
            var bwoinkText = $"[color=#58bfff][Discord] {senderName}[/color]: {escapedText}";
            var bwoinkMessage = new BwoinkTextMessage(userId, SystemUserId, bwoinkText, playSound: true);
            var admins = GetTargetAdmins();

            LogBwoink(bwoinkMessage);

            foreach (var admin in admins)
            {
                RaiseNetworkEvent(bwoinkMessage, admin);
            }

            if (_playerManager.TryGetSessionById(userId, out var session) && !admins.Contains(session.Channel))
            {
                RaiseNetworkEvent(bwoinkMessage, session.Channel);
            }
        }
    }
}
