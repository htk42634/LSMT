using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ColossalFramework;
using ColossalFramework.Packaging;
using ColossalFramework.PlatformServices;
using ICities;
using UnityEngine;
using static AssetDataWrapper;

namespace LoadingScreenModTest
{
    /// <summary>
    /// LoadCustomContent coroutine from LoadingManager.
    /// </summary>
    public sealed class AssetLoader : Instance<AssetLoader>
    {
        const int OBJ = 1; // Package.AssetType.Object
        const int CAM = 103; // UserAssetType.CustomAssetMetaData;

        HashSet<string> loadedIntersections = new HashSet<string>();
        HashSet<string> hiddenAssets = new HashSet<string>();
        readonly int[] loadQueueIndex = { 3, 1, 0, 4, 4, 3, 3, 1, 0, 2, 2, 2 };
        readonly CustomAssetMetaData.Type[] typeMap = { CustomAssetMetaData.Type.Building, CustomAssetMetaData.Type.Prop, CustomAssetMetaData.Type.Tree,
                                                        CustomAssetMetaData.Type.Vehicle, CustomAssetMetaData.Type.Vehicle, CustomAssetMetaData.Type.Building,
                                                        CustomAssetMetaData.Type.Building, CustomAssetMetaData.Type.Prop, CustomAssetMetaData.Type.Citizen,
                                                        CustomAssetMetaData.Type.Road, CustomAssetMetaData.Type.Road, CustomAssetMetaData.Type.Building };
        Dictionary<Package, CustomAssetMetaData.Type> packageTypes = new Dictionary<Package, CustomAssetMetaData.Type>(256);
        Dictionary<string, SomeMetaData> metaDatas = new Dictionary<string, SomeMetaData>(128);
        Dictionary<string, CustomAssetMetaData> citizenMetaDatas = new Dictionary<string, CustomAssetMetaData>();
        Dictionary<string, List<Package.Asset>>[] suspects;
        Dictionary<string, bool> boolValues;

        internal readonly Stack<Package.Asset> stack = new Stack<Package.Asset>(4); // the asset loading stack
        internal int beginMillis, lastMillis, assetCount;
        float progress;
        readonly bool recordAssets = Settings.settings.RecordAssets, checkAssets = Settings.settings.checkAssets, hasAssetDataExtensions;

        internal const int yieldInterval = 350;
        internal bool IsIntersection(string fullName) => loadedIntersections.Contains(fullName);
        internal Package.Asset Current => stack.Count > 0 ? stack.Peek() : null;

        private AssetLoader()
        {
            Dictionary<string, List<Package.Asset>> b = new Dictionary<string, List<Package.Asset>>(4), p = new Dictionary<string, List<Package.Asset>>(4),
                t = new Dictionary<string, List<Package.Asset>>(4), v = new Dictionary<string, List<Package.Asset>>(4),
                c = new Dictionary<string, List<Package.Asset>>(4), r = new Dictionary<string, List<Package.Asset>>(4);
            suspects = new Dictionary<string, List<Package.Asset>>[] { b, p, t, v, v, b, b, p, c, r, r, b };

            SettingsFile sf = GameSettings.FindSettingsFileByName(PackageManager.assetStateSettingsFile);
            boolValues = (Dictionary<string, bool>) Util.Get(sf, "m_SettingsBoolValues");
            List<IAssetDataExtension> extensions = (List<IAssetDataExtension>) Util.Get(LoadingManager.instance.m_AssetDataWrapper, "m_AssetDataExtensions");
            hasAssetDataExtensions = extensions.Count > 0;

            if (hasAssetDataExtensions)
                Util.DebugPrint("IAssetDataExtensions:", extensions.Count);
        }

        public void Setup()
        {
            CustomDeserializer.instance.Setup();
            Sharing.Create();

            if (recordAssets)
                Reports.Create();

            if (Settings.settings.hideAssets)
                Settings.settings.LoadHiddenAssets(hiddenAssets);
        }

        public void Dispose()
        {
            if (Settings.settings.reportAssets)
                Reports.instance.SaveStats();

            if (recordAssets)
                Reports.instance.Dispose();

            UsedAssets.instance?.Dispose();
            Sharing.instance?.Dispose();
            loadedIntersections.Clear(); hiddenAssets.Clear(); packageTypes.Clear(); metaDatas.Clear(); citizenMetaDatas.Clear();
            loadedIntersections = null; hiddenAssets = null; packageTypes = null; metaDatas = null; citizenMetaDatas = null;
            Array.Clear(suspects, 0, suspects.Length); suspects = null;
            boolValues = null; instance = null;
        }

