using BootVideoManager.Core.Api;
using BootVideoManager.Core.Install;
using BootVideoManager.Core.Localization;
using BootVideoManager.Core.Updates;

namespace BootVideoManager.App.Services;

/// <summary>Turns error categories into clear messages; technical details are appended for bug reports.</summary>
public static class UserMessages
{
    public static string For(RepoApiException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.Kind switch
        {
            RepoApiErrorKind.Network => Loc.T(
                "Impossible de joindre steamdeckrepo.com. Vérifiez votre connexion Internet.",
                "Could not reach steamdeckrepo.com. Check your Internet connection."),
            RepoApiErrorKind.Timeout => Loc.T(
                "steamdeckrepo.com met trop de temps à répondre. Réessayez dans un instant.",
                "steamdeckrepo.com is taking too long to answer. Try again in a moment."),
            RepoApiErrorKind.RateLimited => Loc.T(
                "steamdeckrepo.com limite le nombre de requêtes. Patientez une minute avant de réessayer.",
                "steamdeckrepo.com is limiting requests. Wait a minute before trying again."),
            RepoApiErrorKind.HttpError => Loc.T(
                $"steamdeckrepo.com a renvoyé une erreur (HTTP {(int?)exception.StatusCode}). Le site est peut-être en cours de maintenance.",
                $"steamdeckrepo.com returned an error (HTTP {(int?)exception.StatusCode}). The site may be under maintenance."),
            RepoApiErrorKind.InvalidResponse => Loc.T(
                "La réponse de steamdeckrepo.com est illisible. Le site a peut-être évolué : une mise à jour de l'application est sans doute nécessaire.",
                "steamdeckrepo.com's answer could not be read. The site may have changed: the app probably needs an update."),
            RepoApiErrorKind.SignedOut => Loc.T(
                "Votre session steamdeckrepo.com a expiré. Reconnectez-vous dans l'onglet Réglages.",
                "Your steamdeckrepo.com session has expired. Sign in again from the Settings tab."),
            _ => exception.Message,
        };
    }

    public static string For(InstallException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var summary = exception.Kind switch
        {
            InstallErrorKind.Download => Loc.T(
                "Le téléchargement a échoué. Vérifiez votre connexion, puis réessayez.",
                "The download failed. Check your connection, then try again."),
            InstallErrorKind.InvalidFile => Loc.T(
                "Ce fichier n'est pas une vidéo WebM valide : il n'a pas été installé.",
                "This file is not a valid WebM video: it was not installed."),
            InstallErrorKind.FileConflict => Loc.T(
                "Un fichier portant le même nom existe déjà (dans le dossier des vidéos ou des vidéos désactivées) ou a été modifié en dehors de l'application : aucune modification n'a été effectuée.",
                "A file with the same name already exists (in the videos or disabled videos folder) or was modified outside the app: nothing was changed."),
            InstallErrorKind.FileSystem => Loc.T(
                "Impossible d'accéder au disque : vérifiez les droits d'accès et l'espace libre, ou fermez Steam s'il utilise le fichier.",
                "Could not access the disk: check permissions and free space, or close Steam if it is using the file."),
            _ => Loc.T("L'opération a échoué.", "The operation failed."),
        };

        return $"{summary}\n{Loc.T("Détails", "Details")} : {exception.Message}";
    }

    public static string For(UpdateException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return $"{Loc.T("La mise à jour a échoué.", "The update failed.")} {exception.Message}";
    }
}
