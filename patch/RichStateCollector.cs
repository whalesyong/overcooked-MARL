using HarmonyLib;
using Hpmv;
using OrderController;
using SuperchargedPatch.Extensions;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Team17.Online.Multiplayer.Messaging;
using UnityEngine;

namespace SuperchargedPatch
{
    internal static class RichStateCollector
    {
        private static readonly FieldInfo WorkstationInteractersField = AccessTools.Field(typeof(ServerWorkstation), "m_interacters");
        private static readonly FieldInfo WorkstationItemField = AccessTools.Field(typeof(ServerWorkstation), "m_item");
        private static readonly Type WorkstationInteracterType = AccessTools.Inner(typeof(ServerWorkstation), "Interacter");
        private static readonly FieldInfo WorkstationInteracterObjectField = AccessTools.Field(WorkstationInteracterType, "m_object");
        private static readonly FieldInfo WorkstationInteracterActionTimerField = AccessTools.Field(WorkstationInteracterType, "m_actionTimer");

        private static readonly FieldInfo PlayerCarrierObjectsField = AccessTools.Field(typeof(ServerPlayerAttachmentCarrier), "m_carriedObjects");
        private static readonly FieldInfo AttachStationItemField = AccessTools.Field(typeof(ServerAttachStation), "m_item");

        private static readonly FieldInfo WorkableItemOnWorkstationField = AccessTools.Field(typeof(ServerWorkableItem), "m_onWorkstation");
        private static readonly FieldInfo WorkableItemProgressField = AccessTools.Field(typeof(ServerWorkableItem), "m_progress");
        private static readonly FieldInfo WorkableItemSubProgressField = AccessTools.Field(typeof(ServerWorkableItem), "m_subProgress");

        private static readonly FieldInfo ThrowableFlyingField = AccessTools.Field(typeof(ServerThrowableItem), "m_flying");
        private static readonly FieldInfo ThrowableFlightTimerField = AccessTools.Field(typeof(ServerThrowableItem), "m_flightTimer");
        private static readonly FieldInfo ThrowableThrowerField = AccessTools.Field(typeof(ServerThrowableItem), "m_thrower");
        private static readonly FieldInfo ThrowableThrowStartCollidersField = AccessTools.Field(typeof(ServerThrowableItem), "m_ThrowStartColliders");

        private static readonly FieldInfo SessionInteractableSessionField = AccessTools.Field(typeof(ServerSessionInteractable), "m_session");

        private static readonly FieldInfo PilotRotationAngleField = AccessTools.Field(typeof(ServerPilotRotation), "m_angle");
        private static readonly FieldInfo PilotRotationStartAngleField = AccessTools.Field(typeof(ServerPilotRotation), "m_startAngle");

        private static readonly FieldInfo PickupItemSwitcherIndexField = AccessTools.Field(typeof(ServerPickupItemSwitcher), "m_currentItemPrefabIndex");
        private static readonly FieldInfo PlacementItemSwitcherIndexField = AccessTools.Field(typeof(ServerPlacementItemSwitcher), "m_currentItemPrefabIndex");
        private static readonly FieldInfo TriggerColourCycleIndexField = AccessTools.Field(typeof(ServerTriggerColourCycle), "m_currentColourIndex");
        private static readonly FieldInfo CookingStationIsTurnedOnField = AccessTools.Field(typeof(ServerCookingStation), "m_isTurnedOn");
        private static readonly FieldInfo CookingStationIsCookingField = AccessTools.Field(typeof(ServerCookingStation), "m_isCooking");
        private static readonly FieldInfo WashingStationPlateCountField = AccessTools.Field(typeof(ServerWashingStation), "m_plateCount");
        private static readonly FieldInfo WashingStationCleaningTimerField = AccessTools.Field(typeof(ServerWashingStation), "m_cleaningTimer");
        private static readonly FieldInfo PlateReturnStationStackField = AccessTools.Field(typeof(ServerPlateReturnStation), "m_stack");

