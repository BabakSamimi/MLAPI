using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MLAPI.Components;
#if !DISABLE_CRYPTOGRAPHY
using MLAPI.Cryptography;
#endif
using MLAPI.Data;
using MLAPI.Logging;
using MLAPI.Serialization;
using UnityEngine;

namespace MLAPI.Internal
{
    internal static partial class InternalMessageHandler
    {
#if !DISABLE_CRYPTOGRAPHY
        // Runs on client
        internal static void HandleHailRequest(uint clientId, Stream stream, int channelId)
        {
            X509Certificate2 certificate = null;
            byte[] serverDiffieHellmanPublicPart = null;
            using (PooledBitReader reader = PooledBitReader.Get(stream))
            {
                if (NetworkingManager.Singleton.NetworkConfig.EnableEncryption)
                {
                    // Read the certificate
                    if (NetworkingManager.Singleton.NetworkConfig.SignKeyExchange)
                    {
                        // Allocation justification: This runs on client and only once, at initial connection
                        certificate = new X509Certificate2(reader.ReadByteArray());
                        if (CryptographyHelper.VerifyCertificate(certificate, NetworkingManager.Singleton.ConnectedHostname))
                        {
                            // The certificate is not valid :(
                            // Man in the middle.
                            if (LogHelper.CurrentLogLevel <= LogLevel.Normal) if (LogHelper.CurrentLogLevel <= LogLevel.Normal) LogHelper.LogWarning("Invalid certificate. Disconnecting");
                            NetworkingManager.Singleton.StopClient();
                            return;
                        }
                        else
                        {
                            NetworkingManager.Singleton.NetworkConfig.ServerX509Certificate = certificate;
                        }
                    }

                    // Read the ECDH
                    // Allocation justification: This runs on client and only once, at initial connection
                    serverDiffieHellmanPublicPart = reader.ReadByteArray();
                    
                    // Verify the key exchange
                    if (NetworkingManager.Singleton.NetworkConfig.SignKeyExchange)
                    {
                        byte[] serverDiffieHellmanPublicPartSignature = reader.ReadByteArray();

                        RSACryptoServiceProvider rsa = certificate.PublicKey.Key as RSACryptoServiceProvider;

                        if (rsa != null)
                        {
                            using (SHA256Managed sha = new SHA256Managed())
                            {
                                if (!rsa.VerifyData(serverDiffieHellmanPublicPart, sha, serverDiffieHellmanPublicPartSignature))
                                {
                                    if (LogHelper.CurrentLogLevel <= LogLevel.Normal) if (LogHelper.CurrentLogLevel <= LogLevel.Normal) LogHelper.LogWarning("Invalid signature. Disconnecting");
                                    NetworkingManager.Singleton.StopClient();
                                    return;
                                }   
                            }
                        }
                    }
                }
            }

            using (PooledBitStream outStream = PooledBitStream.Get())
            {
                using (PooledBitWriter writer = PooledBitWriter.Get(outStream))
                {
                    if (NetworkingManager.Singleton.NetworkConfig.EnableEncryption)
                    {
                        // Create a ECDH key
                        EllipticDiffieHellman diffieHellman = new EllipticDiffieHellman(EllipticDiffieHellman.DEFAULT_CURVE, EllipticDiffieHellman.DEFAULT_GENERATOR, EllipticDiffieHellman.DEFAULT_ORDER);
                        NetworkingManager.Singleton.clientAesKey = diffieHellman.GetSharedSecret(serverDiffieHellmanPublicPart);
                        byte[] diffieHellmanPublicKey = diffieHellman.GetPublicKey();
                        writer.WriteByteArray(diffieHellmanPublicKey);
                        if (NetworkingManager.Singleton.NetworkConfig.SignKeyExchange)
                        {
                            RSACryptoServiceProvider rsa = certificate.PublicKey.Key as RSACryptoServiceProvider;

                            if (rsa != null)
                            {
                                using (SHA256CryptoServiceProvider sha = new SHA256CryptoServiceProvider())
                                {
                                    writer.WriteByteArray(rsa.Encrypt(sha.ComputeHash(diffieHellmanPublicKey), false));   
                                }
                            }
                            else
                            {
                                throw new CryptographicException("[MLAPI] Only RSA certificates are supported. No valid RSA key was found");
                            }
                        }
                    }
                }
                // Send HailResponse
                InternalMessageHandler.Send(NetworkingManager.Singleton.ServerClientId, MLAPIConstants.MLAPI_CERTIFICATE_HAIL_RESPONSE, "MLAPI_INTERNAL", outStream, SecuritySendFlags.None, null, true);
            }
        }

