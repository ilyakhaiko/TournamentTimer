using System;
using LiveSplit.Model;
using LiveSplit.UI.Components;

namespace LiveSplit.TournamentTimerBridge
{
    public sealed class TournamentTimerBridgeFactory : IComponentFactory
    {
        public string ComponentName
        {
            get { return "TournamentTimer Bridge"; }
        }

        public string Description
        {
            get { return "Sends LiveSplit Start/Split/Reset events to the local TournamentTimer Runner UI bridge."; }
        }

        public ComponentCategory Category
        {
            get { return ComponentCategory.Control; }
        }

        public string UpdateName
        {
            get { return ComponentName; }
        }

        public string UpdateURL
        {
            get { return ""; }
        }

        public string XMLURL
        {
            get { return ""; }
        }

        public Version Version
        {
            get { return Version.Parse("0.1.0"); }
        }

        public IComponent Create(LiveSplitState state)
        {
            return new TournamentTimerBridgeComponent(state);
        }
    }
}