        private static readonly FieldInfo PlateReturnControllerPlatesField = AccessTools.Field(typeof(PlateReturnController), "m_platesToReturn");
        private static readonly Type PlatesPendingReturnType = AccessTools.Inner(typeof(PlateReturnController), "PlatesPendingReturn");
        private static readonly Type PlatesPendingReturnListType = typeof(FastList<>).MakeGenericType(PlatesPendingReturnType);
        private static readonly PropertyInfo PlatesPendingReturnCountProperty = AccessTools.Property(PlatesPendingReturnListType, "Count");
        private static readonly FieldInfo PlatesPendingReturnItemsField = AccessTools.Field(PlatesPendingReturnListType, "_items");
        private static readonly FieldInfo PlatesPendingReturnStationField = AccessTools.Field(PlatesPendingReturnType, "m_station");
        private static readonly FieldInfo PlatesPendingReturnTimerField = AccessTools.Field(PlatesPendingReturnType, "m_timer");
        private static readonly FieldInfo PlatesPendingReturnPlatingStepField = AccessTools.Field(PlatesPendingReturnType, "m_platingStepData");

        private static readonly FieldInfo TeamMonitorScoreField = AccessTools.Field(typeof(ServerTeamMonitor), "m_score");
        private static readonly FieldInfo OrderControllerNextOrderIdField = AccessTools.Field(typeof(ServerOrderControllerBase), "m_nextOrderID");
        private static readonly FieldInfo OrderControllerActiveOrdersField = AccessTools.Field(typeof(ServerOrderControllerBase), "m_activeOrders");
        private static readonly FieldInfo OrderControllerTimerUntilOrderField = AccessTools.Field(typeof(ServerOrderControllerBase), "m_timerUntilOrder");
        private static readonly FieldInfo OrderControllerComboIndexField = AccessTools.Field(typeof(ServerOrderControllerBase), "m_comboIndex");
        private static readonly MethodInfo OrderControllerGetNextTimeBetweenOrdersMethod = AccessTools.Method(typeof(ServerOrderControllerBase), "GetNextTimeBetweenOrders");

        public static void CollectDataForFrame(OutputData currentFrameData)
        {
            var entities = new List<EntityWarpSpec>();
            var entityList = EntitySerialisationRegistry.m_EntitiesList;
            if (entityList == null)
            {
                return;
            }

            for (int i = 0; i < entityList.Count; i++)
            {
                var entry = entityList._items[i];
                if (entry == null || entry.m_GameObject == null)
                {
                    continue;
                }

                var entityState = BuildEntityState(entry, currentFrameData);
                if (entityState != null)
                {
                    entities.Add(entityState);
                }
            }

            if (entities.Count > 0)
            {
                currentFrameData.EntityState = entities;
            }
        }

