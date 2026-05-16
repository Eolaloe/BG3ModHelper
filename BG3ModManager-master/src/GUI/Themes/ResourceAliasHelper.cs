using System.Windows.Markup;
using System.Xaml;

namespace DivinityModManager.Themes;

internal class ResourceAliasHelper : MarkupExtension
{
	public object ResourceKey { get; set; }

	public override object ProvideValue(IServiceProvider serviceProvider)
	{
		IRootObjectProvider rootObjectProvider = (IRootObjectProvider)serviceProvider.GetService(typeof(IRootObjectProvider));
		IDictionary dictionary = rootObjectProvider?.RootObject as IDictionary;
		return dictionary?[ResourceKey];
	}
}
