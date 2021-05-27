using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using ColossalFramework.Importers;
using ColossalFramework.Packaging;
using UnityEngine;

namespace LoadingScreenModTest
{
    internal sealed class Sharing : Instance<Sharing>
    {
        const int maxData = 600;
        const int evictData = 500;
        const int maxMaterial = 70;
        const int indexHistory = 9;
        const int targetCost = 600;
        const int evictCost = 1000;
        const int SHIFT = 18;
        const int MASK = (1 << SHIFT) - 1;
        const int COSTMASK = ~MASK;
        const int OBJ = 1; // Package.AssetType.Object
        const int MAT = 2;
        const int TEX = 3;
        const int MSH = 4;
        const int OBJSHIFT = 13;
        const int MTSHIFT = 16;
        const int OBJMAXSIZE = 409600; // 50 << 13 (~5 ms)

        internal int LoaderAhead => loaderIndex - mainIndex;
        internal string Status => string.Concat(currentCount.ToString(), " ", currentCosts.ToString());
        static bool Supports(int type) => type <= 4 & type >= 1; // Object Material Texture StaticMesh

        // Asset checksum to asset data.
        LinkedHashMap<string, Triple> data = new LinkedHashMap<string, Triple>(300);
        readonly object mutex = new object();

        // Asset data costs.
        int currentCosts;

        // Loader and Main queue indices.
        int loaderIndex, mainIndex;

        // Local to Main.
        int currentCount, maxCount, readyIndex = -1;

        // Meshes and textures from LoadWorker to MTWorker.
        readonly ConcurrentQueue<Triple> mtQueue = new ConcurrentQueue<Triple>(32);

        // Queue indices from MTWorker to Main.
        readonly Atomic<int> readySlot = new Atomic<int>();

        // Local to LoadWorker.
        List<Package.Asset> loadList;
        List<Triple> loadedList;

        // Caller locks.
        void Prune()
        {
            int costs = currentCosts, mnIndex = mainIndex;
            int oldIndex = mnIndex - indexHistory;

            for (int count = data.Count; count > 0; count--)
            {
                int costAndIndex = data.Eldest.code;
                int dataIndex = costAndIndex & MASK;

                if (oldIndex < dataIndex && count < evictData && costs < evictCost || mnIndex <= dataIndex)
                    break;

                costs -= costAndIndex >> SHIFT;
                //Console.WriteLine("    pruned " + data.EldestKey + "  " + dataIndex);
                data.RemoveEldest();
            }

            currentCosts = costs;
        }

        void Send(int acc)
        {
            if (loadedList.Count > 0)
            {
                lock (mutex)
                {
                    foreach (Triple t in loadedList)
                    {
                        string checksum = t.obj as string;

                        if (!data.ContainsKey(checksum)) // this check is necessary
                        {
                            t.obj = null;
                            data.Add(checksum, t);
                        }
                    }

                    currentCosts += acc;
                }

                loadedList.Clear();
            }
        }

