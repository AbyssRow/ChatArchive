using ChatArchive.Core.Data;
using ChatArchive.Core.IO;
using ChatArchive.Core.Media;
using ChatArchive.Core.Repositories;

namespace ChatArchive.App.Services;

/// <summary>应用级服务容器：数据库、仓储与媒体定位器。</summary>
public sealed class AppServices
{
    private static readonly object Gate = new();
    private static AppServices? _instance;

    public ArchiveDatabase Database { get; }
    public MediaLocator MediaLocator { get; }
    public ConversationRepository Conversations { get; }
    public SearchRepository Search { get; }
    public SenderRepository Senders { get; }
    public StatsRepository Stats { get; }
    public ContactRepository Contacts { get; }
    public ContactRepository ContactRepository => Contacts;
    public AvatarStorageService AvatarStorage { get; }
    public AvatarStorageService AvatarStorageService => AvatarStorage;
    public AppSettings Settings { get; }

    private AppServices(AppSettings settings)
    {
        Settings = settings;
        var dataDir = settings.GetValidDataDirectory();
        try
        {
            Directory.CreateDirectory(dataDir);
        }
        catch
        {
            dataDir = AppSettings.DefaultDataDirectory;
            Directory.CreateDirectory(dataDir);
        }

        Database = new ArchiveDatabase(Path.Combine(dataDir, "chat_archive.db"));
        Database.EnsureSchema();
        Database.CleanEmptyConversations();
        Database.RepairDuplicateConversationsAndSenders();
        MediaLocator = new MediaLocator(Path.Combine(dataDir, "media"));
        Conversations = new ConversationRepository(Database);
        Search = new SearchRepository(Database);
        Senders = new SenderRepository(Database);
        Stats = new StatsRepository(Database);
        Contacts = new ContactRepository(Database);
        AvatarStorage = new AvatarStorageService(Path.Combine(dataDir, "avatars"));
    }

    public static AppServices Instance
    {
        get
        {
            lock (Gate)
            {
                return _instance ??= new AppServices(AppSettings.Load());
            }
        }
    }
}
