using DivinityModManager.Util.ScreenReader;

using System.Reflection;
using System.Windows;

namespace DivinityModManager;

internal class Program
{
	private static SplashScreen _splash;
	private static string _libDirectory;

	private static Assembly ResolveAssembly(object sender, ResolveEventArgs args)
	{
		var assyName = new AssemblyName(args.Name);

		var newPath = Path.Combine(_libDirectory, assyName.Name);
		if (!newPath.EndsWith(".dll"))
		{
			newPath += ".dll";
		}

		if (File.Exists(newPath))
		{
			var assy = Assembly.LoadFile(newPath);
			return assy;
		}
		return null;
	}

	private static void OnAppExit(object sender, EventArgs e)
	{
		//CrossSpeakManager: Make sure to always call the Close() method before your application closes.
		Services.ScreenReader?.Close();
	}

	[STAThread]
	static void Main(string[] args)
	{
		_libDirectory = Path.Join(AppDomain.CurrentDomain.BaseDirectory, "_Lib");
		AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;

		_splash = new SplashScreen("Resources/BG3MMSplashScreen.png");
		_splash.Show(false, false);

		var app = new App
		{
			Splash = _splash
		};
		app.Exit += OnAppExit;
		app.InitializeComponent();
		app.Run();
	}
}