        void LoadPackage(int firstIndex, int lastIndex, Package package, Package.Asset[] q)
        {
            loadList.Clear();
            int index = firstIndex;
            bool canLoad;

            lock (mutex)
            {
                loaderIndex = firstIndex;
                Prune();
                int matCount = 0, materialLimit = Mathf.Min(maxMaterial, maxData - data.Count);

                foreach (Package.Asset asset in package)
                {
                    string name = asset.name;
                    int type = asset.type;

                    if (!Supports(type) || name.EndsWith("_SteamPreview") || name.EndsWith("_Snapshot") || name == "UserAssetData")
                        continue;

                    string checksum = asset.checksum;

                    // Some workshop assets contain hundreds of materials. Probably by mistake.
                    if (type == TEX && (texturesMain.ContainsKey(checksum) || texturesLod.ContainsKey(checksum)) ||
                        type == MSH && meshes.ContainsKey(checksum) ||
                        type == MAT && (materialsMain.ContainsKey(checksum) || materialsLod.ContainsKey(checksum) || ++matCount > materialLimit))
                        continue;

                    // These citizen assets are unused.
                    if (type == OBJ && name.StartsWith(" - "))
                        break;

                    if (data.TryGetValue(checksum, out Triple t))
                    {
                        t.code = t.code & COSTMASK | lastIndex;
                        data.Reinsert(checksum);

                        if (index < lastIndex && ReferenceEquals(asset, q[index]))
                            index++;
                    }
                    else
                        loadList.Add(asset);
                }

                canLoad = CanLoad();
                //Console.WriteLine("    L0 " + firstIndex + ": " + Profiling.Millis + " " + data.Count + " " + currentCosts + " " + q[firstIndex].fullName);
            }

            if (loadList.Count == 0)
                return;

            using (FileStream fs = new FileStream(package.packagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 8192))
            {
                if (!canLoad)
                {
                    fs.Position = loadList[0].offset;

                    lock (mutex)
                    {
                        while (!CanLoad())
                            Monitor.Wait(mutex);

                        //Console.WriteLine("    L1 " + firstIndex + ": " + Profiling.Millis + " " + data.Count + " " + currentCosts + " " + q[firstIndex].fullName);
                    }
                }

                loadedList.Clear();
                int code = (lastIndex - index + 1) << SHIFT | lastIndex, acc = 0;

                for (int k = 0; k < loadList.Count; k++)
                {
                    Package.Asset asset = loadList[k];
                    byte[] bytes = LoadAsset(fs, asset);
                    //InitOne(asset.checksum);
                    int type = asset.type, costAndIndex = lastIndex;

                    if (type > OBJ) // Material Texture StaticMesh
                    {
                        if (type > MAT) // Texture StaticMesh
                        {
                            mtQueue.Enqueue(new Triple(asset, bytes, code));
                            int cost = asset.size >> MTSHIFT;
                            costAndIndex |= cost << SHIFT;
                            acc += cost;
                        }

                        loadedList.Add(new Triple(asset.checksum, bytes, costAndIndex));
                    }
                    else // Object
                    {
                        int size = asset.size;

                        if (size > 32768) // GetStream
                        {
                            int cost = Mathf.Min(size, OBJMAXSIZE) >> OBJSHIFT;
                            costAndIndex |= cost << SHIFT;
                            acc += cost;
                        }

                        loadedList.Add(new Triple(asset.checksum, bytes, costAndIndex));

                        if (index < lastIndex && ReferenceEquals(asset, q[index]))
                        {
                            int d = index - firstIndex;

                            if (d < 3 || (d & 3) == 0)
                            {
                                Send(acc);
                                acc = 0;
                                code = (lastIndex - index) << SHIFT | lastIndex;
                            }

                            index++;
                        }
                    }
                }

                Send(acc);
                loadList.Clear();
            }
        }

        static byte[] LoadAsset(FileStream fs, Package.Asset asset)
        {
            int remaining = asset.size;

            if (remaining > 222444000 || remaining < 0)
                throw new IOException("Asset " + asset.fullName + " size: " + remaining);

            long offset = asset.offset;

            if (offset != fs.Position)
                fs.Position = offset;

            byte[] bytes = new byte[remaining];
            int got = 0;

            while (remaining > 0)
            {
                int n = fs.Read(bytes, got, remaining);

                if (n == 0)
                    throw new IOException("Unexpected end of file: " + asset.fullName);

                got += n; remaining -= n;
            }

            return bytes;
        }

        static int Forward(int index, Package p, Package.Asset[] q)
        {
            while (++index < q.Length && ReferenceEquals(p, q[index].package))
                ;

            return index - 1;
        }

        void LoadWorker(object param)
        {
            Thread.CurrentThread.Name = "LoadWorker";
            Package.Asset[] q = (Package.Asset[]) param;
            loadList = new List<Package.Asset>(64);
            loadedList = new List<Triple>(64);
            int firstIndex = 0;

            while (firstIndex < q.Length)
            {
                Package p = q[firstIndex].package;
                int lastIndex = Forward(firstIndex, p, q);

                try
                {
                    LoadPackage(firstIndex, lastIndex, p, q);
                }
                catch (Exception e)
                {
                    Util.DebugPrint("LoadWorker", p.packageName, e.Message);
                }

                //Console.WriteLine("    LD " + lastIndex + ": " + Profiling.Millis + " " + q[lastIndex].fullName);
                mtQueue.Enqueue(new Triple(lastIndex)); // end-of-asset marker
                firstIndex = lastIndex + 1;
            }

            mtQueue.SetCompleted();
            loadList = null; loadedList = null; q = null;
        }

