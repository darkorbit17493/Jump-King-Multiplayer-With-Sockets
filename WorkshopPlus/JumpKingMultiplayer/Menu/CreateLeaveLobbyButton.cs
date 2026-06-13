using BehaviorTree;
using JumpKing.PauseMenu.BT;
using JumpKingMultiplayer.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using JumpKingMultiplayer.Helpers;

namespace JumpKingMultiplayer.Menu
{

    public class CreateLeaveLobbyButton : TextButtonToggle
    {
        public CreateLeaveLobbyButton() : base(MultiplayerManager.IsOnline())
        {
        }

        protected override string GetName()
        {
            return MultiplayerManager.IsOnline() ? "Leave Lobby" : "Create Lobby";
        }

        protected override void OnToggle()
        {
            MultiplayerManager.ToggleOnline();
        }
    }
}
