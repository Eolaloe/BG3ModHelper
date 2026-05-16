using DivinityModManager.Models;

using DynamicData;

namespace DivinityModManager
{
	public interface IModRegistryService
	{
		bool TryGetDisplayName(string uuid, out string name);
		bool ModExists(string uuid);
		bool ModIsActive(string uuid);
	}
}

namespace DivinityModManager.AppServices
{
	public class ModRegistryService : IModRegistryService
	{
		private readonly SourceCache<DivinityModData, string> _mods;

		public bool TryGetDisplayName(string uuid, out string name)
		{
			name = "";
			var mod = _mods.Lookup(uuid);
			if (mod.HasValue)
			{
				name = mod.Value.DisplayName;
				return true;
			}
			return false;
		}

		public bool ModExists(string uuid)
		{
			var mod = _mods.Lookup(uuid);
			if (mod.HasValue)
			{
				return true;
			}
			return false;
		}

		public bool ModIsActive(string uuid)
		{
			var mod = _mods.Lookup(uuid);
			if (mod.HasValue && mod.Value.IsActive)
			{
				return true;
			}
			return false;
		}

		public ModRegistryService(SourceCache<DivinityModData, string> mods)
		{
			_mods = mods;
		}
	}
}
