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

        private enum BridgePublishResult
        {
            Fallback,
            Handled,
            Failed,
        }

        private async Task<BridgePublishResult> TryPublishDiscordAHelpBridgeAsync(NetUserId userId, DiscordRelayInteraction existingEmbed, WebhookPayload payload) // Corvax: keep the bridge logic additive so the base queue flow can stay master-shaped.
        {
            var bridgeResult = await PublishDiscordAHelpAsync(new AHelpDiscordPublishRequest(userId, payload, false));
            if (bridgeResult != null)
            {
                existingEmbed.Id = bridgeResult.Value.RootMessageId.ToString();
                _relayMessages[userId] = existingEmbed;
                return BridgePublishResult.Handled;
            }
            else if (OnDiscordAHelpPublishRequested != null)
            {
                _sawmill.Log(LogLevel.Error,
                    $"Discord ahelp bridge is installed but failed to publish message for user {userId}; skipping direct webhook fallback to avoid duplicate posts.");
                _relayMessages.Remove(userId);
                return BridgePublishResult.Failed;
            }
            else
            {
                return BridgePublishResult.Fallback;
            }
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
