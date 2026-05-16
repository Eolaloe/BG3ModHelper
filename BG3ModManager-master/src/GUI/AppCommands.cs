using System.Windows.Controls;

namespace DivinityModManager;

public static class AppCommands
{
	public static ReactiveCommand<object, Unit> Clear { get; }

	private static void ClearText(object sender)
	{
		if (sender is MenuItem menuItem)
		{
			var cm = menuItem.FindVisualParent<ContextMenu>();
			if (cm != null && cm.PlacementTarget is TextBox tb)
			{
				tb.Clear();
			}
		}
		else if (sender is TextBox textBox)
		{
			textBox.Clear();
		}
		else if (sender is ContextMenu cm)
		{
			if (cm.PlacementTarget is TextBox tb)
			{
				tb.Clear();
			}
		}
	}

	static AppCommands()
	{
		Clear = ReactiveCommand.Create<object>(ClearText);
	}
}
