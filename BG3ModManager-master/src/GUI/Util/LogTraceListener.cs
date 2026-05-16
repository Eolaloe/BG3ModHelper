using System.Diagnostics;

namespace DivinityModManager.Util;

public class LogTraceListener : TextWriterTraceListener
{
	private readonly Dictionary<string, string> replacePaths = [];

	private void MaybeAddReplacement(string key, string path)
	{
		if (!String.IsNullOrEmpty(path))
		{
			replacePaths.Add(key, path);
		}
	}

	public LogTraceListener(string fileName, string name) : base(fileName, name)
	{
		MaybeAddReplacement("%LOCALAPPDATA%", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
		MaybeAddReplacement("%APPDATA%", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
		MaybeAddReplacement("%USERPROFILE%", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
	}

	private string ReplaceText(string message)
	{
		if (!String.IsNullOrEmpty(message))
		{
			foreach (var kvp in replacePaths)
			{
				message = message.Replace(kvp.Value, kvp.Key);
			}
		}
		return message;
	}

	public override void Write(string message)
	{
		base.Write(ReplaceText(message));
	}

	public override void WriteLine(string message)
	{
		base.WriteLine(ReplaceText(message));
	}
}