        void Report()
        {
            Settings s = Settings.settings;

            if (s.loadUsed)
                UsedAssets.instance.ReportMissingAssets();

            if (recordAssets)
            {
                if (s.reportAssets)
                    Reports.instance.Save(hiddenAssets, Sharing.instance.texhit, Sharing.instance.mathit, Sharing.instance.meshit);

                if (s.hideAssets)
                    s.SaveHiddenAssets(hiddenAssets, Reports.instance.GetMissing(), Reports.instance.GetDuplicates());

                if (!s.enableDisable)
                    Reports.instance.ClearAssets();
            }

            Sharing.instance.Dispose();
        }

        public IEnumerator LoadCustomContent()
        {
            LoadingManager.instance.m_loadingProfilerMain.BeginLoading("LoadCustomContent");
            LoadingManager.instance.m_loadingProfilerCustomContent.Reset();
            LoadingManager.instance.m_loadingProfilerCustomAsset.Reset();
            LoadingManager.instance.m_loadingProfilerCustomContent.BeginLoading("District Styles");
            LoadingManager.instance.m_loadingProfilerCustomAsset.PauseLoading();
            LevelLoader.instance.assetsStarted = true;

            int i, j;
            DistrictStyle districtStyle;
            DistrictStyleMetaData districtStyleMetaData;
            List<DistrictStyle> districtStyles = new List<DistrictStyle>();
            HashSet<string> styleBuildings = new HashSet<string>();
            FastList<DistrictStyleMetaData> districtStyleMetaDatas = new FastList<DistrictStyleMetaData>();
            FastList<Package> districtStylePackages = new FastList<Package>();
            Package.Asset europeanStyles = PackageManager.FindAssetByName("System." + DistrictStyle.kEuropeanStyleName);

            if (europeanStyles != null && europeanStyles.isEnabled)
            {
                districtStyle = new DistrictStyle(DistrictStyle.kEuropeanStyleName, true);
                Util.InvokeVoid(LoadingManager.instance, "AddChildrenToBuiltinStyle", GameObject.Find("European Style new"), districtStyle, false);
                Util.InvokeVoid(LoadingManager.instance, "AddChildrenToBuiltinStyle", GameObject.Find("European Style others"), districtStyle, true);

                if (Settings.settings.SkipPrefabs)
                    PrefabLoader.RemoveSkippedFromStyle(districtStyle);

                districtStyles.Add(districtStyle);
            }

            if (LevelLoader.Check(715190))
            {
                Package.Asset asset = PackageManager.FindAssetByName("System." + DistrictStyle.kEuropeanSuburbiaStyleName);

                if (asset != null && asset.isEnabled)
                {
                    districtStyle = new DistrictStyle(DistrictStyle.kEuropeanSuburbiaStyleName, true);
                    Util.InvokeVoid(LoadingManager.instance, "AddChildrenToBuiltinStyle", GameObject.Find("Modder Pack 3"), districtStyle, false);

                    if (Settings.settings.SkipPrefabs)
                        PrefabLoader.RemoveSkippedFromStyle(districtStyle);

                    districtStyles.Add(districtStyle);
                }
            }

            if (LevelLoader.Check(1148020))
            {
                Package.Asset asset = PackageManager.FindAssetByName("System." + DistrictStyle.kModderPack5StyleName);

                if (asset != null && asset.isEnabled)
                {
                    districtStyle = new DistrictStyle(DistrictStyle.kModderPack5StyleName, true);
                    Util.InvokeVoid(LoadingManager.instance, "AddChildrenToBuiltinStyle", GameObject.Find("Modder Pack 5"), districtStyle, false);

                    if (Settings.settings.SkipPrefabs)
                        PrefabLoader.RemoveSkippedFromStyle(districtStyle);

                    districtStyles.Add(districtStyle);
                }
            }

            if (Settings.settings.SkipPrefabs)
                PrefabLoader.UnloadSkipped();

            foreach (Package.Asset asset in PackageManager.FilterAssets(UserAssetType.DistrictStyleMetaData))
            {
                try
                {
                    if (asset != null && asset.isEnabled)
                    {
                        districtStyleMetaData = asset.Instantiate<DistrictStyleMetaData>();

                        if (districtStyleMetaData != null && !districtStyleMetaData.builtin)
                        {
                            districtStyleMetaDatas.Add(districtStyleMetaData);
                            districtStylePackages.Add(asset.package);

                            if (districtStyleMetaData.assets != null)
                                for (i = 0; i < districtStyleMetaData.assets.Length; i++)
                                    styleBuildings.Add(districtStyleMetaData.assets[i]);
                        }
                    }
                }
                catch (Exception ex)
                {
                    CODebugBase<LogChannel>.Warn(LogChannel.Modding, string.Concat(new object[] {ex.GetType(), ": Loading custom district style failed[", asset, "]\n", ex.Message}));
                }
            }

            LoadingManager.instance.m_loadingProfilerCustomAsset.ContinueLoading();
            LoadingManager.instance.m_loadingProfilerCustomContent.EndLoading();

            if (Settings.settings.loadUsed)
                UsedAssets.Create();

            LoadingScreen.instance.DualSource.Add(L10n.Get(L10n.CUSTOM_ASSETS));
            LoadingManager.instance.m_loadingProfilerCustomContent.BeginLoading("Calculating asset load order");
            PrintMem();
            Package.Asset[] queue = GetLoadQueue(styleBuildings);
            Util.DebugPrint("LoadQueue", queue.Length, Profiling.Millis);
            LoadingManager.instance.m_loadingProfilerCustomContent.EndLoading();
            LoadingManager.instance.m_loadingProfilerCustomContent.BeginLoading("Loading Custom Assets");
            Sharing.instance.Start(queue);
            beginMillis = lastMillis = Profiling.Millis;

            for (i = 0; i < queue.Length; i++)
            {
                if ((i & 63) == 0)
                    PrintMem();

                Sharing.instance.WaitForWorkers(i);
                stack.Clear();
                Package.Asset assetRef = queue[i];

                try
                {
                    LoadImpl(assetRef, i);
                }
                catch (Exception e)
                {
                    AssetFailed(assetRef, assetRef.package, e);
                }

                if (Profiling.Millis - lastMillis > yieldInterval)
                {
                    lastMillis = Profiling.Millis;
                    progress = 0.15f + (i + 1) * 0.7f / queue.Length;
                    LoadingScreen.instance.SetProgress(progress, progress, assetCount, assetCount - i - 1 + queue.Length, beginMillis, lastMillis);
                    yield return null;
                }
            }

            lastMillis = Profiling.Millis;
            LoadingScreen.instance.SetProgress(0.85f, 1f, assetCount, assetCount, beginMillis, lastMillis);
            LoadingManager.instance.m_loadingProfilerCustomContent.EndLoading();
            Util.DebugPrint(assetCount, "custom assets loaded in", lastMillis - beginMillis);
            CustomDeserializer.instance.SetCompleted();
            PrintMem();
            queue = null;
            stack.Clear();
            Report();

            LoadingManager.instance.m_loadingProfilerCustomContent.BeginLoading("Finalizing District Styles");
            LoadingManager.instance.m_loadingProfilerCustomAsset.PauseLoading();

            for (i = 0; i < districtStyleMetaDatas.m_size; i++)
            {
                try
                {
                    districtStyleMetaData = districtStyleMetaDatas.m_buffer[i];
                    districtStyle = new DistrictStyle(districtStyleMetaData.name, false);

                    if (districtStylePackages.m_buffer[i].GetPublishedFileID() != PublishedFileId.invalid)
                        districtStyle.PackageName = districtStylePackages.m_buffer[i].packageName;

                    if (districtStyleMetaData.assets != null)
                    {
                        // Not making sense here.
                        for (j = 0; j < districtStyleMetaData.assets.Length; j++)
                        {
                            BuildingInfo bi = CustomDeserializer.FindLoaded<BuildingInfo>(districtStyleMetaData.assets[j] + "_Data"); // no?

                            if (bi != null)
                            {
                                districtStyle.Add(bi);

                                if (districtStyleMetaData.builtin) // this is always false
                                    bi.m_dontSpawnNormally = !districtStyleMetaData.assetRef.isEnabled; // no
                            }
                            else
                                CODebugBase<LogChannel>.Warn(LogChannel.Modding, "Warning: Missing asset (" + districtStyleMetaData.assets[j] + ") in style " + districtStyleMetaData.name); // indexing error in base game
                        }

                        districtStyles.Add(districtStyle);
                    }
                }
                catch (Exception ex)
                {
                    CODebugBase<LogChannel>.Warn(LogChannel.Modding, ex.GetType() + ": Loading district style failed\n" + ex.Message);
                }
            }

            Singleton<DistrictManager>.instance.m_Styles = districtStyles.ToArray();

            if (Singleton<BuildingManager>.exists)
                Singleton<BuildingManager>.instance.InitializeStyleArray(districtStyles.Count);

            if (Settings.settings.enableDisable)
            {
                Util.DebugPrint("Going to enable and disable assets");
                LoadingScreen.instance.DualSource.Add(L10n.Get(L10n.ENABLING_AND_DISABLING));
                yield return null;
                EnableDisableAssets();
            }

            LoadingManager.instance.m_loadingProfilerCustomAsset.ContinueLoading();
            LoadingManager.instance.m_loadingProfilerCustomContent.EndLoading();
            LoadingManager.instance.m_loadingProfilerMain.EndLoading();
            LevelLoader.instance.assetsFinished = true;
        }

