using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ColossalFramework.Importers;
using ColossalFramework.Packaging;
using ColossalFramework.UI;
using UnityEngine;

namespace LoadingScreenModTest
{
    internal sealed class CustomDeserializer : Instance<CustomDeserializer>
    {
        internal const string SKIP_PREFIX = "lsm___";
        Package.Asset[] assets;
        Dictionary<string, object> packages = new Dictionary<string, object>(256);
        Dictionary<Type, int> types;
        AtlasObj prevAtlasObj;
        Texture2D largeSprite, smallSprite, halfSprite;
        ConcurrentQueue<AtlasObj> atlasIn, atlasOut;
        HashSet<string> skippedProps;
        readonly bool loadUsed = Settings.settings.loadUsed, recordUsed = Settings.settings.RecordAssets & Settings.settings.loadUsed,
            optimizeThumbs = Settings.settings.optimizeThumbs;
        bool skipProps = Settings.settings.skipPrefabs;
        const int THUMBW = 109, THUMBH = 100, TIPW = 492, TIPH = 147, HALFW = 66, HALFH = 66;

        // Binary search is a bit faster than a lookup table.
        const int MODINFO = 0;
        const int INT_32 = 3;
        const int STRING = 6;
        const int BUILDINGINFO_PROP = 10;
        const int PACKAGE_ASSET = 13;
        const int VECTOR3 = 16;
        const int NETINFO_LANE = 20;
        const int ITEMCLASS = 23;
        const int DATETIME = 26;
        const int UITEXTUREATLAS = 29;
        const int VECTOR4 = 32;
        const int BUILDINGINFO_PATHINFO = 35;
        const int NETINFO_NODE = 39;
        const int NETINFO_SEGMENT = 42;
        const int NETINFO = 45;
        const int MILESTONE = 48;
        const int VECTOR2 = 52;
        const int MESSAGEINFO = 56;
        const int VEHICLEINFO_EFFECT = 59;
        const int TRANSPORTINFO = 62;
        const int VEHICLEINFO_VEHICLETRAILER = 65;
        const int VEHICLEINFO_VEHICLEDOOR = 70;
        const int BUILDINGINFO = 74;
        const int BUILDINGINFO_SUBINFO = 77;
        const int DEPOTAI_SPAWNPOINT = 81;
        const int PROPINFO_EFFECT = 86;
        const int PROPINFO_VARIATION = 89;
        const int VEHICLEINFO_MESHINFO = 95;
        const int BUILDINGINFO_MESHINFO = 103;
        const int PROPINFO_SPECIALPLACE = 109;
        const int TREEINFO_VARIATION = 116;
        const int DICT_STRING_BYTE_ARRAY = 125;
        const int PROPINFO_PARKINGSPACE = 3232;
        const int DISASTERPROPERTIES_DISASTERSETTINGS = 11386;

        static Package.Asset[] Assets
        {
            get
            {
                if (instance.assets == null)
                    instance.assets = FilterAssets(Package.AssetType.Object);

                return instance.assets;
            }
        }

        HashSet<string> SkippedProps
        {
            get
            {
                if (skippedProps == null)
                {
                    skippedProps = PrefabLoader.instance?.SkippedProps;

                    if (skippedProps == null || skippedProps.Count == 0)
                    {
                        skipProps = false;
                        skippedProps = new HashSet<string>();
                    }
                }

                return skippedProps;
            }
        }

        private CustomDeserializer() { }

        internal void Setup()
        {
            types = new Dictionary<Type, int>(64)
            {
                [typeof(ModInfo)] = MODINFO,
                [typeof(TerrainModify.Surface)] = INT_32,
                [typeof(SteamHelper.DLC_BitMask)] = INT_32,
                [typeof(ItemClass.Availability)] = INT_32,
                [typeof(ItemClass.Placement)] = INT_32,
                [typeof(ItemClass.Service)] = INT_32,
                [typeof(ItemClass.SubService)] = INT_32,
                [typeof(ItemClass.Level)] = INT_32,
                [typeof(CustomAssetMetaData.Type)] = INT_32,
                [typeof(VehicleInfo.VehicleType)] = INT_32,
                [typeof(BuildingInfo.PlacementMode)] = INT_32,
                [typeof(BuildingInfo.ZoningMode)] = INT_32,
                [typeof(Vehicle.Flags)] = INT_32,
                [typeof(CitizenInstance.Flags)] = INT_32,
                [typeof(NetInfo.ConnectGroup)] = INT_32,
                [typeof(PropInfo.DoorType)] = INT_32,
                [typeof(LightEffect.BlinkType)] = INT_32,
                [typeof(EventManager.EventType)] = INT_32,
                [typeof(string)] = STRING,
                [typeof(BuildingInfo.Prop)] = BUILDINGINFO_PROP,
                [typeof(Package.Asset)] = PACKAGE_ASSET,
                [typeof(Vector3)] = VECTOR3,
                [typeof(NetInfo.Lane)] = NETINFO_LANE,
                [typeof(ItemClass)] = ITEMCLASS,
                [typeof(DateTime)] = DATETIME,
                [typeof(UITextureAtlas)] = UITEXTUREATLAS,
                [typeof(Vector4)] = VECTOR4,
                [typeof(BuildingInfo.PathInfo)] = BUILDINGINFO_PATHINFO,
                [typeof(NetInfo.Node)] = NETINFO_NODE,
                [typeof(NetInfo.Segment)] = NETINFO_SEGMENT,
                [typeof(NetInfo)] = NETINFO,
                [typeof(ManualMilestone)] = MILESTONE,
                [typeof(CombinedMilestone)] = MILESTONE,
                [typeof(Vector2)] = VECTOR2,
                [typeof(MessageInfo)] = MESSAGEINFO,
                [typeof(VehicleInfo.Effect)] = VEHICLEINFO_EFFECT,
                [typeof(TransportInfo)] = TRANSPORTINFO,
                [typeof(VehicleInfo.VehicleTrailer)] = VEHICLEINFO_VEHICLETRAILER,
                [typeof(VehicleInfo.VehicleDoor)] = VEHICLEINFO_VEHICLEDOOR,
                [typeof(BuildingInfo)] = BUILDINGINFO,
                [typeof(BuildingInfo.SubInfo)] = BUILDINGINFO_SUBINFO,
                [typeof(DepotAI.SpawnPoint)] = DEPOTAI_SPAWNPOINT,
                [typeof(PropInfo.Effect)] = PROPINFO_EFFECT,
                [typeof(PropInfo.Variation)] = PROPINFO_VARIATION,
                [typeof(VehicleInfo.MeshInfo)] = VEHICLEINFO_MESHINFO,
                [typeof(BuildingInfo.MeshInfo)] = BUILDINGINFO_MESHINFO,
                [typeof(PropInfo.SpecialPlace)] = PROPINFO_SPECIALPLACE,
                [typeof(TreeInfo.Variation)] = TREEINFO_VARIATION,
                [typeof(Dictionary<string, byte[]>)] = DICT_STRING_BYTE_ARRAY,
                [typeof(PropInfo.ParkingSpace)] = PROPINFO_PARKINGSPACE,
                [typeof(DisasterProperties.DisasterSettings)] = DISASTERPROPERTIES_DISASTERSETTINGS
            };

            if (optimizeThumbs)
            {
                largeSprite = new Texture2D(TIPW, TIPH, TextureFormat.ARGB32, false, false);
                smallSprite = new Texture2D(THUMBW, THUMBH, TextureFormat.ARGB32, false, false);
                halfSprite = new Texture2D(HALFW, HALFH, TextureFormat.ARGB32, false, false);
                largeSprite.name = "tooltip";
                smallSprite.name = "thumb";
                halfSprite.name = "thumbDisabled";
                smallSprite.SetPixels32(Enumerable.Repeat(new Color32(64, 64, 64, 255), THUMBW * THUMBH).ToArray());
                smallSprite.Apply(false);
                atlasIn = new ConcurrentQueue<AtlasObj>(64);
                atlasOut = new ConcurrentQueue<AtlasObj>(32);
                new Thread(AtlasWorker).Start();
            }
        }

