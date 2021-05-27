using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ColossalFramework.Packaging;
using UnityEngine;
using static CustomAssetMetaData.Type;
using static LoadingScreenModTest.Reports;

namespace LoadingScreenModTest
{
    internal sealed class Reports : Instance<Reports>
    {
        internal const int ENABLED = 1;
        internal const int USEDDIR = 2;
        internal const int USEDIND = 4;
        internal const int FAILED = 8;
        internal const int AVAILABLE = 16;
        internal const int MISSING = 32;
        internal const int NAME_CHANGED = 64;
        internal const int USED = USEDDIR | USEDIND;

        Dictionary<string, Item> assets = new Dictionary<string, Item>(256);
        List<List<Package.Asset>> duplicates = new List<List<Package.Asset>>(4);
        List<AssetError<int>> weirdTextures, largeTextures, largeMeshes, extremeMeshes;
        HashSet<Package> namingConflicts;

        readonly string[] allHeadings =
        {
            L10n.Get(L10n.BUILDINGS_AND_PARKS),
            L10n.Get(L10n.PROPS),
            L10n.Get(L10n.TREES),
            L10n.Get(L10n.VEHICLES),
            L10n.Get(L10n.CITIZENS),
            L10n.Get(L10n.NETS),
            L10n.Get(L10n.NETS_IN_BUILDINGS),
            L10n.Get(L10n.PROPS_IN_BUILDINGS),
            L10n.Get(L10n.TREES_IN_BUILDINGS)
        };
        readonly CustomAssetMetaData.Type[] allTypes = { CustomAssetMetaData.Type.Building, Prop, CustomAssetMetaData.Type.Tree, CustomAssetMetaData.Type.Vehicle,
            CustomAssetMetaData.Type.Citizen, Road, Road, Prop, CustomAssetMetaData.Type.Tree };
        int texturesShared, materialsShared, meshesShared;
        readonly DateTime jsEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        string reportFilePath;
        StreamWriter w;
        static readonly char[] forbidden = { ':', '*', '?', '<', '>', '|', '#', '%', '&', '{', '}', '$', '!', '@', '+', '`', '=', '\\', '/', '"', '\'' };
        static readonly string[] jsEsc = { @"\", "\"" };

        const string spaces = "&nbsp;&nbsp;";
        const string steamid = @"<a target=""_blank"" href=""https://steamcommunity.com/sharedfiles/filedetails/?id=";
        const string privateAssetLink = @"<a target=""_blank"" href=""https://steamcommunity.com/workshop/filedetails/discussion/667342976/357284767251931800/"">";
        const string weirdTextureLink = @"<a target=""_blank"" href=""https://steamcommunity.com/workshop/filedetails/discussion/667342976/1639789306562159404/"">";
        const string largeTextureLink = @"<a target=""_blank"" href=""https://steamcommunity.com/workshop/filedetails/discussion/667342976/1639789306562181099/"">";
        const string namingConflictLink = @"<a target=""_blank"" href=""https://steamcommunity.com/workshop/filedetails/discussion/667342976/1639789306562171733/"">";
        const string largeMeshLink = @"<a target=""_blank"" href=""https://steamcommunity.com/workshop/filedetails/discussion/667342976/1639789306562193628/"">";

        private Reports()
        {
            if (Settings.settings.checkAssets)
            {
                weirdTextures = new List<AssetError<int>>();
                largeTextures = new List<AssetError<int>>();
                largeMeshes = new List<AssetError<int>>();
                extremeMeshes = new List<AssetError<int>>();
                namingConflicts = new HashSet<Package>();
            }
        }

        internal void Dispose() => instance = null;

        internal void ClearAssets()
        {
            assets.Clear(); duplicates.Clear();
            assets = null; duplicates = null;
            weirdTextures = null; largeTextures = null; largeMeshes = null; extremeMeshes = null; namingConflicts = null;
        }

        internal void AssetFailed(Package.Asset assetRef)
        {
            Item item = FindItem(assetRef);

            if (item != null)
                item.Failed = true;
        }

        internal void Duplicate(List<Package.Asset> list) => duplicates.Add(list);

        internal void AddPackage(Package.Asset mainAssetRef, CustomAssetMetaData.Type type, bool enabled, bool useddir)
        {
            string fullName = mainAssetRef.fullName;

            if (!string.IsNullOrEmpty(fullName))
                assets[fullName] = new Available(mainAssetRef, type, enabled, useddir);
        }

        internal void AddPackage(Package p)
        {
            Package.Asset mainAssetRef = AssetLoader.FindMainAssetRef(p);
            string fullName = mainAssetRef?.fullName;

            if (!string.IsNullOrEmpty(fullName) && !IsKnown(fullName))
                assets.Add(fullName, new Available(mainAssetRef, Unknown, false, false));
        }

        internal bool IsKnown(Package.Asset assetRef) => assets.ContainsKey(assetRef.fullName);
        internal bool IsKnown(string fullName) => assets.ContainsKey(fullName);

        internal void AddReference(Package.Asset knownRef, string fullName, CustomAssetMetaData.Type type)
        {
            if (!assets.TryGetValue(fullName, out Item child))
                assets.Add(fullName, child = new Missing(fullName, type));
            else
                child.type = type;

            assets[knownRef.fullName].Add(child);
        }

