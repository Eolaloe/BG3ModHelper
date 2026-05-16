namespace BG3MM_UpdateHelper;

public static class Constants
{
    // mod.io
    public const int    MODIO_GAME_ID          = 6715;
    public const string MODIO_API_BASE         = "https://g-6715.modapi.io";
    public const string MODIO_API_KEY_HELP_URL = "https://mod.io/me/access";

    // Nexus Mods
    public const string NEXUS_API_BASE         = "https://api.nexusmods.com";
    public const string NEXUS_GAME_DOMAIN      = "baldursgate3";
    public const string NEXUS_API_KEY_HELP_URL = "https://www.nexusmods.com/settings/api-keys";
    public const string NEXUS_FILE_METADATA_BASE =
        "https://file-metadata.nexusmods.com/file/nexus-files-s3-meta";
    public const int    NEXUS_BG3_GAME_ID      = 3474;

    // Nexus UUID↔ModId community DB (GitHub)
    public const string NEXUS_UUID_DB_URL =
        "https://raw.githubusercontent.com/Eolaloe/bg3-nexus-uuid-db/main/uuid_nexus_db.json";
    public const string NEXUS_UUID_SUBMIT_URL =
        "https://bg3-nexus-uuid-db.vercel.app/api/contribute";  // Vercel (미구현, 향후)
    public const int    NEXUS_UUID_DB_CACHE_HOURS = 12;

    // BG3MM process
    public const string BG3MM_PROCESS_NAME = "BG3ModManager";
    public const string BG3MM_EXE_NAME     = "BG3ModManager.exe";

    // BG3 mods folder
    public const string BG3_MODS_FOLDER_RELATIVE = @"Larian Studios\Baldur's Gate 3\Mods";

    // Helper data folder — %LOCALAPPDATA%\BG3MM_UpdateHelper
    public const string APP_DATA_FOLDER   = "BG3MM_UpdateHelper";
    public const string PAK_BACKUP_SUFFIX = ".bak";

    // mod.io batch query limit
    public const int MODIO_BATCH_SIZE = 100;
}