        internal static void PrintMem()
        {
            string s = "[LSMT] Mem " + Profiling.Millis.ToString();

            try
            {
                if (Application.platform == RuntimePlatform.WindowsPlayer)
                {
                    MemoryAPI.GetUsage(out int pfMegas, out int wsMegas);
                    s += string.Concat(" ", wsMegas.ToString(), " ", pfMegas.ToString());
                }

                if (Sharing.HasInstance)
                    s += string.Concat(" ", Sharing.instance.Misses.ToString(), " ", Sharing.instance.LoaderAhead.ToString());
            }
            catch (Exception)
            {
            }

            Console.WriteLine(s);
        }

        internal void LoadImpl(Package.Asset assetRef, int i = -1)
        {
            try
            {
                stack.Push(assetRef);
                Console.WriteLine(string.Concat("[LSMT] ", i.ToString(), ": ", Profiling.Millis.ToString(), " ", Sharing.instance.Status, " ", assetRef.fullName));
                string name = assetRef.name;
                LoadingManager.instance.m_loadingProfilerCustomAsset.BeginLoading(ShortName(name));
                GameObject go = AssetDeserializer.Instantiate(assetRef, true, true) as GameObject;

                if (go == null)
                    throw new Exception(string.Concat(assetRef.fullName, ": no GameObject"));

                Package package = assetRef.package;
                bool known = packageTypes.TryGetValue(package, out CustomAssetMetaData.Type packageType);
                bool isRoad = packageType == CustomAssetMetaData.Type.Road;

                if (checkAssets && name != go.name)
                {
                    //Util.DebugPrint("AddNamingConflict", package.packageName, name, go.name);
                    Reports.instance.AddNamingConflict(package);
                }

                string fullName = known & !isRoad || !name.Contains(".") || !IsPillarOrElevation(assetRef, isRoad)
                    ? assetRef.fullName
                    : PillarOrElevationName(package.packageName, name);

                go.name = fullName;
                go.SetActive(false);
                PrefabInfo info = go.GetComponent<PrefabInfo>();
                info.m_isCustomContent = true;

                if (info.m_Atlas != null && !string.IsNullOrEmpty(info.m_InfoTooltipThumbnail))
                    info.m_InfoTooltipAtlas = info.m_Atlas;

                PropInfo pi;
                TreeInfo ti;
                BuildingInfo bi;
                VehicleInfo vi;
                CitizenInfo ci;
                NetInfo ni;

                if ((pi = go.GetComponent<PropInfo>()) != null)
                {
                    if (pi.m_lodObject != null)
                        pi.m_lodObject.SetActive(false);

                    Initialize(pi);
                }
                else if ((ti = go.GetComponent<TreeInfo>()) != null)
                    Initialize(ti);
                else if ((bi = go.GetComponent<BuildingInfo>()) != null)
                {
                    if (bi.m_lodObject != null)
                        bi.m_lodObject.SetActive(false);

                    if (package.version < 7)
                        LegacyMetroUtils.PatchBuildingPaths(bi);

                    Initialize(bi);

                    if (bi.GetAI() is IntersectionAI)
                        loadedIntersections.Add(fullName);
                }
                else if ((vi = go.GetComponent<VehicleInfo>()) != null)
                {
                    if (vi.m_lodObject != null)
                        vi.m_lodObject.SetActive(false);

                    Initialize(vi);
                }
                else if ((ci = go.GetComponent<CitizenInfo>()) != null)
                {
                    if (ci.m_lodObject != null)
                        ci.m_lodObject.SetActive(false);

                    if (citizenMetaDatas.TryGetValue(fullName, out CustomAssetMetaData meta))
                        citizenMetaDatas.Remove(fullName);
                    else
                        meta = GetMetaDataFor(assetRef);

                    if (meta != null && ci.InitializeCustomPrefab(meta))
                    {
                        ci.gameObject.SetActive(true);
                        Initialize(ci);
                    }
                    else
                    {
                        info = null;
                        CODebugBase<LogChannel>.Warn(LogChannel.Modding, "Custom citizen [" + fullName + "] template not available in selected theme. Asset not added in game.");
                    }
                }
                else if ((ni = go.GetComponent<NetInfo>()) != null)
                    Initialize(ni);
                else
                    info = null;

                if (hasAssetDataExtensions && info != null)
                    CallExtensions(assetRef, info);
            }
            finally
            {
                stack.Pop();
                assetCount++;
                LoadingManager.instance.m_loadingProfilerCustomAsset.EndLoading();
            }
        }

