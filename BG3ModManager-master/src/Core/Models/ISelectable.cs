using System.Windows;

namespace DivinityModManager.Models;

public interface ISelectable
{
	bool IsSelected { get; set; }
	Visibility Visibility { get; set; }
	bool CanDrag { get; }
}
