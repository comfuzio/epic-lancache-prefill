﻿namespace EpicPrefill
{
    //TODO document
    //TODO fix this warning
    [SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "Fix this.")]
    public sealed class EpicGamesManager
    {
        private readonly IAnsiConsole _ansiConsole;
        private readonly DownloadArguments _downloadArgs;

        private readonly DownloadHandler _downloadHandler;
        private EpicGamesApi _epicApi;
        private readonly AppInfoHandler _appInfoHandler;
        private ManifestHandler _manifestHandler;
        private readonly UserAccountManager _userAccountManager;
        private HttpClientFactory _httpClientFactory;

        private readonly PrefillSummaryResult _prefillSummaryResult = new PrefillSummaryResult();

        public EpicGamesManager(IAnsiConsole ansiConsole, DownloadArguments downloadArgs)
        {
            _ansiConsole = ansiConsole;
            _downloadArgs = downloadArgs;

            // Setup required classes
            _downloadHandler = new DownloadHandler(_ansiConsole);
            _appInfoHandler = new AppInfoHandler(_ansiConsole);
            _userAccountManager = UserAccountManager.LoadFromFile(_ansiConsole);

            _httpClientFactory = new HttpClientFactory(_ansiConsole, _userAccountManager);
            _epicApi = new EpicGamesApi(_ansiConsole, _httpClientFactory);
            _manifestHandler = new ManifestHandler(_ansiConsole, _httpClientFactory, _downloadArgs);
        }

        //TODO document
        public async Task InitializeAsync()
        {
            await _userAccountManager.LoginAsync();
        }

        public async Task DownloadMultipleAppsAsync(bool downloadAllOwnedGames, List<string> manualIds = null)
        {
            List<GameAsset> allOwnedGames = await GetAllAvailableAppsAsync();

            var appIdsToDownload = LoadPreviouslySelectedApps();
            if (manualIds != null)
            {
                appIdsToDownload.AddRange(manualIds);
            }
            if (downloadAllOwnedGames)
            {
                appIdsToDownload = allOwnedGames.Select(e => e.AppId).ToList();
            }

            // Whitespace divider
            _ansiConsole.WriteLine();

            foreach (var appId in appIdsToDownload)
            {
                var app = allOwnedGames.First(e => e.AppId == appId);
                try
                {
                    await DownloadSingleAppAsync(app);
                }
                catch (Exception e) when (e is LancacheNotFoundException)
                {
                    // We'll want to bomb out the entire process for these exceptions, as they mean we can't prefill any apps at all
                    throw;
                }
                catch (Exception e)
                {
                    // Need to catch any exceptions that might happen during a single download, so that the other apps won't be affected
                    _ansiConsole.LogMarkupLine(Red($"Unexpected download error : {e.Message}  Skipping app..."));
                    _ansiConsole.MarkupLine("");
                    _prefillSummaryResult.FailedApps++;
                }
            }

            _ansiConsole.LogMarkupLine("Prefill complete!");
            _prefillSummaryResult.RenderSummaryTable(_ansiConsole);
        }

        private async Task DownloadSingleAppAsync(GameAsset app)
        {
            // Only download the app if it isn't up to date
            if (_downloadArgs.Force == false && _appInfoHandler.AppIsUpToDate(app))
            {
                _prefillSummaryResult.AlreadyUpToDate++;
                return;
            }

            _ansiConsole.LogMarkupLine($"Starting {Cyan(app.Title)}");

            // Download the latest manifest, and build the list of requests in order to download the app
            List<ManifestUrl> allManifestUrls = await _epicApi.GetAllDownloadUrlsAsync(app);
            ManifestUrl manifestDownloadUrl = _epicApi.GetManifestDownloadUrl(allManifestUrls);
            var rawManifestBytes = await _manifestHandler.DownloadManifestAsync(app, manifestDownloadUrl);
            var chunkDownloadQueue = _manifestHandler.ParseManifest(rawManifestBytes, manifestDownloadUrl);

            // Logging some metadata about the downloads
            var downloadTimer = Stopwatch.StartNew();
            var totalBytes = ByteSize.FromBytes(chunkDownloadQueue.Sum(e => (long)e.DownloadSizeBytes));
            _prefillSummaryResult.TotalBytesTransferred += totalBytes;

            var verboseChunkCount = AppConfig.VerboseLogs ? $"from {LightYellow(chunkDownloadQueue.Count)} chunks" : "";
            _ansiConsole.LogMarkupLine($"Downloading {Magenta(totalBytes.ToDecimalString())} {verboseChunkCount}");

            // Finally run the queued downloads
            var downloadSuccessful = await _downloadHandler.DownloadQueuedChunksAsync(chunkDownloadQueue, allManifestUrls);
            if (downloadSuccessful)
            {
                // Logging some metrics about the download
                _ansiConsole.LogMarkupLine($"Finished in {LightYellow(downloadTimer.FormatElapsedString())} - {Magenta(totalBytes.CalculateBitrate(downloadTimer))}");
                _ansiConsole.WriteLine();

                _appInfoHandler.MarkDownloadAsSuccessful(app);
                _prefillSummaryResult.Updated++;
            }
            else
            {
                _prefillSummaryResult.FailedApps++;
            }
        }

        //TODO should this just be merged with GetOwnedAppsAsync?
        public async Task<List<GameAsset>> GetAllAvailableAppsAsync()
        {
            return await _epicApi.GetOwnedAppsAsync();
        }

        #region Select Apps

        public void SetAppsAsSelected(List<TuiAppInfo> userSelected)
        {
            List<string> selectedAppIds = userSelected.Where(e => e.IsSelected)
                                                      .Select(e => e.AppId)
                                                      .ToList();
            File.WriteAllText(AppConfig.UserSelectedAppsPath, JsonSerializer.Serialize(selectedAppIds, SerializationContext.Default.ListString));

            _ansiConsole.LogMarkupLine($"Selected {Magenta(selectedAppIds.Count)} apps to prefill!  ");
        }

        public List<string> LoadPreviouslySelectedApps()
        {
            if (File.Exists(AppConfig.UserSelectedAppsPath))
            {
                return JsonSerializer.Deserialize(File.ReadAllText(AppConfig.UserSelectedAppsPath), SerializationContext.Default.ListString);
            }
            return new List<string>();
        }

        #endregion
    }
}