        void Initialize<T>(T info) where T : PrefabInfo
        {
            string brokenAssets = LoadingManager.instance.m_brokenAssets;
            PrefabCollection<T>.InitializePrefabs("Custom Assets", info, null);
            LoadingManager.instance.m_brokenAssets = brokenAssets;
            string fullName = info.name;

            if (CustomDeserializer.FindLoaded<T>(fullName, tryName:false) == null)
                throw new Exception(string.Concat(typeof(T).Name, " ", fullName, " failed"));
        }

        // Any nondeterminacy in the asset load order is undesired. It does not matter much for healthy assets but
        // we want duplicates, especially failing ones, in the same order every time.
        int PackageComparison(Package a, Package b)
        {
            int d = string.Compare(a.packageName, b.packageName);

            if (d != 0)
                return d;

            Package.Asset ma = a.Find(a.packageMainAsset), mb = b.Find(b.packageMainAsset);

            if (ma == null | mb == null)
                return 0;

            bool ea = IsEnabled(ma), eb = IsEnabled(mb);
            return ea == eb ? (int) mb.offset - (int) ma.offset : ea ? -1 : 1;
        }

        Package.Asset[] GetLoadQueue(HashSet<string> styleBuildings)
        {
            Package[] packages = { };

            try
            {
                packages = PackageManager.allPackages.Where(p => p.FilterAssets(UserAssetType.CustomAssetMetaData).Any()).ToArray();
                Array.Sort(packages, PackageComparison);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogException(e);
            }

            // Why this load order? By having related and identical assets close to each other, we get more loader cache hits (of meshes and textures)
            // in Sharing. We also get faster disk reads.
            // [0] tree, citizen    [1] propvar, prop    [2] pillar, elevation, road    [3] sub-building, building    [4] trailer, vehicle
            List<Package.Asset>[] queues = { new List<Package.Asset>(32),  new List<Package.Asset>(128), new List<Package.Asset>(32),
                                             new List<Package.Asset>(128), new List<Package.Asset>(64) };
            Util.DebugPrint("Sorted at", Profiling.Millis);
            List<Package.Asset> assetRefs = new List<Package.Asset>(8);
            HashSet<string> loads = new HashSet<string>();
            string prev = string.Empty;
            SteamHelper.DLC_BitMask notMask = ~SteamHelper.GetOwnedDLCMask();
            bool loadEnabled = Settings.settings.loadEnabled & !Settings.settings.enableDisable, loadUsed = Settings.settings.loadUsed;
            //PrintPackages(packages);

            foreach (Package p in packages)
            {
                Package.Asset mainAssetRef = null;

                try
                {
                    CustomDeserializer.instance.AddPackage(p);
                    Package.Asset mainAsset = p.Find(p.packageMainAsset);
                    string pn = p.packageName;
                    bool enabled = loadEnabled && IsEnabled(mainAsset);
                    bool want = enabled || styleBuildings.Contains(mainAsset.fullName);

                    // Fast exit.
                    if (!want && !(loadUsed && UsedAssets.instance.GotPackage(pn)))
                        continue;

                    CustomAssetMetaData meta = GetAssetRefs(mainAsset, assetRefs);
                    int count = assetRefs.Count;
                    mainAssetRef = assetRefs[count - 1];
                    CustomAssetMetaData.Type type = typeMap[(int) meta.type];
                    packageTypes.Add(p, type);
                    bool used = loadUsed && UsedAssets.instance.IsUsed(mainAssetRef, type);
                    want = want && (AssetImporterAssetTemplate.GetAssetDLCMask(meta) & notMask) == 0;

                    if (count > 1 && !used && loadUsed)
                        for (int i = 0; i < count - 1; i++)
                            if (type != CustomAssetMetaData.Type.Road && UsedAssets.instance.IsUsed(assetRefs[i], type) || type == CustomAssetMetaData.Type.Road &&
                                UsedAssets.instance.IsUsed(assetRefs[i], CustomAssetMetaData.Type.Road, CustomAssetMetaData.Type.Building))
                            {
                                used = true;
                                break;
                            }

                    if (want | used)
                    {
                        if (recordAssets)
                            Reports.instance.AddPackage(mainAssetRef, type, want, used);

                        if (pn != prev)
                        {
                            prev = pn;
                            loads.Clear();
                        }

                        List<Package.Asset> q = queues[loadQueueIndex[(int) type]];

                        for (int i = 0; i < count - 1; i++)
                        {
                            Package.Asset assetRef = assetRefs[i];

                            if (loads.Add(assetRef.name) || !IsDuplicate(assetRef, type, queues, false))
                                q.Add(assetRef);
                        }

                        if (loads.Add(mainAssetRef.name) || !IsDuplicate(mainAssetRef, type, queues, true))
                        {
                            q.Add(mainAssetRef);

                            if (hasAssetDataExtensions)
                                metaDatas[mainAssetRef.fullName] = new SomeMetaData(meta.userDataRef, meta.name);

                            if (type == CustomAssetMetaData.Type.Citizen)
                                citizenMetaDatas[mainAssetRef.fullName] = meta;
                        }
                    }
                }
                catch (Exception e)
                {
                    AssetFailed(mainAssetRef, p, e);
                }
            }

            CheckSuspects();
            loads.Clear(); loads = null;
            Package.Asset[] queue = new Package.Asset[queues.Sum(lst => lst.Count)];

            for (int i = 0, k = 0; i < queues.Length; i++)
            {
                queues[i].CopyTo(queue, k);
                k += queues[i].Count;
                queues[i].Clear(); queues[i] = null;
            }

            queues = null;
            return queue;
        }

