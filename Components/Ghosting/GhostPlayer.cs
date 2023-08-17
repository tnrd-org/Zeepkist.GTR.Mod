﻿using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using TMPro;
using TNRD.Zeepkist.GTR.Cysharp.Threading.Tasks;
using TNRD.Zeepkist.GTR.DTOs.ResponseModels;
using TNRD.Zeepkist.GTR.Mod.Components.Ghosting.Readers;
using TNRD.Zeepkist.GTR.Mod.Patches;
using UnityEngine;
using UnityEngine.Rendering;
using ZeepkistNetworking;
using ZeepSDK.Cosmetics;

namespace TNRD.Zeepkist.GTR.Mod.Components.Ghosting;

public class GhostPlayer : MonoBehaviourWithLogging
{
    private IGhostReader ghostReader;

    private int SoapboxId => ghostReader.SoapboxId;
    private int HatId => ghostReader.HatId;
    private int ColorId => ghostReader.ColorId;

    private bool hasSetMaterials;

    private bool downloadedGhost;
    private FrameData frameData;
    private Transform tr;

    private static float Ticker =>
        PlayerManager.Instance.currentMaster.PlayersReady.FirstOrDefault()?.ticker.what_ticker ?? 0;

    private SetupModelCar ghostModel;
    private SetupModelCar cameraManModel;
    private FadeGhostModel ghostFader;
    private DisplayPlayerName nameDisplay;

    protected override void Awake()
    {
        base.Awake();
        tr = transform;
        frameData = new FrameData();

        Plugin.ConfigShowGhosts.SettingChanged += OnShowGhostsChanged;
        Plugin.ConfigShowGhostNames.SettingChanged += OnShowNamesChanged;
    }

    private void OnDestroy()
    {
        Plugin.ConfigShowGhosts.SettingChanged -= OnShowGhostsChanged;
        Plugin.ConfigShowGhostNames.SettingChanged -= OnShowNamesChanged;
    }

    private void OnShowGhostsChanged(object sender, EventArgs e)
    {
        ghostModel.gameObject.SetActive(Plugin.ConfigShowGhosts.Value);
    }

    private void OnShowNamesChanged(object sender, EventArgs e)
    {
        nameDisplay.gameObject.SetActive(Plugin.ConfigShowGhostNames.Value);
    }

    public void Initialize(
        NetworkedZeepkistGhost original,
        string name,
        Color? color,
        RecordResponseModel recordModel
    )
    {
        GetGhost(recordModel).Forget();

        ghostModel = original.ghostModel;
        cameraManModel = original.cameraManModel;
        ghostFader = original.ghostFader;
        nameDisplay = original.nameDisplay;

        original.ghostFader.gameObject.SetActive(true);
        original.ghostFader.enabled = false;
        original.nameDisplay.DoSetup(name, recordModel.User.SteamId);
        if (color.HasValue)
            original.nameDisplay.theDisplayName.color = color.Value;

        original.isDead = false;
        original.isPhotoMode = false;
        original.frontLeftDead = false;
        original.frontRightDead = false;
        original.rearLeftDead = false;
        original.rearRightDead = false;
        original.armsUp = false;
        original.brake = false;
        original.steer = 0;
    }