        void MTWorker()
        {
            Thread.CurrentThread.Name = "MTWorker";
            int ready = -1;

            while (mtQueue.Dequeue(out Triple t))
            {
                int code = t.code;
                int lastIndex = code & MASK;
                int index = lastIndex - (int) ((uint) code >> SHIFT);

                if (ready < index)
                {
                    ready = index;
                    //Console.WriteLine("    mt " + index + ": " + Profiling.Millis);
                    readySlot.Set(index);
                }

                if (t.obj is Package.Asset asset)
                    try
                    {
                        byte[] bytes = t.bytes;

                        if (asset.type == TEX)
                            DeserializeTextObj(asset, bytes, lastIndex);
                        else
                            DeserializeMeshObj(asset, bytes, lastIndex);
                    }
                    catch (Exception e)
                    {
                        Util.DebugPrint("MTWorker", asset.fullName, e.Message);
                    }
            }

            readySlot.Set(int.MaxValue);
        }

        void DeserializeMeshObj(Package.Asset asset, byte[] bytes, int index)
        {
            MeshObj mo;

            using (MemStream stream = new MemStream(bytes, 0))
            using (MemReader reader = new MemReader(stream))
            {
                if (DeserializeHeader(reader) != typeof(Mesh))
                    throw new IOException("Asset " + asset.fullName + " should be Mesh");

                string name = reader.ReadString();
                Vector3[] vertices = reader.ReadVector3Array();
                Color[] colors = reader.ReadColorArray();
                Vector2[] uv = reader.ReadVector2Array();
                Vector3[] normals = reader.ReadVector3Array();
                Vector4[] tangents = reader.ReadVector4Array();
                BoneWeight[] boneWeights = reader.ReadBoneWeightsArray();
                Matrix4x4[] bindposes = reader.ReadMatrix4x4Array();
                int count = reader.ReadInt32();
                int[][] triangles = new int[count][];

                for (int i = 0; i < count; i++)
                    triangles[i] = reader.ReadInt32Array();

                mo = new MeshObj
                {
                    name = name,
                    vertices = vertices,
                    colors = colors,
                    uv = uv,
                    normals = normals,
                    tangents = tangents,
                    boneWeights = boneWeights,
                    bindposes = bindposes,
                    triangles = triangles
                };
            }

            string checksum = asset.checksum;

            lock (mutex)
            {
                if (data.TryGetValue(checksum, out Triple t))
                {
                    t.obj = mo;
                    t.bytes = null;
                }
                else
                    data.Add(checksum, new Triple(mo, asset.size >> MTSHIFT << SHIFT | index));
            }
        }

        void DeserializeTextObj(Package.Asset asset, byte[] bytes, int index)
        {
            TextObj to;

            using (MemStream stream = new MemStream(bytes, 0))
            using (MemReader reader = new MemReader(stream))
            {
                Type t = DeserializeHeader(reader);

                if (t != typeof(Texture2D) && t != typeof(Image))
                    throw new IOException("Asset " + asset.fullName + " should be Texture2D or Image");

                string name = reader.ReadString();
                bool linear = reader.ReadBoolean();
                int anisoLevel = asset.package.version >= 6 ? reader.ReadInt32() : 1;
                int count = reader.ReadInt32();
                Image image = new Image(reader.ReadBytes(count));
                byte[] pix = image.GetAllPixels();

                to = new TextObj
                {
                    name = name,
                    pixels = pix,
                    width = image.width,
                    height = image.height,
                    anisoLevel = anisoLevel,
                    format = image.format,
                    mipmap = image.mipmapCount > 1,
                    linear = linear
                };

                // image.Clear(); TODO test
                image = null;
            }

            string checksum = asset.checksum;

            lock (mutex)
            {
                if (data.TryGetValue(checksum, out Triple t))
                {
                    t.obj = to;
                    t.bytes = null;
                }
                else
                    data.Add(checksum, new Triple(to, asset.size >> MTSHIFT << SHIFT | index));
            }
        }

        static Type DeserializeHeader(MemReader reader)
        {
            if (reader.ReadBoolean())
                return null;

            return Type.GetType(reader.ReadString());
        }

        internal void WaitForWorkers(int index)
        {
            lock (mutex)
            {
                mainIndex = index;

                if (mustPrune)
                    Prune();

                currentCount = data.Count;

                if (CanLoad())
                    Monitor.Pulse(mutex);
            }

            maxCount = Mathf.Max(currentCount, maxCount);

            while (readyIndex < index)
                readyIndex = readySlot.Get();
        }

        // Caller locks.
        bool CanLoad() => currentCosts < targetCost && data.Count < evictData || loaderIndex - mainIndex < 4;

