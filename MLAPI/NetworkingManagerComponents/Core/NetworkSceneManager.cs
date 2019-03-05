using System.Collections.Generic;
using System;
using MLAPI.Data;
using MLAPI.Internal;
using MLAPI.Logging;
using MLAPI.Serialization;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MLAPI.Components
{
    /// <summary>
    /// Main class for managing network scenes
    /// </summary>
    public static class NetworkSceneManager
    {
        internal static readonly HashSet<string> registeredSceneNames = new HashSet<string>();
        internal static readonly Dictionary<string, uint> sceneNameToIndex = new Dictionary<string, uint>();
        internal static readonly Dictionary<uint, string> sceneIndexToString = new Dictionary<uint, string>();
        internal static Dictionary<Guid, SceneSwitchProgress> sceneSwitchProgresses = new Dictionary<Guid, SceneSwitchProgress>();
        private static Scene lastScene;
        private static Scene nextScene;
        private static bool isSwitching = false;
        internal static uint currentSceneIndex = 0;
        internal static Guid currentSceneSwitchProgressGuid = new Guid();

        internal static void SetCurrentSceneIndex()
        {
            if (!sceneNameToIndex.ContainsKey(SceneManager.GetActiveScene().name))
            {
                if (LogHelper.CurrentLogLevel <= LogLevel.Normal) LogHelper.LogWarning("The current scene (" + SceneManager.GetActiveScene().name + ") is not regisered as a network scene.");
                return;
            }
            currentSceneIndex = sceneNameToIndex[SceneManager.GetActiveScene().name];
            CurrentActiveSceneIndex = currentSceneIndex;
        }

        internal static uint CurrentActiveSceneIndex { get; private set; } = 0;

        /// <summary>
        /// Switches to a scene with a given name. Can only be called from Server
        /// </summary>
        /// <param name="sceneName">The name of the scene to switch to</param>
        public static SceneSwitchProgress SwitchScene(string sceneName)
        {
            if (isSwitching)
            {
                if (LogHelper.CurrentLogLevel <= LogLevel.Normal) LogHelper.LogWarning("Scene switch already in progress");
                return null;
            }
            else if(!registeredSceneNames.Contains(sceneName))
            {
                if (LogHelper.CurrentLogLevel <= LogLevel.Normal) LogHelper.LogWarning("The scene " + sceneName + " is not registered as a switchable scene.");
                return null;
            }
            
            SpawnManager.ServerDestroySpawnedSceneObjects(); //Destroy current scene objects before switching.
            currentSceneIndex = sceneNameToIndex[sceneName];
            isSwitching = true;
            lastScene = SceneManager.GetActiveScene();

            SceneSwitchProgress switchSceneProgress = new SceneSwitchProgress();
            sceneSwitchProgresses.Add(switchSceneProgress.guid, switchSceneProgress);
            currentSceneSwitchProgressGuid = switchSceneProgress.guid;

            AsyncOperation sceneLoad = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Additive);
            nextScene = SceneManager.GetSceneByName(sceneName);
            sceneLoad.completed += (AsyncOperation AsyncOp) => { OnSceneLoaded(AsyncOp, switchSceneProgress.guid, null); };

            switchSceneProgress.SetSceneLoadOperation(sceneLoad);

            if (NetworkingManager.Singleton.NetworkConfig.UsePrefabSync)
            {
                using (PooledBitStream stream = PooledBitStream.Get())
                {
                    using (PooledBitWriter writer = PooledBitWriter.Get(stream))
                    {
                        writer.WriteUInt32Packed(sceneNameToIndex[sceneName]);
                        writer.WriteByteArray(switchSceneProgress.guid.ToByteArray());
                        
                        InternalMessageHandler.Send(MLAPIConstants.MLAPI_SWITCH_SCENE, "MLAPI_INTERNAL", stream, SecuritySendFlags.None, null);
                    }
                }
            }
            
            return switchSceneProgress;
        }

        /// <summary>
        /// Called on client
        /// </summary>
        /// <param name="sceneIndex"></param>
        /// <param name="switchSceneGuid"></param>
        internal static void OnSceneSwitch(uint sceneIndex, Guid switchSceneGuid, Dictionary<ulong, ulong> instanceIdNetworkIdSoftSyncLookup)
        {
            if (!sceneIndexToString.ContainsKey(sceneIndex) || !registeredSceneNames.Contains(sceneIndexToString[sceneIndex]))
            {
                if (LogHelper.CurrentLogLevel <= LogLevel.Normal) LogHelper.LogWarning("Server requested a scene switch to a non registered scene");
                return;
            }
            else if (SceneManager.GetActiveScene().name == sceneIndexToString[sceneIndex])
            {
                return; //This scene is already loaded. This usually happends at first load
            }

            //  This has been commented out as it shouldn't be needed. The server send messages about removed
            //  objects, the client should not need to determine by itself what objects to remove.
            //SpawnManager.DestroySceneObjects();
            lastScene = SceneManager.GetActiveScene();

            string sceneName = sceneIndexToString[sceneIndex];
            AsyncOperation sceneLoad = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Additive);
            nextScene = SceneManager.GetSceneByName(sceneName);
            sceneLoad.completed += (AsyncOperation operation) => { OnSceneLoaded(operation, switchSceneGuid, instanceIdNetworkIdSoftSyncLookup); };
        }

        private static void OnSceneLoaded(AsyncOperation operation, Guid switchSceneGuid, Dictionary<ulong, ulong> instanceIdNetworkIdSoftSyncLookup)
        {
            CurrentActiveSceneIndex = sceneNameToIndex[nextScene.name];
            SceneManager.SetActiveScene(nextScene);
            
            List<NetworkedObject> objectsToKeep = SpawnManager.SpawnedObjectsList;
            
            for (int i = 0; i < objectsToKeep.Count; i++)
            {
                //In case an object has been set as a child of another object it has to be unchilded in order to be moved from one scene to another.
                if (objectsToKeep[i].gameObject.transform.parent != null)
                {
                    objectsToKeep[i].gameObject.transform.parent = null;
                }
                
                SceneManager.MoveGameObjectToScene(objectsToKeep[i].gameObject, nextScene);
            }

            AsyncOperation sceneUnload = SceneManager.UnloadSceneAsync(lastScene);
            sceneUnload.completed += (AsyncOperation AsyncOp) =>
            {
                if (NetworkingManager.Singleton.IsServer)
                {
                    OnSceneUnloadServer(AsyncOp, switchSceneGuid);
                }
                else
                {
                    OnSceneUnloadClient(AsyncOp, switchSceneGuid, instanceIdNetworkIdSoftSyncLookup);
                }
            };
        }

        private static void OnSceneUnloadServer(AsyncOperation operation, Guid switchSceneGuid)
        {
            if (NetworkingManager.Singleton.NetworkConfig.UsePrefabSync)
            {
                SpawnManager.ServerSpawnNewSceneObjectsSweep();
            }
            else
            {
                NetworkedObject[] networkedObjects = MonoBehaviour.FindObjectsOfType<NetworkedObject>();

                using (PooledBitStream stream = PooledBitStream.Get())
                {
                    using (PooledBitWriter writer = PooledBitWriter.Get(stream))
                    {
                        writer.WriteUInt32Packed(currentSceneIndex);
                        writer.WriteByteArray(switchSceneGuid.ToByteArray());

                        uint newObjectsCount = 0;
                            
                        for (int i = 0; i < networkedObjects.Length; i++)
                        {
                            if (networkedObjects[i].IsSceneObject == null)
                                newObjectsCount++;
                        }
                            
                        writer.WriteUInt32Packed(newObjectsCount);

                        for (int i = 0; i < networkedObjects.Length; i++)
                        {
                            if (networkedObjects[i].IsSceneObject == null)
                            {
                                SpawnManager.SpawnNetworkedObjectLocally(networkedObjects[i], SpawnManager.GetNetworkObjectId(), true, false, NetworkingManager.Singleton.ServerClientId, null, false, 0, false);
                                    
                                writer.WriteUInt64Packed(networkedObjects[i].PrefabInstanceId);
                                writer.WriteUInt64Packed(networkedObjects[i].NetworkId);
                            }
                        }

                        InternalMessageHandler.Send(MLAPIConstants.MLAPI_SWITCH_SCENE, "MLAPI_INTERNAL", stream, SecuritySendFlags.None, null);
                    }
                }
            }

            //Tell server that scene load is completed
            if (NetworkingManager.Singleton.IsHost)
            {
                OnClientSwitchSceneCompleted(NetworkingManager.Singleton.LocalClientId, switchSceneGuid);
            }

            isSwitching = false;
        }

        private static void OnSceneUnloadClient(AsyncOperation operation, Guid switchSceneGuid, Dictionary<ulong, ulong> instanceIdNetworkIdSoftSyncLookup)
        {
            if (NetworkingManager.Singleton.NetworkConfig.UsePrefabSync)
            {
                NetworkedObject[] networkedObjects = MonoBehaviour.FindObjectsOfType<NetworkedObject>();

                for (int i = 0; i < networkedObjects.Length; i++)
                {
                    if (networkedObjects[i].IsSceneObject == null)
                    {
                        if (SpawnManager.customDestroyHandlers.ContainsKey(networkedObjects[i].PrefabHash))
                        {
                            SpawnManager.customDestroyHandlers[networkedObjects[i].PrefabHash](networkedObjects[i]);
                            SpawnManager.OnDestroyObject(networkedObjects[i].NetworkId, false);
                        }
                        else
                        {
                            MonoBehaviour.Destroy(networkedObjects[i].gameObject);
                        }
                    }
                }
            }
            else
            {
                NetworkedObject[] networkedObjects = MonoBehaviour.FindObjectsOfType<NetworkedObject>();

                for (int i = 0; i < networkedObjects.Length; i++)
                {
                    if (networkedObjects[i].IsSceneObject == null)
                    {
                        if (instanceIdNetworkIdSoftSyncLookup.ContainsKey(networkedObjects[i].PrefabInstanceId))
                        {
                            SpawnManager.SpawnNetworkedObjectLocally(networkedObjects[i], instanceIdNetworkIdSoftSyncLookup[networkedObjects[i].PrefabInstanceId], true, false, NetworkingManager.Singleton.ServerClientId, null, false, 0, false);
                        }
                    }
                }
            }
            
            using (PooledBitStream stream = PooledBitStream.Get())
            {
                using (PooledBitWriter writer = PooledBitWriter.Get(stream))
                {
                    writer.WriteByteArray(switchSceneGuid.ToByteArray());
                    InternalMessageHandler.Send(NetworkingManager.Singleton.ServerClientId, MLAPIConstants.MLAPI_CLIENT_SWITCH_SCENE_COMPLETED, "MLAPI_INTERNAL", stream, SecuritySendFlags.None, null);
                }
            }
            
            isSwitching = false;
        }

        internal static bool HasSceneMismatch(uint sceneIndex)
        {
            return SceneManager.GetActiveScene().name != sceneIndexToString[sceneIndex];
        }

        /// <summary>
        /// Called on server
        /// </summary>
        /// <param name="clientId"></param>
        /// <param name="switchSceneGuid"></param>
        internal static void OnClientSwitchSceneCompleted(uint clientId, Guid switchSceneGuid) 
        {
            if (switchSceneGuid == Guid.Empty) 
            {
                //If Guid is empty it means the client has loaded the start scene of the server and the server would never have a switchSceneProgresses created for the start scene.
                return;
            }
            if (!sceneSwitchProgresses.ContainsKey(switchSceneGuid)) 
            {
                return;
            }

            sceneSwitchProgresses[switchSceneGuid].AddClientAsDone(clientId);
        }


        internal static void RemoveClientFromSceneSwitchProgresses(uint clientId) 
        {
            foreach (SceneSwitchProgress switchSceneProgress in sceneSwitchProgresses.Values)
                switchSceneProgress.RemoveClientAsDone(clientId);
        }
    }
}