        private static EntityWarpSpec BuildEntityState(EntitySerialisationEntry entry, OutputData currentFrameData)
        {
            var gameObject = entry.m_GameObject;
            var entityId = (int)entry.m_Header.m_uEntityID;
            var thrift = new EntityWarpSpec
            {
                EntityId = entityId,
            };
            thrift.__isset.entityId = true;

            var marker = gameObject.GetComponent<EntityPathReferenceMarker>();
            if (marker != null)
            {
                thrift.EntityPathReference = marker.EntityPath?.ToThrift();
            }

            bool hasSpecificState = false;

            if (currentFrameData.Chefs != null && currentFrameData.Chefs.ContainsKey(entityId))
            {
                thrift.Chef = currentFrameData.Chefs[entityId];
                hasSpecificState = true;
            }

            if (gameObject.GetComponent<ServerPlayerAttachmentCarrier>() is ServerPlayerAttachmentCarrier carrier)
            {
                var chefCarry = BuildChefCarry(carrier);
                if (chefCarry != null)
                {
                    thrift.ChefCarry = chefCarry;
                    hasSpecificState = true;
                }
            }

            if (gameObject.GetComponent<ServerAttachStation>() is ServerAttachStation attachStation)
            {
                thrift.AttachStation = new AttachStationWarpData
                {
                    Item = ToEntityIdOrRef(AttachStationItemField.GetValue(attachStation)),
                };
                hasSpecificState = true;
            }

            if (gameObject.GetComponent<ServerIngredientContainer>() is ServerIngredientContainer ingredientContainer)
            {
                var serialisable = ingredientContainer.GetServerUpdate();
                thrift.IngredientContainer = new IngredientContainerWarpData
                {
                    MsgData = serialisable != null ? serialisable.ToBytes() : null,
                };
                hasSpecificState = true;
            }

            if (gameObject.GetComponent<ServerWorkstation>() is ServerWorkstation workstation)
            {
                thrift.Workstation = BuildWorkstation(workstation);
                hasSpecificState = true;
            }

            if (gameObject.GetComponent<ServerWorkableItem>() is ServerWorkableItem workableItem)
            {
                thrift.WorkableItem = new WorkableItemWarpData
                {
                    Progress = SafeInt(WorkableItemProgressField.GetValue(workableItem)),
                    SubProgress = SafeInt(WorkableItemSubProgressField.GetValue(workableItem)),
                    OnWorkstation = SafeBool(WorkableItemOnWorkstationField.GetValue(workableItem)),
                };
                hasSpecificState = true;
            }

            if (gameObject.GetComponent<ServerThrowableItem>() is ServerThrowableItem throwableItem)
            {
                thrift.ThrowableItem = BuildThrowableItem(throwableItem);
                hasSpecificState = true;
            }

            if (gameObject.GetComponent<ServerTerminal>() is ServerTerminal terminal)
            {
                var terminalState = BuildTerminal(terminal);
                if (terminalState != null)
                {
                    thrift.Terminal = terminalState;
                    hasSpecificState = true;
                }
            }

            if (gameObject.GetComponent<ServerPilotRotation>() is ServerPilotRotation pilotRotation)
            {
                thrift.PilotRotation = new PilotRotationWarpData
                {
                    Angle = SafeFloat(PilotRotationAngleField.GetValue(pilotRotation)) + SafeFloat(PilotRotationStartAngleField.GetValue(pilotRotation)),
                };
                hasSpecificState = true;
            }

            if (gameObject.GetComponent<ServerMixingHandler>() is ServerMixingHandler mixingHandler)
            {
                var progress = TryReadProgress(mixingHandler.GetServerUpdate(), "m_progress", "m_mixingProgress");
                if (progress.HasValue)
                {
                    thrift.MixingHandler = new MixingHandlerWarpData { Progress = progress.Value };
                    hasSpecificState = true;
                }
            }

            if (gameObject.GetComponent<ServerCookingHandler>() is ServerCookingHandler cookingHandler)
            {
                var progress = TryReadProgress(cookingHandler.GetServerUpdate(), "m_progress", "m_cookingProgress");
                if (progress.HasValue)
                {
                    thrift.CookingHandler = new CookingHandlerWarpData { Progress = progress.Value };
                    hasSpecificState = true;
                }
            }

            if (gameObject.GetComponent<ServerPickupItemSwitcher>() is ServerPickupItemSwitcher pickupSwitcher)
            {
                thrift.PickupItemSwitcher = new PickupItemSwitcherWarpData
                {
                    Index = SafeInt(PickupItemSwitcherIndexField.GetValue(pickupSwitcher)),
                };
                hasSpecificState = true;
            }
            else if (gameObject.GetComponent<ServerPlacementItemSwitcher>() is ServerPlacementItemSwitcher placementSwitcher)
            {
                thrift.PickupItemSwitcher = new PickupItemSwitcherWarpData
                {
                    Index = SafeInt(PlacementItemSwitcherIndexField.GetValue(placementSwitcher)),
                };
                hasSpecificState = true;
            }

            if (gameObject.GetComponent<ServerTriggerColourCycle>() is ServerTriggerColourCycle colourCycle)
            {
                thrift.TriggerColourCycle = new TriggerColourCycleWarpData
                {
                    Index = SafeInt(TriggerColourCycleIndexField.GetValue(colourCycle)),
                };
                hasSpecificState = true;
            }

            if (gameObject.GetComponent<ServerPlateStackBase>() is ServerPlateStackBase plateStack)
            {
                var stack = BuildStack(plateStack);
                if (stack != null)
                {
                    thrift.Stack = stack;
                    hasSpecificState = true;
                }
            }

            if (gameObject.GetComponent<ServerKitchenFlowControllerBase>() is ServerKitchenFlowControllerBase kitchenFlow)
            {
                thrift.PlateReturnController = BuildPlateReturnController(kitchenFlow.GetPlateReturnController());
                thrift.KitchenController = BuildKitchenController(kitchenFlow);
                hasSpecificState = thrift.PlateReturnController != null || thrift.KitchenController != null;
            }

            if (gameObject.GetComponent<ServerWashingStation>() is ServerWashingStation washingStation)
            {
                thrift.WashingStation = new WashingStationWarpData
                {
                    PlateCount = SafeInt(WashingStationPlateCountField.GetValue(washingStation)),
                    Progress = SafeFloat(WashingStationCleaningTimerField.GetValue(washingStation)),
                };
                hasSpecificState = true;
            }

            if (gameObject.GetComponent<ServerCookingStation>() is ServerCookingStation cookingStation)
            {
                thrift.CookingStation = new CookingStationWarpData
                {
                    IsTurnedOn = SafeBool(CookingStationIsTurnedOnField.GetValue(cookingStation)),
                    IsCooking = SafeBool(CookingStationIsCookingField.GetValue(cookingStation)),
                };
                hasSpecificState = true;
            }

            if (gameObject.GetComponent<ServerPlateReturnStation>() is ServerPlateReturnStation returnStation)
            {
                thrift.PlateReturnStation = new PlateReturnStationWarpData
                {
                    Stack = ToEntityIdOrRef(PlateReturnStationStackField.GetValue(returnStation)),
                };
                hasSpecificState = true;
            }

            if (!hasSpecificState)
            {
                return null;
            }

            PopulatePhysics(thrift, gameObject);
            return thrift;
        }

