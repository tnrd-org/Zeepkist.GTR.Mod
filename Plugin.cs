﻿using System;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Steamworks;
using TNRD.Zeepkist.GTR.Cysharp.Threading.Tasks;
using TNRD.Zeepkist.GTR.Mod.Components;
using TNRD.Zeepkist.GTR.Mod.Components.Ghosting;
using TNRD.Zeepkist.GTR.Mod.Patches;
using TNRD.Zeepkist.GTR.SDK;
using UnityEngine;
using Result = TNRD.Zeepkist.GTR.FluentResults.Result;

namespace TNRD.Zeepkist.GTR.Mod
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        private static Plugin instance;

        private Harmony harmony;

        [Obsolete("Use Plugin.CreateLogger instead")]
        public static ManualLogSource Log;

        public static ConfigEntry<bool> ConfigEnableRecords;
        public static ConfigEntry<bool> ConfigEnableGhosts;
        public static ConfigEntry<bool> ConfigEnableVoting;

        public static ConfigEntry<bool> ConfigShowGhosts;
        public static ConfigEntry<bool> ConfigShowGhostNames;

        public static ConfigEntry<KeyCode> ConfigToggleEnableGhosts;
        public static ConfigEntry<KeyCode> ConfigToggleEnableRecords;
        public static ConfigEntry<KeyCode> ConfigToggleShowGhosts;
        public static ConfigEntry<KeyCode> ConfigToggleShowGhostNames;

        public static ManualLogSource CreateLogger(string sourceName)
        {
            return BepInEx.Logging.Logger.CreateLogSource(instance.Info.Metadata.Name + "." + sourceName);
        }

        public static AssetBundle AssetBundle { get; private set; }

        private void Awake()
        {
            instance = this;
            Log = Logger;

            if (!LoadUIAssembly())
            {
                Logger.LogFatal("Unable to load UI assembly");
                return;
            }

            if (!LoadAssetBundle())
            {
                Logger.LogFatal("Unable to load asset bundle");
            }

            SdkInitializer.Initialize();
            SetupConfig();

            harmony = new Harmony(PluginInfo.PLUGIN_GUID);
            harmony.PatchAll();

            gameObject.AddComponent<GhostRecorder>();
            gameObject.AddComponent<LevelCreator>();
            gameObject.AddComponent<ResultScreenshotter>();
            gameObject.AddComponent<RecordSubmitter>();
            gameObject.AddComponent<OnlineGhostLoader>();
            gameObject.AddComponent<OfflineGhostLoader>();
            gameObject.AddComponent<ShortcutsHandler>();
            gameObject.AddComponent<RatingPopupHandler>();

            // Plugin startup logic
            Logger.LogInfo($"Plugin {PluginInfo.PLUGIN_GUID} is loaded!");

            MainMenuUi_Awake.Awake += MainMenuUiOnAwake;
        }

        private bool LoadUIAssembly()
        {
            string dir = Path.GetDirectoryName(Info.Location);
            string assemblyPath = dir + "/TNRD.Zeepkist.GTR.UI.dll";
            try
            {
                Assembly asm = Assembly.LoadFile(assemblyPath);
                return asm != null;
            }
            catch (Exception e)
            {
                Logger.LogFatal(e.Message);
                return false;
            }
        }

        private bool LoadAssetBundle()
        {
            string dir = Path.GetDirectoryName(Info.Location);
            string assetBundlePath = dir + "/gtr-ui";
            try
            {
                AssetBundle = AssetBundle.LoadFromFile(assetBundlePath);
                return AssetBundle != null;
            }
            catch (Exception e)
            {
                Logger.LogFatal(e.Message);
                return false;
            }
        }

        private void OnDestroy()
        {
            MainMenuUi_Awake.Awake -= MainMenuUiOnAwake;
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.Alpha3))
            {
                var canvasPrefab = AssetBundle.LoadAsset<GameObject>("TournamentCanvas");
                Instantiate(canvasPrefab);
            }
        }

        private void SetupConfig()
        {
            ConfigEnableRecords = Config.Bind("General", "Enable Records", true, "Should records be tracked");
            ConfigEnableGhosts = Config.Bind("General", "Enable Ghosts", true, "Should ghosts be enabled");
            ConfigEnableVoting = Config.Bind("General", "Enable Voting", false, "Should voting be enabled");

            ConfigShowGhosts = Config.Bind("Visibility", "Show Ghosts", true, "Should ghosts be shown");
            ConfigShowGhostNames = Config.Bind("Visibility", "Show Ghost Names", true, "Should ghost names be shown");

            ConfigToggleEnableRecords = Config.Bind("Keys",
                "Toggle Enable Records",
                KeyCode.H,
                "Toggles if records should be enabled");

            ConfigToggleEnableGhosts = Config.Bind("Keys",
                "Toggle Enable Ghosts",
                KeyCode.J,
                "Toggles if ghosts should be enabled");

            ConfigToggleShowGhosts = Config.Bind("Keys",
                "Toggle Ghost Model Visibility",
                KeyCode.K,
                "Toggles the ghost visibility");
            ConfigToggleShowGhostNames = Config.Bind("Keys",
                "Toggle Ghost Name Visibility",
                KeyCode.L,
                "Toggles the ghost name visibility");
        }

        private void MainMenuUiOnAwake()
        {
            Login().Forget();
        }

        private async UniTaskVoid Login()
        {
            Logger.LogInfo("Waiting for SteamClient to become valid");
            while (!SteamClient.IsValid)
            {
                await UniTask.Yield();
            }

            Logger.LogInfo("Waiting for SteamUser to log in");
            while (!SteamClient.IsLoggedOn)
            {
                await UniTask.Yield();
            }

            Result result = await AttemptLogin();
            if (result.IsSuccess)
            {
                PlayerManager.Instance.messenger.Log("[GTR] Logged in", 2.5f);
                Result updateName = await UsersApi.UpdateName();
            }
            else
            {
                await UniTask.Delay(TimeSpan.FromSeconds(1));
                PlayerManager.Instance.messenger.LogError("[GTR] Failed to log in", 2.5f);
                Logger.LogError($"Failed to log in: {result.ToString()}");
            }
        }

        private async UniTask<Result> AttemptLogin()
        {
            Logger.LogInfo("Logging in to GTR");
            Result result = await UsersApi.Login(PluginInfo.PLUGIN_VERSION);
            if (result.IsSuccess)
                return result;

            PlayerManager.Instance.messenger.LogError("[GTR] Failed to log in, trying again in 5 seconds", 2.5f);
            Logger.LogWarning("Failed so waiting 5 seconds for another attempt");
            await UniTask.Delay(TimeSpan.FromSeconds(5));
            result = await UsersApi.Login(PluginInfo.PLUGIN_VERSION);
            if (result.IsSuccess)
                return result;

            PlayerManager.Instance.messenger.LogError("[GTR] Failed to log in, trying again in 10 seconds", 2.5f);
            Logger.LogWarning("Failed so waiting 10 seconds for another attempt");
            await UniTask.Delay(TimeSpan.FromSeconds(10));
            return await UsersApi.Login(PluginInfo.PLUGIN_VERSION);
        }
    }
}