        internal void SetCompleted()
        {
            if (optimizeThumbs)
                atlasIn.SetCompleted();
        }

        internal void Dispose()
        {
            Fetch<BuildingInfo>.Dispose(); Fetch<PropInfo>.Dispose(); Fetch<TreeInfo>.Dispose(); Fetch<VehicleInfo>.Dispose();
            Fetch<CitizenInfo>.Dispose(); Fetch<NetInfo>.Dispose();
            types?.Clear(); types = null; atlasIn = null; atlasOut = null;
            prevAtlasObj = null; largeSprite = null; smallSprite = null; halfSprite = null;
            assets = null; packages.Clear(); packages = null; skippedProps = null; instance = null;
        }

        internal object CustomDeserialize(Package p, Type t, PackageReader r)
        {
            if (!types.TryGetValue(t, out int typeid))
                return null;

            switch (typeid)
            {
                case MODINFO:
                    return new ModInfo
                    {
                        modName = r.ReadString(),
                        modWorkshopID = r.ReadUInt64()
                    };

                case INT_32:
                    return r.ReadInt32();

                case STRING:
                    return r.ReadString();

                case BUILDINGINFO_PROP:
                    return ReadBuildingInfoProp(r);

                case PACKAGE_ASSET:
                    return ReadPackageAsset(p, r);

                case VECTOR3:
                    return r.ReadVector3();

                case NETINFO_LANE:
                    return ReadNetInfoLane(p, r);

                case ITEMCLASS:
                    return ReadItemClass(r);

                case DATETIME:
                    return r.ReadDateTime();

                case UITEXTUREATLAS:
                    return optimizeThumbs ? ReadUITextureAtlas(p, r) : PackageHelper.CustomDeserialize(p, t, r);

                case VECTOR4:
                    return r.ReadVector4();

                case BUILDINGINFO_PATHINFO:
                    return ReadBuildingInfoPathInfo(p, r);

                case NETINFO_NODE:
                    return ReadNetInfoNode(p, r);

                case NETINFO_SEGMENT:
                    return ReadNetInfoSegment(p, r);

                case NETINFO:
                    return ReadNetInfo(p, r);

                case MILESTONE:
                    return ReadMilestone(r);

                case VECTOR2:
                    return r.ReadVector2();

                case MESSAGEINFO:
                    return ReadMessageInfo(r);

                case VEHICLEINFO_EFFECT:
                    return ReadVehicleInfoEffect(r);

                case TRANSPORTINFO:
                    return ReadTransportInfo(r);

                case VEHICLEINFO_VEHICLETRAILER:
                    return ReadVehicleInfoVehicleTrailer(p, r);

                case VEHICLEINFO_VEHICLEDOOR:
                    return ReadVehicleInfoVehicleDoor(r);

                case BUILDINGINFO:
                    return ReadBuildingInfo(p, r);

                case BUILDINGINFO_SUBINFO:
                    return ReadBuildingInfoSubInfo(p, r);

                case DEPOTAI_SPAWNPOINT:
                    return ReadDepotAISpawnPoint(r);

                case PROPINFO_EFFECT:
                    return ReadPropInfoEffect(r);

                case PROPINFO_VARIATION:
                    return ReadPropInfoVariation(p, r);

                case VEHICLEINFO_MESHINFO:
                    return ReadVehicleInfoMeshInfo(p, r);

                case BUILDINGINFO_MESHINFO:
                    return ReadBuildingInfoMeshInfo(p, r);

                case PROPINFO_SPECIALPLACE:
                    return ReadPropInfoSpecialPlace(r);

                case TREEINFO_VARIATION:
                    return ReadTreeInfoVariation(p, r);

                case DICT_STRING_BYTE_ARRAY:
                    return ReadDictStringByteArray(r);

                case PROPINFO_PARKINGSPACE:
                    return ReadPropInfoParkingSpace(r);

                case DISASTERPROPERTIES_DISASTERSETTINGS:
                    return ReadDisasterPropertiesDisasterSettings(r);

                default:
                    return null;
            }
        }