        internal Stream GetStream(Package.Asset asset)
        {
            string checksum = asset.checksum;
            //RefOne(checksum);
            Triple t;
            int size = asset.size;
            bool remove = size > 32768 || asset.name.EndsWith("_Data");

            lock (mutex)
            {
                if (remove)
                {
                    t = data.Remove(checksum);
                    int cost = t?.code >> SHIFT ?? 0;

                    if (cost > 0)
                    {
                        int pre = currentCosts;
                        int post = pre - cost;
                        currentCosts = post;

                        if (pre >= targetCost && post < targetCost)
                            Monitor.Pulse(mutex);
                    }
                }
                else
                    data.TryGetValue(checksum, out t);
            }

            if (t?.bytes is byte[] bytes)
                return new MemStream(bytes, 0);

            //Util.DebugPrint("!Loading OBJ asset", asset.fullName, checksum);

            return new FileStream(asset.package.packagePath, FileMode.Open, FileAccess.Read, FileShare.Read, Mathf.Min(size, 8192))
            {
                Position = asset.offset
            };
        }

        internal Mesh GetMesh(string checksum, Package package, bool isMain)
        {
            //RefOne(checksum);
            Mesh mesh;
            Triple t;

            lock (mutex)
            {
                if (meshes.TryGetValue(checksum, out mesh))
                {
                    meshit++;

                    if (checkAssets && !isMain)
                        Check(package, mesh, checksum);

                    return mesh;
                }

                data.TryGetValue(checksum, out t);
            }

            if (t?.obj is MeshObj mo)
            {
                mesh = new Mesh();
                mesh.name = mo.name;
                mesh.vertices = mo.vertices;
                mesh.colors = mo.colors;
                mesh.uv = mo.uv;
                mesh.normals = mo.normals;
                mesh.tangents = mo.tangents;
                mesh.boneWeights = mo.boneWeights;
                mesh.bindposes = mo.bindposes;

                for (int i = 0; i < mo.triangles.Length; i++)
                    mesh.SetTriangles(mo.triangles[i], i);

                mespre++;
            }
            else if (t?.bytes is byte[] bytes)
            {
                mesh = AssetDeserializer.Instantiate(package, bytes, isMain) as Mesh;
                mesload++;
            }
            else
            {
                Package.Asset asset = package.FindByChecksum(checksum);
                mesh = AssetDeserializer.InstantiateOne(asset, isMain, false) as Mesh;
                mesload++;
            }

            if (checkAssets && !isMain)
                Check(package, mesh, checksum);

            if (shareMeshes)
                lock (mutex)
                {
                    meshes[checksum] = mesh;
                    int cost = data.Remove(checksum)?.code >> SHIFT ?? 0;

                    if (cost > 0)
                    {
                        int pre = currentCosts;
                        int post = pre - cost;
                        currentCosts = post;

                        if (pre >= targetCost && post < targetCost)
                            Monitor.Pulse(mutex);
                    }
                }

            return mesh;
        }

        internal Texture2D GetTexture(string checksum, Package package, bool isMain)
        {
            //RefOne(checksum);
            Texture2D texture2D;
            Triple t;

            lock (mutex)
            {
                if (isMain && texturesMain.TryGetValue(checksum, out texture2D))
                {
                    texhit++;
                    return texture2D;
                }
                else if (!isMain && (texturesLod.TryGetValue(checksum, out texture2D) || texturesMain.TryGetValue(checksum, out texture2D)))
                {
                    texpre++;
                    return UnityEngine.Object.Instantiate(texture2D);
                }

                data.TryGetValue(checksum, out t);
            }

            if (t?.obj is TextObj to)
            {
                texture2D = new Texture2D(to.width, to.height, to.format, to.mipmap, to.linear);
                texture2D.LoadRawTextureData(to.pixels);
                texture2D.Apply();
                texture2D.name = to.name;
                texture2D.anisoLevel = to.anisoLevel;
                texpre++;
            }
            else if (t?.bytes is byte[] bytes)
            {
                //Util.DebugPrint("!Loading tex bytes", checksum);
                texture2D = AssetDeserializer.Instantiate(package, bytes, isMain) as Texture2D;
                texload++;
            }
            else
            {
                //Util.DebugPrint("!Loading tex asset", checksum);
                Package.Asset asset = package.FindByChecksum(checksum);
                texture2D = AssetDeserializer.InstantiateOne(asset, isMain, false) as Texture2D;
                texload++;
            }

            if (shareTextures)
                lock (mutex)
                {
                    if (isMain)
                        texturesMain[checksum] = texture2D;
                    else
                        texturesLod[checksum] = texture2D;

                    int cost = data.Remove(checksum)?.code >> SHIFT ?? 0;

                    if (cost > 0)
                    {
                        int pre = currentCosts;
                        int post = pre - cost;
                        currentCosts = post;

                        if (pre >= targetCost && post < targetCost)
                            Monitor.Pulse(mutex);
                    }
                }

            return texture2D;
        }

