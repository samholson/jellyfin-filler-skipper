using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using Jellyfin.Data.Enums;

namespace Jellyfin.Plugin.AnimeFiller.Tasks
{
    public class SyncFillerTask : IScheduledTask
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger<SyncFillerTask> _logger;

        public SyncFillerTask(ILibraryManager libraryManager, ILogger<SyncFillerTask> logger)
        {
            _libraryManager = libraryManager;
            _logger = logger;
        }

        public string Name => "Sync Anime Filler Status";

        public string Key => "SyncAnimeFillerStatus";

        public string Category => "Anime Filler Skip";

        public string Description => "Scrapes animefillerlist.com for series in your library, tagging filler episodes and prefixing titles if enabled.";

        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            if (Plugin.FillerService == null)
            {
                _logger.LogError("AnimeFillerService is not initialized.");
                progress.Report(100.0);
                return;
            }

            var seriesQuery = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Series },
                Recursive = true
            };

            var seriesList = _libraryManager.GetItemList(seriesQuery);
            if (seriesList == null || seriesList.Count == 0)
            {
                _logger.LogInformation("No series found in the library.");
                progress.Report(100.0);
                return;
            }

            int totalSeries = seriesList.Count;
            int processed = 0;

            foreach (var series in seriesList)
            {
                cancellationToken.ThrowIfCancellationRequested();

                bool isLikelyAnime = series.Genres.Contains("Anime", StringComparer.OrdinalIgnoreCase) ||
                                     series.ProviderIds.ContainsKey("Anilist") ||
                                     series.ProviderIds.ContainsKey("Mal") ||
                                     series.ProviderIds.ContainsKey("AniDB");

                if (!isLikelyAnime)
                {
                    _logger.LogDebug("Skipping '{Name}' as it is not identified as anime.", series.Name);
                    processed++;
                    progress.Report((double)processed / totalSeries * 100.0);
                    continue;
                }

                _logger.LogInformation("Syncing filler status for series: {Name}", series.Name);
                
                var fillerMap = await Plugin.FillerService.GetFillerDataAsync(series.Name, cancellationToken).ConfigureAwait(false);
                if (fillerMap == null)
                {
                    _logger.LogWarning("Could not resolve filler data for anime series: {Name}", series.Name);
                    processed++;
                    progress.Report((double)processed / totalSeries * 100.0);
                    continue;
                }

                // Query all episodes for this series
                var episodeQuery = new InternalItemsQuery
                {
                    ParentId = series.Id,
                    IncludeItemTypes = new[] { BaseItemKind.Episode },
                    Recursive = true
                };

                var episodes = _libraryManager.GetItemList(episodeQuery);
                if (episodes != null)
                {
                    var episodeList = episodes.OfType<Episode>().ToList();
                    foreach (var episode in episodeList)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        int? absoluteNum = GetAbsoluteEpisodeNumber(episode, episodeList);

                        if (!absoluteNum.HasValue)
                        {
                            _logger.LogDebug("Episode '{SeriesName}' S{Season}E{Episode} has no absolute number. Skipping.", 
                                series.Name, episode.ParentIndexNumber, episode.IndexNumber);
                            continue;
                        }

                        var config = Plugin.Instance?.Configuration;
                        string prefix = config?.EpisodeTitlePrefix ?? "[Filler] ";
                        bool hasPrefix = episode.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

                        if (fillerMap.TryGetValue(absoluteNum.Value, out string? type) && 
                            (type == "Filler" || type == "Mixed"))
                        {
                            bool modified = false;

                            // 1. Tags update
                            var tags = new List<string>(episode.Tags);
                            if (!tags.Contains("Filler", StringComparer.OrdinalIgnoreCase))
                            {
                                tags.Add("Filler");
                                episode.Tags = tags.ToArray();
                                modified = true;
                            }

                            // 2. Title prefixing update
                            if (config != null && config.PrefixEpisodeTitles)
                            {
                                if (!hasPrefix)
                                {
                                    episode.Name = prefix + episode.Name;
                                    modified = true;
                                }
                            }
                            else if (hasPrefix)
                            {
                                // Remove prefix if title prefixing is disabled in config
                                episode.Name = episode.Name.Substring(prefix.Length);
                                modified = true;
                            }

                            if (modified)
                            {
                                _logger.LogInformation("Marking '{SeriesName}' Ep {EpNum} ({Title}) as Filler", 
                                    series.Name, absoluteNum.Value, episode.Name);
                                await _libraryManager.UpdateItemAsync(episode, series, ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                            }
                        }
                        else
                        {
                            // Clean up if it was previously marked but is now canon
                            bool modified = false;
                            
                            var tags = new List<string>(episode.Tags);
                            if (tags.Contains("Filler", StringComparer.OrdinalIgnoreCase))
                            {
                                tags.RemoveAll(t => t.Equals("Filler", StringComparison.OrdinalIgnoreCase));
                                episode.Tags = tags.ToArray();
                                modified = true;
                            }

                            if (hasPrefix)
                            {
                                episode.Name = episode.Name.Substring(prefix.Length);
                                modified = true;
                            }

                            if (modified)
                            {
                                _logger.LogInformation("Removing filler status from '{SeriesName}' Ep {EpNum} ({Title})", 
                                    series.Name, absoluteNum.Value, episode.Name);
                                await _libraryManager.UpdateItemAsync(episode, series, ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                            }
                        }
                    }
                }

                processed++;
                progress.Report((double)processed / totalSeries * 100.0);
            }

            _logger.LogInformation("Anime filler status sync completed.");
            progress.Report(100.0);
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return new[]
            {
                new TaskTriggerInfo
                {
                    Type = "WeeklyTrigger",
                    DayOfWeek = DayOfWeek.Sunday,
                    TimeOfDayTicks = TimeSpan.FromHours(2).Ticks
                }
            };
        }

        internal int? GetAbsoluteEpisodeNumber(Episode episode, List<Episode> allEpisodes)
        {
            if (episode.ParentIndexNumber == 1 || !episode.ParentIndexNumber.HasValue)
            {
                return episode.IndexNumber;
            }

            int priorCount = 0;
            for (int season = 1; season < episode.ParentIndexNumber.Value; season++)
            {
                var seasonMax = allEpisodes
                    .Where(e => e.ParentIndexNumber == season && e.IndexNumber.HasValue)
                    .Select(e => e.IndexNumber!.Value)
                    .DefaultIfEmpty(0)
                    .Max();

                priorCount += seasonMax;
            }

            return priorCount + episode.IndexNumber;
        }
    }
}