        private static void PopulatePhysics(EntityWarpSpec thrift, GameObject gameObject)
        {
            var transform = gameObject.transform;
            Vector3 position = transform.position;
            UnityEngine.Quaternion rotation = transform.rotation;
            Vector3 velocity = Vector3.zero;
            Vector3 angularVelocity = Vector3.zero;
            if (gameObject.GetPhysicsContainerIfExists() is Rigidbody container)
            {
                position = container.transform.position;
                rotation = container.transform.rotation;
                velocity = container.velocity;
                angularVelocity = container.angularVelocity;
            }
            else if (gameObject.GetComponent<Rigidbody>() is Rigidbody rb)
            {
                velocity = rb.velocity;
                angularVelocity = rb.angularVelocity;
            }

            thrift.Position = position.ToThrift();
            thrift.Rotation = rotation.ToThrift();
            thrift.Velocity = velocity.ToThrift();
            thrift.AngularVelocity = angularVelocity.ToThrift();
        }

        private static ChefCarryWarpData BuildChefCarry(ServerPlayerAttachmentCarrier carrier)
        {
            var carriedObjects = PlayerCarrierObjectsField.GetValue(carrier) as Array;
            if (carriedObjects == null || carriedObjects.Length == 0)
            {
                return new ChefCarryWarpData();
            }

            return new ChefCarryWarpData
            {
                CarriedItem = ToEntityIdOrRef(carriedObjects.GetValue(0)),
            };
        }

        private static WorkstationWarpData BuildWorkstation(ServerWorkstation workstation)
        {
            var thrift = new WorkstationWarpData
            {
                Item = ToEntityIdOrRef(WorkstationItemField.GetValue(workstation)),
            };

            var interacters = WorkstationInteractersField.GetValue(workstation) as IList;
            if (interacters != null && interacters.Count > 0)
            {
                thrift.Interacters = new List<WorkstationInteracterWarpData>();
                for (int i = 0; i < interacters.Count; i++)
                {
                    var interacter = interacters[i];
                    var interacterObject = WorkstationInteracterObjectField.GetValue(interacter);
                    var interacterRef = ToEntityIdOrRef(interacterObject);
                    if (interacterRef == null || !interacterRef.__isset.entityId)
                    {
                        continue;
                    }

                    thrift.Interacters.Add(new WorkstationInteracterWarpData
                    {
                        ChefEntityId = interacterRef.EntityId,
                        ActionTimer = SafeFloat(WorkstationInteracterActionTimerField.GetValue(interacter)),
                    });
                }
            }

            return thrift;
        }

