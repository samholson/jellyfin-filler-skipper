using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model;
using MediaBrowser.Model.MediaSegments;
using Jellyfin.Plugin.AnimeFiller.Providers;
using Jellyfin.Plugin.AnimeFiller.Configuration;
using Jellyfin.Data.Enums;
using Moq;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Jellyfin.Plugin.AnimeFiller.Tests
{
    public class FillerSegmentProviderTests
    {
        private readonly Mock<ILibraryManager> _libraryManagerMock;
        private readonly Mock<IApplicationPaths> _appPathsMock;
        private readonly Mock<IXmlSerializer> _xmlSerializerMock;
        private readonly Plugin _plugin;
        private PluginConfiguration _config = new();

        public FillerSegmentProviderTests()
        {
            _libraryManagerMock = new Mock<ILibraryManager>();
            _appPathsMock = new Mock<IApplicationPaths>();
            _xmlSerializerMock = new Mock<IXmlSerializer>();
            
            // Setting path mock behavior to prevent NullReference in BasePlugin constructor
            _appPathsMock.Setup(a => a.PluginConfigurationsPath).Returns(".");
            _appPathsMock.Setup(a => a.PluginsPath).Returns(".");
            _appPathsMock.Setup(a => a.ConfigurationDirectoryPath).Returns(".");
            _appPathsMock.Setup(a => a.DataPath).Returns(".");
            _appPathsMock.Setup(a => a.ProgramDataPath).Returns(".");
            _appPathsMock.Setup(a => a.CachePath).Returns(".");
            _appPathsMock.Setup(a => a.TempDirectory).Returns(".");
            _appPathsMock.Setup(a => a.LogDirectoryPath).Returns(".");

            _xmlSerializerMock
                .Setup(x => x.DeserializeFromFile(It.IsAny<Type>(), It.IsAny<string>()))
                .Returns(() => _config);

            _plugin = new Plugin(_appPathsMock.Object, _xmlSerializerMock.Object);
        }

        [Fact]
        public async Task Supports_ShouldReturnFalse_WhenCreateSkipButtonIsDisabled()
        {
            _config.CreateSkipButton = false;
            _plugin.UpdateConfiguration(_config);
            var provider = new FillerSegmentProvider(_libraryManagerMock.Object);

            var episode = new Episode
            {
                Tags = new[] { "Filler" },
                RunTimeTicks = 1000
            };

            var supports = await provider.Supports(episode);
            Assert.False(supports);
        }

        [Fact]
        public async Task Supports_ShouldReturnFalse_WhenItemIsNotEpisode()
        {
            _config.CreateSkipButton = true;
            _plugin.UpdateConfiguration(_config);
            var provider = new FillerSegmentProvider(_libraryManagerMock.Object);

            var series = new Series
            {
                Tags = new[] { "Filler" }
            };

            var supports = await provider.Supports(series);
            Assert.False(supports);
        }

        [Fact]
        public async Task Supports_ShouldReturnFalse_WhenEpisodeNotTaggedFiller()
        {
            _config.CreateSkipButton = true;
            _plugin.UpdateConfiguration(_config);
            var provider = new FillerSegmentProvider(_libraryManagerMock.Object);

            var episode = new Episode
            {
                Tags = new[] { "Canon" },
                RunTimeTicks = 1000
            };

            var supports = await provider.Supports(episode);
            Assert.False(supports);
        }

        [Fact]
        public async Task Supports_ShouldReturnFalse_WhenEpisodeHasNoRuntime()
        {
            _config.CreateSkipButton = true;
            _plugin.UpdateConfiguration(_config);
            var provider = new FillerSegmentProvider(_libraryManagerMock.Object);

            var episode = new Episode
            {
                Tags = new[] { "Filler" },
                RunTimeTicks = 0
            };

            var supports = await provider.Supports(episode);
            Assert.False(supports);
        }

        [Fact]
        public async Task Supports_ShouldReturnTrue_WhenEpisodeTaggedFillerWithRuntime()
        {
            _config.CreateSkipButton = true;
            _plugin.UpdateConfiguration(_config);
            var provider = new FillerSegmentProvider(_libraryManagerMock.Object);

            var episode = new Episode
            {
                Tags = new[] { "Filler" },
                RunTimeTicks = 50000
            };

            var supports = await provider.Supports(episode);
            Assert.True(supports);
        }

        [Fact]
        public async Task GetMediaSegments_ShouldReturnEmpty_WhenItemIsNotEpisode()
        {
            var provider = new FillerSegmentProvider(_libraryManagerMock.Object);
            var itemId = Guid.NewGuid();

            var series = new Series { Id = itemId };
            _libraryManagerMock.Setup(l => l.GetItemById(itemId)).Returns(series);

            var request = new MediaSegmentGenerationRequest { ItemId = itemId };
            var segments = await provider.GetMediaSegments(request, CancellationToken.None);

            Assert.Empty(segments);
        }

        [Fact]
        public async Task GetMediaSegments_ShouldReturnEmpty_WhenEpisodeHasNoRuntime()
        {
            var provider = new FillerSegmentProvider(_libraryManagerMock.Object);
            var itemId = Guid.NewGuid();

            var episode = new Episode { Id = itemId, RunTimeTicks = 0 };
            _libraryManagerMock.Setup(l => l.GetItemById(itemId)).Returns(episode);

            var request = new MediaSegmentGenerationRequest { ItemId = itemId };
            var segments = await provider.GetMediaSegments(request, CancellationToken.None);

            Assert.Empty(segments);
        }

        [Fact]
        public async Task GetMediaSegments_ShouldReturnSegment_WhenEpisodeIsValid()
        {
            _config.FillerSegmentType = "Intro";
            _plugin.UpdateConfiguration(_config);
            var provider = new FillerSegmentProvider(_libraryManagerMock.Object);
            var itemId = Guid.NewGuid();

            var episode = new Episode { Id = itemId, RunTimeTicks = 1200000000 }; // 2 minutes
            _libraryManagerMock.Setup(l => l.GetItemById(itemId)).Returns(episode);

            var request = new MediaSegmentGenerationRequest { ItemId = itemId };
            var segments = await provider.GetMediaSegments(request, CancellationToken.None);

            Assert.Single(segments);
            var segment = segments.First();
            Assert.Equal(itemId, segment.ItemId);
            Assert.Equal(0, segment.StartTicks);
            Assert.Equal(1200000000, segment.EndTicks);
            Assert.Equal(MediaSegmentType.Intro, segment.Type);
        }
    }
}
