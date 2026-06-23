using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AnimeFiller.Configuration
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        public bool AutoSkipFiller { get; set; } = false;

        public bool CreateSkipButton { get; set; } = true;

        public string FillerSegmentType { get; set; } = "Recap";

        public bool PrefixEpisodeTitles { get; set; } = false;

        public string EpisodeTitlePrefix { get; set; } = "[Filler] ";

        public int ThrottlingDelayMs { get; set; } = 2000;
    }
}