        object ReadBuildingInfoProp(PackageReader r)
        {
            string propName = r.ReadString();
            string treeName = r.ReadString();
            PropInfo pi = GetProp(propName);        // old name format (without package name) is possible
            TreeInfo ti = Get<TreeInfo>(treeName);  // old name format (without package name) is possible

            if (recordUsed)
            {
                if (!string.IsNullOrEmpty(propName))
                    AddRef(pi, propName, CustomAssetMetaData.Type.Prop);

                if (!string.IsNullOrEmpty(treeName))
                    AddRef(ti, treeName, CustomAssetMetaData.Type.Tree);
            }

            return new BuildingInfo.Prop
            {
                m_prop = pi,
                m_tree = ti,
                m_position = r.ReadVector3(),
                m_angle = r.ReadSingle(),
                m_probability = r.ReadInt32(),
                m_fixedHeight = r.ReadBoolean()
            };
        }

        static object ReadPackageAsset(Package p, PackageReader r)
        {
            string checksum = r.ReadString();
            Package.Asset ret = p.FindByChecksum(checksum);
            return ret == null && p.version < 3 ? PackageManager.FindAssetByChecksum(checksum) : ret;
        }

        object ReadNetInfoLane(Package p, PackageReader r) => new NetInfo.Lane
        {
            m_position = r.ReadSingle(),
            m_width = r.ReadSingle(),
            m_verticalOffset = r.ReadSingle(),
            m_stopOffset = r.ReadSingle(),
            m_speedLimit = r.ReadSingle(),
            m_direction = (NetInfo.Direction) r.ReadInt32(),
            m_laneType = (NetInfo.LaneType) r.ReadInt32(),
            m_vehicleType = (VehicleInfo.VehicleType) r.ReadInt32(),
            m_stopType = (VehicleInfo.VehicleType) r.ReadInt32(),
            m_laneProps = ReadNetLaneProps(p, r),
            m_allowConnect = r.ReadBoolean(),
            m_useTerrainHeight = r.ReadBoolean(),
            m_centerPlatform = r.ReadBoolean(),
            m_elevated = r.ReadBoolean()
        };

        static object ReadItemClass(PackageReader r) => ItemClassCollection.FindClass(r.ReadString());

        object ReadUITextureAtlas(Package p, PackageReader r)
        {
            Package.Asset current = AssetLoader.instance.Current;

            if (ReferenceEquals(current, prevAtlasObj?.asset))
            {
                ReadOutUITextureAtlas(p, r);
                return prevAtlasObj.atlas;
            }

            UITextureAtlas atlas = ScriptableObject.CreateInstance<UITextureAtlas>();
            atlas.name = r.ReadString();
            AtlasObj ao = prevAtlasObj = new AtlasObj
            {
                asset = current,
                atlas = atlas
            };

            if (p.version > 3)
                ao.bytes = r.ReadBytes(r.ReadInt32());
            else
            {
                ao.width = r.ReadInt32();
                ao.height = r.ReadInt32();
                ao.bytes = ReadColorArray(r);
            }

            string name = r.ReadString();
            Shader shader = Shader.Find(name);
            Material material = null;

            if (shader != null)
                material = new Material(shader);
            else
                Debug.Log("Warning: texture atlas shader *" + name + "* not found.");

            atlas.material = material;
            atlas.padding = r.ReadInt32();
            int count = r.ReadInt32();
            ao.sprites = new List<UITextureAtlas.SpriteInfo>(count);
            //Trace.Newline();
            //Trace.Pr(AssetLoader.instance.Current.fullName);

            for (int i = 0; i < count; i++)
            {
                Rect region = new Rect(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                UITextureAtlas.SpriteInfo sprite = new UITextureAtlas.SpriteInfo
                {
                    name = r.ReadString(),
                    region = region
                };

                ao.sprites.Add(sprite);
                //Trace.Pr(" " + spriteName, "---", Mathf.FloorToInt(w * region.xMin), Mathf.FloorToInt(h * region.yMin), sw, sh);
                //Trace.Pr(" " + spriteName, "---", region.x, region.y, region.width, region.height);
            }

            atlasIn.Enqueue(ao);
            ReceiveAvailable();
            return atlas;
        }

        static void ReadOutUITextureAtlas(Package p, PackageReader r)
        {
            r.ReadString();

            if (p.version > 3)
                r.ReadBytes(r.ReadInt32());
            else
            {
                r.ReadInt32();
                r.ReadInt32();
                r.ReadBytes(r.ReadInt32() << 4);
            }

            r.ReadString();
            r.ReadInt32();
            int count = r.ReadInt32();

            for (int i = 0; i < count; i++)
            {
                r.ReadInt32(); r.ReadInt32(); r.ReadInt32(); r.ReadInt32();
                r.ReadString();
            }
        }

        object ReadBuildingInfoPathInfo(Package p, PackageReader r)
        {
            string fullName = r.ReadString();
            NetInfo ni = Get<NetInfo>(fullName);

            if (recordUsed && !string.IsNullOrEmpty(fullName))
                AddRef(ni, fullName, CustomAssetMetaData.Type.Road);

            BuildingInfo.PathInfo path = new BuildingInfo.PathInfo();
            path.m_netInfo = ni;
            path.m_nodes = r.ReadVector3Array();
            path.m_curveTargets = r.ReadVector3Array();
            path.m_invertSegments = r.ReadBoolean();
            path.m_maxSnapDistance = r.ReadSingle();

            if (p.version >= 5)
            {
                path.m_forbidLaneConnection = r.ReadBooleanArray();
                path.m_trafficLights = (BuildingInfo.TrafficLights[]) (object) r.ReadInt32Array();
                path.m_yieldSigns = r.ReadBooleanArray();
            }

            return path;
        }