        static CustomAssetMetaData GetAssetRefs(Package.Asset mainAsset, List<Package.Asset> assetRefs)
        {
            CustomAssetMetaData lastmeta = AssetDeserializer.InstantiateOne(mainAsset) as CustomAssetMetaData;
            Package.Asset mainAssetRef = lastmeta.assetRef;
            Package.Asset obj = null;
            assetRefs.Clear();

            foreach (Package.Asset asset in mainAsset.package)
            {
                int type = asset.type;

                if (type == OBJ)
                {
                    obj = asset;

                    if (ReferenceEquals(asset, mainAssetRef))
                        break;
                }
                else if (type == CAM)
                {
                    if (obj != null)
                    {
                        string name = obj.name;
                        int len = name.Length;

                        if (len >= 35 && name[len - 34] == '-' && name[len - 35] == ' ' && name[len - 33] == ' ') // suspect
                        {
                            GetSecondaryAssetRefs(mainAsset, assetRefs);
                            break;
                        }

                        assetRefs.Add(obj);
                        obj = null;
                    }
                    else
                    {
                        GetSecondaryAssetRefs(mainAsset, assetRefs);
                        break;
                    }
                }
            }

            if (mainAssetRef != null)
                assetRefs.Add(mainAssetRef);

            return lastmeta;
        }

