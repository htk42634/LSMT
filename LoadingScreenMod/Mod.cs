using ICities;

namespace LoadingScreenModTest
{
    public sealed class Mod : IUserMod, ILoadingExtension
    {
        static bool created = false;
        public string Name => "Loading Screen Mod [Test]";
        public string Description => L10n.SetAndGet(L10n.NEW_LOADING_OPTIONS);

        public void OnSettingsUI(UIHelperBase helper) => Settings.settings.OnSettingsUI(helper);
        public void OnCreated(ILoading loading) { }
        public void OnReleased() { }
        public void OnLevelLoaded(LoadMode mode) { Util.DebugPrint("OnLevelLoaded at", Profiling.Millis); }
        public void OnLevelUnloading() { }

        public void OnEnabled()
        {
            L10n.SetCurrent();

            if (!created)
                if (BuildConfig.applicationVersion.StartsWith(Settings.SUPPORTED))
                {
                    //Util.HddAssets();

                    LevelLoader.Create().Deploy();
                    //PackageManagerFix.Create().Deploy();
                    created = true;
                    //Trace.Start();
                }
                else
                    Util.DebugPrint(L10n.Get(L10n.MAJOR_GAME_UPDATE));
        }

        public void OnDisabled()
        {
            LevelLoader.instance?.Dispose();
            Settings.settings.helper = null;
            //PackageManagerFix.instance?.Dispose();
            created = false;
            //Trace.Flush();
            //Trace.Stop();
        }
    }
}
