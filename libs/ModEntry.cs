using MelonLoader;
using HarmonyLib;
using System.Collections;
using System.Reflection;

[assembly: MelonInfo(
    typeof(KogamaOfflinePatch.KogamaOfflinePatch),
    "KogamaOfflinePatch",
    "2.0.7",
    "Marshal")]

[assembly: MelonGame("Multiverse ApS", "KoGaMa")]

namespace KogamaOfflinePatch
{
    public class KogamaOfflinePatch : MelonMod
    {

        private bool _il2cppPatchesApplied = false;
        private bool _freeCamInitialized = false;
        private int _attemptCount = 0;
        private const int MaxPatchAttempts = 30;

        public override void OnInitializeMelon()
        {
            MelonLogger.Msg("Kogama Offline Patch v0.1.0");
            MelonLogger.Msg("Photon redirect (Well...Umm, yeah) 127.0.0.1:5055 (local Photon Server)");
            MelonLogger.Msg("Map loader (Well yeah) http://127.0.0.1:8080");
            MelonLogger.Msg("IL2CPP patches will be applied on first scene load...");
            System.AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoaded;
        }
        private void OnAssemblyLoaded(object sender, System.AssemblyLoadEventArgs args)
        {
            try
            {
                var asmName = args.LoadedAssembly.GetName().Name ?? "";
                if (asmName.IndexOf("Assembly-CSharp", System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    MelonLogger.Msg($"[KogamaOfflinePatch] {asmName} loaded applying Assembly-CSharp patches NOW (before any Awake).");
                    RegionConfigPatch.TryApply(HarmonyInstance);
                    BypassMVGameControllerInit.Apply(HarmonyInstance);
                }
            }
            catch { }
        }
        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            _cams = null;
            if (sceneName != "DesktopBase")
            {
                BypassMVGameControllerInit._startGameInvoked = false;
            }
            MelonLogger.Msg($"[KogamaOfflinePatch] OnSceneWasLoaded fired: '{sceneName}' (buildIndex={buildIndex}, patchesApplied={_il2cppPatchesApplied})");

            if (_il2cppPatchesApplied) return;
            
            if (_attemptCount >= MaxPatchAttempts)
            {
                if (_attemptCount == MaxPatchAttempts)
                {
                    MelonLogger.Error($"[KogamaOfflinePatch] Gave up after {MaxPatchAttempts} scene loads IL2CPP types still missing.");
                    _attemptCount++;
                }
                return;
            }

            _attemptCount++;
            MelonLogger.Msg($"[KogamaOfflinePatch] Scene loaded: '{sceneName}' (buildIndex={buildIndex}, attempt {_attemptCount}/{MaxPatchAttempts})");

            System.Type peerType =
                FindTypeInAnyAssembly("Il2CppExitGames.Client.Photon.PhotonPeer") ??
                FindTypeInAnyAssembly("ExitGames.Client.Photon.PhotonPeer");

            if (peerType == null)
            {
                MelonLogger.Msg($"[KogamaOfflinePatch] PhotonPeer still not registered will retry on next scene.");
                return;
            }
            MelonLogger.Msg($"[KogamaOfflinePatch] PhotonPeer found: {peerType.FullName}");

            RegionConfigPatch.TryApply(HarmonyInstance);

            BypassMVGameControllerInit.Apply(HarmonyInstance);

            MelonLogger.Msg("[KogamaOfflinePatch] PhotonPeer type is registered applying patches...");

            PhotonRedirect.Apply(HarmonyInstance);

            PhotonInProcessStub.Apply(HarmonyInstance);

            MapLoaderPatch.Apply(HarmonyInstance);

            UnityWebRequestSpy.Apply(HarmonyInstance);

            _il2cppPatchesApplied = true;

            MelonLogger.Msg("[KogamaOfflinePatch] Patches applied.");
        }
        private int _rcTickCounter = 0;
        private bool _freeCamEnabled = false;
        private UnityEngine.Camera _freeCam;
        private float _freeCamSpeed = 10f;

        private bool _startGameScheduled = false;

        private UnityEngine.GameObject _spectatorCam;