        static object ReadNetInfoNode(Package p, PackageReader r)
        {
            NetInfo.Node node = new NetInfo.Node();
            Sharing inst = Sharing.instance;
            string checksum = r.ReadString();
            node.m_mesh = string.IsNullOrEmpty(checksum) ? null : inst.GetMesh(checksum, p, true);
            checksum = r.ReadString();
            node.m_material = string.IsNullOrEmpty(checksum) ? null : inst.GetMaterial(checksum, p, true);
            checksum = r.ReadString();
            node.m_lodMesh = string.IsNullOrEmpty(checksum) ? null : inst.GetMesh(checksum, p, false);
            checksum = r.ReadString();
            node.m_lodMaterial = string.IsNullOrEmpty(checksum) ? null : inst.GetMaterial(checksum, p, false);
            node.m_flagsRequired = (NetNode.Flags) r.ReadInt32();
            node.m_flagsForbidden = (NetNode.Flags) r.ReadInt32();
            node.m_connectGroup = (NetInfo.ConnectGroup) r.ReadInt32();
            node.m_directConnect = r.ReadBoolean();
            node.m_emptyTransparent = r.ReadBoolean();
            return node;
        }

        static object ReadNetInfoSegment(Package p, PackageReader r)
        {
            NetInfo.Segment segment = new NetInfo.Segment();
            Sharing inst = Sharing.instance;
            string checksum = r.ReadString();
            segment.m_mesh = string.IsNullOrEmpty(checksum) ? null : inst.GetMesh(checksum, p, true);
            checksum = r.ReadString();
            segment.m_material = string.IsNullOrEmpty(checksum) ? null : inst.GetMaterial(checksum, p, true);
            checksum = r.ReadString();
            segment.m_lodMesh = string.IsNullOrEmpty(checksum) ? null : inst.GetMesh(checksum, p, false);
            checksum = r.ReadString();
            segment.m_lodMaterial = string.IsNullOrEmpty(checksum) ? null : inst.GetMaterial(checksum, p, false);
            segment.m_forwardRequired = (NetSegment.Flags) r.ReadInt32();
            segment.m_forwardForbidden = (NetSegment.Flags) r.ReadInt32();
            segment.m_backwardRequired = (NetSegment.Flags) r.ReadInt32();
            segment.m_backwardForbidden = (NetSegment.Flags) r.ReadInt32();
            segment.m_emptyTransparent = r.ReadBoolean();
            segment.m_disableBendNodes = r.ReadBoolean();
            return segment;
        }

        static object ReadNetInfo(Package p, PackageReader r)
        {
            string name = r.ReadString();

            if (AssetLoader.instance.GetPackageTypeFor(p) == CustomAssetMetaData.Type.Road)
                return Get<NetInfo>(p, name); // elevations, bridges, slopes, tunnels in nets
            else
                return Get<NetInfo>(name); // train lines, metro lines in buildings (stations)
        }

        static object ReadMilestone(PackageReader r) => MilestoneCollection.FindMilestone(r.ReadString());

        static object ReadMessageInfo(PackageReader r)
        {
            MessageInfo mi = new MessageInfo();
            mi.m_firstID1 = r.ReadString();
            if (mi.m_firstID1.Equals(string.Empty))
                mi.m_firstID1 = null;
            mi.m_firstID2 = r.ReadString();
            if (mi.m_firstID2.Equals(string.Empty))
                mi.m_firstID2 = null;
            mi.m_repeatID1 = r.ReadString();
            if (mi.m_repeatID1.Equals(string.Empty))
                mi.m_repeatID1 = null;
            mi.m_repeatID2 = r.ReadString();
            if (mi.m_repeatID2.Equals(string.Empty))
                mi.m_repeatID2 = null;
            return mi;
        }

        static object ReadVehicleInfoEffect(PackageReader r) => new VehicleInfo.Effect
        {
            m_effect = EffectCollection.FindEffect(r.ReadString()),
            m_parkedFlagsForbidden = (VehicleParked.Flags) r.ReadInt32(),
            m_parkedFlagsRequired = (VehicleParked.Flags) r.ReadInt32(),
            m_vehicleFlagsForbidden = (Vehicle.Flags) r.ReadInt32(),
            m_vehicleFlagsRequired = (Vehicle.Flags) r.ReadInt32()
        };

        static object ReadTransportInfo(PackageReader r) => PrefabCollection<TransportInfo>.FindLoaded(r.ReadString());

        static object ReadVehicleInfoVehicleTrailer(Package p, PackageReader r)
        {
            string name = r.ReadString();

            return new VehicleInfo.VehicleTrailer
            {
                m_info = Get<VehicleInfo>(p, p.packageName + "." + name, name, false),
                m_probability = r.ReadInt32(),
                m_invertProbability = r.ReadInt32()
            };
        }

        static object ReadVehicleInfoVehicleDoor(PackageReader r) => new VehicleInfo.VehicleDoor
        {
            m_type = (VehicleInfo.DoorType) r.ReadInt32(),
            m_location = r.ReadVector3()
        };

        static object ReadBuildingInfo(Package p, PackageReader r)
        {
            string name = r.ReadString();

            if (AssetLoader.instance.GetPackageTypeFor(p) == CustomAssetMetaData.Type.Road)
                return Get<BuildingInfo>(p, name); // pillars in elevations and nets
            else
                return Get<BuildingInfo>(name); // do these exist?
        }

        static object ReadBuildingInfoSubInfo(Package p, PackageReader r)
        {
            string name = r.ReadString();
            string fullName = p.packageName + "." + name;
            BuildingInfo bi = null;

            if (fullName == AssetLoader.instance.Current.fullName || name == AssetLoader.instance.Current.fullName)
                Util.DebugPrint("Warning:", fullName, "wants to be a sub-building for itself");
            else
                bi = Get<BuildingInfo>(p, fullName, name, true);

            return new BuildingInfo.SubInfo
            {
                m_buildingInfo = bi,
                m_position = r.ReadVector3(),
                m_angle = r.ReadSingle(),
                m_fixedHeight = r.ReadBoolean()
            };
        }