        internal Material GetMaterial(string checksum, Package package, bool isMain)
        {
            //RefOne(checksum);
            MaterialData mat;
            Triple t;

            lock (mutex)
            {
                if (isMain && materialsMain.TryGetValue(checksum, out mat))
                {
                    mathit++;
                    texhit += mat.textureCount;
                    return mat.material;
                }
                else if (!isMain && materialsLod.TryGetValue(checksum, out mat))
                {
                    matpre++;
                    texpre += mat.textureCount;

                    if (checkAssets)
                        Check(package, mat, checksum);

                    // This is key to efficient usage of the LOD texture atlases (InitRenderDataImpl).
                    return new Material(mat.material);
                    //return mat.material; Looks also fine to me.
                }

                data.TryGetValue(checksum, out t);
            }

            if (t?.bytes is byte[] bytes)
            {
                mat = AssetDeserializer.Instantiate(package, bytes, isMain) as MaterialData;
                matpre++;
            }
            else
            {
                Package.Asset asset = package.FindByChecksum(checksum);
                mat = AssetDeserializer.InstantiateOne(asset, isMain, false) as MaterialData;
                matload++;
            }

            if (checkAssets && !isMain)
                Check(package, mat, checksum);

            if (shareMaterials)
                lock (mutex)
                {
                    data.Remove(checksum);

                    if (isMain)
                        materialsMain[checksum] = mat;
                    else
                        materialsLod[checksum] = mat;
                }

            return mat.material;
        }

        void Check(Package package, Mesh mesh, string checksum)
        {
            int v;

            // The reject threshold in CS is 65000 / 16: "LOD has too many vertices".
            if ((v = mesh.vertices.Length) > 4062)
            {
                //Util.DebugPrint("!AddExtremeMesh", v, "vertices", package.packageMainAsset);
                Reports.instance.AddExtremeMesh(package, checksum, v);
            }
            else if ((v = mesh.triangles.Length) >= 1800)
            {
                //Util.DebugPrint("!AddLargeMesh", v / 3, "tris", checksum);
                Reports.instance.AddLargeMesh(package, checksum, -v / 3);
            }
            else if ((v = mesh.vertices.Length) >= 1000)
            {
                //Util.DebugPrint("!AddLargeMesh", v, "vertices", checksum);
                Reports.instance.AddLargeMesh(package, checksum, v);
            }
        }

        internal void Check(MaterialData mat, Texture2D texture2D)
        {
            int w = texture2D.width, h = texture2D.height;

            if (!IsPowerOfTwo(w) || !IsPowerOfTwo(h))
                weirdMaterials.Add(mat, w << 16 | h);

            if (w * h >= 262144)
                largeMaterials.Add(mat, w << 16 | h);
        }

        void Check(Package package, MaterialData mat, string checksum)
        {
            if (weirdMaterials.TryGetValue(mat, out int v))
            {
                //Util.DebugPrint("AddWeirdTexture", v, checksum);
                Reports.instance.AddWeirdTexture(package, checksum, v);
            }

            if (largeMaterials.TryGetValue(mat, out v))
            {
                //Util.DebugPrint("AddLargeTexture", v, checksum);
                Reports.instance.AddLargeTexture(package, checksum, v);
            }
        }

        static bool IsPowerOfTwo(int i) => (i & (i - 1)) == 0;

        internal int texhit, mathit, meshit;
        int texpre, texload, matpre, matload, mespre, mesload;
        internal int Misses => texload + matload + mesload;
        Dictionary<string, Texture2D> texturesMain = new Dictionary<string, Texture2D>(256);
        Dictionary<string, Texture2D> texturesLod = new Dictionary<string, Texture2D>(256);
        Dictionary<string, MaterialData> materialsMain = new Dictionary<string, MaterialData>(128);
        Dictionary<string, MaterialData> materialsLod = new Dictionary<string, MaterialData>(128);
        Dictionary<string, Mesh> meshes = new Dictionary<string, Mesh>(256);
        Dictionary<MaterialData, int> weirdMaterials, largeMaterials;
        readonly bool shareTextures, shareMaterials, shareMeshes, mustPrune;
        internal readonly bool checkAssets;

