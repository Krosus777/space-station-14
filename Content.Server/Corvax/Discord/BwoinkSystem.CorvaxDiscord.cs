using System;
using System.Threading.Tasks;
using Content.Server.Corvax.Discord;
using Robust.Shared.Network;
using Robust.Shared.Utility;

namespace Content.Server.Administration.Systems
{
    public sealed partial class BwoinkSystem
    {
        public event Func<AHelpDiscordPublishRequest, Task<AHelpDiscordPublishResult?>>? OnDiscordAHelpPublishRequested;

        private async Task<AHelpDiscordPublishResult?> PublishDiscordAHelpAsync(AHelpDiscordPublishRequest request)
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

        public void ReceiveExternalAHelpMessage(NetUserId userId, string text, string senderName)
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
