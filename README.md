# Boot Video Manager

Gestionnaire de **vidéos de démarrage** (startup movies) pour **Steam Big Picture** et le **Steam Deck**,
façon « mod launcher » : parcourez le catalogue de [steamdeckrepo.com](https://steamdeckrepo.com/),
prévisualisez une vidéo, installez-la en un clic et retirez-la proprement.

Application desktop multiplateforme (Windows, Linux / Steam Deck) en C# / .NET 10 et Avalonia.

> Application non officielle, sans lien avec Valve ni avec steamdeckrepo.com. Les vidéos appartiennent à
> leurs auteurs, crédités sur chaque fiche.

![Catalogue et aperçu](docs/screenshots/preview.png)

![Vidéos installées](docs/screenshots/installed.png)

## Fonctionnalités

- **Catalogue** complet de steamdeckrepo.com (≈ 8 400 vidéos) : miniature, titre, auteur, durée, « j'aime »,
  téléchargements, type et appareils ciblés.
- **Recherche** instantanée (titre ou auteur, sans tenir compte des accents), **tri** (tendances, plus
  téléchargées, plus aimées, récentes, anciennes) et **filtres** : démarrage / veille, Steam Deck / Steam Machine,
  durée.
- **Aperçu vidéo** en boucle dans l'application (libvlc), avec pause et coupure du son.
- **Installation** avec barre de progression et annulation : le fichier est téléchargé à part, vérifié
  (signature WebM, taille, empreinte SHA-256) puis placé dans `config/uioverrides/movies/` sous un nom propre et
  unique (`{slug}_{id}.webm`).
- **Onglet « Installées »** : toutes les vidéos du dossier, **y compris les intros d'origine de Steam**
  (`steamui/movies` : Steam Deck, Steam Deck OLED, Big Picture, SteamOS…) **et les vidéos cachées dans le cache de
  Steam** (`config/communityitemscache/startupmovies`, que la lecture aléatoire de Steam utilise aussi), avec leur
  statut :
  - *Installée par l'application* : suppression directe ;
  - *Modifiée depuis l'installation* ou *Ajoutée hors de l'application* : **jamais supprimée sans confirmation**.
  - *Intro d'origine de Steam* : jamais modifiée ni supprimée ;
  - *Cachée dans le dossier cache de Steam* : désactivable (rangée dans `startupmovies_disabled/`), suppression
    avec confirmation ; les objets achetés dans la boutique des points restent gérés par Steam ;
  - « Tout retirer » procède en deux confirmations distinctes.
- **Activer / désactiver sans retélécharger ni supprimer** : un interrupteur par vidéo décide si Steam peut la
  jouer (et la tirer au sort avec « Lecture aléatoire »). Une vidéo désactivée est rangée dans
  `config/uioverrides/movies_disabled/`, que Steam ne lit pas ; une intro d'origine activée est copiée dans
  `config/uioverrides/movies/` (`steam_default_*.webm`), l'original restant intact.
- **Import** d'un fichier `.webm` local.
- **Détection automatique de Steam** : registre Windows, `~/.steam`, `~/.local/share/Steam`, Flatpak, Snap ; choix
  manuel possible et mémorisé.
- **Hors ligne** : le dernier catalogue connu reste consultable, avec un message clair.

## Installation

### Windows

1. Téléchargez `BootVideoManager-<version>-win-x64.exe` (ou générez-le, voir [Build](#build)).
2. Placez-le où vous voulez et lancez-le : c'est un exécutable unique qui contient tout (runtime .NET, DLL et
   composants VLC). Au premier lancement, ses bibliothèques natives sont extraites dans `%TEMP%\.net`.

### Linux / Steam Deck (mode Bureau)

1. Générez l'archive `linux-x64` (voir [Build](#build)) ou l'AppImage (`packaging/linux/build-appimage.sh`).
2. Décompressez, puis `chmod +x BootVideoManager && ./BootVideoManager`.
3. L'aperçu vidéo utilise la **libvlc du système** : installez VLC via votre gestionnaire de paquets. Sans libvlc,
   tout fonctionne sauf l'aperçu, qui affiche un message explicatif. Sur SteamOS (système en lecture seule),
   l'aperçu n'est donc pas disponible pour l'instant (voir [Limites](#limites-connues)).

### Activer la vidéo dans Steam

Steam › **Paramètres** › **Personnalisation** › vidéo de démarrage. Sur Windows, la vidéo n'est jouée qu'au
lancement du mode **Big Picture**. Si Steam reste bloqué sur un écran noir, retirez la vidéo depuis l'onglet
*Installées*.

## Build

Prérequis : [SDK .NET 10](https://dotnet.microsoft.com/download) (version fixée par `global.json`).

```bash
dotnet build BootVideoManager.slnx
dotnet run --project src/BootVideoManager.App
dotnet test --solution BootVideoManager.slnx
```

Tester sans toucher à son vrai Steam : créez un dossier contenant un sous-dossier `config`, puis

```bash
dotnet run --project src/BootVideoManager.App -- --steam-root /chemin/vers/FauxSteam
```

Archives autonomes (tests puis publication) :

```powershell
./build/publish.ps1                     # win-x64 + linux-x64 → artifacts/publish/
./build/publish.ps1 -Runtime win-x64
```

```bash
./build/publish.sh linux-x64            # sous Linux (conserve le bit exécutable)
./packaging/linux/build-appimage.sh     # AppImage, nécessite appimagetool
```

## Architecture

```
src/BootVideoManager.Core    Services sans UI, entièrement testés
  Api/       client steamdeckrepo.com, parseur tolérant, nouvelles tentatives polies
  Catalog/   cache disque, rafraîchissement conditionnel, recherche / filtres / tris
  Install/   téléchargement vérifié, manifeste, réconciliation, désinstallation sûre
  Steam/     détection des installations Steam
  Caching/   cache des miniatures
  Platform/  chemins de l'application, réglages
src/BootVideoManager.App     Avalonia 12 + CommunityToolkit.Mvvm (Views / ViewModels / Services / Controls)
tests/BootVideoManager.Core.Tests   xUnit v3 (140 tests)
```

Détails et justifications : [docs/architecture.md](docs/architecture.md). Analyse de l'API et des chemins Steam :
[docs/research.md](docs/research.md).

### Données locales

| | Windows | Linux |
|---|---|---|
| Manifeste et réglages | `%APPDATA%\BootVideoManager\` | `~/.config/BootVideoManager/` |
| Cache (catalogue, miniatures) | `%LOCALAPPDATA%\BootVideoManager\cache\` | `~/.cache/BootVideoManager/` |

Le cache peut être supprimé sans risque. Le manifeste mémorise ce que l'application a installé ; s'il est
supprimé, les vidéos restent en place et demandent simplement une confirmation avant suppression.

## Respect du site

- Un seul appel au catalogue complet, mis en cache **une heure**, puis requêtes conditionnelles
  (`If-Modified-Since` → réponse 304 sans contenu) ; rafraîchissement manuel limité à une fois par minute.
- Recherche, tri et filtres **en local** : aucune requête pendant la navigation.
- Miniatures en cache disque, 4 téléchargements simultanés au maximum.
- User-Agent identifiable, respect de `Retry-After`, au plus 3 tentatives, jamais de boucle.
- Les téléchargements passent par le lien officiel du site (`/post/download/{id}`).

## Limites connues

- **Testé sous Windows 11 uniquement.** Le code Linux (chemins, Flatpak/Snap) est couvert par des tests unitaires
  mais n'a pas été essayé sur un Steam Deck réel.
- **Pas de filtre OLED / LCD** : steamdeckrepo.com ne fournit pas cette information.
- **Aperçu sous Linux** : dépend de la libvlc du système ; pas de paquet Flatpak pour l'instant (il faudrait y
  embarquer VLC). Le script AppImage n'a pas été exécuté (nécessite Linux).
- **Mode manette** : navigation clavier et repères de focus renforcés, défilement infini ; pas encore d'interface
  dédiée au mode Jeu du Deck.
- Les vidéos de veille s'installent comme les vidéos de démarrage ; leur sélection comme vidéo de sortie de
  veille dans Steam n'a pas été vérifiée sur un Deck.

## Crédits

- Catalogue, miniatures et vidéos : [steamdeckrepo.com](https://steamdeckrepo.com/) et ses créateurs.
- Projets ayant inspiré l'intégration : [steam-deck-repo-manager](https://github.com/waylaidwanderer/steam-deck-repo-manager)
  et le Steam Repo Manager historique de CapitaineJSparrow.
- [Avalonia](https://avaloniaui.net/), [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet),
  [LibVLCSharp](https://github.com/videolan/libvlcsharp), [System.IO.Abstractions](https://github.com/TestableIO/System.IO.Abstractions).
