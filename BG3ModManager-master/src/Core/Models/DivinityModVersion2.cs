using Newtonsoft.Json;

namespace DivinityModManager.Models;

[JsonObject(MemberSerialization.OptIn)]
public class DivinityModVersion2 : ReactiveObject
{
	[Reactive] public ulong Major { get; set; }
	[Reactive] public ulong Minor { get; set; }
	[Reactive] public ulong Revision { get; set; }
	[Reactive] public ulong Build { get; set; }

	[JsonProperty][Reactive] public string Version { get; set; }

	private ulong versionInt = 0;

	[JsonProperty]
	public ulong VersionInt
	{
		get { return versionInt; }
		set => ParseInt(value);
	}

	private void UpdateVersion()
	{
		Version = $"{Major}.{Minor}.{Revision}.{Build}";
	}

	public ulong ToInt()
	{
		return (Major << 55) + (Minor << 47) + (Revision << 31) + Build;
	}

	public override string ToString()
	{
		return String.Format("{0}.{1}.{2}.{3}", Major, Minor, Revision, Build);
	}

	public void ParseInt(ulong nextVersionInt)
	{
		nextVersionInt = Math.Max(ulong.MinValue, Math.Min(nextVersionInt, ulong.MaxValue));
		if (versionInt != nextVersionInt)
		{
			versionInt = nextVersionInt;
			if (versionInt != 0)
			{
				Major = versionInt >> 55;
				Minor = (versionInt >> 47) & 0xFF;
				Revision = (versionInt >> 31) & 0xFFFF;
				Build = versionInt & 0x7FFFFFFFUL;
			}
			else
			{
				Major = Minor = Revision = Build = 0;
			}
			this.RaisePropertyChanged("VersionInt");
		}
	}

	public static DivinityModVersion2 FromInt(ulong vInt)
	{
		if (vInt == 1 || vInt == 268435456)
		{
			// 1.0.0.0
			vInt = 36028797018963968;
		}
		return new DivinityModVersion2(vInt);
	}

	public static bool operator >(DivinityModVersion2 a, DivinityModVersion2 b)
	{
		return a.VersionInt > b.VersionInt;
	}

	public static bool operator <(DivinityModVersion2 a, DivinityModVersion2 b)
	{
		return a.VersionInt < b.VersionInt;
	}

	public static bool operator >=(DivinityModVersion2 a, DivinityModVersion2 b)
	{
		return a.VersionInt >= b.VersionInt;
	}

	public static bool operator <=(DivinityModVersion2 a, DivinityModVersion2 b)
	{
		return a.VersionInt <= b.VersionInt;
	}

	public DivinityModVersion2()
	{
		this.WhenAnyValue(x => x.VersionInt).Subscribe((x) =>
		{
			UpdateVersion();
		});
	}

	public DivinityModVersion2(ulong vInt) : this()
	{
		ParseInt(vInt);
	}

	public DivinityModVersion2(ulong headerMajor, ulong headerMinor, ulong headerRevision, ulong headerBuild) : this()
	{
		Major = headerMajor;
		Minor = headerMinor;
		Revision = headerRevision;
		Build = headerBuild;
		versionInt = ToInt();
		UpdateVersion();
	}

	public static readonly DivinityModVersion2 Empty = new(0);
}