        private static ThrowableItemWarpData BuildThrowableItem(ServerThrowableItem throwableItem)
        {
            var thrift = new ThrowableItemWarpData
            {
                IsFlying = SafeBool(ThrowableFlyingField.GetValue(throwableItem)),
                FlightTimer = SafeFloat(ThrowableFlightTimerField.GetValue(throwableItem)),
                ThrowerEntityId = -1,
                ThrowStartColliders = new List<ColliderRef>(),
            };

            var throwerRef = ToEntityIdOrRef(ThrowableThrowerField.GetValue(throwableItem));
            if (throwerRef != null && throwerRef.__isset.entityId)
            {
                thrift.ThrowerEntityId = throwerRef.EntityId;
            }

            var colliders = ThrowableThrowStartCollidersField.GetValue(throwableItem) as Collider[];
            if (colliders != null)
            {
                for (int i = 0; i < colliders.Length; i++)
                {
                    var collider = colliders[i];
                    if (collider == null)
                    {
                        continue;
                    }

                    var colliderOwner = ToEntityIdOrRef(collider.gameObject);
                    if (colliderOwner == null)
                    {
                        continue;
                    }

                    var siblings = collider.gameObject.GetComponents<Collider>();
                    int colliderIndex = Array.IndexOf(siblings, collider);
                    thrift.ThrowStartColliders.Add(new ColliderRef
                    {
                        Entity = colliderOwner,
                        ColliderIndex = colliderIndex,
                    });
                }
            }

            return thrift;
        }

        private static TerminalWarpData BuildTerminal(ServerTerminal terminal)
        {
            var session = SessionInteractableSessionField.GetValue(terminal);
            if (session == null)
            {
                return new TerminalWarpData();
            }

            var interacter = TryExtractObjectField(session, "m_interacter", "m_interactor", "m_object");
            var interacterRef = ToEntityIdOrRef(interacter);
            if (interacterRef == null || !interacterRef.__isset.entityId)
            {
                return new TerminalWarpData();
            }

            return new TerminalWarpData
            {
                InteracterEntityId = interacterRef.EntityId,
            };
        }

        private static StackWarpData BuildStack(ServerPlateStackBase plateStack)
        {
            var serverStack = TryExtractObjectField(plateStack, "m_stack");
            if (serverStack == null)
            {
                return null;
            }

            var stackItems = TryExtractEnumerable(serverStack, "_items", "m_items", "m_stack", "m_contents");
            if (stackItems == null)
            {
                return null;
            }

            var contents = new List<EntityIdOrRef>();
            foreach (var item in stackItems)
            {
                var entityRef = ToEntityIdOrRef(item);
                if (entityRef != null)
                {
                    contents.Add(entityRef);
                }
            }

            return new StackWarpData
            {
                StackContents = contents,
            };
        }

        private static PlateReturnControllerWarpData BuildPlateReturnController(PlateReturnController controller)
        {
            if (controller == null)
            {
                return null;
            }

            var platesToReturn = PlateReturnControllerPlatesField.GetValue(controller);
            if (platesToReturn == null)
            {
                return new PlateReturnControllerWarpData();
            }

            int count = SafeInt(PlatesPendingReturnCountProperty.GetValue(platesToReturn, null));
            var itemsArray = PlatesPendingReturnItemsField.GetValue(platesToReturn) as Array;
            var thrift = new PlateReturnControllerWarpData
            {
                Plates = new List<PlatePendingReturnData>(),
            };
            if (itemsArray == null)
            {
                return thrift;
            }

            for (int i = 0; i < count && i < itemsArray.Length; i++)
            {
                var plate = itemsArray.GetValue(i);
                if (plate == null)
                {
                    continue;
                }

                var stationRef = ToEntityIdOrRef(PlatesPendingReturnStationField.GetValue(plate));
                if (stationRef == null || !stationRef.__isset.entityId)
                {
                    continue;
                }

                thrift.Plates.Add(new PlatePendingReturnData
                {
                    ReturnStationEntityId = stationRef.EntityId,
                    Timer = SafeFloat(PlatesPendingReturnTimerField.GetValue(plate)),
                });
            }

            return thrift;
        }