        private Sharing()
        {
            shareTextures = Settings.settings.shareTextures;
            shareMaterials = Settings.settings.shareMaterials;
            shareMeshes = Settings.settings.shareMeshes;
            mustPrune = !(shareTextures & shareMaterials & shareMeshes);
            checkAssets = Settings.settings.checkAssets;

            if (checkAssets)
            {
                weirdMaterials = new Dictionary<MaterialData, int>();
                largeMaterials = new Dictionary<MaterialData, int>();
            }
        }

        internal void Dispose()
        {
            Util.DebugPrint("Textures / Materials / Meshes shared:", texhit, "/", mathit, "/", meshit, "pre-loaded:", texpre, "/", matpre, "/", mespre,
                            "loaded:", texload, "/", matload, "/", mesload);
            Util.DebugPrint("Max cache", maxCount);
            //PrintPackages();

            lock (mutex)
            {
                data.Clear(); data = null;
                texturesMain.Clear(); texturesLod.Clear(); materialsMain.Clear(); materialsLod.Clear(); meshes.Clear();
                texturesMain = null; texturesLod = null; materialsMain = null; materialsLod = null; meshes = null;
                weirdMaterials = null; largeMaterials = null; instance = null;
            }
        }

        internal void Start(Package.Asset[] queue)
        {
            new Thread(LoadWorker).Start(queue);
            new Thread(MTWorker).Start();
        }

        //Dictionary<string, int> all = new Dictionary<string, int>(4096);
        //int refcnt;

        //void InitOne(string checksum)
        //{
        //    lock (all)
        //    {
        //        all.TryGetValue(checksum, out int i);
        //        all[checksum] = i;
        //    }
        //}

        //void RefOne(string checksum)
        //{
        //    lock (all)
        //    {
        //        if (!all.TryGetValue(checksum, out int i) || i < 0)
        //            all[checksum] = i - 1;
        //        else
        //            all[checksum] = i + 1;

        //        refcnt++;
        //    }
        //}

        //void PrintPackages()
        //{
        //    Util.DebugPrint("Loads:", all.Count, "Refs:", refcnt);
        //    Package[] packages = PackageManager.allPackages.Where(p => p.FilterAssets(UserAssetType.CustomAssetMetaData).Any()).ToArray();
        //    Array.Sort(packages, AssetLoader.instance.PackageComparison);

        //    foreach (Package p in packages)
        //    {
        //        Trace.Pr(p.packageName, "\t\t", p.packagePath, "   ", p.version);

        //        foreach (Package.Asset a in p)
        //        {
        //            string refs = all.TryGetValue(a.checksum, out int i) ? "Refs: " + i : "";

        //            Trace.Pr(a.isMainAsset ? " *" : "  ", a.fullName.PadRight(120), a.checksum, a.type.ToString().PadRight(19),
        //                a.offset.ToString().PadLeft(8), a.size.ToString().PadLeft(8), refs);
        //        }
        //    }
        //}
    }

    sealed class MeshObj
    {
        internal string name;
        internal Vector3[] vertices;
        internal Color[] colors;
        internal Vector2[] uv;
        internal Vector3[] normals;
        internal Vector4[] tangents;
        internal BoneWeight[] boneWeights;
        internal Matrix4x4[] bindposes;
        internal int[][] triangles;
    }

    sealed class TextObj
    {
        internal string name;
        internal byte[] pixels;
        internal int width;
        internal int height;
        internal int anisoLevel;
        internal TextureFormat format;
        internal bool mipmap;
        internal bool linear;
    }

    sealed class Triple
    {
        internal object obj;
        internal byte[] bytes;
        internal int code;

        internal Triple(object obj, byte[] bytes, int code)
        {
            this.obj = obj;
            this.bytes = bytes;
            this.code = code;
        }

        internal Triple(object obj, int code)
        {
            this.obj = obj;
            this.code = code;
        }

        internal Triple(int index)
        {
            code = index;
        }
    }

    // Critical fixes for loading performance.
    internal sealed class Fixes : DetourUtility<Fixes>
    {
        private Fixes()
        {
            init(typeof(BuildConfig), "ResolveCustomAssetName", typeof(CustomDeserializer), "ResolveCustomAssetName");
            init(typeof(PackageReader), "ReadByteArray", typeof(MemReader), "DreadByteArray");
        }
    }
}