        internal void AddMissing(string fullName, CustomAssetMetaData.Type type)
        {
            if (!assets.TryGetValue(fullName, out Item child))
                assets.Add(fullName, new Missing(fullName, type, useddir: true));
            else
                child.UsedDir = true;
        }

        internal void AddWeirdTexture(Package p, string c, int v) => weirdTextures.Add(new AssetError<int>(p, c, v));
        internal void AddLargeTexture(Package p, string c, int v) => largeTextures.Add(new AssetError<int>(p, c, v));
        internal void AddLargeMesh(Package p, string c, int v) => largeMeshes.Add(new AssetError<int>(p, c, v));
        internal void AddExtremeMesh(Package p, string c, int v) => extremeMeshes.Add(new AssetError<int>(p, c, v));
        internal void AddNamingConflict(Package p) => namingConflicts.Add(p);

        internal bool IsUsed(Package.Asset mainAssetRef)
        {
            string fullName = mainAssetRef?.fullName;
            return !string.IsNullOrEmpty(fullName) && assets.TryGetValue(fullName, out Item item) && item.Used;
        }

        internal string[] GetMissing() => assets.Values.Which(MISSING).Select(item => item.FullName).ToArray();
        internal string[] GetDuplicates() => duplicates.Select(list => list[0].fullName).ToArray();

        internal void SetIndirectUsages()
        {
            foreach (Item item in assets.Values)
                if (item.UsedDir)
                    SetIndirectUsages(item);
        }

        static void SetIndirectUsages(Item item)
        {
            if (item.Uses != null)
                foreach (Item child in item.Uses)
                    if (!child.UsedInd)
                    {
                        child.UsedInd = true;
                        SetIndirectUsages(child);
                    }
        }

        static void SetNameChanges(Item[] missingItems)
        {
            foreach (Item missing in missingItems)
                if (missing.HasPackageName && CustomDeserializer.instance.HasPackages(missing.packageName))
                    missing.NameChanged = true;
        }

        Dictionary<Item, List<Item>> GetUsedBy()
        {
            Dictionary<Item, List<Item>> usedBy = new Dictionary<Item, List<Item>>(assets.Count / 4);

            try
            {
                foreach (Item item in assets.Values)
                    if (item.Uses != null)
                        foreach (Item child in item.Uses)
                            if (usedBy.TryGetValue(child, out List<Item> list))
                                list.Add(item);
                            else
                                usedBy.Add(child, new List<Item>(2) { item });

                Comparison<Item> f = (a, b) => string.Compare(a.name, b.name);

                foreach (List<Item> list in usedBy.Values)
                    list.Sort(f);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogException(e);
            }

            return usedBy;
        }