        static object ReadDepotAISpawnPoint(PackageReader r) => new DepotAI.SpawnPoint
        {
            m_position = r.ReadVector3(),
            m_target = r.ReadVector3()
        };
        static PropInfo.Effect ReadPropInfoEffect(PackageReader r) => new PropInfo.Effect
        {
            m_effect = EffectCollection.FindEffect(r.ReadString()),
            m_position = r.ReadVector3(),
            m_direction = r.ReadVector3()
        };

        static object ReadPropInfoVariation(Package p, PackageReader r)
        {
            string name = r.ReadString();
            string fullName = p.packageName + "." + name;
            PropInfo pi = null;

            if (fullName == AssetLoader.instance.Current.fullName)
                Util.DebugPrint("Warning:", fullName, "wants to be a prop variation for itself");
            else
                pi = Get<PropInfo>(p, fullName, name, false);

            return new PropInfo.Variation
            {
                m_prop = pi,
                m_probability = r.ReadInt32()
            };
        }

        static object ReadVehicleInfoMeshInfo(Package p, PackageReader r)
        {
            VehicleInfo.MeshInfo vehicleMesh = new VehicleInfo.MeshInfo();
            string checksum = r.ReadString();

            if (!string.IsNullOrEmpty(checksum))
            {
                Package.Asset asset = p.FindByChecksum(checksum);
                GameObject go = AssetDeserializer.Instantiate(asset, true, false) as GameObject;
                vehicleMesh.m_subInfo = go.GetComponent<VehicleInfoBase>();
                go.SetActive(false);

                if (vehicleMesh.m_subInfo.m_lodObject != null)
                    vehicleMesh.m_subInfo.m_lodObject.SetActive(false);
            }
            else
                vehicleMesh.m_subInfo = null;

            vehicleMesh.m_vehicleFlagsForbidden = (Vehicle.Flags) r.ReadInt32();
            vehicleMesh.m_vehicleFlagsRequired = (Vehicle.Flags) r.ReadInt32();
            vehicleMesh.m_parkedFlagsForbidden = (VehicleParked.Flags) r.ReadInt32();
            vehicleMesh.m_parkedFlagsRequired = (VehicleParked.Flags) r.ReadInt32();
            return vehicleMesh;
        }

        static object ReadBuildingInfoMeshInfo(Package p, PackageReader r)
        {
            BuildingInfo.MeshInfo buildingMesh = new BuildingInfo.MeshInfo();
            string checksum = r.ReadString();

            if (!string.IsNullOrEmpty(checksum))
            {
                Package.Asset asset = p.FindByChecksum(checksum);
                GameObject go = AssetDeserializer.Instantiate(asset, true, false) as GameObject;
                buildingMesh.m_subInfo = go.GetComponent<BuildingInfoBase>();
                go.SetActive(false);

                if (buildingMesh.m_subInfo.m_lodObject != null)
                    buildingMesh.m_subInfo.m_lodObject.SetActive(false);
            }
            else
                buildingMesh.m_subInfo = null;

            buildingMesh.m_flagsForbidden = (Building.Flags) r.ReadInt32();
            buildingMesh.m_flagsRequired = (Building.Flags) r.ReadInt32();
            buildingMesh.m_position = r.ReadVector3();
            buildingMesh.m_angle = r.ReadSingle();
            return buildingMesh;
        }

        static object ReadPropInfoSpecialPlace(PackageReader r) => new PropInfo.SpecialPlace
        {
            m_specialFlags = (CitizenInstance.Flags) r.ReadInt32(),
            m_position = r.ReadVector3(),
            m_direction = r.ReadVector3()
        };

        static object ReadTreeInfoVariation(Package p, PackageReader r)
        {
            string name = r.ReadString();
            string fullName = p.packageName + "." + name;
            TreeInfo ti = null;

            if (fullName == AssetLoader.instance.Current.fullName)
                Util.DebugPrint("Warning:", fullName, "wants to be a tree variation for itself");
            else
                ti = Get<TreeInfo>(p, fullName, name, false);

            return new TreeInfo.Variation
            {
                m_tree = ti,
                m_probability = r.ReadInt32()
            };
        }

        static object ReadDictStringByteArray(PackageReader r)
        {
            int count = r.ReadInt32();
            Dictionary<string, byte[]> dict = new Dictionary<string, byte[]>(count);

            for (int i = 0; i < count; i++)
            {
                string key = r.ReadString();
                dict[key] = r.ReadBytes(r.ReadInt32());
            }

            return dict;
        }

        static object ReadPropInfoParkingSpace(PackageReader r) => new PropInfo.ParkingSpace
        {
            m_position = r.ReadVector3(),
            m_direction = r.ReadVector3(),
            m_size = r.ReadVector3()
        };

        static object ReadDisasterPropertiesDisasterSettings(PackageReader r) => new DisasterProperties.DisasterSettings
        {
            m_disasterName = r.ReadString(),
            m_randomProbability = r.ReadInt32()
        };

        NetLaneProps ReadNetLaneProps(Package p, PackageReader r)
        {
            int count = r.ReadInt32();
            NetLaneProps laneProps = ScriptableObject.CreateInstance<NetLaneProps>();
            laneProps.m_props = new NetLaneProps.Prop[count];

            for (int i = 0; i < count; i++)
                laneProps.m_props[i] = ReadNetLaneProp(p, r);

            return laneProps;
        }

        NetLaneProps.Prop ReadNetLaneProp(Package p, PackageReader r)
        {
            string propName, treeName;

            NetLaneProps.Prop o = new NetLaneProps.Prop
            {
                m_flagsRequired = (NetLane.Flags) r.ReadInt32(),
                m_flagsForbidden = (NetLane.Flags) r.ReadInt32(),
                m_startFlagsRequired = (NetNode.Flags) r.ReadInt32(),
                m_startFlagsForbidden = (NetNode.Flags) r.ReadInt32(),
                m_endFlagsRequired = (NetNode.Flags) r.ReadInt32(),
                m_endFlagsForbidden = (NetNode.Flags) r.ReadInt32(),
                m_colorMode = (NetLaneProps.ColorMode) r.ReadInt32(),
                m_prop = GetProp(propName = r.ReadString()),
                m_tree = Get<TreeInfo>(treeName = r.ReadString()),
                m_position = r.ReadVector3(),
                m_angle = r.ReadSingle(),
                m_segmentOffset = r.ReadSingle(),
                m_repeatDistance = r.ReadSingle(),
                m_minLength = r.ReadSingle(),
                m_cornerAngle = r.ReadSingle(),
                m_probability = r.ReadInt32()
            };

            if (recordUsed)
            {
                if (!string.IsNullOrEmpty(propName))
                    AddRef(o.m_prop, propName, CustomAssetMetaData.Type.Prop);

                if (!string.IsNullOrEmpty(treeName))
                    AddRef(o.m_tree, treeName, CustomAssetMetaData.Type.Tree);
            }

            return o;
        }

