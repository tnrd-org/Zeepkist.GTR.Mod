﻿using System;
using System.Collections.Generic;
using System.Linq;
using TNRD.Zeepkist.GTR.Cysharp.Threading.Tasks;
using TNRD.Zeepkist.GTR.FluentResults;
using TNRD.Zeepkist.GTR.Mod.Api.Levels;
using TNRD.Zeepkist.GTR.Mod.Components.Ghosting;
using TNRD.Zeepkist.GTR.Mod.Patches;
using ZeepkistClient;
using ZeepSDK.Racing;

namespace TNRD.Zeepkist.GTR.Mod.Components;

public class RecordSubmitter : MonoBehaviourWithLogging
{
    private static readonly string[] bannedLevels = new[]
    {
        "BE6DBC63CD48A2B1B0B14E7F337FD4BF0813DD6C" // NYE KICK OR CLUTCH VOTING MAP Decorated by Fred (ioi8)
    };

    private bool HasScreenshot => screenshotBuffer != null;
    private bool HasGhost => !string.IsNullOrEmpty(ghostJson);
    private SetupCar setupCar;
    private ReadyToReset readyToReset;

    private string ghostJson;
    private byte[] screenshotBuffer;
    private float time;
    private List<float> splits;

    protected override void Awake()
    {
        base.Awake();
        ResultScreenshotter.ScreenshotTaken += OnScreenshotTaken;
        GhostRecorder.GhostRecorded += OnGhostRecorded;
        RacingApi.RoundStarted += OnRoundStarted;
        GameMaster_CrossedFinishOnline.CrossedFinishOnline += OnCrossedFinishOnline;
    }

    private void OnDestroy()
    {
        RacingApi.RoundStarted -= OnRoundStarted;
    }

    private void OnCrossedFinishOnline()
    {
        WinCompare.Result result = PlayerManager.Instance.currentMaster.playerResults.First();
        time = result.time;
        splits = result.split_times;
    }

    private void OnRoundStarted()
    {
        setupCar = PlayerManager.Instance.currentMaster.carSetups.FirstOrDefault();
        if (setupCar == null)
            Logger.LogError("We're trying to log a ghost but there's no car available!");

        readyToReset = PlayerManager.Instance.currentMaster.PlayersReady.FirstOrDefault();
        if (readyToReset == null)
            Logger.LogError("We're trying to log a ghost but there's no car available!");

        ghostJson = string.Empty;
        screenshotBuffer = null;
    }

    private void OnGhostRecorded(string json)
    {
        ghostJson = json;
        CheckForSubmission();
    }

    private void OnScreenshotTaken(byte[] bytes)
    {
        screenshotBuffer = bytes;
        CheckForSubmission();
    }

    private void CheckForSubmission()
    {
        if (!HasGhost || !HasScreenshot)
            return;

        if (!Plugin.ConfigEnableRecords.Value)
            return;

        SubmitRecord().Forget();
    }

    private async UniTask SubmitRecord()
    {
        if (!ZeepkistNetwork.IsConnected)
            return;

        int user = SdkWrapper.Instance.UsersApi.UserId;

        if (string.IsNullOrEmpty(InternalLevelApi.CurrentLevelHash))
            return;

        if (bannedLevels.Contains(InternalLevelApi.CurrentLevelHash, StringComparer.OrdinalIgnoreCase))
            return;

        Result result = await SdkWrapper.Instance.RecordsApi.Submit(builder =>
        {
            builder
                .WithLevel(InternalLevelApi.CurrentLevelHash)
                .WithUser(user)
                .WithTime(time)
                .WithSplits(splits.ToArray)
                .WithGhostData(ghostJson)
                .WithScreenshotData(Convert.ToBase64String(screenshotBuffer))
                .WithIsValid(splits.Count == PlayerManager.Instance.currentMaster.racePoints)
                .WithGameVersion($"{PlayerManager.Instance.version.version}.{PlayerManager.Instance.version.patch}")
                .WithModVersion(MyPluginInfo.PLUGIN_VERSION);
        });

        if (result.IsFailed)
        {
            PlayerManager.Instance.messenger.LogError("[GTR] Unable to submit record", 2.5f);
            Logger.LogError(result.ToString());
        }
        else if (Plugin.ConfigShowRecordSetMessage.Value)
        {
            PlayerManager.Instance.messenger.Log("[GTR] Record submitted", 2.5f);
        }
    }
}
