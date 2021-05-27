using ColossalFramework.Importers;
using System;
using System.IO;
using System.Reflection;
using UnityEngine;
using ColossalFramework.Packaging;
using ColossalFramework;

namespace LoadingScreenModTest
{
    internal sealed class AssetDeserializer
    {
        readonly Package package;
        readonly PackageReader reader;
        bool isMain;
        readonly bool isTop;

        internal static object Instantiate(Package.Asset asset, bool isMain, bool isTop)
        {
            using (Stream stream = Sharing.instance.GetStream(asset))
            using (PackageReader reader = GetReader(stream))
            {
                return new AssetDeserializer(asset.package, reader, isMain, isTop).Deserialize();
            }
        }

        internal static object Instantiate(Package package, byte[] bytes, bool isMain)
        {
            using (MemStream stream = new MemStream(bytes, 0))
            using (PackageReader reader = new MemReader(stream))
            {
                return new AssetDeserializer(package, reader, isMain, false).Deserialize();
            }
        }

        internal static object InstantiateOne(Package.Asset asset, bool isMain = true, bool isTop = true)
        {
            Package p = asset.package;

            using (FileStream fs = new FileStream(p.packagePath, FileMode.Open, FileAccess.Read, FileShare.Read, Mathf.Min(asset.size, 8192)))
            {
                fs.Position = asset.offset;

                using (PackageReader reader = new PackageReader(fs))
                {
                    return new AssetDeserializer(p, reader, isMain, isTop).Deserialize();
                }
            }
        }

        AssetDeserializer(Package package, PackageReader reader, bool isMain, bool isTop)
        {
            this.package = package;
            this.reader = reader;
            this.isMain = isMain;
            this.isTop = isTop;
        }

        object Deserialize()
        {
            if (!DeserializeHeader(out Type type))
                return null;

            if (type == typeof(GameObject))
                return DeserializeGameObject();
            if (type == typeof(Mesh))
                return DeserializeMesh();
            if (type == typeof(Material))
                return DeserializeMaterial();
            if (type == typeof(Texture2D) || type == typeof(Image))
                return DeserializeTexture();
            if (typeof(ScriptableObject).IsAssignableFrom(type))
                return DeserializeScriptableObject(type);

            return DeserializeObject(type);
        }

        object DeserializeSingleObject(Type type)
        {
            object obj = CustomDeserializer.instance.CustomDeserialize(package, type, reader);

            if (obj != null)
                return obj;
            else if (typeof(ScriptableObject).IsAssignableFrom(type) || typeof(GameObject).IsAssignableFrom(type))
                return Instantiate(package.FindByChecksum(reader.ReadString()), isMain, false);
            else
                return reader.ReadUnityType(type, package);
        }

        UnityEngine.Object DeserializeScriptableObject(Type type)
        {
            ScriptableObject so = ScriptableObject.CreateInstance(type);
            so.name = reader.ReadString();
            DeserializeFields(so, type, false);
            return so;
        }