        PropInfo GetProp(string fullName)
        {
            if (string.IsNullOrEmpty(fullName) || skipProps && SkippedProps.Contains(fullName))
                return null;
            else
                return Get<PropInfo>(fullName);
        }

        // Works with (fullName = asset name), too.
        static T Get<T>(string fullName) where T : PrefabInfo
        {
            if (string.IsNullOrEmpty(fullName))
                return null;

            T info = FindLoaded<T>(fullName);

            if (info == null && instance.Load(ref fullName, FindAsset(fullName)))
                info = FindLoaded<T>(fullName);

            return info;
        }

        // For nets and pillars, the reference can be to a custom asset (dotted) or a built-in asset.
        static T Get<T>(Package package, string name) where T : PrefabInfo
        {
            if (string.IsNullOrEmpty(name))
                return null;

            string stripName = PackageHelper.StripName(name);
            T info = FindLoaded<T>(package.packageName + "." + stripName);

            if (info == null)
            {
                Package.Asset data = package.Find(stripName);

                if (data != null)
                {
                    string fullName = data.fullName;

                    if (instance.Load(ref fullName, data))
                        info = FindLoaded<T>(fullName);
                }
                else
                    info = Get<T>(name);
            }

            return info;
        }

        // For sub-buildings, name may be package.assetname.
        static T Get<T>(Package package, string fullName, string name, bool tryName) where T : PrefabInfo
        {
            T info = FindLoaded<T>(fullName);

            if (tryName && info == null)
                info = FindLoaded<T>(name);

            if (info == null)
            {
                Package.Asset data = package.Find(name);

                if (tryName && data == null)
                    data = FindAsset(name); // yes, name

                if (data != null)
                    fullName = data.fullName;
                else if (name.IndexOf('.') >= 0)
                    fullName = name;

                if (instance.Load(ref fullName, data))
                    info = FindLoaded<T>(fullName);
            }

            return info;
        }

        // Optimized version.
        internal static T FindLoaded<T>(string fullName, bool tryName = true) where T : PrefabInfo
        {
            if (string.IsNullOrEmpty(fullName))
                return null;

            Dictionary<string, PrefabCollection<T>.PrefabData> prefabDict = Fetch<T>.PrefabDict;

            if (prefabDict.TryGetValue(fullName, out PrefabCollection<T>.PrefabData prefabData))
                return prefabData.m_prefab;

            // Old-style (early 2015) custom asset full name?
            if (tryName && fullName.IndexOf('.') < 0 && !LevelLoader.instance.HasFailed(fullName))
            {
                Package.Asset[] a = Assets;

                for (int i = 0; i < a.Length; i++)
                    if (fullName == a[i].name && prefabDict.TryGetValue(a[i].package.packageName + "." + fullName, out prefabData))
                        return prefabData.m_prefab;
            }

            return null;
        }

        /// <summary>
        /// Given packagename.assetname, find the asset. Works with (fullName = asset name), too.
        /// </summary>
        internal static Package.Asset FindAsset(string fullName)
        {
            // Fast fail.
            if (LevelLoader.instance.HasFailed(fullName))
                return null;

            int j = fullName.IndexOf('.');

            if (j >= 0)
            {
                string name = fullName.Substring(j + 1);

                if (instance.packages.TryGetValue(fullName.Substring(0, j), out object obj))
                    if (obj is Package p)
                        return p.Find(name);
                    else
                    {
                        List<Package> list = obj as List<Package>;
                        Package.Asset asset;

                        for (int i = 0; i < list.Count; i++)
                            if ((asset = list[i].Find(name)) != null)
                                return asset;
                    }
            }
            else
            {
                Package.Asset[] a = Assets;

                // We also try the old (early 2015) naming that does not contain the package name. FindLoaded does this, too.
                for (int i = 0; i < a.Length; i++)
                    if (fullName == a[i].name)
                        return a[i];
            }

            return null;
        }

        bool Load(ref string fullName, Package.Asset data)
        {
            if (loadUsed)
                if (data != null)
                    try
                    {
                        fullName = data.fullName;

                        // There is at least one asset (411236307) on the workshop that wants to include itself. Asset Editor quite
                        // certainly no longer accepts that but in the early days, it was possible.
                        if (fullName != AssetLoader.instance.Current.fullName && !LevelLoader.instance.HasFailed(fullName))
                        {
                            if (recordUsed)
                                Reports.instance.AddPackage(data.package);

                            AssetLoader.instance.LoadImpl(data);
                            return true;
                        }
                    }
                    catch (Exception e)
                    {
                        AssetLoader.instance.AssetFailed(data, data.package, e);
                    }
                else
                    AssetLoader.instance.NotFound(fullName);
            else
                LevelLoader.instance.AddFailed(fullName);

            return false;
        }

        void AddRef(PrefabInfo info, string fullName, CustomAssetMetaData.Type type)
        {
            if (info == null)
            {
                if (type == CustomAssetMetaData.Type.Prop && skipProps && SkippedProps.Contains(fullName))
                    return;

                // The referenced asset is missing.
                Package.Asset container = FindContainer();

                if (container != null)
                    Reports.instance.AddReference(container, fullName, type);
            }
            else if (info.m_isCustomContent)
            {
                string r = info.name;
                Package.Asset container = FindContainer();

                if (!string.IsNullOrEmpty(r) && container != null)
                {
                    string packageName = container.package.packageName;
                    int i = r.IndexOf('.');
                    string r2;

                    if (i >= 0 && (i != packageName.Length || !r.StartsWith(packageName)) && (r2 = FindMain(r)) != null)
                        Reports.instance.AddReference(container, r2, type);
                }
            }
        }