        public override void OnUpdate()
        {
            if (_il2cppPatchesApplied && !BypassMVGameControllerInit._startGameInvoked && !_startGameScheduled)
            {
                var desktopType = BypassMVGameControllerInit.FindTypeInAnyAssembly("Il2Cpp.MVGameControllerDesktop") ?? BypassMVGameControllerInit.FindTypeInAnyAssembly("MVGameControllerDesktop");
                if (desktopType != null)
                {
                    var instProp = desktopType.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (instProp != null)
                    {
                        var inst = instProp.GetValue(null, null);
                        if (inst != null)
                        {
                            MelonLogger.Msg("[KogamaOfflinePatch] OnUpdate detected StartGame() was missed. Scheduling invocation for next frame.");
                            _startGameScheduled = true;
                            MelonCoroutines.Start(DelayedForceStartGame(inst));
                        }
                    }
                }
            }
            if (_il2cppPatchesApplied && BypassMVGameControllerInit._cachedSpawnerPosition != UnityEngine.Vector3.zero)
            {
                try
                {
                    var controllerType = BypassMVGameControllerInit.FindTypeInAnyAssembly("Il2Cpp.MVGameControllerBase");
                    if (controllerType != null)
                    {
                        var localPlayerProp = controllerType.GetProperty("LocalPlayer", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                        if (localPlayerProp != null)
                        {
                            var localPlayer = localPlayerProp.GetValue(null, null);
                            if (localPlayer != null)
                            {
                                var posProp = localPlayer.GetType().GetProperty("Position");
                                if (posProp != null && posProp.CanWrite)
                                {
                                    posProp.SetValue(localPlayer, BypassMVGameControllerInit._cachedSpawnerPosition);
                                }
                            }
                        }
                    }
                }
                catch { }
                if (!_camLockStarted)
                {
                    _camLockStarted = true;
                    MelonCoroutines.Start(LockCameraToEndOfFrame());
                }
            }
            if (_il2cppPatchesApplied)
            {
                BypassMVGameControllerInit.DriveJoinStateForward();
            }
        }

        private bool _camLockStarted = false;

        private IEnumerator LockCameraToEndOfFrame()
        {
            while (true)
            {
                yield return new UnityEngine.WaitForEndOfFrame();
                
                var mainCam = UnityEngine.Camera.main;
                if (mainCam != null)
                {
                    var spawnerPos = BypassMVGameControllerInit._cachedSpawnerPosition;
                    if (spawnerPos != UnityEngine.Vector3.zero)
                    {
                        mainCam.transform.position = spawnerPos + new UnityEngine.Vector3(0, 20, 0);
                        mainCam.transform.rotation = UnityEngine.Quaternion.Euler(70, 0, 0);
                    }
                }
            }
        }

        private UnityEngine.Camera[] _cams;

private int _camSceneCheckTick = 0;

public override void OnLateUpdate()
{
    if (!_il2cppPatchesApplied) return;

    try
    {
        var controllerType = BypassMVGameControllerInit.FindTypeInAnyAssembly("Il2Cpp.MVGameControllerBase");
        if (controllerType != null)
        {
            var localPlayerProp = controllerType.GetProperty("LocalPlayer", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            object localPlayer = localPlayerProp?.GetValue(null, null);
            if (localPlayer != null) return; 
        }
    }
    catch { }

    var spawnerPos = BypassMVGameControllerInit._cachedSpawnerPosition;
    if (spawnerPos == UnityEngine.Vector3.zero) return;

    var mainCam = UnityEngine.Camera.main;
    if (mainCam == null) return;
    try
    {
        mainCam.transform.position = spawnerPos + new UnityEngine.Vector3(0, 3, -5);
        mainCam.transform.rotation = UnityEngine.Quaternion.Euler(15, 0, 0);
    }
    catch { }
}

        private System.Collections.IEnumerator DelayedForceStartGame(object instance)
        {
            yield return null;
            BypassMVGameControllerInit.ForceStartGame(instance);
        }
        private static System.Type FindTypeInAnyAssembly(string fullName)
        {
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                System.Type t = null;
                try { t = asm.GetType(fullName, throwOnError: false); }
                catch { }
                if (t != null) return t;
            }
            return null;
        }
    }
}