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
using Jellyfin.Plugin.AnimeFiller.Tasks;
using Jellyfin.Plugin.AnimeFiller.Configuration;
using Jellyfin.Plugin.AnimeFiller.Services;
using Jellyfin.Data.Enums;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AnimeFiller.Tests
{
    public class SyncFillerTaskTests
    {
        private class TestableAnimeFillerService : AnimeFillerService
        {
            public Dictionary<string, Dictionary<int, string>> MockData { get; } = new();

            public TestableAnimeFillerService(ILogger<AnimeFillerService> logger) : base(logger)
            {
            }

            public override Task<Dictionary<int, string>?> GetFillerDataAsync(string showName, CancellationToken cancellationToken)
            {
                if (MockData.TryGetValue(showName, out var data))
                {
                    return Task.FromResult<Dictionary<int, string>?>(data);
                }
                return Task.FromResult<Dictionary<int, string>?>(null);
            }
        }

        private readonly Mock<ILibraryManager> _libraryManagerMock;
        private readonly Mock<ILogger<SyncFillerTask>> _loggerMock;
        private readonly Mock<IApplicationPaths> _appPathsMock;
        private readonly Mock<IXmlSerializer> _xmlSerializerMock;
        private readonly Plugin _plugin;
        private PluginConfiguration _config = new();

        public SyncFillerTaskTests()
        {
            _libraryManagerMock = new Mock<ILibraryManager>();
            _loggerMock = new Mock<ILogger<SyncFillerTask>>();
            _appPathsMock = new Mock<IApplicationPaths>();
            _xmlSerializerMock = new Mock<IXmlSerializer>();

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
        public void GetAbsoluteEpisodeNumber_ShouldCalculateCorrectly()
        {
            var task = new SyncFillerTask(_libraryManagerMock.Object, _loggerMock.Object);

            var episodeS1E1 = new Episode { ParentIndexNumber = 1, IndexNumber = 1 };
            var episodeS1E12 = new Episode { ParentIndexNumber = 1, IndexNumber = 12 };
            var episodeS2E1 = new Episode { ParentIndexNumber = 2, IndexNumber = 1 };
            var episodeS2E24 = new Episode { ParentIndexNumber = 2, IndexNumber = 24 };
            var episodeS3E3 = new Episode { ParentIndexNumber = 3, IndexNumber = 3 };

            var allEpisodes = new List<Episode>
            {
                episodeS1E1, episodeS1E12,
                episodeS2E1, episodeS2E24,
                episodeS3E3
            };

            // Season 1: Max index is 12.
            // Season 2: Max index is 24.

            // Season 1 should return its own index
            Assert.Equal(1, task.GetAbsoluteEpisodeNumber(episodeS1E1, allEpisodes));
            Assert.Equal(12, task.GetAbsoluteEpisodeNumber(episodeS1E12, allEpisodes));

            // Season 2: should sum Season 1 max index (12) + index (1) = 13
            Assert.Equal(13, task.GetAbsoluteEpisodeNumber(episodeS2E1, allEpisodes));
            Assert.Equal(36, task.GetAbsoluteEpisodeNumber(episodeS2E24, allEpisodes));

            // Season 3: should sum Season 1 (12) + Season 2 (24) + index (3) = 39
            Assert.Equal(39, task.GetAbsoluteEpisodeNumber(episodeS3E3, allEpisodes));
        }

        [Fact]
        public async Task ExecuteAsync_ShouldSkipNonAnime()
        {
            var task = new SyncFillerTask(_libraryManagerMock.Object, _loggerMock.Object);
            var progressMock = new Mock<IProgress<double>>();

            var nonAnimeSeries = new Series
            {
                Name = "Breaking Bad",
                Genres = new[] { "Drama" }
            };

            var seriesList = new List<BaseItem> { nonAnimeSeries };
            _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes.Contains(BaseItemKind.Series)))).Returns(seriesList);

            var serviceMock = new Mock<ILogger<AnimeFillerService>>();
            var testService = new TestableAnimeFillerService(serviceMock.Object);
            Plugin.FillerService = testService;

            await task.ExecuteAsync(progressMock.Object, CancellationToken.None);

            // Verify we did not call GetFillerDataAsync for non-anime
            Assert.Empty(testService.MockData);
        }

        [Fact]
        public async Task ExecuteAsync_ShouldApplyTagsAndPrefixesToFiller()
        {
            _config.PrefixEpisodeTitles = true;
            _config.EpisodeTitlePrefix = "[Filler] ";
            _plugin.UpdateConfiguration(_config);

            var task = new SyncFillerTask(_libraryManagerMock.Object, _loggerMock.Object);
            var progressMock = new Mock<IProgress<double>>();

            var animeSeries = new Series
            {
                Id = Guid.NewGuid(),
                Name = "Naruto",
                Genres = new[] { "Anime" }
            };

            var episode1 = new Episode
            {
                Id = Guid.NewGuid(),
                SeriesId = animeSeries.Id,
                Name = "Episode 1",
                ParentIndexNumber = 1,
                IndexNumber = 1,
                Tags = Array.Empty<string>()
            };

            var episode2 = new Episode
            {
                Id = Guid.NewGuid(),
                SeriesId = animeSeries.Id,
                Name = "Episode 2",
                ParentIndexNumber = 1,
                IndexNumber = 2,
                Tags = new[] { "Filler" }
            };

            var seriesList = new List<BaseItem> { animeSeries };
            _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes.Contains(BaseItemKind.Series)))).Returns(seriesList);

            var episodeList = new List<BaseItem> { episode1, episode2 };
            _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes.Contains(BaseItemKind.Episode) && q.ParentId == animeSeries.Id))).Returns(episodeList);

            var serviceMock = new Mock<ILogger<AnimeFillerService>>();
            var testService = new TestableAnimeFillerService(serviceMock.Object);
            testService.MockData["Naruto"] = new Dictionary<int, string>
            {
                { 1, "Filler" },
                { 2, "Canon" }
            };
            Plugin.FillerService = testService;

            await task.ExecuteAsync(progressMock.Object, CancellationToken.None);

            // Episode 1 was Canon, now marked as Filler
            Assert.Contains("Filler", episode1.Tags);
            Assert.StartsWith("[Filler] ", episode1.Name);

            // Episode 2 was Filler, now marked as Canon (tags/prefix removed)
            Assert.DoesNotContain("Filler", episode2.Tags);
            Assert.Equal("Episode 2", episode2.Name);
        }
    }
}
