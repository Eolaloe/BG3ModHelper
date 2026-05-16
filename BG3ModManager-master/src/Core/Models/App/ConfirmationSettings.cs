using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Threading.Tasks;

namespace DivinityModManager.Models.App;

[DataContract]
public class ConfirmationSettings : ReactiveObject
{
	[Reactive, DataMember] public bool DisableAdminModeWarning { get; set; }
}
