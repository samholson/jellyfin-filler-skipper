using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model;
using MediaBrowser.Model.MediaSegments;
using Jellyfin.Data.Enums;

namespace Jellyfin.Plugin.AnimeFiller.Providers
{
    public class FillerSegmentProvider : IMediaSegmentProvider
    {
        private readonly ILibraryManager _libraryManager;

        public FillerSegmentProvider(ILibraryManager libraryManager)
        {
            _libraryManager = libraryManager;
        }

        public string Name => "Anime Filler Segments";

        public ValueTask<bool> Supports(BaseItem item)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null || !config.CreateSkipButton)
            {
                return ValueTask.FromResult(false);
            }

            // Only support Episodes that are tagged as Filler
            if (item is Episode episode && episode.Tags.Contains("Filler", StringComparer.OrdinalIgnoreCase))
            {
                return ValueTask.FromResult(episode.RunTimeTicks.HasValue && episode.RunTimeTicks.Value > 0);
            }

            return ValueTask.FromResult(false);
        }

        public Task<IReadOnlyList<MediaSegmentDto>> GetMediaSegments(MediaSegmentGenerationRequest request, CancellationToken cancellationToken)
        {
            var item = _libraryManager.GetItemById(request.ItemId);
            if (item is not Episode episode)
            {
                return Task.FromResult<IReadOnlyList<MediaSegmentDto>>(Array.Empty<MediaSegmentDto>());
            }

            long runtimeTicks = episode.RunTimeTicks ?? 0;
            if (runtimeTicks <= 0)
            {
                return Task.FromResult<IReadOnlyList<MediaSegmentDto>>(Array.Empty<MediaSegmentDto>());
            }

            var config = Plugin.Instance?.Configuration;
            string segmentTypeStr = config?.FillerSegmentType ?? "Recap";

            if (!Enum.TryParse<MediaSegmentType>(segmentTypeStr, true, out var segmentType))
            {
                segmentType = MediaSegmentType.Recap; // Default fallback
            }

            var segmentDto = new MediaSegmentDto
            {
                Id = Guid.NewGuid(),
                ItemId = episode.Id,
                Type = segmentType,
                StartTicks = 0,
                EndTicks = runtimeTicks
            };

            IReadOnlyList<MediaSegmentDto> segments = new[] { segmentDto };
            return Task.FromResult(segments);
        }
    }
}
