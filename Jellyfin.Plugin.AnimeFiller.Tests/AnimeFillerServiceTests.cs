using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Jellyfin.Plugin.AnimeFiller.Services;

namespace Jellyfin.Plugin.AnimeFiller.Tests
{
    public class AnimeFillerServiceTests
    {
        private class TestableAnimeFillerService : AnimeFillerService
        {
            public Func<string, string?>? HtmlMockHandler { get; set; }

            public TestableAnimeFillerService(ILogger<AnimeFillerService> logger) : base(logger)
            {
            }

            protected override Task<string?> GetHtmlContentAsync(string url, CancellationToken cancellationToken)
            {
                if (HtmlMockHandler != null)
                {
                    return Task.FromResult(HtmlMockHandler(url));
                }
                return Task.FromResult<string?>(null);
            }
        }

        private readonly Mock<ILogger<AnimeFillerService>> _loggerMock;

        public AnimeFillerServiceTests()
        {
            _loggerMock = new Mock<ILogger<AnimeFillerService>>();
        }

        [Fact]
        public void NormalizeTitleToSlug_ShouldNormalizeProperly()
        {
            var service = new AnimeFillerService(_loggerMock.Object);

            Assert.Equal("naruto-shippuden", service.NormalizeTitleToSlug("Naruto: Shippuden"));
            Assert.Equal("attack-on-titan-season-3", service.NormalizeTitleToSlug("Attack on Titan Season 3"));
            Assert.Equal("one-piece", service.NormalizeTitleToSlug("One Piece!"));
            Assert.Equal("my-hero-academia", service.NormalizeTitleToSlug("My Hero Academia..."));
            Assert.Equal("rezero", service.NormalizeTitleToSlug("Re:Zero"));
        }

        [Fact]
        public async Task GetFillerDataAsync_Method1_TableParsing_ShouldParseSuccessfully()
        {
            var service = new TestableAnimeFillerService(_loggerMock.Object);
            
            const string mockHtml = @"
                <html>
                <body>
                    <table class='EpisodeList'>
                        <tbody>
                            <tr id='eps-1' class='filler'><td>1</td></tr>
                            <tr id='eps-2' class='mixed'><td>2</td></tr>
                            <tr id='eps-3' class='manga_canon'><td>3</td></tr>
                        </tbody>
                    </table>
                </body>
                </html>";

            service.HtmlMockHandler = (url) =>
            {
                if (url.Contains("/shows/naruto-table"))
                {
                    return mockHtml;
                }
                return null;
            };

            var data = await service.GetFillerDataAsync("naruto-table", CancellationToken.None);

            Assert.NotNull(data);
            Assert.Equal(3, data.Count);
            Assert.Equal("Filler", data[1]);
            Assert.Equal("Mixed", data[2]);
            Assert.Equal("Canon", data[3]);
        }

        [Fact]
        public async Task GetFillerDataAsync_Method2_CondensedParsing_ShouldParseSuccessfully()
        {
            var service = new TestableAnimeFillerService(_loggerMock.Object);
            
            const string mockHtml = @"
                <html>
                <body>
                    <div id='Condensed'>
                        <div class='filler'>
                            <span class='Episodes'>1-3, 5, 8-10</span>
                        </div>
                        <div class='mixed_canon/filler'>
                            <span class='Episodes'>4, 6</span>
                        </div>
                        <div class='manga_canon'>
                            <span class='Episodes'>7</span>
                        </div>
                    </div>
                </body>
                </html>";

            service.HtmlMockHandler = (url) =>
            {
                if (url.Contains("/shows/naruto-condensed"))
                {
                    return mockHtml;
                }
                return null;
            };

            var data = await service.GetFillerDataAsync("naruto-condensed", CancellationToken.None);

            Assert.NotNull(data);
            Assert.Equal("Filler", data[1]);
            Assert.Equal("Filler", data[2]);
            Assert.Equal("Filler", data[3]);
            Assert.Equal("Mixed", data[4]);
            Assert.Equal("Filler", data[5]);
            Assert.Equal("Mixed", data[6]);
            Assert.Equal("Canon", data[7]);
            Assert.Equal("Filler", data[8]);
            Assert.Equal("Filler", data[9]);
            Assert.Equal("Filler", data[10]);
        }

        [Fact]
        public async Task GetFillerDataAsync_FuzzyMatch_ShouldRecoverAndMatch()
        {
            var service = new TestableAnimeFillerService(_loggerMock.Object);

            const string showsListHtml = @"
                <html>
                <body>
                    <div class='shows-list'>
                        <a href='/shows/naruto-shippuden'>Naruto Shippuden</a>
                        <a href='/shows/one-piece'>One Piece</a>
                    </div>
                </body>
                </html>";

            const string episodeHtml = @"
                <html>
                <body>
                    <table class='EpisodeList'>
                        <tbody>
                            <tr id='eps-1' class='filler'><td>1</td></tr>
                        </tbody>
                    </table>
                </body>
                </html>";

            service.HtmlMockHandler = (url) =>
            {
                if (url.EndsWith("/shows/naruto-shippuuden"))
                {
                    // Return 404 for wrong slug
                    return null;
                }
                if (url.EndsWith("/shows"))
                {
                    // Return directory of shows
                    return showsListHtml;
                }
                if (url.EndsWith("/shows/naruto-shippuden"))
                {
                    // Return episode details for correct slug
                    return episodeHtml;
                }
                return null;
            };

            var data = await service.GetFillerDataAsync("Naruto Shippuuden", CancellationToken.None);

            Assert.NotNull(data);
            Assert.True(data.ContainsKey(1));
            Assert.Equal("Filler", data[1]);
        }
    }
}
