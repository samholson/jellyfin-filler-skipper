using System;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Jellyfin.Plugin.AnimeFiller.Configuration;
using Jellyfin.Plugin.AnimeFiller.Services;

namespace Jellyfin.Plugin.AnimeFiller
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        public override string Name => "Anime Filler Skip";

        public override Guid Id => Guid.Parse("d71d3a5a-8b89-4475-b461-125026df332f");

        public override string Description => "Scrapes animefillerlist.com to identify anime filler episodes, providing native skip segments and tags.";

        public static Plugin? Instance { get; private set; }

        public static AnimeFillerService? FillerService { get; set; }

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = "AnimeFillerSkip",
                    EmbeddedResourcePath = string.Format("{0}.Configuration.configPage.html", GetType().Namespace)
                }
            };
        }
    }
}