        static void GetSecondaryAssetRefs(Package.Asset mainAsset, List<Package.Asset> assetRefs)
        {
            Util.DebugPrint("!GetSecondaryAssetRefs", mainAsset.fullName);
            assetRefs.Clear();

            foreach (Package.Asset asset in mainAsset.package.FilterAssets(UserAssetType.CustomAssetMetaData))
                if (!ReferenceEquals(asset, mainAsset))
                {
                    CustomAssetMetaData meta = AssetDeserializer.InstantiateOne(asset) as CustomAssetMetaData;
                    Package.Asset assetRef = meta.assetRef;

                    if (assetRef != null)
                        assetRefs.Add(assetRef);
                    else
                        Util.DebugPrint("!NULL asset", mainAsset.fullName);
                }
        }

        CustomAssetMetaData.Type GetMetaTypeFor(Package.Asset assetRef, CustomAssetMetaData.Type packageType)
        {
            if (packageType != CustomAssetMetaData.Type.Road || IsMainAssetRef(assetRef))
                return packageType;

            return typeMap[(int) GetMetaDataFor(assetRef).type];
        }

        CustomAssetMetaData.Type GetMetaTypeFor(Package.Asset assetRef, CustomAssetMetaData.Type packageType, bool isMainAssetRef)
        {
            if (isMainAssetRef | packageType != CustomAssetMetaData.Type.Road)
                return packageType;

            return typeMap[(int) GetMetaDataFor(assetRef).type];
        }

        static CustomAssetMetaData GetMetaDataFor(Package.Asset assetRef)
        {
            bool seeking = true;

            foreach (Package.Asset asset in assetRef.package)
                if (seeking)
                {
                    if (ReferenceEquals(asset, assetRef))
                        seeking = false;
                }
                else if (asset.type.m_Value == CAM)
                {
                    CustomAssetMetaData meta = AssetDeserializer.InstantiateOne(asset) as CustomAssetMetaData;

                    if (ReferenceEquals(meta.assetRef, assetRef))
                        return meta;
                    else
                        break;
                }

            Util.DebugPrint("!assetRef mismatch", assetRef.fullName);

            foreach (Package.Asset asset in assetRef.package.FilterAssets(UserAssetType.CustomAssetMetaData))
            {
                CustomAssetMetaData meta = AssetDeserializer.InstantiateOne(asset) as CustomAssetMetaData;

                if (ReferenceEquals(meta.assetRef, assetRef))
                    return meta;
            }

            Util.DebugPrint("!Cannot get metadata for", assetRef.fullName);
            return null;
        }

        static CustomAssetMetaData GetMainMetaDataFor(Package p)
        {
            Package.Asset mainAsset = p.Find(p.packageMainAsset);
            return mainAsset != null ? AssetDeserializer.InstantiateOne(mainAsset) as CustomAssetMetaData : null;
        }

        internal CustomAssetMetaData.Type GetPackageTypeFor(Package p)
        {
            if (packageTypes.TryGetValue(p, out CustomAssetMetaData.Type packageType))
                return packageType;

            CustomAssetMetaData lastmeta = GetMainMetaDataFor(p);

            if (lastmeta != null)
            {
                packageType = typeMap[(int) lastmeta.type];
                packageTypes.Add(p, packageType);
                return packageType;
            }

            Util.DebugPrint("!Cannot get package type for", p.packagePath);
            return CustomAssetMetaData.Type.Building; // Unknown maps to building.
        }