    private async UniTaskVoid GetGhost(RecordResponseModel record)
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Zeepkist",
            "GTR",
            "Ghosts");

        if (!Directory.Exists(folder))
            Directory.CreateDirectory(folder);

        string filename = Path.Combine(folder, $"{record.Id}.bin");

        byte[] buffer;

        if (File.Exists(filename))
        {
            this.Logger().LogInfo($"Reading ghost from disk: {filename}");
            buffer = await File.ReadAllBytesAsync(filename);
        }
        else
        {
            this.Logger().LogInfo($"Download ghost: {record.GhostUrl}");
            using (HttpClient client = new HttpClient())
            {
                HttpResponseMessage response = await client.GetAsync(record.GhostUrl);
                if (response.IsSuccessStatusCode)
                {
                    buffer = await response.Content.ReadAsByteArrayAsync();
                    await File.WriteAllBytesAsync(filename, buffer);
                }
                else
                {
                    Logger.LogError($"Unable to download ghost: {response.StatusCode}");
                    return;
                }
            }
        }

        ProcessGhost(buffer);
    }

    private void ProcessGhost(byte[] buffer)
    {
        Logger.LogInfo("Processing ghost");
        ghostReader = ReaderRepository.GetReader(buffer);
        ghostReader.Read(buffer);

        // nzg.UpdateGhostModel();
        CosmeticWardrobe wardrobe = PlayerManager.Instance.objectsList.wardrobe;

        // nzg.zeepkistID
        ghostModel.DoCarSetup(
            CosmeticsApi.GetSoapbox(SoapboxId,false),
            CosmeticsApi.GetHat(HatId,false),
            CosmeticsApi.GetColor(ColorId,false),
            true);
        cameraManModel.DoCarSetup(null,
            CosmeticsApi.GetHat(HatId,false),
            CosmeticsApi.GetColor(ColorId,false),
            false);
        ghostFader.DoSetup();

        ghostModel.gameObject.SetActive(Plugin.ConfigShowGhosts.Value);
        nameDisplay.gameObject.SetActive(Plugin.ConfigShowGhostNames.Value);

        // CleanGameObject(gameObject);

        downloadedGhost = true;
    }

    private static void CleanGameObject(GameObject obj)
    {
        Component[] objectComponents = obj.GetComponents(typeof(Component));
        Component[] childComponents = obj.GetComponentsInChildren(typeof(Component));
        objectComponents = objectComponents.Concat(childComponents).ToArray();

        foreach (Component c in objectComponents)
        {
            if (c == null)
                continue;

            bool keep = c is Transform or MeshFilter or MeshRenderer or GhostPlayer or FadeGhostModel;

            if (!keep)
            {
                Destroy(c);
            }
        }
    }

    private void Update()
    {
        if (!downloadedGhost)
            return;

        ghostReader.GetFrameData(Ticker, ref frameData);
        if (frameData != null)
        {
            tr.position = frameData.Position;
            tr.rotation = frameData.Rotation;
        }

        try
        {
            UpdateGhostVisuals();
        }
        catch (ArgumentOutOfRangeException)
        {
            // Ignoring on purpose
        }
    }

    private void UpdateGhostVisuals()
    {
        const float minDistance = 2.5f;
        const float maxDistance = 8f;
        const float maxAlpha = 0.25f;

        float playerDist = PlayerManager.Instance.currentMaster.isPhotoMode
            ? 1000
            : Vector3.Distance(transform.position,
                PlayerManager.Instance.currentMaster.carSetups[0].transform.position);

        if (playerDist < 3f)
        {
            DisableRenderers();
            return;
        }

        EnableRenderers();

        float fadeAmount = Mathf.Lerp(0f, maxAlpha, Mathf.InverseLerp(minDistance, maxDistance, playerDist));

        nameDisplay.theDisplayName.color = nameDisplay.theDisplayName.color with
        {
            a = Mathf.InverseLerp(minDistance, maxDistance, playerDist)
        };

        float r = 1;
        float g = 1;
        float b = 1;

        if (PlayerManager.Instance.objectsList.wardrobe.everyColor.ContainsKey(ColorId))
        {
            r = ((CosmeticColor)PlayerManager.Instance.objectsList.wardrobe.everyColor[ColorId]).skinColor.color.r;
            g = ((CosmeticColor)PlayerManager.Instance.objectsList.wardrobe.everyColor[ColorId]).skinColor.color.g;
            b = ((CosmeticColor)PlayerManager.Instance.objectsList.wardrobe.everyColor[ColorId]).skinColor.color.b;
        }

        if (!hasSetMaterials)
        {
            ghostFader.localMaterial = new Material(Shader.Find("Standard"));
            ToFadeMode(ghostFader.localMaterial);
        }

        ghostFader.localMaterial.color = new Color(r, g, b, fadeAmount);

        if (hasSetMaterials)
            return;

        foreach (Renderer renderer in ghostFader.modelRenderers)
        {
            Material[] materials = renderer.sharedMaterials;

            for (int i = 0; i < materials.Length; i++)
            {
                materials[i] = ghostFader.localMaterial;
            }

            renderer.sharedMaterials = materials;
        }

        hasSetMaterials = true;
    }

    private static void ToFadeMode(Material material)
    {
        material.SetOverrideTag("RenderType", "Transparent");
        material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
        material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
        material.SetInt("_ZWrite", 0);
        material.DisableKeyword("_ALPHATEST_ON");
        material.EnableKeyword("_ALPHABLEND_ON");
        material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        material.renderQueue = (int)RenderQueue.Transparent;
    }

    private void DisableRenderers()
    {
        foreach (Renderer renderer in ghostFader.modelRenderers)
        {
            renderer.enabled = false;
        }

        nameDisplay.theDisplayName.color = nameDisplay.theDisplayName.color with
        {
            a = 0
        };
    }

    private void EnableRenderers()
    {
        foreach (Renderer renderer in ghostFader.modelRenderers)
        {
            renderer.enabled = true;
        }
    }
}
