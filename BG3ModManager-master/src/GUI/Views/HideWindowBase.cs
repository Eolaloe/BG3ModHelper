using AdonisUI.Controls;

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DivinityModManager.Views;

public class HideWindowBase<TViewModel> : AdonisWindow, IViewFor<TViewModel> where TViewModel : class
{
	/// <summary>
	/// The view model dependency property.
	/// </summary>
	public static readonly DependencyProperty ViewModelProperty =
		DependencyProperty.Register("ViewModel",
		typeof(TViewModel),
		typeof(HideWindowBase<TViewModel>),
		new PropertyMetadata(null));

	/// <summary>
	/// Gets the binding root view model.
	/// </summary>
	public TViewModel BindingRoot => ViewModel;

	/// <inheritdoc/>
	public TViewModel ViewModel
	{
		get => (TViewModel)GetValue(ViewModelProperty);
		set => SetValue(ViewModelProperty, value);
	}

	/// <inheritdoc/>
	object IViewFor.ViewModel
	{
		get => ViewModel;
		set => ViewModel = (TViewModel)value;
	}

	public HideWindowBase()
	{
		Closing += HideWindow_Closing;
		KeyDown += (o, e) =>
		{
			if (!e.Handled && e.Key == System.Windows.Input.Key.Escape)
			{
				if (Keyboard.FocusedElement == null || Keyboard.FocusedElement.GetType() != typeof(TextBox))
				{
					Hide();
				}
			}
		};
	}

	protected override void OnSourceInitialized(EventArgs e)
	{
		base.OnSourceInitialized(e);
	}

	public virtual void HideWindow_Closing(object sender, CancelEventArgs e)
	{
		e.Cancel = true;
		Hide();
	}
}
