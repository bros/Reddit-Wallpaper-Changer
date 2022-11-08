﻿using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Reddit_Wallpaper_Changer.Properties;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace Reddit_Wallpaper_Changer
{
    public class WallpaperChanger
    {
        private const int MaxRetryAttempts = 50;

        private static readonly IReadOnlyList<string> TopValues = new List<string> { "&t=day", "&t=year", "&t=all", "&t=month", "&t=week" };
        private static readonly IReadOnlyList<string> SortValues = new List<string> { "&sort=relevance", "&sort=hot", "&sort=top", "&sort=comments", "&sort=new" };
        private static readonly IReadOnlyList<string> ImageExtensions = new List<string> { ".JPG", ".JPEG", ".BMP", ".GIF", ".PNG" };

        private readonly MainThreadMarshaller _uiMarshaller;
        private readonly Database _database;
        private readonly Random _random = new Random();
        private readonly List<string> _currentSessionHistory = new List<string>();

        private int _noResultCount;

        public WallpaperChanger(MainThreadMarshaller uiMarshaller, Database database)
        {
            _uiMarshaller = uiMarshaller;
            _database = database;
        }

        //======================================================================
        // Set the wallpaper
        //======================================================================
        public async Task<bool> SetWallpaperAsync(RedditLink redditLink)
        {
            Logger.Instance.LogMessageToFile("Setting wallpaper.", LogLevel.Information);

            if (!WallpaperLinkValid(redditLink))
                return false;

            HelperMethods.ResetManualOverride();

            _uiMarshaller.UpdateStatus("Setting Wallpaper");

            if (!string.IsNullOrEmpty(redditLink.Url))
            {
                redditLink.Url = await ConvertRedditLinkToImageLink(redditLink.Url, _random, ImageExtensions).ConfigureAwait(false);

                var uri = new Uri(redditLink.Url);
                var extension = Path.GetExtension(uri.LocalPath);
                var fileName = $"{redditLink.ThreadId}{extension}";
                var wallpaperFile = Path.Combine(Path.GetTempPath(), fileName);

                redditLink.SaveAsCurrentWallpaper(extension, wallpaperFile);
                redditLink.LogDetails();

                if (ImageExtensions.Contains(extension.ToUpper()))
                {
                    await DownloadWallpaperAsync(uri.AbsoluteUri, wallpaperFile);

                    if (!await SetWallpaperAsync(redditLink, wallpaperFile))
                        return false;
                }
                else
                {
                    Logger.Instance.LogMessageToFile($"Wallpaper URL failed validation: {extension.ToUpper()}", LogLevel.Warning);

                    _uiMarshaller.RestartChangeWallpaperTimer();
                }

                using (var wc = HelperMethods.CreateWebClient())
                {
                    var bytes = await wc.DownloadDataTaskAsync(uri).ConfigureAwait(false);
                    if (!bytes.Any())
                        _uiMarshaller.RestartChangeWallpaperTimer();
                }
            }
            else
                _uiMarshaller.RestartChangeWallpaperTimer();

            return true;
        }

        // TODO refactor, add cancellation
        //======================================================================
        // Search for a wallpaper
        //======================================================================
        public async Task SearchForWallpaperAsync()
        {
            while (true)
            {
                Logger.Instance.LogMessageToFile("Looking for a wallpaper.", LogLevel.Information);

                if (MaxRetriesExceeded())
                    return;

                _uiMarshaller.UpdateStatus("Finding New Wallpaper");

                try
                {
                    var url = GetRedditSearchUrl(_random, _uiMarshaller);
                    var jsonData = await GetJsonDataAsync(url, _uiMarshaller).ConfigureAwait(false);

                    try
                    {
                        if (jsonData.Any())
                        {
                            var redditResult = GetRedditResult(JToken.Parse(jsonData));

                            JToken token = null;

                            try
                            {
                                foreach (var toke in redditResult.Reverse())
                                {
                                    token = toke;
                                }

                                if (token == null)
                                {
                                    if (redditResult.HasValues)
                                    {
                                        var randIndex = _random.Next(0, redditResult.Count() - 1);
                                        token = redditResult.ElementAt(randIndex);
                                    }
                                    else
                                    {
                                        ++_noResultCount;

                                        _uiMarshaller.UpdateStatus("No results found, searching again.");
                                        Logger.Instance.LogMessageToFile("No search results, trying to change wallpaper again.", LogLevel.Information);

                                        continue;
                                    }
                                }

                                if ((WallpaperGrabType)Settings.Default.wallpaperGrabType == WallpaperGrabType.Random)
                                    token = redditResult.ElementAt(_random.Next(0, redditResult.Count() - 1));

                                var tokenData = token["data"];

                                _uiMarshaller.SetCurrentThread($"http://reddit.com{tokenData["permalink"]}");

                                var redditLink = new RedditLink(
                                    tokenData["url"].ToString(),
                                    tokenData["title"].ToString(),
                                    tokenData["id"].ToString());

                                if (!await ChangeWallpaperIfValidImageAsync(redditLink).ConfigureAwait(false))
                                    return;
                            }
                            catch (InvalidOperationException)
                            {
                                _uiMarshaller.LogFailure("Your search query is bringing up no results.",
                                    "No results from the search query.");
                            }
                        }
                        else
                        {
                            _uiMarshaller.LogFailure("Subreddit Probably Doesn't Exist",
                                "Subreddit probably does not exist.");

                            _noResultCount++;

                            return;
                        }
                    }
                    catch (JsonReaderException ex)
                    {
                        _uiMarshaller.LogFailure($"Unexpected error: {ex.Message}",
                            $"Unexpected error: {ex.Message}", LogLevel.Error);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Instance.LogMessageToFile(ex.Message, LogLevel.Warning);
                }

                return;
            }
        }

        private async Task<bool> SetWallpaperAsync(RedditLink redditLink, string wallpaperFile)
        {
            if (!WallpaperSizeValid(wallpaperFile))
                return false;

            await ActiveDesktop.SetWallpaperAsync(wallpaperFile).ConfigureAwait(false);

            _noResultCount = 0;

            _uiMarshaller.UpdateStatus("Wallpaper Changed!");

            Logger.Instance.LogMessageToFile("Wallpaper changed!", LogLevel.Information);

            _currentSessionHistory.Add(redditLink.ThreadId);

            var redditImage = await _database.AddWallpaperToHistoryAsync(redditLink)
                                             .ConfigureAwait(false);

            redditImage.SaveToThumbnailCache();

            _uiMarshaller.AddImageToHistory(redditImage);

            if (!Settings.Default.disableNotifications && Settings.Default.wallpaperInfoPopup)
                _uiMarshaller.OpenPopupInfoWindow(redditLink);

            if (Settings.Default.autoSave)
                HelperMethods.SaveCurrentWallpaper(Settings.Default.currentWallpaperName);

            _uiMarshaller.UpdateStatus("");

            return true;
        }

        private bool WallpaperSizeValid(string wallpaperFile)
        {
            if (Settings.Default.fitWallpaper)
            {
                var screen = ControlHelpers.GetScreenDimensions();

                using (var img = Image.FromFile(wallpaperFile))
                {
                    if (screen.Width != img.Width || screen.Height != img.Height)
                    {
                        _uiMarshaller.LogFailure("Wallpaper resolution mismatch.",
                            $"Wallpaper size mismatch. Screen: {screen.Width}x{screen.Height}, Wallpaper: {img.Width}x{img.Height}");

                        _noResultCount++;
                        return false;
                    }
                }
            }

            return true;
        }

        private bool WallpaperLinkValid(RedditLink redditLink)
        {
            if (_database.IsBlacklisted(redditLink.Url))
            {
                _uiMarshaller.UpdateStatus("Wallpaper is blacklisted.");
                Logger.Instance.LogMessageToFile("The selected wallpaper has been blacklisted, searching again.", LogLevel.Warning);
                _uiMarshaller.DisableChangeWallpaperTimer();

                return false;
            }

            if (!Settings.Default.manualOverride && 
                Settings.Default.suppressDuplicates && 
                _currentSessionHistory.Contains(redditLink.ThreadId))
            {
                _uiMarshaller.UpdateStatus("Wallpaper already used this session.");
                Logger.Instance.LogMessageToFile("The selected wallpaper has already been used this session, searching again.", LogLevel.Warning);
                _uiMarshaller.DisableChangeWallpaperTimer();

                return false;
            }

            return true;
        }

        private bool MaxRetriesExceeded()
        {
            if (_noResultCount < MaxRetryAttempts)
                return false;

            _noResultCount = 0;

            Logger.Instance.LogMessageToFile($"No results after {MaxRetryAttempts} attempts. Disabling Reddit Wallpaper Changer.", LogLevel.Warning);

            _uiMarshaller.ShowNoResultsBalloonTip(MaxRetryAttempts);
            _uiMarshaller.UpdateStatus("RWC Disabled.");
            _uiMarshaller.DisableChangeWallpaperTimer();

            return true;
        }

        private async Task<bool> ChangeWallpaperIfValidImageAsync(RedditLink redditLink)
        {
            Logger.Instance.LogMessageToFile($"Found a wallpaper! Title: {redditLink.Title}, URL: {redditLink.Url}, ThreadID: {redditLink.ThreadId}", LogLevel.Information);

            // Validate URL 
            if (await HelperMethods.ValidateImageAsync(redditLink).ConfigureAwait(false))
            {
                if (await HelperMethods.ValidateImgurImageAsync(redditLink.Url).ConfigureAwait(false))
                {
                    if (!await SetWallpaperAsync(redditLink).ConfigureAwait(false))
                    {
                        _noResultCount++;

                        await SearchForWallpaperAsync().ConfigureAwait(false);
                    }
                }
                else
                {
                    _uiMarshaller.LogFailure("Wallpaper has been removed from Imgur.", 
                        "The selected wallpaper was deleted from Imgur, searching again.");

                    _noResultCount++;

                    await SearchForWallpaperAsync().ConfigureAwait(false);
                }
            }
            else
            {
                _uiMarshaller.LogFailure("The selected URL is not for an image.", 
                    "Not a direct wallpaper URL, searching again.");

                _noResultCount++;
                return false;
            }

            return true;
        }

        private static async Task DownloadWallpaperAsync(string uri, string fileName)
        {
            if (File.Exists(fileName))
            {
                try
                {
                    File.Delete(fileName);
                }
                catch (IOException ex)
                {
                    Logger.Instance.LogMessageToFile($"Unexpected error deleting old wallpaper: {ex.Message}", LogLevel.Warning);
                }
            }

            try
            {
                using (var wc = HelperMethods.CreateWebClient())
                {
                    await wc.DownloadFileTaskAsync(uri, fileName).ConfigureAwait(false);
                }
            }
            catch (WebException ex)
            {
                Logger.Instance.LogMessageToFile($"Unexpected Error: {ex.Message}", LogLevel.Error);
            }
        }

        private static async Task<string> ConvertRedditLinkToImageLink(string url, Random random, IEnumerable<string> imageExtensions)
        {
            var originalUri = new Uri(url);
            var originalExtension = Path.GetExtension(originalUri.LocalPath);
            var extensionNotImageType = !imageExtensions.Contains(originalExtension.ToUpper());

            if (url.Contains("imgur.com/a/"))
                return await GetImgurAlbumUrlAsync(originalUri, random).ConfigureAwait(false);
            else if (extensionNotImageType && url.Contains("deviantart"))
                return await GetDeviantArtUrlAsync(originalUri).ConfigureAwait(false);
            else if (extensionNotImageType && url.Contains("imgur.com"))
                return await GetImgurUrlAsync(originalUri).ConfigureAwait(false);

            return url;
        }

        private static async Task<string> GetDeviantArtUrlAsync(Uri uri)
        {
            var httpWebRequest = (HttpWebRequest)WebRequest.Create($"http://backend.deviantart.com/oembed?url={uri}");
            httpWebRequest.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            httpWebRequest.ContentType = "application/json";
            httpWebRequest.Accept = "*/*";
            httpWebRequest.Method = "GET";

            using (var httpResponse = (HttpWebResponse)await httpWebRequest.GetResponseAsync().ConfigureAwait(false))
            using (var streamReader = new StreamReader(httpResponse.GetResponseStream()))
            {
                var jsonResult = await streamReader.ReadToEndAsync().ConfigureAwait(false);

                return JToken.Parse(jsonResult)["url"].ToString();
            }
        }

        private static async Task<string> GetImgurAlbumUrlAsync(Uri uri, Random random)
        {
            var imgurId = new StringBuilder(uri.ToString()).Replace("https://", "")
                                                           .Replace("http://", "")
                                                           .Replace("imgur.com/a/", "")
                                                           .Replace("//", "")
                                                           .Replace("/", "")
                                                           .ToString();

            var imgurUri = new Uri($"https://api.imgur.com/3/album/{imgurId}");
            var jsonResult = await HelperMethods.GetImgurJsonStringAsync(imgurUri)
                                                .ConfigureAwait(false);

            var imgurResult = JToken.Parse(jsonResult)["data"]["images"];
            var i = imgurResult.Count();
            var selc = 0;

            if (i - 1 != 0)
                selc = random.Next(0, i - 1);

            return imgurResult.ElementAt(selc)["link"].ToString();
        }

        private static async Task<string> GetImgurUrlAsync(Uri uri)
        {
            var imgurId = new StringBuilder(uri.ToString()).Replace("https://", "")
                                                           .Replace("http://", "")
                                                           .Replace("imgur.com/", "")
                                                           .Replace("//", "")
                                                           .Replace("/", "")
                                                           .ToString();

            var baseUri = new Uri($"https://api.imgur.com/3/image/{imgurId}");

            var jsonResult = await HelperMethods.GetImgurJsonStringAsync(baseUri)
                                                .ConfigureAwait(false);

            return JToken.Parse(jsonResult)["data"]["link"].ToString();
        }

        // TODO refactor
        private static string GetRedditSearchUrl(Random random, MainThreadMarshaller uiMarshaller)
        {
            var subreddits = new StringBuilder(Settings.Default.subredditsUsed).Replace(" ", "")
                                                                               .Replace("www.reddit.com/", "")
                                                                               .Replace("reddit.com/", "")
                                                                               .Replace("http://", "")
                                                                               .Replace("/r/", "")
                                                                               .ToString();
            var subs = subreddits.Split('+');
            var sub = subs[random.Next(0, subs.Length)];

            uiMarshaller.UpdateStatus("Searching /r/" + sub + " for a wallpaper...");
            Logger.Instance.LogMessageToFile("Selected sub to search: " + sub, LogLevel.Information);

            var formURL = new StringBuilder("http://www.reddit.com/");

            if (sub.Length == 0)
            {
                formURL.Append("r/all");
            }
            else if (sub.Contains("/m/"))
            {
                subreddits = subreddits
                       .Replace("http://", "")
                       .Replace("https://", "")
                       .Replace("user/", "u/");

                formURL.Append(subreddits);
            }
            else
            {
                formURL.Append($"r/").Append(sub);
            }

            var query = "/search.json?q=" + 
                WebUtility.UrlEncode(Settings.Default.searchQuery) +
                "+self%3Ano+((url%3A.png+OR+url%3A.jpg+OR+url%3A.jpeg)+OR+(url%3Aimgur.png+OR+url%3Aimgur.jpg+OR+url%3Aimgur.jpeg)+OR+(url%3Adeviantart))" +
                "&restrict_sr=on";

            if (Settings.Default.includeNsfw)
                query += "&include_over_18=on";

            switch ((WallpaperGrabType)Settings.Default.wallpaperGrabType)
            {
                case WallpaperGrabType.Random:
                    formURL.Append(query)
                           .Append(SortValues[random.Next(0, SortValues.Count)])
                           .Append(TopValues[random.Next(0, TopValues.Count)]);
                    break;
                case WallpaperGrabType.Newest:
                    formURL.Append(query).Append("&sort=new");
                    break;
                case WallpaperGrabType.HotToday:
                    formURL.Append(query).Append("&sort=hot&t=day");
                    break;
                case WallpaperGrabType.TopLastHour:
                    formURL.Append(query).Append("&sort=top&t=hour");
                    break;
                case WallpaperGrabType.TopToday:
                    formURL.Append(query).Append("&sort=top&t=day");
                    break;
                case WallpaperGrabType.TopWeek:
                    formURL.Append(query).Append("&sort=top&t=week");
                    break;
                case WallpaperGrabType.TopMonth:
                    formURL.Append(query).Append("&sort=top&t=month");
                    break;
                case WallpaperGrabType.TopYear:
                    formURL.Append(query).Append("&sort=top&t=year");
                    break;
                case WallpaperGrabType.TopAllTime:
                    formURL.Append(query).Append("&sort=top&t=all");
                    break;
                case WallpaperGrabType.TrulyRandom:
                    formURL.Append("/random.json?p=").Append(Guid.NewGuid());
                    break;
                default:
                    throw new InvalidEnumArgumentException(nameof(Settings.Default.wallpaperGrabType), Settings.Default.wallpaperGrabType, typeof(WallpaperGrabType));
            }

            var result = formURL.ToString();

            Logger.Instance.LogMessageToFile("Full URL Search String: " + result, LogLevel.Information);

            return result;
        }

        private static async Task<string> GetJsonDataAsync(string url, MainThreadMarshaller uiMarshaller)
        {
            try
            {
                Logger.Instance.LogMessageToFile("Searching Reddit for a wallpaper.", LogLevel.Information);

                using (var wc = HelperMethods.CreateWebClient())
                {
                    return await wc.DownloadStringTaskAsync(url).ConfigureAwait(false);
                }
            }
            catch (WebException ex)
            {
                uiMarshaller.LogFailure(ex.Message, $"Reddit server error: {ex.Message}", 
                    LogLevel.Error);

                throw;
            }
            catch (Exception ex)
            {
                uiMarshaller.LogFailure("Error downloading search results.", 
                    $"Error downloading search results: {ex.Message}", LogLevel.Error);

                throw;
            }
        }

        private static JToken GetRedditResult(JToken jToken)
        {
            if ((WallpaperGrabType)Settings.Default.wallpaperGrabType == WallpaperGrabType.TrulyRandom)
                return JToken.Parse(jToken.First.ToString())["data"]["children"];
            else
                return jToken["data"]["children"];
        }
    }
}
