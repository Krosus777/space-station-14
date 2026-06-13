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

        private async Task<AHelpDiscordPublishResult?> TryPublishDiscordAHelpAsync(NetUserId userId, WebhookPayload payload, bool onCallRelay) // Corvax: keep the core bwoink file on a single bridge call and isolate Discord transport here.
        {
            var handlers = OnDiscordAHelpPublishRequested;
            if (handlers == null)
                return null;

            AHelpDiscordPublishResult? result = null;
            foreach (Func<AHelpDiscordPublishRequest, Task<AHelpDiscordPublishResult?>> handler in handlers.GetInvocationList())
            {
                result = await handler(new AHelpDiscordPublishRequest(userId, payload, onCallRelay));
            }

            return result;
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