        bool IsDuplicate(Package.Asset assetRef, CustomAssetMetaData.Type packageType, List<Package.Asset>[] queues, bool isMainAssetRef)
        {
            CustomAssetMetaData.Type type = GetMetaTypeFor(assetRef, packageType, isMainAssetRef);
            Dictionary<string, List<Package.Asset>> dict = suspects[(int) type];
            string fullName = assetRef.fullName;

            if (dict.TryGetValue(fullName, out List<Package.Asset> assets))
                assets.Add(assetRef);
            else
            {
                assets = new List<Package.Asset>(2);
                FindDuplicates(assetRef, type, queues[loadQueueIndex[(int) type]], assets);

                if (type == CustomAssetMetaData.Type.Building)
                    FindDuplicates(assetRef, type, queues[loadQueueIndex[(int) CustomAssetMetaData.Type.Road]], assets);

                if (assets.Count == 0)
                    return false;

                assets.Add(assetRef);
                dict.Add(fullName, assets);
            }

            return true;
        }

        void FindDuplicates(Package.Asset assetRef, CustomAssetMetaData.Type type, List<Package.Asset> q, List<Package.Asset> assets)
        {
            string name = assetRef.name, pn = assetRef.package.packageName;

            for (int i = q.Count - 1; i >= 0; i--)
            {
                Package.Asset a = q[i];
                Package p = a.package;

                if (p.packageName != pn)
                    break;

                if (a.name == name && GetMetaTypeFor(a, packageTypes[p]) == type)
                    assets.Insert(0, a);
            }
        }

        void CheckSuspects()
        {
            CustomAssetMetaData.Type[] types = { CustomAssetMetaData.Type.Building, CustomAssetMetaData.Type.Prop, CustomAssetMetaData.Type.Tree,
                                                 CustomAssetMetaData.Type.Vehicle, CustomAssetMetaData.Type.Citizen, CustomAssetMetaData.Type.Road };

            foreach (CustomAssetMetaData.Type type in types)
                foreach (KeyValuePair<string, List<Package.Asset>> kvp in suspects[(int) type])
                {
                    List<Package.Asset> assets = kvp.Value;

                    if (assets.Select(a => a.checksum).Distinct().Count() > 1 && assets.Where(a => IsEnabled(a.package)).Count() != 1)
                        Duplicate(kvp.Key, assets);
                }
        }

        bool IsEnabled(Package package)
        {
            Package.Asset mainAsset = package.Find(package.packageMainAsset);
            return mainAsset == null || IsEnabled(mainAsset);
        }

        bool IsEnabled(Package.Asset mainAsset) => !boolValues.TryGetValue(mainAsset.checksum + ".enabled", out bool enabled) | enabled;

        void CallExtensions(Package.Asset assetRef, PrefabInfo info)
        {
            string fullName = assetRef.fullName;

            if (metaDatas.TryGetValue(fullName, out SomeMetaData some))
                metaDatas.Remove(fullName);
            else if (IsMainAssetRef(assetRef))
            {
                CustomAssetMetaData meta = GetMainMetaDataFor(assetRef.package);
                some = new SomeMetaData(meta.userDataRef, meta.name);
            }

            if (some.userDataRef != null)
            {
                if (!(AssetDeserializer.InstantiateOne(some.userDataRef) is UserAssetData uad))
                    uad = new UserAssetData();

                LoadingManager.instance.m_AssetDataWrapper.OnAssetLoaded(some.name, info, uad);
            }
        }

        // Specific.
        static bool IsPillarOrElevation(Package.Asset assetRef, bool knownRoad)
        {
            if (knownRoad)
                return !IsMainAssetRef(assetRef);
            else
            {
                int n = 0;

                foreach (Package.Asset asset in assetRef.package.FilterAssets(UserAssetType.CustomAssetMetaData))
                    if (++n > 1)
                        break;

                return n != 1 && GetMetaDataFor(assetRef).type >= CustomAssetMetaData.Type.RoadElevation;
            }
        }

        static string PillarOrElevationName(string packageName, string name) => packageName + "." + PackageHelper.StripName(name);
        internal static Package.Asset FindMainAssetRef(Package p) => p.FilterAssets(Package.AssetType.Object).LastOrDefault(a => a.name.EndsWith("_Data"));
        static bool IsMainAssetRef(Package.Asset assetRef) => ReferenceEquals(FindMainAssetRef(assetRef.package), assetRef);
        internal static string ShortName(string name_Data) => name_Data.Length > 5 && name_Data.EndsWith("_Data") ? name_Data.Substring(0, name_Data.Length - 5) : name_Data;

        static string ShortAssetName(string fullName_Data)
        {
            int j = fullName_Data.IndexOf('.');

            if (j >= 0 && j < fullName_Data.Length - 1)
                fullName_Data = fullName_Data.Substring(j + 1);

            return ShortName(fullName_Data);
        }

