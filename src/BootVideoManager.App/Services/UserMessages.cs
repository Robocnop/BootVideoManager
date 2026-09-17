using BootVideoManager.Core.Api;
using BootVideoManager.Core.Install;

namespace BootVideoManager.App.Services;

/// <summary>Turns error categories into clear French messages; technical details are appended for bug reports.</summary>
public static class UserMessages
{
    public static string For(RepoApiException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.Kind switch
        {
            RepoApiErrorKind.Network => "Impossible de joindre steamdeckrepo.com. Vérifiez votre connexion Internet.",
            RepoApiErrorKind.Timeout => "steamdeckrepo.com met trop de temps à répondre. Réessayez dans un instant.",
            RepoApiErrorKind.RateLimited => "steamdeckrepo.com limite le nombre de requêtes. Patientez une minute avant de réessayer.",
            RepoApiErrorKind.HttpError => $"steamdeckrepo.com a renvoyé une erreur (HTTP {(int?)exception.StatusCode}). Le site est peut-être en cours de maintenance.",
            RepoApiErrorKind.InvalidResponse => "La réponse de steamdeckrepo.com est illisible. Le site a peut-être évolué : une mise à jour de l'application est sans doute nécessaire.",
            _ => exception.Message,
        };
    }

    public static string For(InstallException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var summary = exception.Kind switch
        {
            InstallErrorKind.Download => "Le téléchargement a échoué. Vérifiez votre connexion, puis réessayez.",
            InstallErrorKind.InvalidFile => "Ce fichier n'est pas une vidéo WebM valide : il n'a pas été installé.",
            InstallErrorKind.FileConflict => "Un fichier portant le même nom existe déjà (dans le dossier des vidéos ou des vidéos désactivées) ou a été modifié en dehors de l'application : aucune modification n'a été effectuée.",
            InstallErrorKind.FileSystem => "Impossible d'accéder au disque : vérifiez les droits d'accès et l'espace libre, ou fermez Steam s'il utilise le fichier.",
            _ => "L'opération a échoué.",
        };

        return $"{summary}\nDétails : {exception.Message}";
    }
}
