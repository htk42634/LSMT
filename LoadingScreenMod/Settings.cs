using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using ColossalFramework.IO;
using ColossalFramework.UI;
using ICities;

namespace LoadingScreenModTest
{
    public class Settings
    {
        const string FILENAME = "LoadingScreenMod.xml";
        internal const string SUPPORTED = "1.13";
        const int VERSION = 10;

        public int version = VERSION;
        public bool loadEnabled = true;
        public bool loadUsed = true;
        public bool shareTextures = true;
        public bool shareMaterials = true;
        public bool shareMeshes = true;
        public bool optimizeThumbs = true;
        public bool reportAssets = false;
        public bool checkAssets = false;
        public string reportDir = string.Empty;
        public bool skipPrefabs = false;
        public string skipFile = string.Empty;
        public bool hideAssets = false;
        public bool useReportDate = true;
        private DateTime skipFileTimestamp = DateTime.MinValue;
        internal bool enableDisable = false;
        internal bool removeVehicles = false;
        internal bool removeCitizenInstances = false;
        internal bool recover = false;

        internal bool SkipPrefabs => skipPrefabs && SkipMatcher != null && ExceptMatcher != null;
        internal bool RecordAssets => reportAssets | hideAssets | enableDisable;
        internal bool Removals => removeVehicles | removeCitizenInstances;
        internal Matcher SkipMatcher { get; private set; }
        internal Matcher ExceptMatcher { get; private set; }

        static Settings singleton;
        internal UIHelperBase helper;
        static internal string DefaultSavePath => Path.Combine(Path.Combine(DataLocation.localApplicationData, "Report"), "LoadingScreenMod");
        static string DefaultSkipFile => Path.Combine(Path.Combine(DataLocation.localApplicationData, "SkippedPrefabs"), "skip.txt");
        static string HiddenAssetsFile => Path.Combine(Path.Combine(DataLocation.localApplicationData, "HiddenAssets"), "hide.txt");

        public static Settings settings
        {
            get
            {
                if (singleton == null)
                    singleton = Load();

                return singleton;
            }
        }

        Settings() { }

        static Settings Load()
        {
            Settings s;

            try
            {
                XmlSerializer serializer = new XmlSerializer(typeof(Settings));

                using (StreamReader reader = new StreamReader(FILENAME))
                    s = (Settings) serializer.Deserialize(reader);
            }
            catch (Exception) { s = new Settings(); }

            if (string.IsNullOrEmpty(s.reportDir = s.reportDir?.Trim()))
                s.reportDir = DefaultSavePath;

            if (string.IsNullOrEmpty(s.skipFile = s.skipFile?.Trim()))
                s.skipFile = DefaultSkipFile;

            s.checkAssets &= s.reportAssets;
            s.version = VERSION;
            return s;
        }

        void Save()
        {
            try
            {
                XmlSerializer serializer = new XmlSerializer(typeof(Settings));

                using (StreamWriter writer = new StreamWriter(FILENAME))
                    serializer.Serialize(writer, this);
            }
            catch (Exception e)
            {
                Util.DebugPrint("Settings.Save");
                UnityEngine.Debug.LogException(e);
            }
        }

        internal DateTime LoadSkipFile()
        {
            try
            {
                if (skipPrefabs)
                {
                    DateTime stamp;
                    bool fileExists = File.Exists(skipFile);

                    if (fileExists && skipFileTimestamp != (stamp = File.GetLastWriteTimeUtc(skipFile)))
                    {
                        Matcher[] matchers = Matcher.Load(skipFile);
                        SkipMatcher = matchers[0];
                        ExceptMatcher = matchers[1];
                        skipFileTimestamp = stamp;
                    }
                    else if (!fileExists)
                        Util.DebugPrint("Skip file", skipFile, "does not exist");
                }
            }
            catch (Exception e)
            {
                Util.DebugPrint("Settings.LoadSkipFile");
                UnityEngine.Debug.LogException(e);
                SkipMatcher = ExceptMatcher = null;
                skipFileTimestamp = DateTime.MinValue;
            }

            return SkipPrefabs ? skipFileTimestamp : DateTime.MinValue;
        }

        internal void LoadHiddenAssets(HashSet<string> hidden)
        {
            try
            {
                using (StreamReader r = new StreamReader(HiddenAssetsFile))
                {
                    string l0;

                    while ((l0 = r.ReadLine()) != null)
                    {
                        string line = l0.Trim();

                        if (!string.IsNullOrEmpty(line) && !line.StartsWith("#"))
                            hidden.Add(line);
                    }
                }
            }
            catch (Exception)
            {
                Util.DebugPrint("Cannot read from " + HiddenAssetsFile);
            }
        }

