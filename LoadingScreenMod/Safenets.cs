using System;
using System.Collections;
using System.Threading;

namespace LoadingScreenModTest
{
    class Safenets : DetourUtility<Safenets>
    {
        delegate void ARef<T>(uint i, ref T item);
        private Safenets() => init(typeof(LoadingProfiler), "ContinueLoading");

        internal static IEnumerator Setup()
        {
            Create().Deploy();
            yield break;
        }

        public static void ContinueLoading(LoadingProfiler loadingProfiler)
        {
            FastList<LoadingProfiler.Event> events = ProfilerSource.GetEvents(loadingProfiler);
            events.Add(new LoadingProfiler.Event(LoadingProfiler.Type.ContinueLoading, null, 0));

            if (Thread.CurrentThread == SimulationManager.instance.m_simulationThread)
            {
                try
                {
                    Util.DebugPrint("Starting recovery at", Profiling.Millis);
                    Safenets.instance.Dispose();
                    PrefabCollection<NetInfo>.BindPrefabs();
                    PrefabCollection<BuildingInfo>.BindPrefabs();
                    PrefabCollection<PropInfo>.BindPrefabs();
                    PrefabCollection<TreeInfo>.BindPrefabs();
                    PrefabCollection<TransportInfo>.BindPrefabs();
                    PrefabCollection<VehicleInfo>.BindPrefabs();
                    PrefabCollection<CitizenInfo>.BindPrefabs();
                    RemoveBadVehicles();
                    FastList<ushort> badNodes = GetBadNodes(), badSegments = GetBadSegments();
                    RemoveBadNodes(badNodes); RemoveBadSegments(badSegments);
                    SimulationManager.instance.SimulationPaused = true;
                    Util.DebugPrint("Recovery finished at", Profiling.Millis);
                }
                catch (Exception e)
                {
                    UnityEngine.Debug.LogException(e);
                }
            }
        }

        static FastList<ushort> GetBadNodes()
        {
            NetInfo substitute = PrefabCollection<NetInfo>.FindLoaded("Large Road");
            FastList<ushort> badNodes = new FastList<ushort>();
            NetManager netManager = NetManager.instance;
            NetNode[] nodes = netManager.m_nodes.m_buffer;
            uint size = netManager.m_nodes.m_size;

            for (uint i = 0; i < size; i++)
                if (nodes[i].m_flags != NetNode.Flags.None)
                {
                    NetInfo info = nodes[i].Info;

                    if (info == null || info.m_netAI == null)
                    {
                        badNodes.Add((ushort) i);
                        nodes[i].Info = substitute;
                    }
                }

            Util.DebugPrint("Found", badNodes.m_size, "bad net nodes");
            return badNodes;
        }

        static FastList<ushort> GetBadSegments()
        {
            NetInfo substitute = PrefabCollection<NetInfo>.FindLoaded("Large Road");
            FastList<ushort> badSegments = new FastList<ushort>();
            NetManager netManager = NetManager.instance;
            NetSegment[] segments = netManager.m_segments.m_buffer;
            uint size = netManager.m_segments.m_size;

            for (uint i = 0; i < size; i++)
                if (segments[i].m_flags != NetSegment.Flags.None)
                {
                    NetInfo info = segments[i].Info;

                    if (info == null || info.m_netAI == null)
                    {
                        badSegments.Add((ushort) i);
                        segments[i].Info = substitute;
                    }
                }

            Util.DebugPrint("Found", badSegments.m_size, "bad net segments");
            return badSegments;
        }

        static void RemoveBadNodes(FastList<ushort> badNodes)
        {
            NetManager netManager = NetManager.instance;
            NetNode[] nodes = netManager.m_nodes.m_buffer;
            int count = 0;

            foreach(ushort node in badNodes)
                if (nodes[node].m_flags != NetNode.Flags.None)
                    try
                    {
                        netManager.ReleaseNode(node);
                        count++;
                    }
                    catch (Exception e)
                    {
                        Util.DebugPrint("Cannot remove net node", node);
                        UnityEngine.Debug.LogException(e);
                    }

            Util.DebugPrint("Removed", count, "bad net nodes");
        }

