using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeFiller.Services
{
    public enum EpisodeFillerType
    {
        Canon,
        Mixed,
        Filler
    }

    public class AnimeFillerService
    {
        private readonly ILogger<AnimeFillerService> _logger;
        private readonly string _cacheFilePath;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private Dictionary<string, Dictionary<int, string>> _cache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly HttpClient _httpClient = new HttpClient();
        private DateTime _lastRequestTime = DateTime.MinValue;

        public AnimeFillerService(ILogger<AnimeFillerService> logger)
        {
            _logger = logger;
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Jellyfin.Plugin.AnimeFiller");
            
            var configDir = Plugin.Instance != null ? Path.GetDirectoryName(Plugin.Instance.ConfigurationFilePath) ?? "." : ".";
            _cacheFilePath = Path.Combine(configDir, "anime-filler-cache.json");
            LoadCache();
        }

        private void LoadCache()
        {
            try
            {
                if (File.Exists(_cacheFilePath))
                {
                    var json = File.ReadAllText(_cacheFilePath);
                    var data = JsonSerializer.Deserialize<Dictionary<string, Dictionary<int, string>>>(json);
                    if (data != null)
                    {
                        _cache = new Dictionary<string, Dictionary<int, string>>(data, StringComparer.OrdinalIgnoreCase);
                        _logger.LogInformation("Loaded {Count} shows from anime filler cache.", _cache.Count);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load anime filler cache.");
            }
        }

        private void SaveCache()
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(_cache, options);
                File.WriteAllText(_cacheFilePath, json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save anime filler cache.");
            }
        }

        private async Task ThrottleAsync(CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration;
            int delayMs = config?.ThrottlingDelayMs ?? 2000;
            
            var elapsed = DateTime.UtcNow - _lastRequestTime;
            if (elapsed.TotalMilliseconds < delayMs)
            {
                int waitTime = delayMs - (int)elapsed.TotalMilliseconds;
                _logger.LogDebug("Throttling request, waiting {WaitTime}ms", waitTime);
                await Task.Delay(waitTime, cancellationToken).ConfigureAwait(false);
            }
            _lastRequestTime = DateTime.UtcNow;
        }

        public string NormalizeTitleToSlug(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return string.Empty;

            // Remove subtitles or parts after colon/hyphen for slug matching (sometimes AFL has simpler slugs)
            // e.g. "Naruto: Shippuden" -> "naruto-shippuden" or "Attack on Titan Season 3" -> "attack-titan" (fuzzy matching handles mismatches)
            string normalized = title.ToLowerInvariant();
            
            // Remove special characters except alphanumeric, spaces, and hyphens
            normalized = Regex.Replace(normalized, @"[^a-z0-9\s\-]", "");
            
            // Replace spaces with hyphens
            normalized = Regex.Replace(normalized, @"\s+", "-");
            
            // Remove multiple consecutive hyphens
            normalized = Regex.Replace(normalized, @"-+", "-");
            
            return normalized.Trim('-');
        }

        public virtual async Task<Dictionary<int, string>?> GetFillerDataAsync(string showName, CancellationToken cancellationToken)
        {
            await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                string slug = NormalizeTitleToSlug(showName);
                if (string.IsNullOrEmpty(slug)) return null;

                if (_cache.TryGetValue(slug, out var cachedData))
                {
                    _logger.LogDebug("Returning cached filler data for {ShowName} ({Slug})", showName, slug);
                    return cachedData;
                }

                _logger.LogInformation("Fetching filler data for {ShowName} ({Slug}) from animefillerlist.com", showName, slug);
                var fillerData = await FetchFillerDataFromWebAsync(slug, cancellationToken).ConfigureAwait(false);

                if (fillerData == null)
                {
                    // Attempt fuzzy matching by scraping show directory
                    _logger.LogWarning("Slug '{Slug}' not found. Attempting fuzzy match from directory list...", slug);
                    var fuzzySlug = await AttemptFuzzyMatchSlugAsync(showName, cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(fuzzySlug) && !fuzzySlug.Equals(slug, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation("Fuzzy matched '{ShowName}' to slug '{FuzzySlug}'", showName, fuzzySlug);
                        
                        if (_cache.TryGetValue(fuzzySlug, out cachedData))
                        {
                            // Map the original slug to the fuzzy slug cache to prevent repeating fuzzy match
                            _cache[slug] = cachedData;
                            SaveCache();
                            return cachedData;
                        }

                        fillerData = await FetchFillerDataFromWebAsync(fuzzySlug, cancellationToken).ConfigureAwait(false);
                        if (fillerData != null)
                        {
                            _cache[fuzzySlug] = fillerData;
                            _cache[slug] = fillerData; // cache both mapping keys
                            SaveCache();
                            return fillerData;
                        }
                    }
                }
                else
                {
                    _cache[slug] = fillerData;
                    SaveCache();
                }

                return fillerData;
            }
            finally
            {
                _lock.Release();
            }
        }

        private async Task<Dictionary<int, string>?> FetchFillerDataFromWebAsync(string slug, CancellationToken cancellationToken)
        {
            try
            {
                await ThrottleAsync(cancellationToken).ConfigureAwait(false);
                string url = $"https://www.animefillerlist.com/shows/{slug}";
                
                string? html = await GetHtmlContentAsync(url, cancellationToken).ConfigureAwait(false);
                if (html == null)
                {
                    _logger.LogWarning("Show slug '{Slug}' returned no HTML (possibly 404).", slug);
                    return null;
                }
                
                var doc = new HtmlDocument();
                doc.LoadHtml(html);

                var fillerMap = new Dictionary<int, string>();

                // Method 1: Parse the detailed EpisodeList table (most descriptive)
                var rows = doc.DocumentNode.SelectNodes("//table[contains(@class, 'EpisodeList')]/tbody/tr");
                if (rows != null && rows.Count > 0)
                {
                    _logger.LogDebug("Parsing detailed EpisodeList table for slug: {Slug}", slug);
                    foreach (var row in rows)
                    {
                        var idAttr = row.GetAttributeValue("id", "");
                        if (!string.IsNullOrEmpty(idAttr) && idAttr.StartsWith("eps-"))
                        {
                            if (int.TryParse(idAttr.Substring(4), out int epNum))
                            {
                                var classAttr = row.GetAttributeValue("class", "");
                                string type = ParseTypeFromClass(classAttr);
                                fillerMap[epNum] = type;
                            }
                        }
                    }
                }
                
                // Method 2: Fallback to parse Condensed sections if table was empty or not found
                if (fillerMap.Count == 0)
                {
                    _logger.LogDebug("Detailed table empty. Parsing Condensed sections for slug: {Slug}", slug);
                    var condensedDiv = doc.DocumentNode.SelectSingleNode("//div[@id='Condensed']");
                    if (condensedDiv != null)
                    {
                        ParseCondensedSection(condensedDiv, "filler", "Filler", fillerMap);
                        ParseCondensedSection(condensedDiv, "mixed_canon/filler", "Mixed", fillerMap);
                        ParseCondensedSection(condensedDiv, "manga_canon", "Canon", fillerMap);
                        ParseCondensedSection(condensedDiv, "anime_canon", "Canon", fillerMap);
                    }
                }

                if (fillerMap.Count > 0)
                {
                    _logger.LogInformation("Successfully parsed {Count} episodes for slug '{Slug}'", fillerMap.Count, slug);
                    return fillerMap;
                }

                _logger.LogWarning("No episodes parsed for slug '{Slug}'", slug);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching/parsing filler data for slug '{Slug}'", slug);
                return null;
            }
        }

        private string ParseTypeFromClass(string classAttr)
        {
            string lower = classAttr.ToLowerInvariant();
            if (lower.Contains("mixed")) return "Mixed";
            if (lower.Contains("filler")) return "Filler";
            return "Canon";
        }

        private void ParseCondensedSection(HtmlNode parentNode, string cssClass, string typeValue, Dictionary<int, string> fillerMap)
        {
            var section = parentNode.SelectSingleNode($".//div[contains(@class, '{cssClass}')]");
            if (section == null) return;

            var episodesNode = section.SelectSingleNode(".//span[@class='Episodes']");
            if (episodesNode == null) return;

            string text = WebUtility.HtmlDecode(episodesNode.InnerText);
            // Text is like: "1-19, 24-25, 45, 49-50"
            var parts = text.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                string trimmed = part.Trim();
                if (trimmed.Contains("-"))
                {
                    var range = trimmed.Split('-');
                    if (range.Length == 2 && int.TryParse(range[0], out int start) && int.TryParse(range[1], out int end))
                    {
                        for (int i = start; i <= end; i++)
                        {
                            fillerMap[i] = typeValue;
                        }
                    }
                }
                else if (int.TryParse(trimmed, out int single))
                {
                    fillerMap[single] = typeValue;
                }
            }
        }

        private async Task<string?> AttemptFuzzyMatchSlugAsync(string showName, CancellationToken cancellationToken)
        {
            try
            {
                await ThrottleAsync(cancellationToken).ConfigureAwait(false);
                string url = "https://www.animefillerlist.com/shows";
                
                string? html = await GetHtmlContentAsync(url, cancellationToken).ConfigureAwait(false);
                if (html == null) return null;

                var doc = new HtmlDocument();
                doc.LoadHtml(html);

                var links = doc.DocumentNode.SelectNodes("//a[starts-with(@href, '/shows/')]");
                if (links == null) return null;

                string targetNormalized = NormalizeTitleToSlug(showName);
                string? bestSlug = null;
                int bestDistance = int.MaxValue;

                foreach (var link in links)
                {
                    string href = link.GetAttributeValue("href", "");
                    // href format: "/shows/slug"
                    string slug = href.Substring(7).Trim('/');
                    if (string.IsNullOrEmpty(slug)) continue;

                    string showTitle = WebUtility.HtmlDecode(link.InnerText).Trim();
                    string showSlugNormalized = NormalizeTitleToSlug(showTitle);

                    // Exact match on normalized slug
                    if (slug.Equals(targetNormalized, StringComparison.OrdinalIgnoreCase) || 
                        showSlugNormalized.Equals(targetNormalized, StringComparison.OrdinalIgnoreCase))
                    {
                        return slug;
                    }

                    // Check if one contains another
                    if (showSlugNormalized.Contains(targetNormalized, StringComparison.OrdinalIgnoreCase) ||
                        targetNormalized.Contains(showSlugNormalized, StringComparison.OrdinalIgnoreCase))
                    {
                        // Prioritize substring containment match
                        bestSlug = slug;
                        bestDistance = 0;
                        break;
                    }

                    // Calculate Levenshtein distance
                    int distance = GetLevenshteinDistance(targetNormalized, showSlugNormalized);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        bestSlug = slug;
                    }
                }

                // Only return fuzzy matches if the edit distance is small compared to length
                if (bestDistance < 5 || (bestSlug != null && bestDistance < targetNormalized.Length / 2))
                {
                    return bestSlug;
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error attempting fuzzy slug match for '{ShowName}'", showName);
                return null;
            }
        }

        private static int GetLevenshteinDistance(string s, string t)
        {
            if (string.IsNullOrEmpty(s)) return string.IsNullOrEmpty(t) ? 0 : t.Length;
            if (string.IsNullOrEmpty(t)) return s.Length;

            int n = s.Length;
            int m = t.Length;
            int[,] d = new int[n + 1, m + 1];

            for (int i = 0; i <= n; d[i, 0] = i++) ;
            for (int j = 0; j <= m; d[0, j] = j++) ;

            for (int i = 1; i <= n; i++)
            {
                for (int j = 1; j <= m; j++)
                {
                    int cost = (t[j - 1] == s[i - 1]) ? 0 : 1;
                    d[i, j] = Math.Min(
                        Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                        d[i - 1, j - 1] + cost);
                }
            }
            return d[n, m];
        }

        protected virtual async Task<string?> GetHtmlContentAsync(string url, CancellationToken cancellationToken)
        {
            try
            {
                using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return null;
                }
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting HTML content from {Url}", url);
                return null;
            }
        }
    }
}