        internal void AssetFailed(Package.Asset assetRef, Package p, Exception e)
        {
            string fullName = assetRef?.fullName;

            if (fullName == null)
            {
                assetRef = FindMainAssetRef(p);
                fullName = assetRef?.fullName;
            }

            if (fullName != null && LevelLoader.instance.AddFailed(fullName))
            {
                if (recordAssets)
                    Reports.instance.AssetFailed(assetRef);

                Util.DebugPrint("Asset failed:", fullName);
                DualProfilerSource profiler = LoadingScreen.instance.DualSource;
                profiler?.CustomAssetFailed(ShortAssetName(fullName));
            }

            if (e != null)
                UnityEngine.Debug.LogException(e);
        }

        internal void NotFound(string fullName)
        {
            if (fullName != null && LevelLoader.instance.AddFailed(fullName))
            {
                Util.DebugPrint("Missing:", fullName);

                if (!hiddenAssets.Contains(fullName))
                    LoadingScreen.instance.DualSource?.CustomAssetNotFound(ShortAssetName(fullName));
            }
        }

        void Duplicate(string fullName, List<Package.Asset> assets)
        {
            if (recordAssets)
                Reports.instance.Duplicate(assets);

            Util.DebugPrint("Duplicate name", fullName);

            if (!hiddenAssets.Contains(fullName))
                LoadingScreen.instance.DualSource?.CustomAssetDuplicate(ShortAssetName(fullName));
        }

        void EnableDisableAssets()
        {
            try
            {
                if (!Settings.settings.reportAssets)
                    Reports.instance.SetIndirectUsages();

                foreach (object obj in CustomDeserializer.instance.AllPackages())
                    if (obj is Package p)
                        EnableDisableAssets(p);
                    else
                        foreach (Package p2 in obj as List<Package>)
                            EnableDisableAssets(p2);

                Reports.instance.ClearAssets();
                GameSettings.FindSettingsFileByName(PackageManager.assetStateSettingsFile).MarkDirty();
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogException(e);
            }
        }

        void EnableDisableAssets(Package p)
        {
            bool used = Reports.instance.IsUsed(FindMainAssetRef(p));

            foreach (Package.Asset asset in p.FilterAssets(UserAssetType.CustomAssetMetaData))
            {
                string key = asset.checksum + ".enabled";

                if (used)
                    boolValues.Remove(key);
                else
                    boolValues[key] = false;
            }
        }

        //void PrintPlugins()
        //{
        //    foreach (KeyValuePair<string, PluginInfo> plugin in plugins)
        //    {
        //        Util.DebugPrint("Plugin ", plugin.Value.name);
        //        Util.DebugPrint("  path", plugin.Key);
        //        Util.DebugPrint("  id", plugin.Value.publishedFileID);
        //        Util.DebugPrint("  assemblies", plugin.Value.assemblyCount);
        //        Util.DebugPrint("  asset data extensions", plugin.Value.GetInstances<IAssetDataExtension>().Length);
        //    }
        //}

        //static void PrintPackages(Package[] packages)
        //{
        //    foreach (Package p in packages)
        //    {
        //        Trace.Pr(p.packageName, "\t\t", p.packagePath, "   ", p.version);

        //        foreach (Package.Asset a in p)
        //            Trace.Pr(a.isMainAsset ? " *" : "  ", a.fullName.PadRight(116), a.checksum, a.type.ToString().PadRight(19),
        //                a.offset.ToString().PadLeft(8), a.size.ToString().PadLeft(8));
        //    }
        //}

        //internal static void PrintProfilers()
        //{
        //    LoadingProfiler[] pp = { LoadingManager.instance.m_loadingProfilerMain, LoadingManager.instance.m_loadingProfilerScenes,
        //            LoadingManager.instance.m_loadingProfilerSimulation, LoadingManager.instance.m_loadingProfilerCustomContent };
        //    string[] names = { "Main:", "Scenes:", "Simulation:", "Custom Content:" };
        //    int i = 0;

        //    using (StreamWriter w = new StreamWriter(Util.GetFileName("Profilers", "txt")))
        //        foreach (LoadingProfiler p in pp)
        //        {
        //            w.WriteLine(); w.WriteLine(names[i++]);
        //            FastList<LoadingProfiler.Event> events = ProfilerSource.GetEvents(p);

        //            foreach (LoadingProfiler.Event e in events)
        //                w.WriteLine((e.m_name ?? "").PadRight(36) + e.m_time.ToString().PadLeft(8) + "   " + e.m_type);
        //        }
        //}
    }

    struct SomeMetaData
    {
        internal Package.Asset userDataRef;
        internal string name;

        internal SomeMetaData(Package.Asset u, string n)
        {
            userDataRef = u;
            name = n;
        }
    }
}