        // Ran on server
        internal static void HandleHailResponse(uint clientId, Stream stream, int channelId)
        {
            if (!NetworkingManager.Singleton.PendingClients.ContainsKey(clientId) || NetworkingManager.Singleton.PendingClients[clientId].ConnectionState != PendingClient.State.PendingHail) return;
            if (!NetworkingManager.Singleton.NetworkConfig.EnableEncryption) return;

            using (PooledBitReader reader = PooledBitReader.Get(stream))
            {
                if (NetworkingManager.Singleton.PendingClients[clientId].KeyExchange != null)
                {
                    byte[] diffieHellmanPublic = reader.ReadByteArray();
                    NetworkingManager.Singleton.PendingClients[clientId].AesKey = NetworkingManager.Singleton.PendingClients[clientId].KeyExchange.GetSharedSecret(diffieHellmanPublic);
                    if (NetworkingManager.Singleton.NetworkConfig.SignKeyExchange)
                    {
                        byte[] diffieHellmanPublicSignature = reader.ReadByteArray();
                        X509Certificate2 certificate = NetworkingManager.Singleton.NetworkConfig.ServerX509Certificate;
                        RSACryptoServiceProvider rsa = certificate.PrivateKey as RSACryptoServiceProvider;

                        if (rsa != null)
                        {
                            using (SHA256Managed sha = new SHA256Managed())
                            {
                                byte[] clientHash = rsa.Decrypt(diffieHellmanPublicSignature, false);
                                byte[] serverHash = sha.ComputeHash(diffieHellmanPublic);
                                
                                if (!CryptographyHelper.ConstTimeArrayEqual(clientHash, serverHash))
                                {
                                    //Man in the middle.
                                    if (LogHelper.CurrentLogLevel <= LogLevel.Normal) if (LogHelper.CurrentLogLevel <= LogLevel.Normal) LogHelper.LogWarning("Signature doesnt match for the key exchange public part. Disconnecting");
                                    NetworkingManager.Singleton.DisconnectClient(clientId);
                                    return;
                                }
                            }
                        }
                        else
                        {
                            throw new CryptographicException("[MLAPI] Only RSA certificates are supported. No valid RSA key was found");
                        }
                    }
                }
            }

            NetworkingManager.Singleton.PendingClients[clientId].ConnectionState = PendingClient.State.PendingConnection;
            NetworkingManager.Singleton.PendingClients[clientId].KeyExchange = null; // Give to GC
            
            // Send greetings, they have passed all the handshakes
            using (PooledBitStream outStream = PooledBitStream.Get())
            {
                using (PooledBitWriter writer = PooledBitWriter.Get(outStream))
                {
                    writer.WriteInt64Packed(DateTime.Now.Ticks); // This serves no purpose.
                }
                InternalMessageHandler.Send(clientId, MLAPIConstants.MLAPI_GREETINGS, "MLAPI_INTERNAL", outStream, SecuritySendFlags.None, null, true);
            }
        }

        internal static void HandleGreetings(uint clientId, Stream stream, int channelId)
        {
            // Server greeted us, we can now initiate our request to connect.
            NetworkingManager.Singleton.SendConnectionRequest();
        }
#endif

