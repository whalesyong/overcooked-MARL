using BepInEx;
using HarmonyLib;
using Hpmv;
using System;
using System.Reflection;
using System.Threading;
using UnityEngine;

namespace SuperchargedPatch
{
    [BepInPlugin("dev.hpmv.overcooked.experimental.supercharged.tas.v1", "Hpmv Overcooked Supercharged TAS Plugin V1", "1.0.0.0")]
//    [BepInProcess("Overcooked2.exe")]
    class TASPatcher : BaseUnityPlugin
    {
        private const string HarmonyId = "dev.hpmv.overcooked.experimental.supercharged.tas.v1";
        private static Harmony patcher;
        private static bool isQuitting;
        private static bool hasShutDown;

        public void Awake()
        {
            Console.WriteLine("TASPatcher.Awake called");
            DontDestroyOnLoad(gameObject);

            if (patcher == null)
            {
                patcher = new Harmony(HarmonyId);
                patcher.PatchAll(Assembly.GetExecutingAssembly());
                foreach (var patched in Harmony.GetAllPatchedMethods())
                {
                    Console.WriteLine("Patched: " + patched.FullDescription());
                }
            }

            BridgeUpdateDriver.EnsureCreated();
            var injector = Injector.Server;
            Console.WriteLine("Injector server initialized");
        }

        public void OnApplicationQuit()
        {
            isQuitting = true;
            Shutdown("OnApplicationQuit");
        }

        public void OnDestroy()
        {
            Console.WriteLine("TASPatcher.OnDestroy called (isQuitting=" + isQuitting + ")");
            if (isQuitting || Environment.HasShutdownStarted)
            {
                Shutdown("OnDestroy");
                return;
            }

            // We occasionally see this fire during scene transitions while the game continues
            // running. Keep bridge infrastructure alive and ensure we still have a frame driver.
            BridgeUpdateDriver.EnsureCreated();
        }

        private static void Shutdown(string source)
        {
            if (hasShutDown)
            {
                return;
            }

            hasShutDown = true;
            Console.WriteLine("TASPatcher.Shutdown from " + source);
            if (source == "OnApplicationQuit" || source == "OnDestroy")
            {
                try
                {
                    ControllerHandler.NotifyApplicationQuit();
                    Thread.Sleep(150);
                }
                catch (Exception e)
                {
                    Console.WriteLine("Failed to emit application quit episode end: " + e.Message);
                }
            }

            if (patcher != null)
            {
                patcher.UnpatchSelf();
                patcher = null;
            }
            Injector.Destroy();
        }
    }

    public class BridgeUpdateDriver : MonoBehaviour
    {
        private static BridgeUpdateDriver instance;
        private const string DriverName = "HpmvBridgeUpdateDriver";

        public static void EnsureCreated()
        {
            if (instance != null)
            {
                return;
            }

            var existing = FindObjectOfType(typeof(BridgeUpdateDriver)) as BridgeUpdateDriver;
            if (existing != null)
            {
                instance = existing;
                DontDestroyOnLoad(instance.gameObject);
                return;
            }

            var go = new GameObject(DriverName);
            DontDestroyOnLoad(go);
            instance = go.AddComponent<BridgeUpdateDriver>();
            Console.WriteLine("BridgeUpdateDriver created");
        }

        public void OnDestroy()
        {
            if (instance == this)
            {
                instance = null;
            }
            Console.WriteLine("BridgeUpdateDriver.OnDestroy called");
        }

        public void FixedUpdate()
        {
            ControllerHandler.FixedUpdate();
        }

        public void Update()
        {
            ControllerHandler.Update();
        }

        public void LateUpdate()
        {
            ControllerHandler.LateUpdate();
        }
    }
}