        static Package.Asset FindContainer()
        {
            Package.Asset container = AssetLoader.instance.Current;

            if (Reports.instance.IsKnown(container))
                return container;

            return KnownMainAssetRef(container.package);
        }

        static string FindMain(string fullName)
        {
            if (Reports.instance.IsKnown(fullName))
                return fullName;

            Package.Asset asset = FindAsset(fullName);

            if (asset != null)
                return KnownMainAssetRef(asset.package)?.fullName;

            return null;
        }

        static Package.Asset KnownMainAssetRef(Package p)
        {
            Package.Asset mainAssetRef = AssetLoader.FindMainAssetRef(p);
            return !string.IsNullOrEmpty(mainAssetRef?.fullName) && Reports.instance.IsKnown(mainAssetRef) ?
                mainAssetRef : null;
        }

        // Optimized version for other mods.
        static string ResolveCustomAssetName(string fullName)
        {
            // Old (early 2015) name?
            if (fullName.IndexOf('.') < 0 && !fullName.StartsWith(SKIP_PREFIX) && !LevelLoader.instance.HasFailed(fullName))
            {
                Package.Asset[] a = Assets;

                for (int i = 0; i < a.Length; i++)
                    if (fullName == a[i].name)
                        return a[i].package.packageName + "." + fullName;
            }

            return fullName;
        }

        static Package.Asset[] FilterAssets(Package.AssetType assetType)
        {
            List<Package.Asset> list = new List<Package.Asset>(256);

            try
            {
                foreach (Package.Asset asset in PackageManager.FilterAssets(assetType))
                    if (asset != null)
                        list.Add(asset);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogException(e);
            }

            Package.Asset[] ret = list.ToArray();
            list.Clear();
            return ret;
        }

        internal void AddPackage(Package p)
        {
            string pn = p.packageName;

            if (string.IsNullOrEmpty(pn))
            {
                Util.DebugPrint(p.packagePath, " Error : no package name");
                return;
            }

            if (packages.TryGetValue(pn, out object obj))
            {
                if (obj is List<Package> list)
                    list.Add(p);
                else
                    packages[pn] = new List<Package>(4) { obj as Package, p };
            }
            else
                packages.Add(pn, p);
        }

        internal bool HasPackages(string packageName) => packages.ContainsKey(packageName);
        internal ICollection<object> AllPackages() => packages.Values;

        internal List<Package> GetPackages(string packageName)
        {
            if (packages.TryGetValue(packageName, out object obj))
                if (obj is Package p)
                    return new List<Package>(1) { p };
                else
                    return obj as List<Package>;
            else
                return null;
        }

        internal static bool AllAvailable<P>(HashSet<string> fullNames, HashSet<string> ignore) where P : PrefabInfo
        {
            foreach (string fullName in fullNames)
                if (!ignore.Contains(fullName) && FindLoaded<P>(fullName, tryName:false) == null)
                {
                    Util.DebugPrint("Must load:", fullName);
                    return false;
                }

            return true;
        }

        // Color[] to RGBA32.
        static byte[] ReadColorArray(PackageReader r)
        {
            int count = r.ReadInt32();
            byte[] bytes = new byte[count << 2];

            for (int i = 0, j = 0; i < count; i++)
            {
                bytes[j++] = (byte) (r.ReadSingle() * 255f);
                bytes[j++] = (byte) (r.ReadSingle() * 255f);
                bytes[j++] = (byte) (r.ReadSingle() * 255f);
                bytes[j++] = (byte) (r.ReadSingle() * 255f);
            }

            return bytes;
        }

        void ProcessAtlas(AtlasObj ao)
        {
            byte[] src;
            int srcWidth, srcHeight;

            if (ao.width == 0)
            {
                Image image = new Image(ao.bytes);
                src = image.GetAllPixels();
                srcWidth = image.width;
                srcHeight = image.height;
                ao.format = image.format;
            }
            else
            {
                src = ao.bytes;
                srcWidth = ao.width;
                srcHeight = ao.height;
                ao.format = TextureFormat.RGBA32;
            }

            List<UITextureAtlas.SpriteInfo> sprites = ao.sprites;
            UITextureAtlas.SpriteInfo thumb = null, tooltip = null;
            int n = 0, other = 0, maxlen = 9999;

            for(int i = 0; i < sprites.Count; i++)
            {
                UITextureAtlas.SpriteInfo s = sprites[i];
                int w = Mathf.FloorToInt(srcWidth * s.region.width), h = Mathf.FloorToInt(srcHeight * s.region.height);

                if (w == THUMBW && h == THUMBH)
                {
                    n++;
                    s.texture = smallSprite;

                    if (s.name.Length < maxlen)
                    {
                        maxlen = s.name.Length;
                        thumb = s;
                    }
                }
                else if (w == TIPW && h == TIPH)
                {
                    other++;
                    s.texture = largeSprite;
                    tooltip = s;
                }
                else
                {
                    other++;
                    s.texture = smallSprite;
                }
            }

            int pad = ao.atlas.padding;

            if (thumb == null || n > 5 || other > 1 || pad > 2 || srcWidth > 512 || tooltip == null && other > 0)
            {
                Util.DebugPrint("!ProcessAtlas", ao.asset.fullName);
                ao.bytes = src;
                ao.width = srcWidth;
                ao.height = srcHeight;
                return;
            }

            int dstHeight = tooltip != null ? 256 : 128;
            byte[] dst = new byte[dstHeight << 11]; // 512 * h * 4
            int x1 = 0, y1 = 0;

            if (tooltip != null)
            {
                CopySprite(src, srcWidth, Mathf.FloorToInt(srcWidth * tooltip.region.x), Mathf.FloorToInt(srcHeight * tooltip.region.y), dst, 512, 0, 0, TIPH << 9, 1);
                SetRect(tooltip, 0, 0, TIPW, TIPH, 512f, dstHeight);
                y1 = TIPH + pad;
            }

            for (int i = 0; i < sprites.Count; i++)
            {
                UITextureAtlas.SpriteInfo s = sprites[i];

                if (s != tooltip && s.name.StartsWith(thumb.name))
                {
                    if (s.name.EndsWith("Disabled") && s != thumb)
                    {
                        CopyHalf(src, srcWidth, Mathf.FloorToInt(srcWidth * s.region.x), Mathf.FloorToInt(srcHeight * s.region.y), dst, 512, x1, y1, HALFW, HALFH);
                        SetRect(s, x1, y1, HALFW, HALFH, 512f, dstHeight);
                        s.texture = halfSprite;
                        x1 += HALFW + pad;
                    }
                    else
                    {
                        CopySprite(src, srcWidth, Mathf.FloorToInt(srcWidth * s.region.x), Mathf.FloorToInt(srcHeight * s.region.y), dst, 512, x1, y1, THUMBW, THUMBH);
                        SetRect(s, x1, y1, THUMBW, THUMBH, 512f, dstHeight);
                        x1 += THUMBW + pad;
                    }
                }
            }

            ao.bytes = dst;
            ao.width = 512;
            ao.height = dstHeight;
        }