        internal static void HandleConnectionRequest(uint clientId, Stream stream, int channelId)
        {
            using (PooledBitReader reader = PooledBitReader.Get(stream))
            {
                ulong configHash = reader.ReadUInt64Packed();
                if (!NetworkingManager.Singleton.NetworkConfig.CompareConfig(configHash))
                {
                    if (LogHelper.CurrentLogLevel <= LogLevel.Normal) LogHelper.LogWarning("NetworkConfiguration mismatch. The configuration between the server and client does not match");
                    NetworkingManager.Singleton.DisconnectClient(clientId);
                    return;
                }

                if (NetworkingManager.Singleton.NetworkConfig.ConnectionApproval)
                {
                    byte[] connectionBuffer = reader.ReadByteArray();
                    NetworkingManager.Singleton.ConnectionApprovalCallback(connectionBuffer, clientId, NetworkingManager.Singleton.HandleApproval);
                }
                else
                {
                    NetworkingManager.Singleton.HandleApproval(clientId, null, true, null, null);
                }
            }
        }

        internal static void HandleConnectionApproved(uint clientId, Stream stream, int channelId)
        {
            using (PooledBitReader reader = PooledBitReader.Get(stream))
            {
                NetworkingManager.Singleton.LocalClientId = reader.ReadUInt32Packed();
                uint sceneIndex = reader.ReadUInt32Packed();
                Guid sceneSwitchProgressGuid = new Guid(reader.ReadByteArray());

                float netTime = reader.ReadSinglePacked();
                int remoteStamp = reader.ReadInt32Packed();
                int msDelay = NetworkingManager.Singleton.NetworkConfig.NetworkTransport.GetRemoteDelayTimeMS(clientId, remoteStamp, out byte error);
                NetworkingManager.Singleton.NetworkTime = netTime + (msDelay / 1000f);

                NetworkingManager.Singleton.ConnectedClients.Add(NetworkingManager.Singleton.LocalClientId, new NetworkedClient() { ClientId = NetworkingManager.Singleton.LocalClientId });

                if (NetworkingManager.Singleton.NetworkConfig.UsePrefabSync)
                {
                    SpawnManager.DestroySceneObjects();
                }
                
                Dictionary<ulong, ulong> instanceIdNetworkIdSoftSyncLookup = new Dictionary<ulong, ulong>();
                
                int objectCount = reader.ReadInt32Packed();
                for (int i = 0; i < objectCount; i++)
                {
                    bool isPlayerObject = reader.ReadBool();
                    ulong networkId = reader.ReadUInt64Packed();
                    uint ownerId = reader.ReadUInt32Packed();
                    bool isSceneObject = reader.ReadBool();

                    ulong prefabHash;
                    ulong instanceId;
                    bool softSync;
                    
                    if (NetworkingManager.Singleton.NetworkConfig.UsePrefabSync)
                    {
                        softSync = false;
                        instanceId = 0;
                        prefabHash = reader.ReadUInt64Packed();
                    }
                    else
                    {
                        softSync = reader.ReadBool();

                        if (softSync)
                        {
                            instanceId = reader.ReadUInt64Packed();
                            prefabHash = 0;
                        }
                        else
                        {
                            prefabHash = reader.ReadUInt64Packed();
                            instanceId = 0;
                        }
                    }

                    Vector3 pos = new Vector3(reader.ReadSinglePacked(), reader.ReadSinglePacked(), reader.ReadSinglePacked());
                    Quaternion rot = Quaternion.Euler(reader.ReadSinglePacked(), reader.ReadSinglePacked(), reader.ReadSinglePacked());

                    /*
                    NetworkedObject netObject = SpawnManager.CreateSpawnedObject(SpawnManager.GetNetworkedPrefabIndexOfHash(prefabHash), networkId, ownerId, isPlayerObject,
                        sceneSpawnedInIndex, sceneDelayedSpawn, destroyWithScene, new Vector3(xPos, yPos, zPos), Quaternion.Euler(xRot, yRot, zRot), isActive, stream, false, 0, true);
                        */

                    if (softSync && NetworkSceneManager.HasSceneMismatch(sceneIndex))
                    {
                        instanceIdNetworkIdSoftSyncLookup.Add(instanceId, networkId);
                    }
                    else
                    {
                        NetworkedObject netObject = SpawnManager.CreateLocalNetworkedObject(softSync, instanceId, prefabHash, pos, rot);
                        SpawnManager.SpawnNetworkedObjectLocally(netObject, networkId, isSceneObject, isPlayerObject, ownerId, stream, false, 0, true);
                    }
                }

                if (NetworkSceneManager.HasSceneMismatch(sceneIndex))
                {
                    NetworkSceneManager.OnSceneSwitch(sceneIndex, sceneSwitchProgressGuid, instanceIdNetworkIdSoftSyncLookup);
                }

                NetworkingManager.Singleton.IsConnectedClient = true;
                
                if (NetworkingManager.Singleton.OnClientConnectedCallback != null)
                    NetworkingManager.Singleton.OnClientConnectedCallback.Invoke(NetworkingManager.Singleton.LocalClientId);
            }
        }

