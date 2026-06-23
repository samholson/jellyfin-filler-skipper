using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using Jellyfin.Plugin.AnimeFiller.Services;

namespace Jellyfin.Plugin.AnimeFiller
{
    public class PluginEntryPoint : IHostedService
    {
        private readonly ISessionManager _sessionManager;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<PluginEntryPoint> _logger;

        public PluginEntryPoint(
            ISessionManager sessionManager,
            ILoggerFactory loggerFactory)
        {
            _sessionManager = sessionManager;
            _loggerFactory = loggerFactory;
            _logger = loggerFactory.CreateLogger<PluginEntryPoint>();

            // Initialize singleton scraper service
            Plugin.FillerService = new AnimeFillerService(loggerFactory.CreateLogger<AnimeFillerService>());
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Initializing Anime Filler Skip playback listener...");
            _sessionManager.PlaybackStart += OnPlaybackStart;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Stopping Anime Filler Skip playback listener...");
            _sessionManager.PlaybackStart -= OnPlaybackStart;
            return Task.CompletedTask;
        }

        private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
        {
            try
            {
                var config = Plugin.Instance?.Configuration;
                if (config == null || !config.AutoSkipFiller)
                {
                    return;
                }

                // Check if the item is an episode
                if (e.Item is not Episode episode)
                {
                    return;
                }

                // Check if episode is tagged as Filler
                bool isFiller = episode.Tags.Contains("Filler", StringComparer.OrdinalIgnoreCase);
                if (!isFiller)
                {
                    return;
                }

                _logger.LogInformation("Auto-skipping filler episode '{EpisodeName}' (Session: {SessionId})", episode.Name, e.Session?.Id);

                if (e.Session != null)
                {
                    _sessionManager.SendPlaystateCommand(
                        string.Empty,
                        e.Session.Id,
                        new PlaystateRequest { Command = PlaystateCommand.NextTrack },
                        CancellationToken.None
                    );
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling auto-skip on PlaybackStart.");
            }
        }
    }
}