        static void CopySprite(byte[] src, int srcWidth, int x0, int y0, byte[] dst, int dstWidth, int x1, int y1, int w, int h)
        {
            int srcStride = srcWidth << 2, dstStride = dstWidth << 2;
            int m = (y0 * srcWidth + x0) << 2, n = (y1 * dstWidth + x1) << 2, count = w << 2;

            for (int j = 0; j < h; j++)
            {
                Buffer.BlockCopy(src, m, dst, n, count);
                m += srcStride;
                n += dstStride;
            }
        }

        static void CopyHalf(byte[] src, int srcWidth, int x0, int y0, byte[] dst, int dstWidth, int x1, int y1, int w, int h)
        {
            int srcStride = (srcWidth - w - (w >> 1)) << 2, dstStride = (dstWidth - w) << 2;
            int m = (y0 * srcWidth + x0 + 4) << 2, n = (y1 * dstWidth + x1) << 2, w2 = w >> 1;

            for (int j = 0, k = 1; j < h; j++)
            {
                for (int i = 0; i < w2; i++)
                {
                    dst[n++] = src[m++];
                    dst[n++] = src[m++];
                    dst[n++] = src[m++];
                    dst[n++] = src[m];
                    m += 5;
                    dst[n++] = src[m++];
                    dst[n++] = src[m++];
                    dst[n++] = src[m++];
                    dst[n++] = src[m++];
                }

                m += srcStride;
                n += dstStride;

                if (++k == 2)
                {
                    k = 0;
                    m += srcWidth << 2;
                }
            }
        }

        static void SetRect(UITextureAtlas.SpriteInfo sprite, int x, int y, int w, int h, float atlasWidth, float atlasHeight)
        {
            sprite.region = new Rect(x / atlasWidth, y / atlasHeight, w / atlasWidth, h / atlasHeight);
        }

        internal void ReceiveAvailable()
        {
            AtlasObj[] aos = atlasOut.DequeueAll();

            if (aos != null)
                for (int i = 0; i < aos.Length; i++)
                     ReceiveAtlas(aos[i]);
        }

        internal void ReceiveRemaining()
        {
            while (atlasOut.Dequeue(out AtlasObj ao))
                ReceiveAtlas(ao);
        }

        static void ReceiveAtlas(AtlasObj ao)
        {
            UITextureAtlas atlas = ao.atlas;

            if (ao.bytes != null && atlas.material != null)
            {
                Texture2D texture = new Texture2D(ao.width, ao.height, ao.format, false, false);
                texture.LoadRawTextureData(ao.bytes);
                texture.Apply(false);
                atlas.material.mainTexture = texture;
                //SaveTex(AssetLoader.instance.Current.fullName + "-atlasmain", texture);
                atlas.AddSprites(ao.sprites);
            }
        }

        void AtlasWorker()
        {
            Thread.CurrentThread.Name = "AtlasWorker";

            while (atlasIn.Dequeue(out AtlasObj ao))
            {
                try
                {
                    ProcessAtlas(ao);
                }
                catch (Exception e)
                {
                    Util.DebugPrint("AtlasWorker", ao.asset.fullName, e.Message);
                    ao.bytes = null;
                }

                atlasOut.Enqueue(ao);
            }

            atlasOut.SetCompleted();
        }

        //static readonly char[] forbidden = { ':', '*', '?', '<', '>', '|', '%', '&', '{', '}', '$', '!', '@', '+', '`', '=', '\\', '/', '"', '\'' };

        //static void SaveTex(string filename, Texture2D tex)
        //{
        //    foreach (char c in forbidden)
        //        filename = filename.Replace(c, '#');

        //    string n;

        //    do
        //    {
        //        n = Path.Combine(@"g:\LSM\UIAtlas", filename + ".png");
        //        filename += "-x";
        //    }
        //    while (File.Exists(n));

        //    File.WriteAllBytes(n, tex.EncodeToPNG());
        //}
    }

    static class Fetch<T> where T : PrefabInfo
    {
        static Dictionary<string, PrefabCollection<T>.PrefabData> prefabDict;

        internal static Dictionary<string, PrefabCollection<T>.PrefabData> PrefabDict
        {
            get
            {
                if (prefabDict == null)
                    prefabDict = (Dictionary<string, PrefabCollection<T>.PrefabData>) Util.GetStatic(typeof(PrefabCollection<T>), "m_prefabDict");

                return prefabDict;
            }
        }

        internal static void Dispose() => prefabDict = null;
    }

    sealed class AtlasObj
    {
        internal Package.Asset asset;
        internal UITextureAtlas atlas;
        internal byte[] bytes;
        internal int width, height;
        internal List<UITextureAtlas.SpriteInfo> sprites;
        internal TextureFormat format;
    }
}