        internal static void HandleAddObject(uint clientId, Stream stream, int channelId)
        {
            using (PooledBitReader reader = PooledBitReader.Get(stream))
            {
                bool isPlayerObject = reader.ReadBool();
                ulong networkId = reader.ReadUInt64Packed();
                uint ownerId = reader.ReadUInt32Packed();
                bool isSceneObject = reader.ReadBool();
                
                ulong prefabHash;
                ulong instanceId;
                bool softSync;
                    
                if (NetworkingManager.Singleton.NetworkConfig.UsePrefabSync)
                {
                    softSync = false;
                    instanceId = 0;
                    prefabHash = reader.ReadUInt64Packed();
                }
                else
                {
                    softSync = reader.ReadBool();

                    if (softSync)
                    {
                        instanceId = reader.ReadUInt64Packed();
                        prefabHash = 0;
                    }
                    else
                    {
                        prefabHash = reader.ReadUInt64Packed();
                        instanceId = 0;
                    }
                }

                Vector3 pos = new Vector3(reader.ReadSinglePacked(), reader.ReadSinglePacked(), reader.ReadSinglePacked());
                Quaternion rot = Quaternion.Euler(reader.ReadSinglePacked(), reader.ReadSinglePacked(), reader.ReadSinglePacked());

                bool hasPayload = reader.ReadBool();
                int payLoadLength = hasPayload ? reader.ReadInt32Packed() : 0;
                
                NetworkedObject netObject = SpawnManager.CreateLocalNetworkedObject(softSync, instanceId, prefabHash, pos, rot);
                SpawnManager.SpawnNetworkedObjectLocally(netObject, networkId, isSceneObject, isPlayerObject, ownerId, stream, hasPayload, payLoadLength, true);

                /*
                NetworkedObject netObject = SpawnManager.CreateSpawnedObject(SpawnManager.GetNetworkedPrefabIndexOfHash(prefabHash), networkId, ownerId, isPlayerObject,
                    sceneSpawnedInIndex, sceneDelayedSpawn, destroyWithScene, new Vector3(xPos, yPos, zPos), Quaternion.Euler(xRot, yRot, zRot), true, stream, hasPayload, payLoadLength, true);
                    */
            }
        }

        internal static void HandleDestroyObject(uint clientId, Stream stream, int channelId)
        {
            using (PooledBitReader reader = PooledBitReader.Get(stream))
            {
                ulong networkId = reader.ReadUInt64Packed();
                SpawnManager.OnDestroyObject(networkId, true);
            }
        }

        internal static void HandleSwitchScene(uint clientId, Stream stream, int channelId)
        {
            using (PooledBitReader reader = PooledBitReader.Get(stream))
            {
                uint sceneIndex = reader.ReadUInt32Packed();
                Guid switchSceneGuid = new Guid(reader.ReadByteArray());
                
                if (NetworkingManager.Singleton.NetworkConfig.UsePrefabSync)
                {
                    NetworkSceneManager.OnSceneSwitch(sceneIndex, switchSceneGuid, null);
                }
                else
                {
                    Dictionary<ulong, ulong> newSceneObjects = new Dictionary<ulong, ulong>();

                    uint count = reader.ReadUInt32Packed();

                    for (int i = 0; i < count; i++)
                    {
                        newSceneObjects.Add(reader.ReadUInt64Packed(), reader.ReadUInt64Packed());
                    }
                    
                    NetworkSceneManager.OnSceneSwitch(sceneIndex, switchSceneGuid, newSceneObjects);
                }
            }
        }

