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
            RepoApiErrorKind.Network => "Impossible de joindre steamdeckrepo.com. Vérifiez votre connexion internet.",
            RepoApiErrorKind.Timeout => "steamdeckrepo.com met trop de temps à répondre. Réessayez dans un instant.",
            RepoApiErrorKind.RateLimited => "steamdeckrepo.com limite le nombre de requêtes. Patientez une minute avant de réessayer.",
            RepoApiErrorKind.HttpError => $"steamdeckrepo.com a renvoyé une erreur (HTTP {(int?)exception.StatusCode}). Le site est peut-être en maintenance.",
            RepoApiErrorKind.InvalidResponse => "La réponse de steamdeckrepo.com n'est pas reconnue. Le site a peut-être changé : une mise à jour de l'application peut être nécessaire.",
            _ => exception.Message,
        };
    }

    public static string For(InstallException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var summary = exception.Kind switch
        {
            InstallErrorKind.Download => "Le téléchargement a échoué. Vérifiez votre connexion puis réessayez.",
            InstallErrorKind.InvalidFile => "Le fichier n'est pas une vidéo WebM valide : il n'a pas été installé.",
            InstallErrorKind.FileConflict => "Un fichier du même nom existe déjà (dossier des vidéos ou des vidéos désactivées) ou a été modifié hors de l'application : rien n'a été changé.",
            InstallErrorKind.FileSystem => "Opération impossible sur le disque (droits d'accès, espace libre, ou fichier utilisé par Steam).",
            _ => "L'opération a échoué.",
        };

        return $"{summary}\nDétail : {exception.Message}";
    }
}
