using BootVideoManager.Core.Localization;

namespace BootVideoManager.App.Localization;

/// <summary>
/// Fixed interface texts used by the XAML views (<c>{x:Static l:Strings.Name}</c>). Messages built in code use
/// <see cref="Loc.T"/> directly, next to where they are shown.
/// </summary>
public static class Strings
{
    // Shell
    public static string AppTagline => Loc.T("Vidéos de démarrage pour Steam Big Picture et Steam Deck", "Startup videos for Steam Big Picture and Steam Deck");

    public static string TabCatalog => Loc.T("CATALOGUE", "CATALOG");

    public static string TabInstalled => Loc.T("INSTALLÉES", "INSTALLED");

    public static string TabSettings => Loc.T("RÉGLAGES", "SETTINGS");

    public static string Navigation => Loc.T("Navigation", "Navigation");

    public static string Footer => Loc.T(
        "Catalogue et vidéos fournis par steamdeckrepo.com. Chaque vidéo appartient à son auteur. Application non officielle, sans lien avec Valve ni avec steamdeckrepo.com.",
        "Catalog and videos provided by steamdeckrepo.com. Each video belongs to its author. Unofficial app, not affiliated with Valve or steamdeckrepo.com.");

    public static string Close => Loc.T("Fermer", "Close");

    public static string Cancel => Loc.T("Annuler", "Cancel");

    // Update banner
    public static string UpdateInstall => Loc.T("Mettre à jour", "Update");

    public static string UpdateSkip => Loc.T("Ignorer cette version", "Skip this version");

    public static string UpdateLater => Loc.T("Plus tard", "Later");

    public static string UpdateNotes => Loc.T("Nouveautés", "What's new");

    // Catalog
    public static string PreviewTitle => Loc.T("APERÇU", "PREVIEW");

    public static string Install => Loc.T("Installer", "Install");

    public static string RemoveFromSteam => Loc.T("Retirer de Steam", "Remove from Steam");

    public static string ViewOnSite => Loc.T("Voir sur steamdeckrepo.com", "View on steamdeckrepo.com");

    public static string DescriptionTitle => Loc.T("DESCRIPTION", "DESCRIPTION");

    public static string SearchPlaceholder => Loc.T("Rechercher un titre ou un auteur…", "Search a title or an author…");

    public static string Search => Loc.T("Rechercher", "Search");

    public static string Sort => Loc.T("Tri", "Sort");

    public static string VideoType => Loc.T("Type de vidéo", "Video type");

    public static string Device => Loc.T("Appareil", "Device");

    public static string Duration => Loc.T("Durée", "Duration");

    public static string ResetFilters => Loc.T("Réinitialiser", "Reset");

    public static string Refresh => Loc.T("Actualiser", "Refresh");

    public static string Retry => Loc.T("Réessayer", "Retry");

    public static string PreviewAndDetails => Loc.T("Aperçu et détails", "Preview and details");

    public static string InstalledBadge => Loc.T("INSTALLÉE", "INSTALLED");

    public static string Remove => Loc.T("Retirer", "Remove");

    public static string ShowMore => Loc.T("Afficher davantage", "Show more");

    public static string Queued => Loc.T("En attente…", "Queued…");

    // Installed
    public static string ImportWebm => Loc.T("Importer un fichier .webm…", "Import a .webm file…");

    public static string Share => Loc.T("Partager ▾", "Share ▾");

    public static string ExportPack => Loc.T("Exporter mes vidéos dans un fichier…", "Export my videos to a file…");

    public static string ImportPack => Loc.T("Importer un pack…", "Import a pack…");

    public static string ShareTip => Loc.T(
        "Partagez votre sélection avec un fichier .bvmpack : vos amis l'importent et l'application télécharge les mêmes vidéos depuis le catalogue.",
        "Share your selection as a .bvmpack file: your friends import it and the app downloads the same videos from the catalog.");

    public static string OpenFolder => Loc.T("Ouvrir le dossier", "Open folder");

    public static string RemoveAll => Loc.T("Tout retirer", "Remove all");

    public static string NoVideoTitle => Loc.T("Aucune vidéo installée", "No video installed");

    public static string NoVideoHint => Loc.T(
        "Parcourez le catalogue pour installer une vidéo de démarrage ou importez votre propre fichier .webm.",
        "Browse the catalog to install a startup video, or import your own .webm file.");

    public static string Enabled => Loc.T("Activée", "Enabled");

    public static string Disabled => Loc.T("Désactivée", "Disabled");

    public static string ToggleTip => Loc.T(
        "Autoriser Steam à lire cette vidéo (la désactiver ne la supprime pas)",
        "Let Steam play this video (disabling it does not delete it)");

