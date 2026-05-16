namespace DivinityModManager.Models.Steam;

public interface IWorkshopPublishFileDetails
{
	string publishedfileid { get; set; }
	long time_created { get; set; }
	long time_updated { get; set; }

	List<WorkshopTag> tags { get; set; }
}