        static void RemoveBadSegments(FastList<ushort> badSegments)
        {
            NetManager netManager = NetManager.instance;
            NetSegment[] segments = netManager.m_segments.m_buffer;
            int count = 0;

            foreach (ushort segment in badSegments)
                if (segments[segment].m_flags != NetSegment.Flags.None)
                    try
                    {
                        netManager.ReleaseSegment(segment, false);
                        count++;
                    }
                    catch (Exception e)
                    {
                        Util.DebugPrint("Cannot remove net segment", segment);
                        UnityEngine.Debug.LogException(e);
                    }

            Util.DebugPrint("Removed", count, "bad net segments");
        }

        static void RemoveBadVehicles()
        {
            VehicleManager vehicleManager = VehicleManager.instance;
            Vehicle[] vehicles = vehicleManager.m_vehicles.m_buffer;
            uint size = vehicleManager.m_vehicles.m_size, count = 0;

            for (uint i = 0; i < size; i++)
                if (vehicles[i].m_flags != 0)
                {
                    VehicleInfo info = vehicles[i].Info;

                    if (info == null || info.m_vehicleAI == null)
                    {
                        try
                        {
                            vehicleManager.ReleaseVehicle((ushort) i);
                            count++;
                        }
                        catch (Exception e)
                        {
                            Util.DebugPrint("Cannot remove vehicle", i);
                            UnityEngine.Debug.LogException(e);
                        }
                    }
                }

            Util.DebugPrint("Removed", count, "bad vehicles");
            VehicleParked[] parked = vehicleManager.m_parkedVehicles.m_buffer;
            size = vehicleManager.m_parkedVehicles.m_size; count = 0;

            for (uint i = 0; i < size; i++)
                if (parked[i].m_flags != 0)
                {
                    VehicleInfo info = parked[i].Info;

                    if (info == null || info.m_vehicleAI == null)
                    {
                        try
                        {
                            vehicleManager.ReleaseParkedVehicle((ushort) i);
                            count++;
                        }
                        catch (Exception e)
                        {
                            Util.DebugPrint("Cannot remove parked vehicle", i);
                            UnityEngine.Debug.LogException(e);
                        }
                    }
                }

            Util.DebugPrint("Removed", count, "bad parked vehicles");
        }

        internal static IEnumerator Removals()
        {
            AsyncAction task = SimulationManager.instance.AddAction(RemoveNow);

            while (!task.completedOrFailed)
                yield return null;
        }

        static void RemoveNow()
        {
            Util.DebugPrint("Removing starts at", Profiling.Millis);

            if (Settings.settings.removeVehicles)
                RemoveVehicles();

            if (Settings.settings.removeCitizenInstances)
                RemoveCitizenInstances();

            Util.DebugPrint("Removing finished at", Profiling.Millis);
        }

        static void RemoveVehicles()
        {
            try
            {
                int n = ForVehicles((uint i, ref Vehicle d) => VehicleManager.instance.ReleaseVehicle((ushort) i));
                Util.DebugPrint("Removed", n, "vehicles");
                n = ForParkedVehicles((uint i, ref VehicleParked d) => VehicleManager.instance.ReleaseParkedVehicle((ushort) i));
                Util.DebugPrint("Removed", n, "parked vehicles");
                ushort[] grid = VehicleManager.instance.m_vehicleGrid;
                ushort[] grid2 = VehicleManager.instance.m_vehicleGrid2;
                ushort[] parkedGrid = VehicleManager.instance.m_parkedGrid;

                for (int k = 0; k < grid.Length; k++)
                    grid[k] = 0;

                for (int k = 0; k < grid2.Length; k++)
                    grid2[k] = 0;

                for (int k = 0; k < parkedGrid.Length; k++)
                    parkedGrid[k] = 0;

                ForCitizens((uint i, ref Citizen d) => { d.SetVehicle(i, 0, 0); d.SetParkedVehicle(i, 0); });
                ForBuildings((uint i, ref Building d) => { d.m_ownVehicles = 0; d.m_guestVehicles = 0; });
                ForTransportLines((uint i, ref TransportLine d) => d.m_vehicles = 0);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogException(e);
            }
        }

