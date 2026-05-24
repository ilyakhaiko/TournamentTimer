using System.Reflection;
using System.Runtime.InteropServices;
using LiveSplit.UI.Components;
using LiveSplit.TournamentTimerBridge;

[assembly: AssemblyTitle("LiveSplit.TournamentTimerBridge")]
[assembly: AssemblyDescription("Sends LiveSplit start/split/reset events to TournamentTimer Runner UI.")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyProduct("LiveSplit.TournamentTimerBridge")]
[assembly: AssemblyCopyright("")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]
[assembly: ComVisible(false)]
[assembly: Guid("20B5BE98-C449-455D-BD1C-AF97601F9414")]
[assembly: AssemblyVersion("0.1.0.0")]
[assembly: AssemblyFileVersion("0.1.0.0")]

[assembly: ComponentFactory(typeof(TournamentTimerBridgeFactory))]