        internal void Save(HashSet<string> hidden, int textures, int materials, int meshes)
        {
            texturesShared = textures; materialsShared = materials; meshesShared = meshes;
            string cityName = AssetLoader.ShortName(LevelLoader.instance.cityName);
            int t0 = Profiling.Millis;

            try
            {
                foreach (char c in forbidden)
                    cityName = cityName.Replace(c, 'x');

                reportFilePath = Util.GetFileName(cityName + " - " + L10n.Get(L10n.ASSETS_REPORT), "htm", Settings.settings.useReportDate);
                Util.DebugPrint("Saving assets report to", reportFilePath);
                w = new StreamWriter(reportFilePath);
                w.WriteLine(@"<!DOCTYPE html><html lang=""" + L10n.Code + @"""><head><meta charset=""UTF-8""><title>" + L10n.Get(L10n.ASSETS_REPORT) + @"</title><style>");
                w.WriteLine(@"* {font-family:sans-serif;}");
                w.WriteLine(@"body {background-color:#f9f6ea;}");
                w.WriteLine(@"section {padding-right:24px;}");
                w.WriteLine(@"div {margin:5px 1px 0px 18px;}");
                w.WriteLine(@".my {display:flex;}");
                w.WriteLine(@".my .mi {margin:10px 0px;min-width:30%;}");
                w.WriteLine(@".my .bx {line-height:133%;padding:8px 12px;background-color:#e8e5d4;border-radius:5px;margin:1px;min-width:58%;}");
                w.WriteLine(@".my .st {font-style:italic;margin:0px;min-width:29%;}");
                w.WriteLine(@"h1 {margin-top:10px;padding:24px 18px;background-color:#e8e5d4;}");
                w.WriteLine(@"h2 {margin-top:0px;border-bottom:1px solid black;}");
                w.WriteLine(@"h3 {margin-top:25px;margin-left:18px;}");
                w.WriteLine(@"a:link {color:#0000e0;text-decoration:inherit;}");
                w.WriteLine(@"a:visited {color:#8000a0;text-decoration:inherit;}");
                w.WriteLine(@"a:hover {text-decoration:underline;}");
                w.WriteLine(@"</style></head><body>");

                H1(Enc(cityName));
                Italics(L10n.Get(L10n.ASSETS_REPORT_FOR_CS));
                Italics(L10n.Get(L10n.TO_STOP_SAVING));
                Br(); Br();
                string[] mainHeadings = allHeadings.Take(6).ToArray();
                CustomAssetMetaData.Type[] mainTypes = allTypes.Take(6).ToArray();

                Sec("#f04040");
                H2(L10n.Get(L10n.ASSETS_THAT_FAILED));
                Item[] failed = assets.Values.Which(FAILED).ToArray();

                if (failed.Length > 0)
                {
                    Report(failed, mainHeadings, mainTypes);
                    Array.Clear(failed, 0, failed.Length); failed = null;
                }
                else
                    Italics(L10n.Get(L10n.NO_FAILED_ASSETS));

                if (Settings.settings.checkAssets)
                {
                    Br(); Br();
                    H2(L10n.Get(L10n.ASSET_ERRORS));

                    if (!ReportErrors())
                        Italics(L10n.Get(L10n.NO_ERRORS_FOUND));

                    Br(); Br();
                    H2(L10n.Get(L10n.ASSET_WARNINGS));

                    if (!ReportWarnings())
                        Italics(L10n.Get(L10n.NO_WARNINGS));

                    weirdTextures.Clear();
                    largeTextures.Clear();
                    largeMeshes.Clear();
                    extremeMeshes.Clear();
                    namingConflicts.Clear();
                }

                SecOff();
                Sec("#f0a840");
                H2(L10n.Get(L10n.ASSETS_THAT_ARE_MISSING));

                if (Settings.settings.loadUsed)
                {
                    SetIndirectUsages();
                    Item[] missing = assets.Values.Which(MISSING).Where(item => !hidden.Contains(item.FullName)).ToArray();
                    SetNameChanges(missing);

                    if (hidden.Count > 0)
                        Italics(L10n.Get(L10n.SECTION_MIGHT_BE_INCOMPLETE));

                    if (missing.Length > 0)
                    {
                        Italics(L10n.Get(L10n.PLACED_BUT_MISSING));
                        ReportMissing(missing, GetUsedBy(), allHeadings, allTypes, USEDDIR, USEDDIR, USEDDIR, USEDDIR, USEDDIR, USEDDIR, 0);
                        Array.Clear(missing, 0, missing.Length); missing = null;
                    }
                    else
                        Italics(L10n.Get(L10n.NO_MISSING_ASSETS));
                }
                else
                    Italics(L10n.Get(L10n.TO_TRACK_MISSING));

                SecOff();
                Sec("#80e0f0");
                H2(L10n.Get(L10n.DUPLICATE_NAMES));
                ReportDuplicates(hidden);

                SecOff();
                Sec("#60b030");
                H2(L10n.Get(L10n.THESE_ARE_USED));

                if (Settings.settings.loadUsed)
                {
                    Item[] used = assets.Values.Which(USED).ToArray();

                    if (used.Length > 0)
                    {
                        Report(used, allHeadings, allTypes, USEDDIR, USEDDIR, USEDDIR, USEDDIR, USEDDIR, USEDDIR, USEDIND);
                        Array.Clear(used, 0, used.Length); used = null;
                    }
                    else
                        Italics(L10n.Get(L10n.NO_USED_ASSETS));

                    SecOff();
                    Sec("#c9c6ba");
                    H2(L10n.Get(L10n.THESE_ARE_UNNECESSARY));
                    Item[] unnecessary = assets.Values.Where(item => item.Enabled && !item.Used && !AssetLoader.instance.IsIntersection(item.FullName)).ToArray();

                    if (unnecessary.Length > 0)
                    {
                        Italics(L10n.Get(L10n.ENABLED_BUT_UNNECESSARY));
                        Report(unnecessary, mainHeadings, mainTypes);
                        Array.Clear(unnecessary, 0, unnecessary.Length); unnecessary = null;
                    }
                    else
                        Italics(L10n.Get(L10n.NO_UNNECESSARY_ASSETS));
                }
                else
                    Italics(L10n.Get(L10n.TO_TRACK_USED));

                SecOff();
                //PrintAssets();
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogException(e);
            }
            finally
            {
                w?.Dispose();
                w = null;
            }

            if (assets.Count > 0)
                SaveBrowser(cityName);

            Util.DebugPrint("Reports created in", Profiling.Millis - t0);
        }

        internal void SaveStats()
        {
            try
            {
                Util.DebugPrint("Saving stats to", reportFilePath);
                w = new StreamWriter(reportFilePath, append:true);
                H2(L10n.Get(L10n.LOADING_STATS));
                H3(L10n.Get(L10n.PERFORMANCE));
                Stat(L10n.Get(L10n.CUSTOM_ASSETS_LOADED), AssetLoader.instance.assetCount, L10n.Get(L10n.ASSETS));
                int dt = AssetLoader.instance.lastMillis - AssetLoader.instance.beginMillis;

                if (dt > 0)
                    Stat(L10n.Get(L10n.LOADING_SPEED), (AssetLoader.instance.assetCount * 1000f / dt).ToString("F1"), L10n.Get(L10n.ASSETS_PER_SECOND));

                Stat(L10n.Get(L10n.ASSETS_LOADING_TIME), Profiling.TimeString(dt + 500), L10n.Get(L10n.MINUTES_SECONDS));
                Stat(L10n.Get(L10n.TOTAL_LOADING_TIME), Profiling.TimeString(Profiling.Millis + 500), L10n.Get(L10n.MINUTES_SECONDS));

                if (Application.platform == RuntimePlatform.WindowsPlayer || Application.platform == RuntimePlatform.WindowsEditor)
                {
                    H3(L10n.Get(L10n.PEAK_MEMORY_USAGE));
                    Stat("RAM", (MemoryAPI.wsMax / 1024f).ToString("F1"), "GB");
                    Stat(L10n.Get(L10n.VIRTUAL_MEMORY), (MemoryAPI.pfMax / 1024f).ToString("F1"), "GB");
                }

                H3(L10n.Get(L10n.SHARING_OF_RESOURCES));
                Stat(L10n.Get(L10n.TEXTURES), texturesShared, L10n.Get(L10n.TIMES));
                Stat(L10n.Get(L10n.MATERIALS), materialsShared, L10n.Get(L10n.TIMES));
                Stat(L10n.Get(L10n.MESHES), meshesShared, L10n.Get(L10n.TIMES));

                H3(L10n.Get(L10n.SKIPPED_PREFABS));
                int[] counts = LevelLoader.instance.skipCounts;
                Stat(L10n.Get(L10n.BUILDING_PREFABS), counts[Matcher.BUILDINGS], string.Empty);
                Stat(L10n.Get(L10n.VEHICLE_PREFABS), counts[Matcher.VEHICLES], string.Empty);
                Stat(L10n.Get(L10n.PROP_PREFABS), counts[Matcher.PROPS], string.Empty);
                w.WriteLine(@"</body></html>");
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogException(e);
            }
            finally
            {
                w?.Dispose();
                w = null;
            }
        }

        private void SaveBrowser(string cityName)
        {
            try
            {
                string filePath = Util.GetFileName(cityName + " - " + L10n.Get(L10n.ASSETS_BROWSER), "htm", Settings.settings.useReportDate);
                Util.DebugPrint("Saving assets browser to", filePath);
                Item[] all = assets.Values.OrderBy(Name).ToArray();
                Dictionary<Item, int> ids = new Dictionary<Item, int>(all.Length);

                for (int i = 0; i < all.Length; i++)
                    ids.Add(all[i], i);

                string opt = @"</option><option>";
                w = new StreamWriter(filePath);
                w.WriteLine(@"<!DOCTYPE html><html lang=""" + L10n.Code + @"""><head><meta charset=""UTF-8""><title>" + L10n.Get(L10n.ASSETS_BROWSER) + @"</title><style>");
                w.WriteLine(@"* {box-sizing:border-box;font-family:'Segoe UI',Verdana,sans-serif;}");
                w.WriteLine(@"body {background-color:#fafaf7;}");
                w.WriteLine(@"h1 {color:#00507a;}");
                w.WriteLine(@".av {color:#f2f2ed;}");
                w.WriteLine(@".mi {color:#f4a820;}");
                w.WriteLine(@".fa {color:#f85800;}");
                w.WriteLine(@".tg {background-color:#00628b;cursor:pointer;margin:8px 0 0;border:none;padding:6px 12px 6px 15px;width:100%;text-align:left;outline:none;font-size:16px;}");
                w.WriteLine(@".tg:hover {filter:brightness(112%);}");
                w.WriteLine(@".tg:after {content:'\002B';font-weight:bold;float:right;}");
                w.WriteLine(@".tg.ex:after {content:'\2212';}");
                w.WriteLine(@"p {color:#7da291;margin-top:16px;margin-bottom:1px;font-style:italic;}");
                w.WriteLine(@"span {color:#8db5a2;margin-left:14px;font-style:italic;}");
                w.WriteLine(@"a:link {color:inherit;text-decoration:inherit;outline:none;}");
                w.WriteLine(@"a:visited {color:inherit;text-decoration:inherit;}");
                w.WriteLine(@"a:hover {text-decoration:underline;}");
                w.WriteLine(@".cc {background-color:#f2f2ed;}");
                w.WriteLine(@".ro {color:#f2f2ed;background-color:#7da291;padding:6px 0px 6px 15px;display:flex;}");
                w.WriteLine(@".ce {flex:1;}");
                w.WriteLine(@".nr {flex:0.75;}");
                w.WriteLine(@".gr {border:1px solid #7da291;padding:0 8px 9px 15px;}");
                w.WriteLine(@"</style></head><body>");
                H1(L10n.Get(L10n.ASSETS_BROWSER) + " - " + Enc(cityName));
                w.WriteLine(@"<noscript><h2 style=""color:red"">JavaScript is required.</h2></noscript>");
                w.WriteLine(@"<input id=""sch"" style=""margin:16px 16px 16px 0;"" type=""search"" placeholder=""" + L10n.Get(L10n.FIND_ASSETS) + @""">");
                w.WriteLine(@"<label for=""sct"">" + L10n.Get(L10n.ORDER_BY) + @"&nbsp;</label>");
                w.WriteLine(@"<select id=""sct""><option>" +
                    L10n.Get(L10n.NAME) + opt +
                    L10n.Get(L10n.TYPE) + opt +
                    L10n.Get(L10n.STATUS) + opt +
                    L10n.Get(L10n.WORKSHOP_ID) + opt +
                    L10n.Get(L10n.DATE) + opt +
                    L10n.Get(L10n.SIZE) + opt +
                    L10n.Get(L10n.USES_COUNT) + opt +
                    L10n.Get(L10n.USED_BY_COUNT) + opt +
                    L10n.Get(L10n.TYPE_AND_STATUS) + opt +
                    L10n.Get(L10n.TYPE_AND_SIZE) + opt +
                    L10n.Get(L10n.TYPE_AND_USED_BY_COUNT) + opt +
                    L10n.Get(L10n.STATUS_AND_USED_BY_COUNT) + opt +
                    L10n.Get(L10n.USED_BY_COUNT_AND_SIZE) + opt +
                    L10n.Get(L10n.USED_IN_CITY) + opt +
                    L10n.Get(L10n.USED_IN_CITY_AND_SIZE) + @"</option></select>");
                w.WriteLine(@"<div id=""top""></div>");
                w.WriteLine(@"<script src=""https://code.jquery.com/jquery-3.3.1.min.js""></script>");
                w.WriteLine(@"<script>const zh=" + (L10n.Code == "zh" ? "1" : "0") + "</script>");
                w.WriteLine(@"<script src=""https://thale5.github.io/js/browse4.js""></script>");
                w.WriteLine(@"<script>");
                StringBuilder s = new StringBuilder(640);
                s.Append("const d=[");
                bool loadUsed = Settings.settings.loadUsed;

                for (int i = 0; i < all.Length; i++)
                {
                    Item item = all[i];
                    s.Append('[').Append(i).Append(',').Append((int) item.type);
                    s.Append(',').Append(GetStatus(item));
                    GetDateAndSize(item, out int date, out int size);
                    s.Append(',').Append(date).Append(',').Append(size);
                    s.Append(',').Append(GetInCity(item, loadUsed));
                    s.Append(',').Append(GetId(item));
                    s.Append(",\"").Append(JsEnc(item.name)).Append("\",[");

                    if (item.Uses != null)
                    {
                        int[] uses = item.Uses.Select(it => ids[it]).ToArray();
                        Array.Sort(uses);
                        s.Append(string.Join(",", uses.Select(u => u.ToString()).ToArray()));
                    }

                    s.Append("]]");

                    if (i < all.Length - 1)
                    {
                        s.Append(',');

                        if (s.Length > 500)
                        {
                            w.WriteLine(s);
                            s.Length = 0;
                        }
                    }
                }

                w.WriteLine(s.Append(']'));
                w.WriteLine(@"$(ini)");
                w.WriteLine(@"</script></body></html>");
                Array.Clear(all, 0, all.Length);
                ids.Clear();
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogException(e);
            }
            finally
            {
                w?.Dispose();
                w = null;
            }
        }

        void Report(IEnumerable<Item> items, string[] headings, CustomAssetMetaData.Type[] types, params int[] usages)
        {
            int usage = 0;

            for (int i = 0; i < headings.Length; i++)
            {
                if (i < usages.Length)
                    usage = usages[i];

                Item[] selected = items.Which(types[i], usage).OrderBy(Name).ToArray();

                if (selected.Length > 0)
                {
                    H3(headings[i]);

                    foreach (Item item in selected)
                        Div(Ref(item));
                }
            }
        }

        void ReportMissing(IEnumerable<Item> items, Dictionary<Item, List<Item>> usedBy, string[] headings, CustomAssetMetaData.Type[] types, params int[] usages)
        {
            StringBuilder s = new StringBuilder(1024);
            int usage = 0;

            for (int i = 0; i < headings.Length; i++)
            {
                if (i < usages.Length)
                    usage = usages[i];

                Item[] selected;

                if (usage == USEDDIR)
                    selected = items.Which(types[i], usage).OrderBy(Name).ToArray();
                else
                    selected = items.Which(types[i]).Where(item => usedBy.ContainsKey(item)).OrderBy(Name).ToArray();

                if (selected.Length > 0)
                {
                    H3(headings[i]);

                    foreach (Item item in selected)
                    {
                        string r = Ref(item);
                        string desc = item.NameChanged ? GetNameChangedDesc(item) : string.Empty;

                        if (usage == USEDDIR)
                        {
                            if (!string.IsNullOrEmpty(desc))
                                r += string.Concat(spaces, "<i>", desc.Replace("<br>", " "), "</i>");

                            Div(r);
                            continue;
                        }

                        s.Length = 0;
                        s.Append(L10n.Get(L10n.USED_BY)).Append(':');
                        int workshopUses = 0;

                        foreach (Item p in usedBy[item])
                        {
                            s.Append("<br>" + Ref(p));

                            if (workshopUses < 2 && FromWorkshop(p))
                                workshopUses++;
                        }

                        if (string.IsNullOrEmpty(desc) && !FromWorkshop(item))
                            if (item.HasPackageName)
                            {
                                if (workshopUses > 0)
                                {
                                    string bug = workshopUses == 1 ? L10n.Get(L10n.ASSET_BUG) : L10n.Get(L10n.ASSET_BUGS);
                                    desc = privateAssetLink + bug + @":</a> " + L10n.Get(L10n.ASSET_USES_PRIVATE_ASSET) + " (" + Enc(item.FullName) + ")";
                                }
                            }
                            else if (item.FullName.EndsWith("_Data"))
                                desc = Enc(item.name) + " " + L10n.Get(L10n.NO_LINK_IS_AVAILABLE);
                            else
                                desc = Enc(item.name) + " " + L10n.Get(L10n.IS_POSSIBLY_DLC);

                        if (!string.IsNullOrEmpty(desc))
                            s.Append("<br><br><i>" + desc + "</i>");

                        Div("my", Cl("mi", r) + Cl("bx", s.ToString()));
                    }
                }
            }
        }

        void ReportDuplicates(HashSet<string> hidden)
        {
            duplicates.Sort((a, b) => string.Compare(a[0].fullName, b[0].fullName));
            StringBuilder s = new StringBuilder(512);
            int n = 0;

            if (hidden.Count > 0)
                Italics(L10n.Get(L10n.SECTION_MIGHT_BE_INCOMPLETE));

            foreach (List<Package.Asset> list in duplicates)
            {
                string fn = list[0].fullName;

                if (hidden.Contains(fn))
                    continue;

                Item[] items = list.Select(a => FindItem(a)).Where(item => item != null).ToArray();

                if (items.Length > 1)
                {
                    string fullName = Enc(fn);
                    s.Length = 0;
                    s.Append(L10n.Get(L10n.SAME_ASSET_NAME) + " (" + fullName + ") " + L10n.Get(L10n.IN_ALL_OF_THESE) + ":");

                    foreach (Item d in items)
                        s.Append("<br>" + Ref(d));

                    Div("my", Cl("mi", fullName) + Cl("bx", s.ToString()));
                    n++;
                }
            }

            if (n == 0)
                Italics(L10n.Get(L10n.NO_DUPLICATES));
        }

        void ReportEWs(IEnumerable<AssetError<string>> all)
        {
            IOrderedEnumerable<IGrouping<Item, AssetError<string>>> ews = all.GroupBy(e => FindItem(e.package)).Where(g => g.Key != null).OrderBy(g => g.Key.name);

            foreach (IGrouping<Item, AssetError<string>> ew in ews)
                Div("my", Cl("mi", Ref(ew.Key)) + Cl("bx", "<i>" + string.Join("<br>", ew.Distinct().Select(e => e.value).ToArray()) + "</i>"));
        }

        bool ReportErrors()
        {
            // Depending on asset type, the CS error threshold is either 65000 / 16 or 65000 / 8.
            List<AssetError<int>> extremes = new List<AssetError<int>>(extremeMeshes.Count);
            foreach (AssetError<int> e in extremeMeshes)
                if (HasExtremeVertices(e))
                    extremes.Add(e);
                else
                    largeMeshes.Add(e);

            string wLink = weirdTextureLink + L10n.Get(L10n.INVALID_LOD_TEXTURE_SIZE) + @"</a> ";
            string nLink = namingConflictLink + L10n.Get(L10n.ASSET_NAMING_CONFLICT) + @"</a>";
            IEnumerable<AssetError<string>> all = weirdTextures.Select(e => e.Map(v => wLink + DecodeTextureSize(v)))
                .Concat(namingConflicts.Select(p => new AssetError<string>(p, string.Empty, nLink)))
                .Concat(extremes.Select(e => e.Map(ExtremeMesh)));

            if (all.Any())
            {
                Italics(L10n.Get(L10n.PROBLEMS_WERE_DETECTED));
                ReportEWs(all);
                return true;
            }
            else
                return false;
        }

        bool ReportWarnings()
        {
            string link = largeTextureLink + L10n.Get(L10n.VERY_LARGE_LOD_TEXTURE) + @"</a> ";
            IEnumerable<AssetError<string>> all = largeTextures.Select(e => e.Map(v => link + DecodeTextureSize(v)))
                .Concat(largeMeshes.Select(e => e.Map(MeshSize)));

            if (all.Any())
            {
                Italics(L10n.Get(L10n.OBSERVATIONS_WERE_MADE));
                ReportEWs(all);
                return true;
            }
            else
                return false;
        }

        static string GetNameChangedDesc(Item missing)
        {
            List<Package> packages = CustomDeserializer.instance.GetPackages(missing.packageName);
            Package.Asset asset = packages.Count == 1 ? AssetLoader.FindMainAssetRef(packages[0]) : null;
            string have = asset != null ? Ref(asset.package.packageName, AssetLoader.ShortName(asset.name)) : Ref(missing.packageName);

            return string.Concat(L10n.Get(L10n.YOU_HAVE), " ",
                have, " ",
                L10n.Get(L10n.DOES_NOT_CONTAIN), " ",
                Enc(missing.name),
                @".<br><a target=""_blank"" href=""https://steamcommunity.com/workshop/filedetails/discussion/667342976/141136086940263481/"">",
                L10n.Get(L10n.NAME_PROBABLY_CHANGED),
                @"</a> ",
                L10n.Get(L10n.BY_THE_AUTHOR));
        }

        static int GetStatus(Item item)
        {
            if (item.Failed)
                return 0;
            else if (item.Available)
                return 1;
            else if (item.NameChanged)
                return 3;
            else
                return 2;
        }

        void GetDateAndSize(Item item, out int date, out int size)
        {
            if (item is Available a)
            {
                try
                {
                    FileInfo fileinfo = new FileInfo(a.mainAssetRef.package.packagePath);
                    long millis = (long) (fileinfo.LastWriteTimeUtc - jsEpoch).TotalMilliseconds;
                    date = (int) (millis >> 20) - 1350000;
                    size = (int) ((fileinfo.Length + 512) >> 10);
                    return;
                }
                catch (Exception e)
                {
                    UnityEngine.Debug.LogException(e);
                }
            }

            date = 0;
            size = 0;
        }

        int GetInCity(Item item, bool loadUsed) => loadUsed ? item.UsedDir ? 3 : item.UsedInd ? 2 : 1 : 0;

        string GetId(Item item)
        {
            if (FromWorkshop(item) && !item.NameChanged)
                return item.packageName;

            if (item.HasPackageName)
                return "0";

            return item.FullName.EndsWith("_Data") ? "-1" : "-2";
        }

        void Sec(string color) => w.Write(string.Concat(@"<section style=""border-right:12px solid ", color, @";"">"));
        void SecOff() => w.Write("<br><br></section>");
        void Div(string line) => w.WriteLine(string.Concat("<div>", line, "</div>"));
        void Div(string cl, string line) => w.WriteLine(Cl(cl, line));
        void Italics(string line) => Div("<i>" + line + "</i>");
        void H1(string line) => w.WriteLine(string.Concat("<h1>", line, "</h1>"));
        void H2(string line) => w.WriteLine(string.Concat("<h2>", line, "</h2>"));
        void H3(string line) => w.WriteLine(string.Concat("<h3>", line, "</h3>"));
        void Stat(string stat, object value, string unit) => Div("my", Cl("st", stat) + Cl(value.ToString() + spaces + unit));
        void Br() => w.Write("<br>");

        static bool FromWorkshop(Item item) => FromWorkshop(item.packageName);
        static bool FromWorkshop(string packageName) => ulong.TryParse(packageName, out ulong id) && id > 99999999;
        static string Ref(Item item) => item.NameChanged ? Enc(item.name) : Ref(item.packageName, item.name);
        static string Ref(string packageName, string name) => FromWorkshop(packageName) ? string.Concat(steamid, packageName, "\">", Enc(name), "</a>") : Enc(name);
        static string Ref(string packageName) => FromWorkshop(packageName) ? string.Concat(steamid, packageName, "\">", L10n.Get(L10n.WORKSHOP_ITEM), " ", packageName, "</a>") : Enc(packageName);
        static string Cl(string cl, string s) => string.Concat("<div class=\"", cl, "\">", s, "</div>");
        static string Cl(string s) => string.Concat("<div>", s, "</div>");
        static string Name(Item item) => item.name;
        static string DecodeTextureSize(int v) => (v >> 16).ToString() + " x " + (v & 0xffff).ToString();
        static string MeshSize(int v) => largeMeshLink + L10n.Get(L10n.VERY_LARGE_LOD_MESH) + @"</a> " + (v < 0 ? -v + " " + L10n.Get(L10n.TRIANGLES) : v + " " + L10n.Get(L10n.VERTICES));
        static string ExtremeMesh(int v) => largeMeshLink + L10n.Get(L10n.EXTREMELY_LARGE_LOD_MESH) + @"</a> " + v + " " + L10n.Get(L10n.VERTICES);
        Item FindItem(Package.Asset assetRef) => FindItem(assetRef.package);

        Item FindItem(Package package)
        {
            string fullName = AssetLoader.FindMainAssetRef(package)?.fullName;
            return !string.IsNullOrEmpty(fullName) && assets.TryGetValue(fullName, out Item item) ? item : null;
        }

        bool HasExtremeVertices(AssetError<int> e)
        {
            Item item = FindItem(e.package);

            if (item != null)
            {
                switch (item.type)
                {
                    case CustomAssetMetaData.Type.Prop:
                    case CustomAssetMetaData.Type.Vehicle:
                    case CustomAssetMetaData.Type.Citizen:
                        return e.value > 4062;

                    default:
                        return e.value > 8125;
                }
            }
            else
                return false;
        }

        // From a more recent mono version.
        // See license https://github.com/mono/mono/blob/master/mcs/class/System.Web/System.Web.Util/HttpEncoder.cs
        static string Enc(string s)
        {
            int len = s.Length;

            if (len > 200)
                return Enc(L10n.Get(L10n.LONG_NAME));

            bool needEncode = false;

            for (int i = 0; i < len; i++)
            {
                char c = s[i];

                if (c == '&' || c == '"' || c == '<' || c == '>' || c > 159 || c == '\'')
                {
                    needEncode = true;
                    break;
                }
            }

            if (!needEncode)
                return s;

            StringBuilder output = new StringBuilder(len + 12);

            for (int i = 0; i < len; i++)
            {
                char ch = s[i];

                switch (ch)
                {
                    case '&':
                        output.Append("&amp;");
                        break;
                    case '>':
                        output.Append("&gt;");
                        break;
                    case '<':
                        output.Append("&lt;");
                        break;
                    case '"':
                        output.Append("&quot;");
                        break;
                    case '\'':
                        output.Append("&#39;");
                        break;
                    case '\uff1c':
                        output.Append("&#65308;");
                        break;
                    case '\uff1e':
                        output.Append("&#65310;");
                        break;
                    default:
                        output.Append(ch);
                        break;
                }
            }

            return output.ToString();
        }

        // JS string escaping.
        static string JsEnc(string s)
        {
            if (s.Length > 100)
                s = s.Substring(0, 100);

            foreach (string e in jsEsc)
                s = s.Replace(e, @"\" + e);

            return s;
        }

        //void PrintAssets()
        //{
        //    List<Item> items = new List<Item>(assets.Values);
        //    items.Sort((a, b) => a.usage - b.usage);

        //    using (StreamWriter w = new StreamWriter(Util.GetFileName("Assets", "txt")))
        //        foreach (Item item in items)
        //        {
        //            string s = item.FullName.PadRight(56);
        //            s += item.Enabled ? " EN" : "   ";
        //            s += item.UsedDir ? " DIR" : "    ";
        //            s += item.UsedInd ? " IND" : "    ";
        //            s += item.Available ? " AV" : "   ";
        //            s += item.Missing ? " MI" : "   ";
        //            s += item.NameChanged ? " NM" : "   ";
        //            s += item.Failed ? " FAIL" : string.Empty;
        //            w.WriteLine(s);
        //        }
        //}
    }

    abstract class Item
    {
        internal string packageName, name;
        internal CustomAssetMetaData.Type type;
        internal byte usage;
        internal abstract string FullName { get; }
        internal virtual HashSet<Item> Uses => null;
        internal bool HasPackageName => !string.IsNullOrEmpty(packageName);
        internal bool Enabled => (usage & ENABLED) != 0;
        internal bool Available => (usage & AVAILABLE) != 0;
        internal bool Missing => (usage & MISSING) != 0;
        internal bool Used => (usage & USED) != 0;

        internal bool UsedDir
        {
            get => (usage & USEDDIR) != 0;
            set => usage |= USEDDIR;        // never unset
        }

        internal bool UsedInd
        {
            get => (usage & USEDIND) != 0;
            set => usage |= USEDIND;        // never unset
        }

        internal bool Failed
        {
            get => (usage & FAILED) != 0;
            set => usage |= FAILED;         // never unset
        }

        internal bool NameChanged
        {
            get => (usage & NAME_CHANGED) != 0;
            set => usage |= NAME_CHANGED;   // never unset
        }

        protected Item(string packageName, string name_Data, CustomAssetMetaData.Type type, int usage)
        {
            this.packageName = packageName;
            this.name = AssetLoader.ShortName(name_Data);
            this.type = type;
            this.usage = (byte) usage;
        }

        protected Item(string fullName, CustomAssetMetaData.Type type, int usage)
        {
            int j = fullName.IndexOf('.');

            if (j >= 0)
            {
                packageName = fullName.Substring(0, j);
                name = AssetLoader.ShortName(fullName.Substring(j + 1));
            }
            else
            {
                packageName = string.Empty;
                name = AssetLoader.ShortName(fullName);
            }

            this.type = type;
            this.usage = (byte) usage;
        }

        internal virtual void Add(Item child) { }
    }

    sealed class Available : Item
    {
        HashSet<Item> uses;
        internal Package.Asset mainAssetRef;
        internal override string FullName => mainAssetRef.fullName;
        internal override HashSet<Item> Uses => uses;

        internal Available(Package.Asset mainAssetRef, CustomAssetMetaData.Type type, bool enabled, bool useddir)
            : base(mainAssetRef.package.packageName, mainAssetRef.name, type, AVAILABLE | (enabled ? ENABLED : 0) | (useddir ? USEDDIR : 0))
        {
            this.mainAssetRef = mainAssetRef;
        }

        internal override void Add(Item child)
        {
            if (uses != null)
                uses.Add(child);
            else
                uses = new HashSet<Item> { child };
        }
    }

    sealed class Missing : Item
    {
        readonly string fullName;
        internal override string FullName => fullName;

        internal Missing(string fullName, CustomAssetMetaData.Type type, bool useddir = false)
            : base(fullName, type, MISSING | (useddir ? USEDDIR : 0))
        {
            this.fullName = fullName;
        }
    }

    sealed class AssetError<V> : IEquatable<AssetError<V>>
    {
        internal readonly Package package;
        internal readonly string checksum;
        internal readonly V value;

        internal AssetError(Package p, string c, V v)
        {
            this.package = p;
            this.checksum = c;
            this.value = v;
        }

        public override bool Equals(object obj) => Equals(obj as AssetError<V>);
        public bool Equals(AssetError<V> other) => other != null && checksum == other.checksum;
        public override int GetHashCode() => checksum.GetHashCode();
        internal AssetError<U> Map<U>(Func<V, U> m) => new AssetError<U>(package, checksum, m(value));
    }

    static class Exts
    {
        internal static IEnumerable<Item> Which(this IEnumerable<Item> items, CustomAssetMetaData.Type type, int usage = 0)
        {
            Func<Item, bool> pred;

            if (usage == 0)
                pred = item => item.type == type;
            else
                pred = item => item.type == type && (item.usage & usage) != 0;

            return items.Where(pred);
        }

        internal static IEnumerable<Item> Which(this IEnumerable<Item> items, int usage) => items.Where(item => (item.usage & usage) != 0);
    }
}