        static void RemoveCitizenInstances()
        {
            try
            {
                int n = ForCitizenInstances((uint i, ref CitizenInstance d) => CitizenManager.instance.ReleaseCitizenInstance((ushort) i));
                Util.DebugPrint("Removed", n, "citizens instances");
                ushort[] grid = CitizenManager.instance.m_citizenGrid;

                for (int k = 0; k < grid.Length; k++)
                    grid[k] = 0;

                ForCitizens((uint i, ref Citizen d) => d.m_instance = 0);
                ForBuildings((uint i, ref Building d) => { d.m_sourceCitizens = 0; d.m_targetCitizens = 0; });
                ForNetNodes((uint i, ref NetNode d) => d.m_targetCitizens = 0);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogException(e);
            }
        }

        static int ForVehicles(ARef<Vehicle> action)
        {
            Vehicle[] b = VehicleManager.instance.m_vehicles.m_buffer;
            int count = 0;

            for (uint i = 1; i < b.Length; i++)
                if (b[i].m_flags != 0)
                    try
                    {
                        action(i, ref b[i]);
                        count++;
                    }
                    catch (Exception e)
                    {
                        UnityEngine.Debug.LogException(e);
                    }

            return count;
        }

        static int ForParkedVehicles(ARef<VehicleParked> action)
        {
            VehicleParked[] b = VehicleManager.instance.m_parkedVehicles.m_buffer;
            int count = 0;

            for (uint i = 1; i < b.Length; i++)
                if (b[i].m_flags != 0)
                    try
                    {
                        action(i, ref b[i]);
                        count++;
                    }
                    catch (Exception e)
                    {
                        UnityEngine.Debug.LogException(e);
                    }

            return count;
        }

        static int ForCitizenInstances(ARef<CitizenInstance> action)
        {
            CitizenInstance[] b = CitizenManager.instance.m_instances.m_buffer;
            int count = 0;

            for (uint i = 1; i < b.Length; i++)
                if ((b[i].m_flags & CitizenInstance.Flags.Created) != 0)
                    try
                    {
                        action(i, ref b[i]);
                        count++;
                    }
                    catch (Exception e)
                    {
                        UnityEngine.Debug.LogException(e);
                    }

            return count;
        }

        static int ForCitizens(ARef<Citizen> action)
        {
            Citizen[] b = CitizenManager.instance.m_citizens.m_buffer;
            int count = 0;

            for (uint i = 1; i < b.Length; i++)
                if ((b[i].m_flags & Citizen.Flags.Created) != 0)
                    try
                    {
                        action(i, ref b[i]);
                        count++;
                    }
                    catch (Exception e)
                    {
                        UnityEngine.Debug.LogException(e);
                    }

            return count;
        }

        static int ForBuildings(ARef<Building> action)
        {
            Building[] b = BuildingManager.instance.m_buildings.m_buffer;
            int count = 0;

            for (uint i = 1; i < b.Length; i++)
                if (b[i].m_flags  != 0)
                    try
                    {
                        action(i, ref b[i]);
                        count++;
                    }
                    catch (Exception e)
                    {
                        UnityEngine.Debug.LogException(e);
                    }

            return count;
        }

        static int ForNetNodes(ARef<NetNode> action)
        {
            NetNode[] b = NetManager.instance.m_nodes.m_buffer;
            int count = 0;

            for (uint i = 1; i < b.Length; i++)
                if (b[i].m_flags != 0)
                    try
                    {
                        action(i, ref b[i]);
                        count++;
                    }
                    catch (Exception e)
                    {
                        UnityEngine.Debug.LogException(e);
                    }

            return count;
        }

        //static int ForNetSegments(ARef<NetSegment> action)
        //{
        //    NetSegment[] b = NetManager.instance.m_segments.m_buffer;
        //    int count = 0;

        //    for (uint i = 1; i < b.Length; i++)
        //        if (b[i].m_flags != 0)
        //            try
        //            {
        //                action(i, ref b[i]);
        //                count++;
        //            }
        //            catch (Exception e)
        //            {
        //                UnityEngine.Debug.LogException(e);
        //            }

        //    return count;
        //}

        static int ForTransportLines(ARef<TransportLine> action)
        {
            TransportLine[] b = TransportManager.instance.m_lines.m_buffer;
            int count = 0;

            for (uint i = 1; i < b.Length; i++)
                if (b[i].m_flags != 0)
                    try
                    {
                        action(i, ref b[i]);
                        count++;
                    }
                    catch (Exception e)
                    {
                        UnityEngine.Debug.LogException(e);
                    }

            return count;
        }
    }
}
