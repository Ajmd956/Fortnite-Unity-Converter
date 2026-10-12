using System;
using System.Linq;
using FortnitePorting.Models.API.Responses;

namespace FortnitePorting.Models.News;

// The changelog shown on Home and on News & Updates. Edit this list for each release.
public static class LocalNews
{
    public static readonly NewsEntry[] Entries =
    [
        new()
        {
            Tag = "Update", Title = "Version 2.0.0", SubTitle = "A big update: the app now runs on Fortnite Porting's file browser and previews.",
            Date = new DateTime(2026, 10, 12),
            Description = "Version 2.0.0 is a big update.\n\n" +
                          "The app is rebuilt on Fortnite Porting's code, so the Files tab, the previews and the material graph are the real thing. " +
                          "Export to Unity sends the selected materials straight to the Convert tab and starts the conversion. " +
                          "Files that are not materials show a notice that they are not available for Unity yet.\n\n" +
                          "Your settings from the old version are not carried over, apart from the update token, so you set your game folder once."
        },
        new()
        {
            Tag = "Files", Title = "Refresh", SubTitle = "Re-read the Fortnite install without restarting.",
            Date = new DateTime(2026, 10, 12),
            Description = "The Files tab has a Refresh button. It clears the file list and reads your Fortnite install again, for example after the game updates."
        },
        new()
        {
            Tag = "News", Title = "Rotating pictures", SubTitle = "Pictures change every 10 seconds.",
            Date = new DateTime(2026, 10, 10),
            Description = "The pictures on the Home cards and the News & Updates tab are real Fortnite loading screens picked at random. " +
                          "They change every 10 seconds and never repeat on screen at the same time."
        },
        new()
        {
            Tag = "Updates", Title = "Updates inside the app", SubTitle = "Update without leaving the app.",
            Date = new DateTime(2026, 10, 10),
            Description = "When a new version is out, a popup shows which version it is and you can update with one click. " +
                          "The app downloads the new version and restarts into it."
        },
        new()
        {
            Tag = "Settings", Title = "Check for updates", SubTitle = "Turn checks on or off, or check now.",
            Date = new DateTime(2026, 10, 10),
            Description = "App Settings has a Check for Updates switch and a Check now button that tells you if you are up to date."
        }
    ];

    public static NewsEntry[] HomeCards => Entries.Take(3).ToArray();

    public static void StartRotation() => LoadingScreens.Start(Entries);
}