    public static string ViewPage => Loc.T("Voir la page", "View page");

    public static string Delete => Loc.T("Supprimer", "Delete");

    // Settings
    public static string SteamFolderTitle => Loc.T("DOSSIER STEAM", "STEAM FOLDER");

    public static string FolderInUse => Loc.T("Dossier utilisé", "Folder in use");

    public static string VideosGoTo => Loc.T("Les vidéos sont placées dans", "Videos are placed in");

    public static string DetectedTitle => Loc.T("INSTALLATIONS DÉTECTÉES", "DETECTED INSTALLATIONS");

    public static string DetectHint => Loc.T(
        "Le registre Windows, ~/.steam, ~/.local/share/Steam ainsi que les installations Flatpak et Snap sont analysés automatiquement.",
        "The Windows registry, ~/.steam, ~/.local/share/Steam and Flatpak and Snap installations are scanned automatically.");

    public static string Use => Loc.T("Utiliser", "Use");

    public static string Redetect => Loc.T("Relancer la détection", "Detect again");

    public static string ChooseFolder => Loc.T("Choisir un dossier…", "Choose a folder…");

    public static string HowToTitle => Loc.T("UTILISER UNE VIDÉO DANS STEAM", "USING A VIDEO IN STEAM");

    public static string HowTo1 => Loc.T(
        "1. Installez une vidéo depuis le catalogue (ou importez un fichier .webm).",
        "1. Install a video from the catalog (or import a .webm file).");

    public static string HowTo2 => Loc.T("2. Dans Steam, ouvrez Paramètres › Personnalisation.", "2. In Steam, open Settings › Customization.");

    public static string HowTo3 => Loc.T(
        "3. Sélectionnez-la dans « Vidéo de démarrage ». Les animations de veille se choisissent de la même manière, dans la rubrique consacrée à la mise en veille.",
        "3. Select it under “Startup movie”. Suspend animations are chosen the same way, in the suspend section.");

    public static string HowToNote => Loc.T(
        "Sous Windows, la vidéo n'est lue qu'au lancement du mode Big Picture. Si Steam reste bloqué sur un écran noir, supprimez ou désactivez la vidéo depuis l'onglet Installées.",
        "On Windows, the video only plays when Big Picture mode starts. If Steam gets stuck on a black screen, delete or disable the video from the Installed tab.");

    public static string UpdatesTitle => Loc.T("MISES À JOUR", "UPDATES");

    public static string CheckAtStartup => Loc.T("Rechercher les mises à jour au démarrage", "Check for updates at startup");

    public static string CheckNow => Loc.T("Rechercher maintenant", "Check now");

    public static string ReleasesPage => Loc.T("Page des versions", "Releases page");

    public static string LanguageTitle => Loc.T("LANGUE", "LANGUAGE");

    public static string RestartNow => Loc.T("Redémarrer maintenant", "Restart now");

    public static string AboutTitle => Loc.T("À PROPOS", "ABOUT");

    public static string AboutText => Loc.T(
        "Le catalogue, les miniatures et les vidéos proviennent de steamdeckrepo.com. Chaque vidéo reste l'œuvre de son auteur, crédité sur sa fiche. Merci à la communauté et à l'équipe du site.",
        "The catalog, thumbnails and videos come from steamdeckrepo.com. Each video remains the work of its author, credited on its page. Thanks to the community and the site's team.");

    public static string AboutPolite => Loc.T(
        "L'application sollicite le site avec modération : catalogue conservé en cache pendant une heure, requêtes conditionnelles, miniatures mises en cache et identification claire (User-Agent).",
        "The app is gentle with the site: catalog cached for an hour, conditional requests, cached thumbnails and a clear identification (User-Agent).");

    public static string OpenSite => Loc.T("Ouvrir steamdeckrepo.com", "Open steamdeckrepo.com");

    public static string OpenLogs => Loc.T("Ouvrir le dossier des journaux", "Open the logs folder");

    public static string LogsHint => Loc.T(
        "En cas de problème, joignez le journal du jour à votre signalement sur GitHub.",
        "If something goes wrong, attach today's log to your report on GitHub.");

    public static string Version => Loc.T("Version", "Version");

    // Preview
    public static string PreviewLoading => Loc.T("Chargement de l'aperçu…", "Loading preview…");

    public static string Pause => Loc.T("Pause", "Pause");

    public static string Play => Loc.T("Lecture", "Play");

    public static string Mute => Loc.T("Couper le son", "Mute");

    public static string Unmute => Loc.T("Rétablir le son", "Unmute");

    public static string Volume => Loc.T("Volume", "Volume");
}