        internal static void HandleClientSwitchSceneCompleted(uint clientId, Stream stream, int channelId)
        {
            using (PooledBitReader reader = PooledBitReader.Get(stream)) 
            {
                NetworkSceneManager.OnClientSwitchSceneCompleted(clientId, new Guid(reader.ReadByteArray()));
            }
        }

        internal static void HandleChangeOwner(uint clientId, Stream stream, int channelId)
        {
            using (PooledBitReader reader = PooledBitReader.Get(stream))
            {
                ulong networkId = reader.ReadUInt64Packed();
                uint ownerClientId = reader.ReadUInt32Packed();
                
                if (SpawnManager.SpawnedObjects[networkId].OwnerClientId == NetworkingManager.Singleton.LocalClientId)
                {
                    //We are current owner.
                    SpawnManager.SpawnedObjects[networkId].InvokeBehaviourOnLostOwnership();
                }
                if (ownerClientId == NetworkingManager.Singleton.LocalClientId)
                {
                    //We are new owner.
                    SpawnManager.SpawnedObjects[networkId].InvokeBehaviourOnGainedOwnership();
                }
                SpawnManager.SpawnedObjects[networkId].OwnerClientId = ownerClientId;
            }
        }

        internal static void HandleAddObjects(uint clientId, Stream stream, int channelId)
        {
            using (PooledBitReader reader = PooledBitReader.Get(stream))
            {
                ushort objectCount = reader.ReadUInt16Packed();
                for (int i = 0; i < objectCount; i++)
                {
                    bool isPlayerObject = reader.ReadBool();
                    ulong networkId = reader.ReadUInt64Packed();
                    uint ownerId = reader.ReadUInt32Packed();
                    bool isSceneObject = reader.ReadBool();

                    ulong prefabHash;
                    ulong instanceId;
                    bool softSync;
                    
                    if (NetworkingManager.Singleton.NetworkConfig.UsePrefabSync)
                    {
                        softSync = false;
                        instanceId = 0;
                        prefabHash = reader.ReadUInt64Packed();
                    }
                    else
                    {
                        softSync = reader.ReadBool();

                        if (softSync)
                        {
                            instanceId = reader.ReadUInt64Packed();
                            prefabHash = 0;
                        }
                        else
                        {
                            prefabHash = reader.ReadUInt64Packed();
                            instanceId = 0;
                        }
                    }
                    
                    Vector3 pos = new Vector3(reader.ReadSinglePacked(), reader.ReadSinglePacked(), reader.ReadSinglePacked());
                    Quaternion rot = Quaternion.Euler(reader.ReadSinglePacked(), reader.ReadSinglePacked(), reader.ReadSinglePacked());
                    
                    /*
                    NetworkedObject netObject = SpawnManager.CreateSpawnedObject(SpawnManager.GetNetworkedPrefabIndexOfHash(prefabHash), networkId, ownerId, isPlayerObject,
                        sceneSpawnedInIndex, sceneDelayedSpawn, destroyWithScene, new Vector3(xPos, yPos, zPos), Quaternion.Euler(xRot, yRot, zRot), true, stream, false, 0, true);
                        */
                    
                    NetworkedObject netObject = SpawnManager.CreateLocalNetworkedObject(softSync, instanceId, prefabHash, pos, rot);
                    SpawnManager.SpawnNetworkedObjectLocally(netObject, networkId, isSceneObject, isPlayerObject, ownerId, stream, false, 0, true);
                }
            }
        }

        internal static void HandleTimeSync(uint clientId, Stream stream, int channelId)
        {
            using (PooledBitReader reader = PooledBitReader.Get(stream))
            {
                float netTime = reader.ReadSinglePacked();
                int timestamp = reader.ReadInt32Packed();

                int msDelay = NetworkingManager.Singleton.NetworkConfig.NetworkTransport.GetRemoteDelayTimeMS(clientId, timestamp, out byte error);
                NetworkingManager.Singleton.NetworkTime = netTime + (msDelay / 1000f);
            }
        }

