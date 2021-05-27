using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using ColossalFramework;
using ColossalFramework.Packaging;
using ColossalFramework.PlatformServices;
using ColossalFramework.UI;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LoadingScreenModTest
{
    /// <summary>
    /// LoadLevelCoroutine from LoadingManager.
    /// </summary>
    public sealed class LevelLoader : DetourUtility<LevelLoader>
    {
        public string cityName;
        readonly HashSet<string> knownFailedAssets = new HashSet<string>(); // assets that failed or are missing
        readonly Dictionary<string, bool> knownFastLoads = new Dictionary<string, bool>(2); // savegames that can be fastloaded
        readonly HashSet<string>[] skippedPrefabs = new HashSet<string>[Matcher.NUM];
        readonly internal int[] skipCounts = new int[Matcher.NUM];
        internal object loadingLock;
        internal Queue<IEnumerator> mainThreadQueue;
        DateTime fullLoadTime, savedSkipStamp;
        int startMillis;
        bool simulationFailed, fastLoad, optimizeThumbs;
        internal bool assetsStarted, assetsFinished;

        private LevelLoader()
        {
            init(typeof(LoadingManager), "LoadLevel", 5, 0, typeof(Package.Asset));
        }

        internal void SetSkippedPrefabs(HashSet<string>[] prefabs) => prefabs.CopyTo(skippedPrefabs, 0);
        internal static bool AssetsActive() => AssetLoader.HasInstance && instance.assetsStarted && !instance.assetsFinished;
        internal bool HasFailed(string fullName) => knownFailedAssets.Contains(fullName);
        internal bool AddFailed(string fullName) => knownFailedAssets.Add(fullName);

        internal void Reset()
        {
            knownFailedAssets.Clear();
            knownFastLoads.Clear();
            Array.Clear(skipCounts, 0, skipCounts.Length);
        }

        internal override void Dispose()
        {
            base.Dispose();
            Reset();
            Array.Clear(skippedPrefabs, 0, skippedPrefabs.Length);
        }

        public Coroutine LoadLevel(Package.Asset asset, string playerScene, string uiScene, SimulationMetaData ngs, bool forceEnvironmentReload = false)
        {
            L10n.SetCurrent();
            LoadingManager lm = Singleton<LoadingManager>.instance;
            bool activated = ngs.m_updateMode == SimulationManager.UpdateMode.LoadGame || ngs.m_updateMode == SimulationManager.UpdateMode.NewGameFromMap ||
                ngs.m_updateMode == SimulationManager.UpdateMode.NewGameFromScenario || Input.GetKey(KeyCode.LeftControl);
            instance.simulationFailed = instance.assetsStarted = instance.assetsFinished = false;

            if (!lm.m_currentlyLoading && !lm.m_applicationQuitting)
            {
                if (lm.m_LoadingWrapper != null)
                    lm.m_LoadingWrapper.OnLevelUnloading(); // OnLevelUnloading

                if (activated)
                {
                    Settings s = Settings.settings;
                    Util.DebugPrint("Options: 2205", s.loadEnabled, s.loadUsed, s.shareTextures, s.shareMaterials, s.shareMeshes, s.optimizeThumbs,
                        s.reportAssets, s.checkAssets, s.skipPrefabs, s.hideAssets);

                    instance.optimizeThumbs = s.optimizeThumbs;
                    s.enableDisable = s.loadUsed && ShiftE();
                    lm.SetSceneProgress(0f);
                    instance.cityName = asset?.name ?? "NewGame";
                    Profiling.Init();
                    CustomDeserializer.Create();
                    Fixes.Create().Deploy();
                    LoadingScreen.Create().Setup();
                }

                lm.LoadingAnimationComponent.enabled = true;
                lm.m_currentlyLoading = true;
                lm.m_metaDataLoaded = false;
                lm.m_simulationDataLoaded = false;
                lm.m_loadingComplete = false;
                lm.m_renderDataReady = false;
                lm.m_essentialScenesLoaded = false;
                lm.m_brokenAssets = string.Empty;
                Util.Set(lm, "m_sceneProgress", 0f);
                Util.Set(lm, "m_simulationProgress", 0f);

                if (activated)
                    Profiling.Start();

                lm.m_loadingProfilerMain.Reset();
                lm.m_loadingProfilerSimulation.Reset();
                lm.m_loadingProfilerScenes.Reset();
                //LoadingManager.instance.m_loadingProfilerCustomContent.Reset();
                //LoadingManager.instance.m_loadingProfilerCustomAsset.Reset();

                IEnumerator iter = activated ? instance.LoadLevelCoroutine(asset, playerScene, uiScene, ngs, forceEnvironmentReload) :
                    (IEnumerator) Util.Invoke(lm, "LoadLevelCoroutine", asset, playerScene, uiScene, ngs, forceEnvironmentReload);

                return lm.StartCoroutine(iter);
            }

            return null;
        }

        public IEnumerator LoadLevelCoroutine(Package.Asset asset, string playerScene, string uiScene, SimulationMetaData ngs, bool forceEnvironmentReload)
        {
            LoadingManager lm = LoadingManager.instance;
            string scene;
            int i = 0;
            yield return null;

            try
            {
                Util.InvokeVoid(lm, "PreLoadLevel");
            }
            catch (Exception e)
            {
                Util.DebugPrint("PreLoadLevel: exception from some mod.");
                UnityEngine.Debug.LogException(e);
            }

            if (!lm.LoadingAnimationComponent.AnimationLoaded)
            {
                lm.m_loadingProfilerScenes.BeginLoading("LoadingAnimation");
                yield return SceneManager.LoadSceneAsync("LoadingAnimation", LoadSceneMode.Additive);
                lm.m_loadingProfilerScenes.EndLoading();
            }

            DateTime skipStamp = Settings.settings.LoadSkipFile();
            AsyncTask task = Singleton<SimulationManager>.instance.AddAction("Loading", (IEnumerator) Util.Invoke(lm, "LoadSimulationData", asset, ngs));
            LoadSaveStatus.activeTask = task;

            if (lm.m_loadedEnvironment == null) // loading from main menu
                fastLoad = false;
            else // loading from in-game (the pause menu)
            {
                while (!lm.m_metaDataLoaded && !task.completedOrFailed) // IL_139
                    yield return null;

                if (SimulationManager.instance.m_metaData == null)
                {
                    SimulationManager.instance.m_metaData = new SimulationMetaData();
                    SimulationManager.instance.m_metaData.m_environment = "Sunny";
                    SimulationManager.instance.m_metaData.Merge(ngs);
                }

                Util.InvokeVoid(lm, "MetaDataLoaded"); // No OnCreated
                string mapThemeName = SimulationManager.instance.m_metaData.m_MapThemeMetaData?.name;
                fastLoad = SimulationManager.instance.m_metaData.m_environment == lm.m_loadedEnvironment &&
                    mapThemeName == lm.m_loadedMapTheme && !forceEnvironmentReload;

                // The game is nicely optimized when loading from the pause menu. We must specifically address the following situations:
                // - environment (biome) stays the same
                // - map theme stays the same
                // - forceEnvironmentReload is false
                // - 'load used assets' is enabled
                // - not all assets and prefabs used in the save being loaded are currently in memory
                // - prefab skipping has changed.

                if (fastLoad)
                {
                    // Check custom asset availability.
                    if (Settings.settings.loadUsed && !IsKnownFastLoad(asset))
                    {
                        while (!IsSaveDeserialized())
                            yield return null;

                        fastLoad = AllAssetsAvailable();
                    }

                    // Check building prefab availability.
                    if (fastLoad)
                    {
                        if (skipStamp != savedSkipStamp)
                            fastLoad = false;
                        else if (Settings.settings.SkipPrefabs && !IsKnownFastLoad(asset))
                        {
                            while (!IsSaveDeserialized())
                                yield return null;

                            fastLoad = AllPrefabsAvailable();
                        }
                    }

                    if (fastLoad) // optimized load
                    {
                        if (Settings.settings.SkipPrefabs && skippedPrefabs[0] != null)
                        {
                            while (!IsSaveDeserialized())
                                yield return null;

                            PrefabLoader.Create().SetSkippedPrefabs(skippedPrefabs);
                            lm.QueueLoadingAction(PrefabLoader.RemoveSkippedFromSimulation());
                        }

                        lm.QueueLoadingAction((IEnumerator) Util.Invoke(lm, "EssentialScenesLoaded"));
                        lm.QueueLoadingAction((IEnumerator) Util.Invoke(lm, "RenderDataReady"));
                        Util.DebugPrint("fast load at", Profiling.Millis);
                    }
                    else // fallback to full load
                    {
                        DestroyLoadedPrefabs();
                        lm.m_loadedEnvironment = null;
                        lm.m_loadedMapTheme = null;
                        Util.DebugPrint("fallback to full load at", Profiling.Millis);
                    }
                }
                else // full load
                {
                    // Notice that there is a race condition in the base game at this point: DestroyAllPrefabs ruins the simulation
                    // if its deserialization has progressed far enough. Typically there is no problem.
                    Util.InvokeVoid(lm, "DestroyAllPrefabs");
                    lm.m_loadedEnvironment = null;
                    lm.m_loadedMapTheme = null;
                    Util.DebugPrint("full load at", Profiling.Millis);
                }
            }

            // Full load.
            if (lm.m_loadedEnvironment == null) // IL_27C
            {
                AsyncOperation op;
                Reset();
                fullLoadTime = DateTime.Now;
                savedSkipStamp = skipStamp;
                Array.Clear(skippedPrefabs, 0, skippedPrefabs.Length);
                loadingLock = Util.Get(lm, "m_loadingLock");
                mainThreadQueue = (Queue<IEnumerator>) Util.Get(lm, "m_mainThreadQueue");

                if (!string.IsNullOrEmpty(playerScene))
                {
                    lm.m_loadingProfilerScenes.BeginLoading(playerScene);
                    op = SceneManager.LoadSceneAsync(playerScene, LoadSceneMode.Single);

                    while (!op.isDone) // IL_2FF
                    {
                        lm.SetSceneProgress(op.progress * 0.01f);
                        yield return null;
                    }

                    lm.m_loadingProfilerScenes.EndLoading();
                }

                while (!lm.m_metaDataLoaded && !task.completedOrFailed) // IL_33C
                    yield return null;

                if (SimulationManager.instance.m_metaData == null)
                {
                    SimulationManager.instance.m_metaData = new SimulationMetaData();
                    SimulationManager.instance.m_metaData.m_environment = "Sunny";
                    SimulationManager.instance.m_metaData.Merge(ngs);
                }

                try
                {
                    Util.InvokeVoid(lm, "MetaDataLoaded"); // OnCreated if loading from the main manu
                }
                catch (Exception e)
                {
                    Util.DebugPrint("OnCreated: exception from some mod.");
                    UnityEngine.Debug.LogException(e);
                }

                if (Settings.settings.SkipPrefabs)
                    PrefabLoader.Create().Deploy();

                KeyValuePair<string, float>[] levels = SetLevels();
                float currentProgress = 0.10f;

                for (i = 0; i < levels.Length; i++)
                {
                    scene = levels[i].Key;
                    lm.m_loadingProfilerScenes.BeginLoading(scene);
                    op = SceneManager.LoadSceneAsync(scene, LoadSceneMode.Additive);

                    while (!op.isDone)
                    {
                        lm.SetSceneProgress(currentProgress + op.progress * (levels[i].Value - currentProgress));
                        yield return null;
                    }

                    lm.m_loadingProfilerScenes.EndLoading();
                    currentProgress = levels[i].Value;
                }

                PrefabLoader.instance?.Revert();

                if (Settings.settings.SkipPrefabs)
                    lm.QueueLoadingAction(PrefabLoader.RemoveSkippedFromSimulation());

                Util.DebugPrint("mainThreadQueue len", mainThreadQueue.Count, "at", Profiling.Millis);

                // Some major mods (Network Extensions 1 & 2, Single Train Track, Metro Overhaul) have a race condition issue
                // in their NetInfo Installer. Everything goes fine if LoadCustomContent() below is NOT queued before the
                // said Installers have finished. This is just a workaround for the issue. The actual fix should be in
                // the Installers. Notice that the built-in loader of the game is also affected.

                do
                {
                    yield return null;
                    yield return null;

                    lock(loadingLock)
                    {
                        i = mainThreadQueue.Count;
                    }
                }
                while (i > 0);

                Util.DebugPrint("mainThreadQueue len 0 at", Profiling.Millis);

                AssetLoader.Create().Setup();
                lm.QueueLoadingAction(AssetLoader.instance.LoadCustomContent());

                if (Settings.settings.recover)
                    lm.QueueLoadingAction(Safenets.Setup());

                RenderManager.Managers_CheckReferences();
                lm.QueueLoadingAction((IEnumerator) Util.Invoke(lm, "EssentialScenesLoaded"));
                RenderManager.Managers_InitRenderData();
                lm.QueueLoadingAction((IEnumerator) Util.Invoke(lm, "RenderDataReady"));
                simulationFailed = HasFailed(task);

                // Performance optimization: do not load scenes while custom assets are loading.
                while (!assetsFinished)
                    yield return null;

                scene = SimulationManager.instance.m_metaData.m_environment + "Properties";

                if (!string.IsNullOrEmpty(scene))
                {
                    lm.m_loadingProfilerScenes.BeginLoading(scene);
                    op = SceneManager.LoadSceneAsync(scene, LoadSceneMode.Additive);

                    while (!op.isDone) // IL_C47
                    {
                        lm.SetSceneProgress(0.85f + op.progress * 0.05f);

                        if (optimizeThumbs)
                            CustomDeserializer.instance.ReceiveAvailable();

                        yield return null;
                    }

                    lm.m_loadingProfilerScenes.EndLoading();
                }

                if (!simulationFailed)
                    simulationFailed = HasFailed(task);

                if (!string.IsNullOrEmpty(uiScene)) // IL_C67
                {
                    lm.m_loadingProfilerScenes.BeginLoading(uiScene);
                    op = SceneManager.LoadSceneAsync(uiScene, LoadSceneMode.Additive);

                    while (!op.isDone) // IL_CDE
                    {
                        lm.SetSceneProgress(0.90f + op.progress * 0.08f);

                        if (optimizeThumbs)
                            CustomDeserializer.instance.ReceiveAvailable();

                        yield return null;
                    }

                    lm.m_loadingProfilerScenes.EndLoading();
                }

                lm.m_loadedEnvironment = SimulationManager.instance.m_metaData.m_environment; // IL_CFE
                lm.m_loadedMapTheme = SimulationManager.instance.m_metaData.m_MapThemeMetaData?.name;

                if (optimizeThumbs)
                    CustomDeserializer.instance.ReceiveRemaining();
            }
            else
            {
                scene = (string) Util.Invoke(lm, "GetLoadingScene");

                if (!string.IsNullOrEmpty(scene))
                {
                    lm.m_loadingProfilerScenes.BeginLoading(scene);
                    yield return SceneManager.LoadSceneAsync(scene, LoadSceneMode.Additive);
                    lm.m_loadingProfilerScenes.EndLoading();
                }
            }

            lm.SetSceneProgress(1f); // IL_DBF

            while (!task.completedOrFailed)
            {
                if (!simulationFailed && (i++ & 7) == 0)
                    simulationFailed = HasFailed(task);

                yield return null;
            }

            if (!simulationFailed)
                simulationFailed = HasFailed(task);

            lm.m_simulationDataLoaded = lm.m_metaDataLoaded;
            LoadingManager.SimulationDataReadyHandler SimDataReady = Util.Get(lm, "m_simulationDataReady") as LoadingManager.SimulationDataReadyHandler;
            SimDataReady?.Invoke();
            SimulationManager.UpdateMode mode = SimulationManager.UpdateMode.Undefined;

            if (ngs != null)
                mode = ngs.m_updateMode;

            lm.QueueLoadingAction(CheckPolicies());

            if (Settings.settings.Removals)
                lm.QueueLoadingAction(Safenets.Removals());

            lm.QueueLoadingAction((IEnumerator) Util.Invoke(lm, "LoadLevelComplete", mode)); // OnLevelLoaded
            PrefabLoader.instance?.Dispose();
            lm.QueueLoadingAction(LoadingComplete());
            knownFastLoads[asset.checksum] = true;
            Util.DebugPrint("Waiting at", Profiling.Millis);
            AssetLoader.PrintMem();
        }

        // Loading complete.
        IEnumerator LoadingComplete()
        {
            LoadingManager.instance.LoadingAnimationComponent.enabled = false;
            AssetLoader.PrintMem();
            AssetLoader.instance?.Dispose();
            Fixes.instance.Dispose();
            CustomDeserializer.instance.Dispose();
            //AssetDeserializer.SaveTextureAtlases();
            Util.DebugPrint("All completed at", Profiling.Millis);
            //AssetLoader.PrintProfilers();
            Profiling.Stop();
            yield break;
        }

        // Check if Policies Panel has not been initialized, for whatever reason.
        IEnumerator CheckPolicies()
        {
            if (ToolsModifierControl.policiesPanel is PoliciesPanel p)
            {
                if (!(bool) Util.Get(p, "m_Initialized"))
                {
                    Util.DebugPrint("PoliciesPanel not initialized yet. Initializing at", Profiling.Millis);

                    try
                    {
                        Util.InvokeVoid(p, "RefreshPanel");
                    }
                    catch (Exception e)
                    {
                        UnityEngine.Debug.LogException(e);
                    }
                }
            }
            else
                Util.DebugPrint("PoliciesPanel is null. Cannot initialize it at", Profiling.Millis);

            yield break;
        }

        /// <summary>
        /// Creates the list of standard prefab levels to load.
        /// </summary>
        KeyValuePair<string, float>[] SetLevels()
        {
            LoadingManager lm = LoadingManager.instance;
            lm.m_supportsExpansion[0] = Check(369150);
            lm.m_supportsExpansion[1] = Check(420610);
            lm.m_supportsExpansion[2] = Check(515191);
            lm.m_supportsExpansion[3] = Check(547502);
            lm.m_supportsExpansion[4] = Check(614580);
            lm.m_supportsExpansion[5] = Check(715191);
            lm.m_supportsExpansion[6] = Check(715194);
            lm.m_supportsExpansion[7] = Check(944071);
            lm.m_supportsExpansion[8] = Check(1146930);

            bool isWinter = SimulationManager.instance.m_metaData.m_environment == "Winter";

            if (isWinter && !lm.m_supportsExpansion[1])
            {
                SimulationManager.instance.m_metaData.m_environment = "Sunny";
                isWinter = false;
            }

            List<KeyValuePair<string, float>> levels = new List<KeyValuePair<string, float>>(20);
            string scene = (string) Util.Invoke(lm, "GetLoadingScene");

            if (!string.IsNullOrEmpty(scene))
                levels.Add(new KeyValuePair<string, float>(scene, 0.015f));

            levels.Add(new KeyValuePair<string, float>(SimulationManager.instance.m_metaData.m_environment + "Prefabs", 0.12f));

            if ((bool) Util.Invoke(lm, "LoginUsed"))
                levels.Add(new KeyValuePair<string, float>(isWinter ? "WinterLoginPackPrefabs" : "LoginPackPrefabs", 0.121f));

            levels.Add(new KeyValuePair<string, float>(isWinter ? "WinterPreorderPackPrefabs" : "PreorderPackPrefabs", 0.122f));
            levels.Add(new KeyValuePair<string, float>(isWinter ? "WinterSignupPackPrefabs" : "SignupPackPrefabs", 0.123f));

            if (Check(346791))
                levels.Add(new KeyValuePair<string, float>("DeluxePackPrefabs", 0.124f));

            if (PlatformService.IsAppOwned(238370u))
                levels.Add(new KeyValuePair<string, float>("MagickaPackPrefabs", 0.125f));
            #if WP
            else
                levels.Add(new KeyValuePair<string, float>("MagickaPackPrefabs", 0.1251f));
            #endif

            if (lm.m_supportsExpansion[0])
                levels.Add(new KeyValuePair<string, float>(isWinter ? "WinterExpansion1Prefabs" : "Expansion1Prefabs", 0.126f));

            if (lm.m_supportsExpansion[1])
                levels.Add(new KeyValuePair<string, float>("Expansion2Prefabs", 0.127f));

            if (lm.m_supportsExpansion[2])
                levels.Add(new KeyValuePair<string, float>("Expansion3Prefabs", 0.128f));

            if (lm.m_supportsExpansion[3])
                levels.Add(new KeyValuePair<string, float>("Expansion4Prefabs", 0.129f));

            if (lm.m_supportsExpansion[4])
                levels.Add(new KeyValuePair<string, float>(isWinter ? "WinterExpansion5Prefabs" : "Expansion5Prefabs", 0.130f));

            if (lm.m_supportsExpansion[5])
                levels.Add(new KeyValuePair<string, float>(SimulationManager.instance.m_metaData.m_environment + "Expansion6Prefabs", 0.131f));

            if (lm.m_supportsExpansion[6])
                levels.Add(new KeyValuePair<string, float>(isWinter ? "WinterExpansion7Prefabs" : "Expansion7Prefabs", 0.132f));

            if (lm.m_supportsExpansion[7])
                levels.Add(new KeyValuePair<string, float>(isWinter ? "WinterExpansion8Prefabs" : "Expansion8Prefabs", 0.1325f));

            if (lm.m_supportsExpansion[8])
                levels.Add(new KeyValuePair<string, float>(isWinter ? "WinterExpansion9Prefabs" : "Expansion9Prefabs", 0.133f));

            for (int i = 0; i < levelStrings.Length; i++)
                if (Check(levelStrings[i].Value))
                    levels.Add(new KeyValuePair<string, float>(levelStrings[i].Key, 0.134f + i * 0.01f / levelStrings.Length));

            if (Check(715190))
            {
                Package.Asset asset = PackageManager.FindAssetByName("System." + DistrictStyle.kEuropeanSuburbiaStyleName);

                if (asset != null && asset.isEnabled)
                    levels.Add(new KeyValuePair<string, float>("ModderPack3Prefabs", 0.144f));
            }

            if (Check(1059820))
                levels.Add(new KeyValuePair<string, float>("ModderPack4Prefabs", 0.145f));

            if (Check(1148020))
            {
                Package.Asset asset = PackageManager.FindAssetByName("System." + DistrictStyle.kModderPack5StyleName);

                if (asset != null && asset.isEnabled)
                    levels.Add(new KeyValuePair<string, float>("ModderPack5Prefabs", 0.1455f));
            }

            if (Check(1148022))
                levels.Add(new KeyValuePair<string, float>("ModderPack6Prefabs", 0.146f));

            if (Check(1531470))
                levels.Add(new KeyValuePair<string, float>("ModderPack7Prefabs", 0.1462f));

            if (Check(1531471))
                levels.Add(new KeyValuePair<string, float>("ModderPack8Prefabs", 0.1464f));

            if (Check(563850))
                levels.Add(new KeyValuePair<string, float>("ChinaPackPrefabs", 0.1466f));

            Package.Asset europeanStyles = PackageManager.FindAssetByName("System." + DistrictStyle.kEuropeanStyleName);

            if (europeanStyles != null && europeanStyles.isEnabled)
                levels.Add(new KeyValuePair<string, float>(SimulationManager.instance.m_metaData.m_environment.Equals("Europe") ? "EuropeNormalPrefabs" : "EuropeStylePrefabs", 0.15f));

            return levels.ToArray();
        }

        KeyValuePair<string, int>[] levelStrings =
            { new KeyValuePair<string, int>("FootballPrefabs",     456200),
              new KeyValuePair<string, int>("Football2Prefabs",    525940),
              new KeyValuePair<string, int>("Football3Prefabs",    526610),
              new KeyValuePair<string, int>("Football4Prefabs",    526611),
              new KeyValuePair<string, int>("Football5Prefabs",    526612),
              new KeyValuePair<string, int>("Station1Prefabs",     547501),
              new KeyValuePair<string, int>("Station2Prefabs",     614582),
              new KeyValuePair<string, int>("Station3Prefabs",     715193),
              new KeyValuePair<string, int>("Station4Prefabs",     815380),
              new KeyValuePair<string, int>("Station5Prefabs",     944070),
              new KeyValuePair<string, int>("Station6Prefabs",     1065490),
              new KeyValuePair<string, int>("Station7Prefabs",     1065491),
              new KeyValuePair<string, int>("Station8Prefabs",     1148021),
              new KeyValuePair<string, int>("Station9Prefabs",     1196100),
              new KeyValuePair<string, int>("Station10Prefabs",    1531472),
              new KeyValuePair<string, int>("Station11Prefabs",    1531473),
              new KeyValuePair<string, int>("FestivalPrefabs",     614581),
              new KeyValuePair<string, int>("ChristmasPrefabs",    715192),
              new KeyValuePair<string, int>("ModderPack1Prefabs",  515190),
              new KeyValuePair<string, int>("ModderPack2Prefabs",  547500) };

        internal static bool Check(int dlc) => SteamHelper.IsDLCOwned((SteamHelper.DLC) dlc) && (!Settings.settings.SkipPrefabs || !Settings.settings.SkipMatcher.Matches(dlc));

        /// <summary>
        /// The savegame is a fast load if it is pre-known or its time stamp is newer than the full load time stamp.
        /// </summary>
        bool IsKnownFastLoad(Package.Asset asset)
        {
            if (knownFastLoads.TryGetValue(asset.checksum, out bool v))
                return v;

            try
            {
                v = fullLoadTime < asset.package.Find(asset.package.packageMainAsset).Instantiate<SaveGameMetaData>().timeStamp;
                knownFastLoads[asset.checksum] = v;
                return v;
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogException(e);
            }

            return false;
        }

        /// <summary>
        /// Checks (and reports) if the simulation thread has failed.
        /// </summary>
        bool HasFailed(AsyncTask simulationTask)
        {
            if (simulationTask.failed)
            {
                try
                {
                    Exception[] exceptions = ((Queue<Exception>) Util.GetStatic(typeof(UIView), "sLastException")).ToArray();
                    string msg = null;

                    if (exceptions.Length > 0)
                        msg = exceptions[exceptions.Length - 1].Message;

                    SimpleProfilerSource profiler = LoadingScreen.instance.SimulationSource;
                    profiler?.Failed(msg);
                    return true;
                }
                catch (Exception e)
                {
                    UnityEngine.Debug.LogException(e);
                }
            }

            return false;
        }

        /// <summary>
        /// Checks if buildings, props, trees, and vehicles have been deserialized from the savegame.
        /// </summary>
        internal bool IsSaveDeserialized()
        {
            if (startMillis == 0)
                startMillis = Profiling.Millis;

            bool ret = Profiling.Millis - startMillis > 12000 || GetSimProgress() > 54;

            if (ret)
                startMillis = 0;

            return ret;
        }

        /// <summary>
        /// Returns the progress of simulation deserialization.
        /// Note: two threads at play here, old values of m_size might be cached for quite some time.
        /// </summary>
        internal static int GetSimProgress()
        {
            try
            {
                FastList<LoadingProfiler.Event> events = ProfilerSource.GetEvents(LoadingManager.instance.m_loadingProfilerSimulation);
                return Thread.VolatileRead(ref events.m_size);
            }
            catch (Exception) { }

            return -1;
        }

        /// <summary>
        /// Checks if the game has all required custom assets currently in memory.
        /// </summary>
        bool AllAssetsAvailable()
        {
            try
            {
                return UsedAssets.Create().AllAssetsAvailable(knownFailedAssets);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogException(e);
            }

            return true;
        }

        /// <summary>
        /// Checks if the game has all required building prefabs currently in memory.
        /// </summary>
        bool AllPrefabsAvailable()
        {
            try
            {
                PrefabLoader.Create().LookupSimulationPrefabs();
                return PrefabLoader.instance.AllPrefabsAvailable();
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogException(e);
            }

            return true;
        }

        bool ShiftE() => Input.GetKey(KeyCode.LeftShift) && Input.GetKey(KeyCode.E);

        static void DestroyLoadedPrefabs()
        {
            DestroyLoaded<NetInfo>();
            DestroyLoaded<BuildingInfo>();
            DestroyLoaded<PropInfo>();
            DestroyLoaded<TreeInfo>();
            DestroyLoaded<TransportInfo>();
            DestroyLoaded<VehicleInfo>();
            DestroyLoaded<CitizenInfo>();
            DestroyLoaded<EventInfo>();
            DestroyLoaded<DisasterInfo>();
            DestroyLoaded<RadioContentInfo>();
            DestroyLoaded<RadioChannelInfo>();
        }

        /// <summary>
        /// Destroys scene prefabs. Unlike DestroyAll(), simulation prefabs are not affected.
        /// </summary>
        static void DestroyLoaded<P>() where P : PrefabInfo
        {
            try
            {
                int n = PrefabCollection<P>.LoadedCount();
                List<P> prefabs = new List<P>(n);

                for (int i = 0; i < n; i++)
                {
                    P info = PrefabCollection<P>.GetLoaded((uint) i);

                    if (info != null)
                    {
                        info.m_prefabDataIndex = -1; // leave simulation prefabs as they are
                        prefabs.Add(info);
                    }
                }

                PrefabCollection<P>.DestroyPrefabs(string.Empty, prefabs.ToArray(), null);

                // This has not been necessary yet. However, it is quite fatal if prefabs are left behind so better be sure.
                if (n != prefabs.Count)
                {
                    object fastList = Util.GetStatic(typeof(PrefabCollection<P>), "m_scenePrefabs");
                    Util.Set(fastList, "m_size", 0, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                }

                object dict = Util.GetStatic(typeof(PrefabCollection<P>), "m_prefabDict");
                dict.GetType().GetMethod("Clear", BindingFlags.Instance | BindingFlags.Public).Invoke(dict, null);
                prefabs.Clear(); prefabs.Capacity = 0;
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogException(e);
            }
        }
    }
}
