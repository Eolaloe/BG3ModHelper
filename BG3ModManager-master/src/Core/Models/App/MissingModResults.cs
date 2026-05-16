using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DivinityModManager.Models.App;

public class MissingModsResults
{
	public Dictionary<string, DivinityMissingModData> Missing { get; } = [];
	public Dictionary<string, DivinityMissingModData> Dependencies { get; } = [];
	public Dictionary<string, DivinityMissingModData> ExtenderRequired { get; } = [];

	public int TotalMissing
	{
		get
		{
			return Missing.Count + Dependencies.Count;
		}
	}

	public void AddMissing(ModuleShortDesc mod, int index)
	{
		if(Dependencies.TryGetValue(mod.UUID, out var dep))
		{
			Dependencies.Remove(mod.UUID);
			dep.Index = index;
			Missing.Add(mod.UUID, dep);
		}
		else if (!Missing.ContainsKey(mod.UUID))
		{
			Missing.Add(mod.UUID, DivinityMissingModData.FromData(mod, index, false));
		}
	}

	public void AddMissing(DivinityLoadOrderEntry mod, int index)
	{
		if (Dependencies.TryGetValue(mod.UUID, out var dep))
		{
			Dependencies.Remove(mod.UUID);
			dep.Index = index;
			Missing.Add(mod.UUID, dep);
		}
		else if (!Missing.ContainsKey(mod.UUID))
		{
			Missing.Add(mod.UUID, DivinityMissingModData.FromData(mod, index, false));
		}
	}

	public void AddDependency(ModuleShortDesc mod, string[] requiredBy = null)
	{
		if (String.IsNullOrEmpty(mod.UUID)) return;

		if(Missing.TryGetValue(mod.UUID, out var existingMissing))
		{
			if (requiredBy != null) existingMissing.RequiredBy.AddRange(requiredBy);
		}
		else
		{
			if (Dependencies.TryGetValue(mod.UUID, out var existing))
			{
				if (requiredBy != null)
				{
					existing.RequiredBy.AddRange(requiredBy);
				}
			}
			else
			{
				Dependencies.Add(mod.UUID, DivinityMissingModData.FromData(mod, -1, true, requiredBy));
			}
		}
	}

	public void AddDependency(ModuleShortDesc mod, DivinityModData requiredByMod)
	{
		if (String.IsNullOrEmpty(mod.UUID)) return;

		var requiredByName = requiredByMod.Name ?? requiredByMod.FileName!;

		if (Missing.TryGetValue(mod.UUID, out var existingMissing))
		{
			if (!String.IsNullOrEmpty(requiredByName)) existingMissing.RequiredBy.Add(requiredByName);
		}
		else
		{
			if (Dependencies.TryGetValue(mod.UUID, out var existing))
			{
				existing.RequiredBy.Add(requiredByName);
			}
			else
			{
				Dependencies.Add(mod.UUID, DivinityMissingModData.FromData(mod, -1, true, [requiredByName]));
			}
		}
	}

	public void AddExtenderRequirement(DivinityModData mod, string[] requiredBy = null)
	{
		if (String.IsNullOrEmpty(mod.UUID)) return;

		if (ExtenderRequired.TryGetValue(mod.UUID, out var existing))
		{
			if (requiredBy != null)
			{
				existing.RequiredBy.AddRange(requiredBy);
			}
		}
		else
		{
			ExtenderRequired.Add(mod.UUID, DivinityMissingModData.FromData(mod, false, requiredBy));
		}
	}

	public string GetMissingMessage()
	{
		if (Missing.Count == 0) return string.Empty;
		var message = string.Join(Environment.NewLine, Missing.Values.OrderBy(x => x.Index));
		return message;
	}

	public string GetDependenciesMessage()
	{
		if (Dependencies.Count == 0) return string.Empty;
		var message = string.Join(Environment.NewLine, Dependencies.Values.OrderBy(x => x.Name));
		return message;
	}

	public string GetExtenderRequiredMessage()
	{
		if (ExtenderRequired.Count == 0) return string.Empty;
		var message = string.Join(Environment.NewLine, ExtenderRequired.Values.OrderBy(x => x.Name));
		return message;
	}
}
