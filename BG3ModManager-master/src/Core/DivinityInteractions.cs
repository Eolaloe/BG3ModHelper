namespace DivinityModManager;

public struct DeleteFilesViewConfirmationData
{
	public int Total;
	public bool PermanentlyDelete;
	public CancellationToken Token;
}

public static class DivinityInteractions
{
	public static readonly Interaction<DeleteFilesViewConfirmationData, bool> ConfirmModDeletion = new();
}