        internal void SaveHiddenAssets(HashSet<string> hidden, string[] missing, string[] duplicates)
        {
            if (hidden.Count == 0 && missing.Length == 0 && duplicates.Length == 0)
                CreateHiddenAssetsFile();
            else
            {
                string hf = HiddenAssetsFile;
                Util.DebugPrint("Saving hidden assets to", hf);

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(hf));

                    using (StreamWriter w = new StreamWriter(hf))
                    {
                        w.WriteLine(L10n.Get(L10n.AS_YOU_KNOW));
                        w.WriteLine(L10n.Get(L10n.USING_THIS_FILE));

                        if (hidden.Count > 0)
                            WriteLines(w, L10n.Get(L10n.THESE_ARE_NOT_REPORTED), hidden.ToArray(), false);

                        string[] m = missing.Where(s => !hidden.Contains(s)).ToArray();
                        string[] d = duplicates.Where(s => !hidden.Contains(s)).ToArray();

                        if (m.Length > 0 || d.Length > 0)
                        {
                            w.WriteLine();
                            w.WriteLine(L10n.Get(L10n.LSM_REPORTED_THESE));
                            WriteLines(w, L10n.Get(L10n.REPORTED_MISSING), m, true);
                            WriteLines(w, L10n.Get(L10n.REPORTED_DUPLICATE), d, true);
                        }
                    }
                }
                catch (Exception)
                {
                    Util.DebugPrint("Cannot write to " + hf);
                }
            }
        }

        static void WriteLines(StreamWriter w, string header, string[] lines, bool tag)
        {
            if (lines.Length > 0)
            {
                w.WriteLine();
                w.WriteLine(header);
                Array.Sort(lines);

                foreach (string s in lines)
                    if (tag)
                        w.WriteLine("#" + s);
                    else
                        w.WriteLine(s);
            }
        }

        void CreateHiddenAssetsFile()
        {
            try
            {
                string hf = HiddenAssetsFile;
                Directory.CreateDirectory(Path.GetDirectoryName(hf));

                using (StreamWriter w = new StreamWriter(hf))
                {
                    w.WriteLine(L10n.Get(L10n.GO_AHEAD));
                }

            }
            catch (Exception)
            {
                Util.DebugPrint("Cannot write to " + HiddenAssetsFile);
            }
        }

        static UIComponent Self(UIHelperBase h) => (h as UIHelper)?.self as UIComponent;

        static void OnVisibilityChanged(UIComponent comp, bool visible)
        {
            if (visible && comp == Self(settings.helper) && comp.childCount == 1)
                settings.LateSettingsUI(settings.helper);
        }

        internal void OnSettingsUI(UIHelperBase newHelper)
        {
            L10n.SetCurrent();

            if (!BuildConfig.applicationVersion.StartsWith(SUPPORTED))
            {
                CreateGroup(newHelper, L10n.Get(L10n.MAJOR_GAME_UPDATE), L10n.Get(L10n.INCOMPATIBLE_VERSION));
                return;
            }

            if (Self(helper) is UIComponent comp)
                comp.eventVisibilityChanged -= OnVisibilityChanged;

            helper = newHelper;
            string repl = L10n.Get(L10n.REPLACE_DUPLICATES);
            UIHelper group = CreateGroup(newHelper, L10n.Get(L10n.LOADING_OPTIONS_FOR_ASSETS), L10n.Get(L10n.CUSTOM_MEANS));
            Check(group, L10n.Get(L10n.LOAD_ENABLED_ASSETS), L10n.Get(L10n.LOAD_ENABLED_IN_CM), loadEnabled, b => { loadEnabled = b; LevelLoader.instance?.Reset(); Save(); });
            Check(group, L10n.Get(L10n.LOAD_USED_ASSETS), L10n.Get(L10n.LOAD_USED_IN_YOUR_CITY), loadUsed, b => { loadUsed = b; LevelLoader.instance?.Reset(); Save(); });
            Check(group, L10n.Get(L10n.SHARE_TEXTURES), repl, shareTextures, b => { shareTextures = b; Save(); });
            Check(group, L10n.Get(L10n.SHARE_MATERIALS), repl, shareMaterials, b => { shareMaterials = b; Save(); });
            Check(group, L10n.Get(L10n.SHARE_MESHES), repl, shareMeshes, b => { shareMeshes = b; Save(); });
            Check(group, L10n.Get(L10n.OPTIMIZE_THUMBNAILS), L10n.Get(L10n.OPTIMIZE_TEXTURES), optimizeThumbs, b => { optimizeThumbs = b; Save(); });

            comp = Self(newHelper);
            comp.eventVisibilityChanged -= OnVisibilityChanged;
            comp.eventVisibilityChanged += OnVisibilityChanged;
        }

        void LateSettingsUI(UIHelperBase helper)
        {
            UIHelper group = CreateGroup(helper, L10n.Get(L10n.REPORTING));
            UICheckBox reportCheck = null, checkCheck = null;
            reportCheck = Check(group, L10n.Get(L10n.SAVE_REPORTS_IN_DIRECTORY), L10n.Get(L10n.SAVE_REPORTS_OF_ASSETS), reportAssets,
                b => { reportAssets = b; checkAssets &= b; checkCheck.isChecked = checkAssets; Save(); });
            TextField(group, reportDir, OnReportDirChanged);
            checkCheck = Check(group, L10n.Get(L10n.CHECK_FOR_ERRORS), null, checkAssets,
                b => { checkAssets = b; reportAssets |= b; reportCheck.isChecked = reportAssets; Save(); });
            Check(group, L10n.Get(L10n.DO_NOT_REPORT_THESE), null, hideAssets, b => { hideAssets = b; Save(); });
            Button(group, L10n.Get(L10n.OPEN_FILE), L10n.Get(L10n.CLICK_TO_OPEN) + " " + HiddenAssetsFile, OnAssetsButton);

            group = CreateGroup(helper, L10n.Get(L10n.PREFAB_SKIPPING), L10n.Get(L10n.PREFAB_MEANS));
            Check(group, L10n.Get(L10n.SKIP_THESE), null, skipPrefabs, b => { skipPrefabs = b; Save(); });
            TextField(group, skipFile, OnSkipFileChanged);

            group = CreateGroup(helper, L10n.Get(L10n.SAFE_MODE), L10n.Get(L10n.AUTOMATICALLY_DISABLED));
            Check(group, L10n.Get(L10n.REMOVE_VEHICLE_AGENTS), null, removeVehicles, b => removeVehicles = b);
            Check(group, L10n.Get(L10n.REMOVE_CITIZEN_AGENTS), null, removeCitizenInstances, b => removeCitizenInstances = b);
            Check(group, L10n.Get(L10n.TRY_TO_RECOVER), null, recover, b => recover = b);
        }

        UIHelper CreateGroup(UIHelperBase parent, string name, string tooltip = null)
        {
            UIHelper group = parent.AddGroup(name) as UIHelper;
            UIPanel content = group.self as UIPanel;

            if (content?.autoLayoutPadding is UnityEngine.RectOffset rect1)
                rect1.bottom /= 2;

            UIPanel container = content?.parent as UIPanel;

            if (container?.autoLayoutPadding is UnityEngine.RectOffset rect2)
                rect2.bottom /= 2;

            if (!string.IsNullOrEmpty(tooltip) && container?.Find<UILabel>("Label") is UILabel label)
                label.tooltip = tooltip;

            return group;
        }

        UICheckBox Check(UIHelper group, string text, string tooltip, bool setChecked, OnCheckChanged action)
        {
            try
            {
                UICheckBox check = group.AddCheckbox(text, setChecked, action) as UICheckBox;

                if (tooltip != null)
                    check.tooltip = tooltip;

                return check;
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogException(e);
                return null;
            }
        }

        void TextField(UIHelper group, string text, OnTextChanged action)
        {
            try
            {
                UITextField field = group.AddTextfield(" ", " ", action, null) as UITextField;
                field.text = text;
                field.width *= 2.8f;
                UIComponent parent = field.parent;
                UILabel label = parent?.Find<UILabel>("Label");

                if (label != null)
                {
                    float h = label.height;
                    label.height = 0; label.Hide();
                    parent.height -= h;
                }
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogException(e);
            }
        }

        void Button(UIHelper group, string text, string tooltip, OnButtonClicked action)
        {
            try
            {
                UIButton button = group.AddButton(text, action) as UIButton;
                button.textScale = 0.875f;
                button.tooltip = tooltip;
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogException(e);
            }
        }

        void OnReportDirChanged(string text)
        {
            if (text != reportDir)
            {
                reportDir = text;
                Save();
            }
        }

        void OnSkipFileChanged(string text)
        {
            if (text != skipFile)
            {
                skipFile = text;
                SkipMatcher = ExceptMatcher = null;
                skipFileTimestamp = DateTime.MinValue;
                Save();
            }
        }

        void OnAssetsButton()
        {
            string hf = HiddenAssetsFile;

            if (!File.Exists(hf))
                CreateHiddenAssetsFile();
            else
                try
                {
                    FileInfo fileinfo = new FileInfo(hf);
                    long size = fileinfo.Length;

                    // Locale may have changed.
                    if (size < 100)
                        CreateHiddenAssetsFile();
                }
                catch (Exception e)
                {
                    UnityEngine.Debug.LogException(e);
                }

            Process.Start(hf);
        }
    }
}