        internal static void HandleNetworkedVarDelta(uint clientId, Stream stream, int channelId)
        {
            using (PooledBitReader reader = PooledBitReader.Get(stream))
            {
                ulong networkId = reader.ReadUInt64Packed();
                ushort orderIndex = reader.ReadUInt16Packed();

                if (SpawnManager.SpawnedObjects.ContainsKey(networkId))
                {
                    NetworkedBehaviour instance = SpawnManager.SpawnedObjects[networkId].GetBehaviourAtOrderIndex(orderIndex);
                    if (instance == null)
                    {
                        if (LogHelper.CurrentLogLevel <= LogLevel.Normal) LogHelper.LogWarning("NetworkedVar message recieved for a non existant behaviour");
                        return;
                    }
                    NetworkedBehaviour.HandleNetworkedVarDeltas(instance.networkedVarFields, stream, clientId, instance);
                }
                else
                {
                    if (LogHelper.CurrentLogLevel <= LogLevel.Normal) LogHelper.LogWarning("NetworkedVar message recieved for a non existant object with id: " + networkId);
                    return;
                }
            }
        }

        internal static void HandleNetworkedVarUpdate(uint clientId, Stream stream, int channelId)
        {
            using (PooledBitReader reader = PooledBitReader.Get(stream))
            {
                ulong networkId = reader.ReadUInt64Packed();
                ushort orderIndex = reader.ReadUInt16Packed();

                if (SpawnManager.SpawnedObjects.ContainsKey(networkId))
                {
                    NetworkedBehaviour instance = SpawnManager.SpawnedObjects[networkId].GetBehaviourAtOrderIndex(orderIndex);
                    if (instance == null)
                    {
                        if (LogHelper.CurrentLogLevel <= LogLevel.Normal) LogHelper.LogWarning("NetworkedVar message recieved for a non existant behaviour");
                        return;
                    }
                    NetworkedBehaviour.HandleNetworkedVarUpdate(instance.networkedVarFields, stream, clientId, instance);
                }
                else
                {
                    if (LogHelper.CurrentLogLevel <= LogLevel.Normal) LogHelper.LogWarning("NetworkedVar message recieved for a non existant object with id: " + networkId);
                    return;
                }
            }
        }
        
        internal static void HandleServerRPC(uint clientId, Stream stream, int channelId)
        {
            using (PooledBitReader reader = PooledBitReader.Get(stream))
            {
                ulong networkId = reader.ReadUInt64Packed();
                ushort behaviourId = reader.ReadUInt16Packed();
                ulong hash = reader.ReadUInt64Packed();

                if (SpawnManager.SpawnedObjects.ContainsKey(networkId)) 
                { 
                    NetworkedBehaviour behaviour = SpawnManager.SpawnedObjects[networkId].GetBehaviourAtOrderIndex(behaviourId);
                    if (behaviour != null)
                    {
                        behaviour.OnRemoteServerRPC(hash, clientId, stream);
                    }
                }
            }
        }
        
        internal static void HandleServerRPCRequest(uint clientId, Stream stream, int channelId, SecuritySendFlags security)
        {
            using (PooledBitReader reader = PooledBitReader.Get(stream))
            {
                ulong networkId = reader.ReadUInt64Packed();
                ushort behaviourId = reader.ReadUInt16Packed();
                ulong hash = reader.ReadUInt64Packed();
                ulong responseId = reader.ReadUInt64Packed();

                if (SpawnManager.SpawnedObjects.ContainsKey(networkId)) 
                { 
                    NetworkedBehaviour behaviour = SpawnManager.SpawnedObjects[networkId].GetBehaviourAtOrderIndex(behaviourId);
                    if (behaviour != null)
                    {
                        object result = behaviour.OnRemoteServerRPC(hash, clientId, stream);

                        using (PooledBitStream responseStream = PooledBitStream.Get())
                        {
                            using (PooledBitWriter responseWriter = PooledBitWriter.Get(responseStream))
                            {
                                responseWriter.WriteUInt64Packed(responseId);
                                responseWriter.WriteObjectPacked(result);
                            }
                            
                            InternalMessageHandler.Send(clientId, MLAPIConstants.MLAPI_SERVER_RPC_RESPONSE, MessageManager.reverseChannels[channelId], responseStream, security, SpawnManager.SpawnedObjects[networkId]);
                        }
                    }
                }
            }
        }
        
