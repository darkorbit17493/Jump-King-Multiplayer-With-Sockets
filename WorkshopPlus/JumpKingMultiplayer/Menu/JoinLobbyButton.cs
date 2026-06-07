using JumpKingMultiplayer.Models;
using Steamworks;
using JumpKingMultiplayer.Helpers;

namespace JumpKingMultiplayer.Menu
{
    public class JoinLobbyButton : TextButtonToggle
    {
        public JoinLobbyButton() : base(ModEntry.Preferences.LobbySettings.OpenToJoin)
        {
        }

        protected override bool CanChange()
        {
            return MultiplayerManager.IsOnline();
        }

        protected override string GetName() => "Join Lobby";

        protected override void OnToggle()
        {
            MultiplayerManager.instance.JoinLobby();
        }
    }
}