        private static KitchenControllerWarpData BuildKitchenController(ServerKitchenFlowControllerBase kitchenFlow)
        {
            var teamMonitor = kitchenFlow.GetMonitorForTeam(TeamID.One);
            if (teamMonitor == null)
            {
                return null;
            }

            var ordersController = teamMonitor.OrdersController;
            if (ordersController == null)
            {
                return null;
            }

            var activeOrders = new List<byte[]>();
            var activeOrderValues = OrderControllerActiveOrdersField.GetValue(ordersController) as IEnumerable;
            if (activeOrderValues != null)
            {
                foreach (var order in activeOrderValues)
                {
                    if (order is Serialisable serialisable)
                    {
                        activeOrders.Add(serialisable.ToBytes());
                    }
                }
            }

            var score = TeamMonitorScoreField.GetValue(teamMonitor) as TeamMonitor.TeamScoreStats;
            double roundTime = 0;
            if (kitchenFlow.m_roundTimer() is ServerRoundTimer roundTimer)
            {
                roundTime = roundTimer.GetRoundTimer();
            }

            double timeSinceLastOrder = 0;
            if (OrderControllerGetNextTimeBetweenOrdersMethod != null)
            {
                var nextTimeBetweenOrders = SafeFloat(OrderControllerGetNextTimeBetweenOrdersMethod.Invoke(ordersController, null));
                var timerUntilOrder = SafeFloat(OrderControllerTimerUntilOrderField.GetValue(ordersController));
                timeSinceLastOrder = nextTimeBetweenOrders - timerUntilOrder;
            }

            return new KitchenControllerWarpData
            {
                RoundTime = roundTime,
                ActiveOrders = activeOrders,
                NextOrderId = SafeInt(OrderControllerNextOrderIdField.GetValue(ordersController)) - 1,
                LastComboIndex = SafeInt(OrderControllerComboIndexField.GetValue(ordersController)),
                TimeSinceLastOrder = timeSinceLastOrder,
                TeamScore = score != null ? score.ToBytes() : null,
            };
        }

        private static double? TryReadProgress(object message, params string[] candidateFields)
        {
            if (message == null)
            {
                return null;
            }

            for (int i = 0; i < candidateFields.Length; i++)
            {
                var field = AccessTools.Field(message.GetType(), candidateFields[i]);
                if (field == null)
                {
                    continue;
                }

                return SafeFloat(field.GetValue(message));
            }

            return null;
        }

        private static IEnumerable TryExtractEnumerable(object obj, params string[] candidateFields)
        {
            if (obj == null)
            {
                return null;
            }

            for (int i = 0; i < candidateFields.Length; i++)
            {
                var field = AccessTools.Field(obj.GetType(), candidateFields[i]);
                if (field == null)
                {
                    continue;
                }

                var value = field.GetValue(obj);
                if (value is Array array)
                {
                    return array;
                }
                if (value is IEnumerable enumerable)
                {
                    return enumerable;
                }
            }

            return null;
        }

        private static object TryExtractObjectField(object obj, params string[] candidateFields)
        {
            if (obj == null)
            {
                return null;
            }

            for (int i = 0; i < candidateFields.Length; i++)
            {
                var field = AccessTools.Field(obj.GetType(), candidateFields[i]);
                if (field != null)
                {
                    return field.GetValue(obj);
                }
            }

            return null;
        }

        private static EntityIdOrRef ToEntityIdOrRef(object obj)
        {
            if (obj == null)
            {
                return null;
            }

            GameObject gameObject = null;
            if (obj is GameObject go)
            {
                gameObject = go;
            }
            else if (obj is Component component)
            {
                gameObject = component.gameObject;
            }
            else if (obj is EntitySerialisationEntry entry)
            {
                return new EntityIdOrRef { EntityId = (int)entry.m_Header.m_uEntityID };
            }

            if (gameObject == null)
            {
                return null;
            }

            var entryForObject = EntitySerialisationRegistry.GetEntry(gameObject);
            if (entryForObject != null)
            {
                return new EntityIdOrRef { EntityId = (int)entryForObject.m_Header.m_uEntityID };
            }

            if (gameObject.GetComponent<EntityPathReferenceMarker>() is EntityPathReferenceMarker marker)
            {
                return new EntityIdOrRef
                {
                    EntityPathReference = marker.EntityPath?.ToThrift(),
                };
            }

            return null;
        }

        private static bool SafeBool(object value)
        {
            return value is bool b && b;
        }

        private static int SafeInt(object value)
        {
            if (value == null)
            {
                return 0;
            }
            if (value is int i)
            {
                return i;
            }
            if (value is uint ui)
            {
                return (int)ui;
            }
            if (value.GetType().IsEnum)
            {
                return Convert.ToInt32(value);
            }
            return Convert.ToInt32(value);
        }

        private static float SafeFloat(object value)
        {
            if (value == null)
            {
                return 0f;
            }
            if (value is float f)
            {
                return f;
            }
            if (value is double d)
            {
                return (float)d;
            }
            return Convert.ToSingle(value);
        }
    }
}