        void DeserializeFields(object obj, Type type, bool resolveMember)
        {
            int count = reader.ReadInt32();

            for (int i = 0; i < count; i++)
            {
                if (DeserializeHeader(out Type t, out string name))
                {
                    FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                    if (field == null && resolveMember)
                        field = type.GetField(ResolveLegacyMember(t, type, name), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                    object value;

                    // Make the common case fast.
                    if (t == typeof(bool))
                        value = reader.ReadBoolean();
                    else if (t == typeof(int))
                        value = reader.ReadInt32();
                    else if (t == typeof(float))
                        value = reader.ReadSingle();
                    else if (t.IsArray)
                    {
                        int n = reader.ReadInt32();
                        Type elementType = t.GetElementType();

                        // Make the common case fast, avoid boxing.
                        if (elementType == typeof(Vector2))
                        {
                            Vector2[] array = new Vector2[n]; value = array;

                            for (int j = 0; j < n; j++)
                                array[j] = reader.ReadVector2();
                        }
                        else if (elementType == typeof(float))
                        {
                            float[] array = new float[n]; value = array;

                            for (int j = 0; j < n; j++)
                                array[j] = reader.ReadSingle();
                        }
                        else
                        {
                            Array array = Array.CreateInstance(elementType, n); value = array;

                            for (int j = 0; j < n; j++)
                                array.SetValue(DeserializeSingleObject(elementType), j);
                        }
                    }
                    else
                        value = DeserializeSingleObject(t);

                    field?.SetValue(obj, value);
                }
            }
        }

        UnityEngine.Object DeserializeGameObject()
        {
            string name = reader.ReadString();
            GameObject go = new GameObject(name);
            go.tag = reader.ReadString();
            go.layer = reader.ReadInt32();
            go.SetActive(reader.ReadBoolean());
            int count = reader.ReadInt32();
            isMain = isTop | count > 3;

            for (int i = 0; i < count; i++)
            {
                Type type;

                if (!DeserializeHeader(out type))
                    continue;

                if (type == typeof(Transform))
                    DeserializeTransform(go.transform);
                else if (type == typeof(MeshFilter))
                    DeserializeMeshFilter(go.AddComponent(type) as MeshFilter);
                else if (type == typeof(MeshRenderer))
                    DeserializeMeshRenderer(go.AddComponent(type) as MeshRenderer);
                else if (typeof(MonoBehaviour).IsAssignableFrom(type))
                    DeserializeMonoBehaviour((MonoBehaviour) go.AddComponent(type));
                else if (type == typeof(SkinnedMeshRenderer))
                    DeserializeSkinnedMeshRenderer(go.AddComponent(type) as SkinnedMeshRenderer);
                else if (type == typeof(Animator))
                    DeserializeAnimator(go.AddComponent(type) as Animator);
                else
                    throw new InvalidDataException("Unknown type to deserialize " + type.Name);
            }

            return go;
        }

        void DeserializeAnimator(Animator animator)
        {
            animator.applyRootMotion = reader.ReadBoolean();
            animator.updateMode = (AnimatorUpdateMode) reader.ReadInt32();
            animator.cullingMode = (AnimatorCullingMode) reader.ReadInt32();
        }

        UnityEngine.Object DeserializeTexture()
        {
            string name = reader.ReadString();
            bool linear = reader.ReadBoolean();
            int anisoLevel = package.version >= 6 ? reader.ReadInt32() : 1;
            int count = reader.ReadInt32();
            Image image = new Image(reader.ReadBytes(count));
            Texture2D texture2D = image.CreateTexture(linear);
            texture2D.name = name;
            texture2D.anisoLevel = anisoLevel;
            return texture2D;
        }

        MaterialData DeserializeMaterial()
        {
            string name = reader.ReadString();
            string shader = reader.ReadString();
            Material material = new Material(Shader.Find(shader));
            material.name = name;
            int count = reader.ReadInt32();
            int textureCount = 0;
            Sharing inst = Sharing.instance;
            Texture2D texture2D = null;

            for (int i = 0; i < count; i++)
            {
                int kind = reader.ReadInt32();

                if (kind == 0)
                    material.SetColor(reader.ReadString(), reader.ReadColor());
                else if (kind == 1)
                    material.SetVector(reader.ReadString(), reader.ReadVector4());
                else if (kind == 2)
                    material.SetFloat(reader.ReadString(), reader.ReadSingle());
                else if (kind == 3)
                {
                    string propertyName = reader.ReadString();

                    if (!reader.ReadBoolean())
                    {
                        string checksum = reader.ReadString();
                        texture2D = inst.GetTexture(checksum, package, isMain);
                        material.SetTexture(propertyName, texture2D);
                        textureCount++;
                    }
                    else
                        material.SetTexture(propertyName, null);
                }
            }

            MaterialData mat = new MaterialData(material, textureCount);

            if (inst.checkAssets && !isMain && texture2D != null)
                inst.Check(mat, texture2D);

            return mat;
        }

        void DeserializeTransform(Transform transform)
        {
            transform.localPosition = reader.ReadVector3();
            transform.localRotation = reader.ReadQuaternion();
            transform.localScale = reader.ReadVector3();
        }

        void DeserializeMeshFilter(MeshFilter meshFilter)
        {
            meshFilter.sharedMesh = Sharing.instance.GetMesh(reader.ReadString(), package, isMain);
        }

        void DeserializeMonoBehaviour(MonoBehaviour behaviour)
        {
            DeserializeFields(behaviour, behaviour.GetType(), false);
        }

        object DeserializeObject(Type type)
        {
            object obj = Activator.CreateInstance(type);
            reader.ReadString();
            DeserializeFields(obj, type, true);
            return obj;
        }

        void DeserializeMeshRenderer(MeshRenderer renderer)
        {
            int count = reader.ReadInt32();
            Material[] array = new Material[count];
            Sharing inst = Sharing.instance;

            for (int i = 0; i < count; i++)
                array[i] = inst.GetMaterial(reader.ReadString(), package, isMain);

            renderer.sharedMaterials = array;
        }

        void DeserializeSkinnedMeshRenderer(SkinnedMeshRenderer smr)
        {
            int count = reader.ReadInt32();
            Material[] array = new Material[count];

            for (int i = 0; i < count; i++)
                array[i] = Sharing.instance.GetMaterial(reader.ReadString(), package, isMain);

            smr.sharedMaterials = array;
            smr.sharedMesh = Sharing.instance.GetMesh(reader.ReadString(), package, isMain);
        }

        UnityEngine.Object DeserializeMesh()
        {
            Mesh mesh = new Mesh();
            mesh.name = reader.ReadString();
            mesh.vertices = reader.ReadVector3Array();
            mesh.colors = reader.ReadColorArray();
            mesh.uv = reader.ReadVector2Array();
            mesh.normals = reader.ReadVector3Array();
            mesh.tangents = reader.ReadVector4Array();
            mesh.boneWeights = reader.ReadBoneWeightsArray();
            mesh.bindposes = reader.ReadMatrix4x4Array();
            mesh.subMeshCount = reader.ReadInt32();

            for (int i = 0; i < mesh.subMeshCount; i++)
                mesh.SetTriangles(reader.ReadInt32Array(), i);

            return mesh;
        }

        bool DeserializeHeader(out Type type)
        {
            type = null;

            if (reader.ReadBoolean())
                return false;

            string typeName = reader.ReadString();
            type = Type.GetType(typeName);

            if (type == null)
            {
                type = Type.GetType(ResolveLegacyType(typeName));

                if (type == null)
                {
                    if (HandleUnknownType(typeName) < 0)
                        throw new InvalidDataException("Unknown type to deserialize " + typeName);

                    return false;
                }
            }

            return true;
        }

        static PackageReader GetReader(Stream stream) => stream is MemStream ms ? new MemReader(ms) : new PackageReader(stream);
        static bool IsPowerOfTwo(int i) => (i & (i - 1)) == 0;

        bool DeserializeHeader(out Type type, out string name)
        {
            type = null;
            name = null;

            if (reader.ReadBoolean())
                return false;

            string typeName = reader.ReadString();
            type = Type.GetType(typeName);
            name = reader.ReadString();

            if (type == null)
            {
                type = Type.GetType(ResolveLegacyType(typeName));

                if (type == null)
                {
                    if (HandleUnknownType(typeName) < 0)
                        throw new InvalidDataException("Unknown type to deserialize " + typeName);

                    return false;
                }
            }

            return true;
        }

        int HandleUnknownType(string type)
        {
            int num = PackageHelper.UnknownTypeHandler(type);
            CODebugBase<InternalLogChannel>.Warn(InternalLogChannel.Packer, string.Concat("Unexpected type '", type, "' detected. No resolver handled this type. Skipping ", num, " bytes."));

            if (num > 0)
            {
                reader.ReadBytes(num);
                return num;
            }
            return -1;
        }

        static string ResolveLegacyType(string type)
        {
            string text = PackageHelper.ResolveLegacyTypeHandler(type);
            CODebugBase<InternalLogChannel>.Warn(InternalLogChannel.Packer, string.Concat("Unkown type detected. Attempting to resolve from '", type, "' to '", text, "'"));
            return text;
        }

        static string ResolveLegacyMember(Type fieldType, Type classType, string member)
        {
            string text = PackageHelper.ResolveLegacyMemberHandler(classType, member);
            CODebugBase<InternalLogChannel>.Warn(InternalLogChannel.Packer, string.Concat("Unkown member detected of type ", fieldType.FullName, " in ", classType.FullName,
                ". Attempting to resolve from '", member, "' to '", text, "'"));
            return text;
        }

        //internal static void SaveTextureAtlases()
        //{
        //    SaveTexture("RgbAtlas", NetManager.instance.m_lodRgbAtlas);
        //    SaveTexture("XysAtlas", NetManager.instance.m_lodXysAtlas);
        //    SaveTexture("AprAtlas", NetManager.instance.m_lodAprAtlas);
        //}

        //static void SaveTexture(string filename, Texture2D tex)
        //{
        //    Texture2D newt = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, tex.mipmapCount > 1);
        //    newt.SetPixels(tex.GetPixels());
        //    newt.Apply();
        //    File.WriteAllBytes(Util.GetFileName(filename, "png"), newt.EncodeToPNG());
        //}
    }

    internal sealed class MaterialData
    {
        internal readonly Material material;
        internal readonly int textureCount;

        internal MaterialData(Material m, int count)
        {
            this.material = m;
            this.textureCount = count;
        }
    }
}