        internal static void HandleServerRPCResponse(uint clientId, Stream stream, int channelId)
        {
            using (PooledBitReader reader = PooledBitReader.Get(stream))
            {
                ulong responseId = reader.ReadUInt64Packed();

                if (ResponseMessageManager.ContainsKey(responseId))
                {
                    RpcResponseBase responseBase = ResponseMessageManager.GetByKey(responseId);

                    if (responseBase.ClientId != clientId) return;
                    
                    ResponseMessageManager.Remove(responseId);
                    
                    responseBase.IsDone = true;
                    responseBase.Result = reader.ReadObjectPacked(responseBase.Type);
                    responseBase.IsSuccessful = true;
                }
            }
        }
        
        internal static void HandleClientRPC(uint clientId, Stream stream, int channelId)
        {
            using (PooledBitReader reader = PooledBitReader.Get(stream))
            {
                ulong networkId = reader.ReadUInt64Packed();
                ushort behaviourId = reader.ReadUInt16Packed();
                ulong hash = reader.ReadUInt64Packed();
                
                if (SpawnManager.SpawnedObjects.ContainsKey(networkId)) 
                {
                    NetworkedBehaviour behaviour = SpawnManager.SpawnedObjects[networkId].GetBehaviourAtOrderIndex(behaviourId);
                    if (behaviour != null)
                    {
                        behaviour.OnRemoteClientRPC(hash, clientId, stream);
                    }
                }
            }
        }
        
        internal static void HandleClientRPCRequest(uint clientId, Stream stream, int channelId, SecuritySendFlags security)
        {
            using (PooledBitReader reader = PooledBitReader.Get(stream))
            {
                ulong networkId = reader.ReadUInt64Packed();
                ushort behaviourId = reader.ReadUInt16Packed();
                ulong hash = reader.ReadUInt64Packed();
                ulong responseId = reader.ReadUInt64Packed();
                
                if (SpawnManager.SpawnedObjects.ContainsKey(networkId)) 
                {
                    NetworkedBehaviour behaviour = SpawnManager.SpawnedObjects[networkId].GetBehaviourAtOrderIndex(behaviourId);
                    if (behaviour != null)
                    {
                        object result = behaviour.OnRemoteClientRPC(hash, clientId, stream);
                        
                        using (PooledBitStream responseStream = PooledBitStream.Get())
                        {
                            using (PooledBitWriter responseWriter = PooledBitWriter.Get(responseStream))
                            {
                                responseWriter.WriteUInt64Packed(responseId);
                                responseWriter.WriteObjectPacked(result);
                            }
                            
                            InternalMessageHandler.Send(clientId, MLAPIConstants.MLAPI_CLIENT_RPC_RESPONSE, MessageManager.reverseChannels[channelId], responseStream, security, null);
                        }
                    }
                }
            }
        }
        
        internal static void HandleClientRPCResponse(uint clientId, Stream stream, int channelId)
        {
            using (PooledBitReader reader = PooledBitReader.Get(stream))
            {
                ulong responseId = reader.ReadUInt64Packed();

                if (ResponseMessageManager.ContainsKey(responseId))
                {
                    RpcResponseBase responseBase = ResponseMessageManager.GetByKey(responseId);
                    
                    if (responseBase.ClientId != clientId) return;
                    
                    ResponseMessageManager.Remove(responseId);
                    
                    responseBase.IsDone = true;
                    responseBase.Result = reader.ReadObjectPacked(responseBase.Type);
                    responseBase.IsSuccessful = true;
                }
            }
        }
        
        internal static void HandleCustomMessage(uint clientId, Stream stream, int channelId)
        {
            NetworkingManager.Singleton.InvokeOnIncomingCustomMessage(clientId, stream);
        }
    }
}
