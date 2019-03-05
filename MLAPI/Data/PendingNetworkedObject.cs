namespace MLAPI.Data
{
    public class PendingNetworkedObject
    {
        public ulong NetworkId;
        public ulong InstanceId;

        /*
         *                     bool isPlayerObject = reader.ReadBool();
                    uint networkId = reader.ReadUInt32Packed();
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
                    
         */
    }
}