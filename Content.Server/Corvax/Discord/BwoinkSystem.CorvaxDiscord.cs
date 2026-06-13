using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Content.Server.Corvax.Discord;
using Content.Server.Discord;
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

        private async Task<bool> TryPublishDiscordAHelpAsync(NetUserId userId, WebhookPayload payload, bool onCallRelay, DiscordRelayInteraction existingEmbed) // Corvax: keep the core bwoink file on a single call-site and isolate Discord transport here.
        {
            var bridgeResult = await PublishDiscordAHelpAsync(new AHelpDiscordPublishRequest(userId, payload, onCallRelay));
            if (bridgeResult != null)
            {
                existingEmbed.Id = bridgeResult.Value.RootMessageId.ToString();
                _relayMessages[userId] = existingEmbed;
                return true;
            }

            if (OnDiscordAHelpPublishRequested != null)
            {
                _sawmill.Log(LogLevel.Error,
                    $"Discord ahelp bridge is installed but failed to publish message for user {userId}; skipping direct webhook fallback to avoid duplicate posts.");
                _relayMessages.Remove(userId);
                return false;
            }

            if (existingEmbed.Id == null)
            {
                var request = await _httpClient.PostAsync($"{_webhookUrl}?wait=true",
                    new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

                var content = await request.Content.ReadAsStringAsync();
                if (!request.IsSuccessStatusCode)
                {
                    _sawmill.Log(LogLevel.Error,
                        $"Discord returned bad status code when posting message (perhaps the message is too long?): {request.StatusCode}\nResponse: {content}");
                    _relayMessages.Remove(userId);
                    return false;
                }

                var id = JsonNode.Parse(content)?["id"];
                if (id == null)
                {
                    _sawmill.Log(LogLevel.Error,
                        $"Could not find id in json-content returned from discord webhook: {content}");
                    _relayMessages.Remove(userId);
                    return false;
                }

                existingEmbed.Id = id.ToString();
            }
            else
            {
                var request = await _httpClient.PatchAsync($"{_webhookUrl}/messages/{existingEmbed.Id}",
                    new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

                if (!request.IsSuccessStatusCode)
                {
                    var content = await request.Content.ReadAsStringAsync();
                    _sawmill.Log(LogLevel.Error,
                        $"Discord returned bad status code when patching message (perhaps the message is too long?): {request.StatusCode}\nResponse: {content}");
                    _relayMessages.Remove(userId);
                    return false;
                }
            }

            _relayMessages[userId] = existingEmbed;
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
