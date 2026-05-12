using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Hpmv;
using SuperchargedPatch.Extensions;
using Team17.Online.Multiplayer.Messaging;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SuperchargedPatch
{
    public static class ControllerHandler
    {
        private const int FullSnapshotIntervalFrames = 300;
        private const float IdlePollIntervalSeconds = 0.5f;
        private const int MissingKitchenFlowEndGraceSteps = 30;
        private const int MissingKitchenFlowEndConsecutiveFrames = 3;
        public static MultiplayerController MultiplayerController;
        private static int lateUpdateCount;
        private static int multiplayerDiscoverTicks;
        private static bool loggedRegistryFieldSnapshot;
        private static int simulationFrameNumber;
        private static int episodeId;
        private static int episodeStep;
        private static bool inEpisode;
        private static bool pendingEpisodeStart;
        private static bool pendingEpisodeEnd;
        private static string pendingEpisodeEndReason;
        private static string currentGameState = "Unknown";
        private static string currentLevelName;
        private static int framesSinceFullSnapshot;
        private static int missingKitchenFlowFrames;
        private static float nextIdlePollRealtime;

        public static void LateUpdate()
        {
            // Fallback discovery in case the MultiplayerController.Update patch fires late
            // or misses a transition.
            if (MultiplayerController == null)
            {
                multiplayerDiscoverTicks += 1;
                if (multiplayerDiscoverTicks % 60 == 0)
                {
                    var discovered = UnityEngine.Object.FindObjectOfType(typeof(MultiplayerController)) as MultiplayerController;
                    if (discovered != null)
                    {
                        MultiplayerController = discovered;
                        Console.WriteLine("Bridge summary: MultiplayerController discovered via FindObjectOfType fallback");
                    }
                }
            }

            // Make sure to flush any network messages before capturing state. Normally this
            // is done as part of MultiplayerController.LateUpdate(), but we're also executing in
            // LateUpdate so the order is not deterministic.
            MultiplayerController?.FlushAllPendingBatchedMessages();

            var sendEpisodeFrame = pendingEpisodeStart || inEpisode;
            var sendLifecycleFrame = pendingEpisodeEnd;
            var sendIdlePoll = !sendEpisodeFrame && !sendLifecycleFrame && ShouldSendIdlePoll();

            if (!sendEpisodeFrame && !sendLifecycleFrame && !sendIdlePoll)
            {
                Injector.Server.DiscardCurrentFrame();
                return;
            }

            var data = Injector.Server.CurrentFrameData;
            if (!sendEpisodeFrame)
            {
                PopulateIdlePollFrame(data);
                Injector.Server.CommitFrame();
                return;
            }

            var input = Injector.Server.CurrentInput;
            if (input.__isset.nextFrame)
            {
                ActiveStateCollector.NotifyFrame(input.NextFrame);
                simulationFrameNumber = input.NextFrame;
            }
            var forceFullSnapshot = ShouldForceFullSnapshot();
            if (forceFullSnapshot)
            {
                ActiveStateCollector.ClearCacheForFullSnapshot();
                data.FullSnapshot = true;
                data.EntityRegistry = BuildEntityRegistrySnapshot();
            }

            ActiveStateCollector.CollectDataForFrame(data);
            RichStateCollector.CollectDataForFrame(data);
            if (!input.__isset.nextFrame)
            {
                simulationFrameNumber += Math.Max(data.PhysicsFramesElapsed, 0);
            }
            data.FrameNumber = simulationFrameNumber;
            data.LastFramePaused = Helpers.IsPaused();
            data.NextFramePaused = TimeManager.IsPaused(TimeManager.PauseLayer.Main);
            var hasKitchenFlow = CaptureRoundTimer(data);
            DetectImplicitEpisodeEnd(hasKitchenFlow);
            AttachEpisodeMetadata(data);

            lateUpdateCount += 1;
            if (lateUpdateCount % 120 == 0)
            {
                var entityCount = EntitySerialisationRegistry.m_EntitiesList?.Count ?? -1;
                var itemCount = data.Items?.Count ?? 0;
                var chefCount = data.Chefs?.Count ?? 0;
                var registryCount = data.EntityRegistry?.Count ?? 0;
                var messageCount = data.ServerMessages?.Count ?? 0;
                Console.WriteLine(
                    $"Bridge summary: tick={lateUpdateCount} entities={entityCount} items={itemCount} chefs={chefCount} registry={registryCount} messages={messageCount} multiplayerCached={MultiplayerController != null}");

                if (entityCount == 0 && !loggedRegistryFieldSnapshot)
                {
                    loggedRegistryFieldSnapshot = true;
                    LogRegistryFieldSnapshot();
                }
            }

            Injector.Server.CommitFrame();
        }

        public static void FixedUpdate()
        {
            Injector.Server.CurrentFrameData.PhysicsFramesElapsed += 1;
        }

        public static void Update()
        {
            if (Injector.Server.CurrentFrameData.PhysicsFramesElapsed == 0)
            {
                FramesSinceLastNoPhysicsFrame = 0;
            }
            else
            {
                FramesSinceLastNoPhysicsFrame += 1;
            }
            Injector.Server.CurrentFrameData.FramesSinceLastNoPhysicsFrame = FramesSinceLastNoPhysicsFrame;
        }

        public static int FramesSinceLastNoPhysicsFrame = 0;

        public static bool ShouldUseLiveBridgeInput()
        {
            return pendingEpisodeStart || inEpisode;
        }

        public static void NotifyGameState(GameState state)
        {
            currentGameState = state.ToString();
            currentLevelName = GetCurrentLevelName();
            if (state == GameState.InLevel)
            {
                if (inEpisode || pendingEpisodeStart)
                {
                    return;
                }
                StartEpisode();
                return;
            }

            if (inEpisode || pendingEpisodeStart)
            {
                EndEpisode("game_state_" + currentGameState);
            }
        }

        public static void NotifyApplicationQuit()
        {
            if (!inEpisode && !pendingEpisodeStart)
            {
                return;
            }

            var data = Injector.Server.CurrentFrameData;
            data.FrameNumber = simulationFrameNumber;
            data.LastFramePaused = Helpers.IsPaused();
            data.NextFramePaused = TimeManager.IsPaused(TimeManager.PauseLayer.Main);
            data.EpisodeId = episodeId;
            data.EpisodeStep = episodeStep;
            data.InEpisode = false;
            data.EpisodeStart = false;
            data.EpisodeEnd = true;
            data.EpisodeEndReason = "application_quit";
            data.GameState = currentGameState;
            data.LevelName = currentLevelName ?? GetCurrentLevelName();
            CaptureRoundTimer(data);

            inEpisode = false;
            pendingEpisodeStart = false;
            pendingEpisodeEnd = false;
            pendingEpisodeEndReason = null;
            Console.WriteLine("Bridge episode end: id=" + episodeId + " reason=application_quit");
            Injector.Server.SendCurrentFrameNowWithoutInputWait();
        }

        public static void RecordButtonInput(int playerEntityId, TASLogicalButtonType type, bool? down = null, bool? justPressed = null, bool? justReleased = null)
        {
            var inputData = EnsureObservedInput(playerEntityId);
            var button = EnsureButtonInput(inputData, type);
            if (down.HasValue)
            {
                button.Down = down.Value;
            }
            if (justPressed.HasValue)
            {
                button.JustPressed = justPressed.Value;
            }
            if (justReleased.HasValue)
            {
                button.JustReleased = justReleased.Value;
            }
        }

        public static void RecordMovementInput(int playerEntityId, TASLogicalValueType type, float value)
        {
            var inputData = EnsureObservedInput(playerEntityId);
            if (inputData.Pad == null)
            {
                inputData.Pad = new PadDirection();
            }

            if (type == TASLogicalValueType.MovementX)
            {
                inputData.Pad.X = value;
            }
            else if (type == TASLogicalValueType.MovementY)
            {
                inputData.Pad.Y = value;
            }
        }

        private static void StartEpisode()
        {
            episodeId += 1;
            episodeStep = 0;
            simulationFrameNumber = 0;
            inEpisode = true;
            pendingEpisodeStart = true;
            pendingEpisodeEnd = false;
            pendingEpisodeEndReason = null;
            framesSinceFullSnapshot = FullSnapshotIntervalFrames;
            missingKitchenFlowFrames = 0;
            nextIdlePollRealtime = 0f;
            currentLevelName = GetCurrentLevelName();
            ActiveStateCollector.ClearCacheForFullSnapshot();
            Console.WriteLine("Bridge episode start: id=" + episodeId + " level=" + currentLevelName);
        }

        private static void EndEpisode(string reason)
        {
            pendingEpisodeEnd = true;
            pendingEpisodeEndReason = reason;
            inEpisode = false;
            missingKitchenFlowFrames = 0;
            nextIdlePollRealtime = Time.realtimeSinceStartup + IdlePollIntervalSeconds;
            Console.WriteLine("Bridge episode end: id=" + episodeId + " reason=" + reason);
        }

        private static bool ShouldForceFullSnapshot()
        {
            if (pendingEpisodeStart)
            {
                return true;
            }

            if (!inEpisode)
            {
                return false;
            }

            framesSinceFullSnapshot += Math.Max(Injector.Server.CurrentFrameData.PhysicsFramesElapsed, 0);
            if (framesSinceFullSnapshot >= FullSnapshotIntervalFrames)
            {
                framesSinceFullSnapshot = 0;
                return true;
            }

            return false;
        }

        private static void AttachEpisodeMetadata(OutputData data)
        {
            data.EpisodeId = episodeId;
            data.EpisodeStep = inEpisode ? episodeStep : Math.Max(episodeStep, 0);
            data.InEpisode = inEpisode;
            data.EpisodeStart = pendingEpisodeStart;
            data.EpisodeEnd = pendingEpisodeEnd;
            data.GameState = currentGameState;
            data.LevelName = currentLevelName ?? GetCurrentLevelName();
            if (pendingEpisodeEndReason != null)
            {
                data.EpisodeEndReason = pendingEpisodeEndReason;
            }

            if (inEpisode)
            {
                episodeStep += 1;
            }

            pendingEpisodeStart = false;
            pendingEpisodeEnd = false;
            pendingEpisodeEndReason = null;
        }

        private static void PopulateIdlePollFrame(OutputData data)
        {
            data.FrameNumber = simulationFrameNumber;
            data.LastFramePaused = Helpers.IsPaused();
            data.NextFramePaused = TimeManager.IsPaused(TimeManager.PauseLayer.Main);
            data.GameState = currentGameState;
            data.LevelName = currentLevelName ?? GetCurrentLevelName();
            AttachEpisodeMetadata(data);
        }

        private static bool ShouldSendIdlePoll()
        {
            var realtime = Time.realtimeSinceStartup;
            if (realtime < nextIdlePollRealtime)
            {
                return false;
            }

            nextIdlePollRealtime = realtime + IdlePollIntervalSeconds;
            return true;
        }

        private static OneInputData EnsureObservedInput(int playerEntityId)
        {
            var data = Injector.Server.CurrentFrameData;
            if (data.ObservedInput == null)
            {
                data.ObservedInput = new Dictionary<int, OneInputData>();
            }
            if (!data.ObservedInput.ContainsKey(playerEntityId))
            {
                data.ObservedInput[playerEntityId] = new OneInputData();
            }
            return data.ObservedInput[playerEntityId];
        }

        private static ButtonInput EnsureButtonInput(OneInputData inputData, TASLogicalButtonType type)
        {
            switch (type)
            {
                case TASLogicalButtonType.Pickup:
                    if (inputData.Pickup == null)
                    {
                        inputData.Pickup = new ButtonInput();
                    }
                    return inputData.Pickup;
                case TASLogicalButtonType.Use:
                    if (inputData.Interact == null)
                    {
                        inputData.Interact = new ButtonInput();
                    }
                    return inputData.Interact;
                case TASLogicalButtonType.Dash:
                    if (inputData.Dash == null)
                    {
                        inputData.Dash = new ButtonInput();
                    }
                    return inputData.Dash;
                default:
                    throw new ArgumentOutOfRangeException("type");
            }
        }

        private static string GetCurrentLevelName()
        {
            try
            {
                return SceneManager.GetActiveScene().name ?? "unknown";
            }
            catch
            {
                return "unknown";
            }
        }

        private static List<EntityRegistryData> BuildEntityRegistrySnapshot()
        {
            var snapshot = new List<EntityRegistryData>();
            var entitiesList = EntitySerialisationRegistry.m_EntitiesList;
            if (entitiesList == null)
            {
                return snapshot;
            }

            for (int i = 0; i < entitiesList.Count; i++)
            {
                var entry = entitiesList._items[i];
                if (entry == null || entry.m_GameObject == null)
                {
                    continue;
                }

                var gameObject = entry.m_GameObject;
                var datum = new EntityRegistryData()
                {
                    EntityId = (int)entry.m_Header.m_uEntityID,
                    Name = gameObject.name,
                    Pos = gameObject.transform.position.ToThrift(),
                    SyncEntityTypes = new List<int>(),
                    Components = new List<string>(),
                    SpawnNames = new List<string>(),
                };

                foreach (var component in gameObject.GetComponents<Component>())
                {
                    if (component != null)
                    {
                        datum.Components.Add(component.GetType().Name);
                    }
                }

                var spawnableCollection = gameObject.GetComponent<SpawnableEntityCollection>();
                if (spawnableCollection != null)
                {
                    foreach (var spawnable in spawnableCollection.GetSpawnables())
                    {
                        if (spawnable != null)
                        {
                            datum.SpawnNames.Add(spawnable.name);
                        }
                    }
                }

                for (int j = 0; j < entry.m_ServerSynchronisedComponents.Count; j++)
                {
                    datum.SyncEntityTypes.Add((int)entry.m_ServerSynchronisedComponents._items[j].GetEntityType());
                }

                snapshot.Add(datum);
            }

            return snapshot;
        }

        private static void DetectImplicitEpisodeEnd(bool hasKitchenFlow)
        {
            if (!inEpisode)
            {
                missingKitchenFlowFrames = 0;
                return;
            }

            if (hasKitchenFlow)
            {
                missingKitchenFlowFrames = 0;
                return;
            }

            if (episodeStep < MissingKitchenFlowEndGraceSteps)
            {
                return;
            }

            missingKitchenFlowFrames += 1;
            if (missingKitchenFlowFrames >= MissingKitchenFlowEndConsecutiveFrames)
            {
                EndEpisode("kitchen_flow_missing");
            }
        }

        private static bool CaptureRoundTimer(OutputData data)
        {
            var kitchenFlowController = ServerKitchenFlowControllerBaseExt.FindController();
            if (kitchenFlowController == null)
            {
                return false;
            }

            if (!(kitchenFlowController.m_roundTimer() is ServerRoundTimer roundTimer))
            {
                return false;
            }

            data.RoundTimeSeconds = roundTimer.GetRoundTimer();
            data.RoundTimeRemainingSeconds = roundTimer.GetTimeLeftSeconds();
            return true;
        }

        private static void LogRegistryFieldSnapshot()
        {
            try
            {
                Console.WriteLine("Bridge diag: EntitySerialisationRegistry static field snapshot begin");
                var fields = typeof(EntitySerialisationRegistry).GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                foreach (var field in fields)
                {
                    var value = field.GetValue(null);
                    if (value == null)
                    {
                        continue;
                    }

                    if (value is IDictionary dictionary)
                    {
                        Console.WriteLine($"Bridge diag: {field.Name} ({field.FieldType.Name}) dictCount={dictionary.Count}");
                        continue;
                    }

                    if (value is IEnumerable enumerable && !(value is string))
                    {
                        int count = -1;
                        if (value is ICollection collection)
                        {
                            count = collection.Count;
                        }
                        else
                        {
                            int iterCount = 0;
                            foreach (var _ in enumerable)
                            {
                                iterCount += 1;
                                if (iterCount > 2000)
                                {
                                    break;
                                }
                            }
                            count = iterCount;
                        }
                        Console.WriteLine($"Bridge diag: {field.Name} ({field.FieldType.Name}) enumCount={count}");
                    }
                }
                Console.WriteLine("Bridge diag: EntitySerialisationRegistry static field snapshot end");

                foreach (var field in fields)
                {
                    var value = field.GetValue(null);
                    if (value == null || value is string || value is IEnumerable)
                    {
                        continue;
                    }
                    LogObjectFieldSnapshot(value, "static." + field.Name);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("Bridge diag failed: " + e.Message);
            }
        }

        private static void LogObjectFieldSnapshot(object instance, string prefix)
        {
            try
            {
                Console.WriteLine("Bridge diag: " + prefix + " snapshot begin (" + instance.GetType().FullName + ")");
                var fields = instance.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                foreach (var field in fields)
                {
                    var value = field.GetValue(instance);
                    if (value == null)
                    {
                        continue;
                    }

                    if (value is IDictionary dictionary)
                    {
                        Console.WriteLine($"Bridge diag: {prefix}.{field.Name} ({field.FieldType.Name}) dictCount={dictionary.Count}");
                        continue;
                    }

                    if (value is IEnumerable enumerable && !(value is string))
                    {
                        int count = -1;
                        if (value is ICollection collection)
                        {
                            count = collection.Count;
                        }
                        else
                        {
                            int iterCount = 0;
                            foreach (var _ in enumerable)
                            {
                                iterCount += 1;
                                if (iterCount > 2000)
                                {
                                    break;
                                }
                            }
                            count = iterCount;
                        }
                        Console.WriteLine($"Bridge diag: {prefix}.{field.Name} ({field.FieldType.Name}) enumCount={count}");
                    }
                }
                Console.WriteLine("Bridge diag: " + prefix + " snapshot end");
            }
            catch (Exception e)
            {
                Console.WriteLine("Bridge diag " + prefix + " failed: " + e.Message);
            }
        }
    }

    [HarmonyPatch(typeof(MultiplayerController), "Update")]
    public static class MultiplayerControllerAwakePatch
    {
        public static void Postfix(MultiplayerController __instance)
        {
            // Do this, because FindObjectOfType is very very slow.
            ControllerHandler.MultiplayerController = __instance;
            Console.WriteLine("Bridge summary: MultiplayerController cached");
        }
    }

    [HarmonyPatch(typeof(ClientFlowControllerBase), "RunLevelIntro")]
    public static class PatchClientFlowControllerBaseRunLevelIntro
    {
        [HarmonyPostfix]
        public static void Postfix(ref IEnumerator __result)
        {
            __result = WaitForNoPhysicsFrame(__result);
        }

        private static IEnumerator WaitForNoPhysicsFrame(IEnumerator then)
        {
            for (int i = 0; ; i++)
            {
                yield return null;
                if (Injector.Server.CurrentFrameData.PhysicsFramesElapsed == 0)
                {
                    Console.WriteLine("Successfully started with non-physics frame after " + i + " tries");
                    break;
                }
                if (i == 6)
                {
                    Console.WriteLine("Warning: FAILED TO WAIT FOR NO PHYSICS FRAME");
                    break;
                }
            }
            while (then.MoveNext())
            {
                yield return then.Current;
            }
            yield break;
        }
    }

    [HarmonyPatch(typeof(ServerFlowControllerBase), "ChangeGameState")]
    public static class PatchServerFlowControllerBaseChangeGameState
    {
        [HarmonyPostfix]
        public static void Postfix(GameState state)
        {
            ControllerHandler.NotifyGameState(state);
            if (state == GameState.InLevel)
            {
                Console.WriteLine("At game start, physics phase shift is " + ControllerHandler.FramesSinceLastNoPhysicsFrame);
            }
        }
    }
}
