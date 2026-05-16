using CrossSpeak;

using DivinityModManager.Util;

namespace DivinityModManager
{
	public interface IScreenReaderService
	{
		bool IsScreenReaderActive();
		void Output(string text, bool interrupt = false);
		void Speak(string text, bool interrupt = false);
		void Close();
		void Silence();
	}
}

namespace DivinityModManager.AppServices
{
	public class ScreenReaderService : IScreenReaderService
	{
		private static readonly string[] _dlls = ["nvdaControllerClient64.dll", "SAAPI64.dll", "Tolk.dll"];
		private static bool _loadedDlls = false;

		public bool IsScreenReaderActive()
		{
			if (EnsureInit(false))
			{
				return !String.IsNullOrWhiteSpace(CrossSpeakManager.Instance.DetectScreenReader());
			}
			return false;
		}

		public void Close()
		{
			if (CrossSpeakManager.Instance.IsLoaded())
			{
				CrossSpeakManager.Instance.Close();
			}
		}

		public void Silence()
		{
			if (CrossSpeakManager.Instance.IsLoaded())
			{
				CrossSpeakManager.Instance.Silence();
			}
		}

		private bool EnsureInit(bool trySAPI = false)
		{
			if (!_loadedDlls)
			{
				var libPath = Path.Combine(DivinityApp.GetAppDirectory(), "_Lib");
				foreach (var dll in _dlls)
				{
					var filePath = Path.Combine(libPath, dll);
					try
					{
						if (File.Exists(filePath))
						{
							NativeLibraryHelper.LoadLibrary(filePath);
						}
					}
					catch (Exception ex)
					{
						DivinityApp.Log($"Error loading '{dll}':\n{ex}");
					}
				}
				_loadedDlls = true;
			}
			if (!CrossSpeakManager.Instance.IsLoaded())
			{
				CrossSpeakManager.Instance.Initialize();
				if (trySAPI && !CrossSpeakManager.Instance.HasSpeech())
				{
					CrossSpeakManager.Instance.TrySAPI(true);
				}
			}
			return CrossSpeakManager.Instance.IsLoaded();
		}

		public void Output(string text, bool interrupt = true)
		{
			if (EnsureInit(true))
			{
				CrossSpeakManager.Instance.Output(text, interrupt);
			}
		}

		public void Speak(string text, bool interrupt = true)
		{
			if (EnsureInit(true))
			{
				CrossSpeakManager.Instance.Output(text, interrupt);
			}
		}
	}
}
