using DivinityModManager.Util.ScreenReader;

using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace DivinityModManager.Controls;

public class ModEntryGrid : Grid
{
	public ModEntryGrid() : base() { }

	protected override AutomationPeer OnCreateAutomationPeer()
	{
		return new ModEntryGridAutomationPeer(this);
	}
}
