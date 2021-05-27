using System;
using System.Collections.Generic;
using System.Threading;

namespace LoadingScreenModTest
{
    /// <summary>
    /// A thread-safe queue. Enqueue never blocks. Dequeue blocks while the queue is empty.
    /// SetCompleted unblocks all blocked threads.
    /// </summary>
    internal sealed class ConcurrentQueue<T>
    {
        readonly Queue<T> queue;
        readonly object sync = new object();
        bool completed;

        internal ConcurrentQueue(int capacity)
        {
            queue = new Queue<T>(capacity);
        }

        internal void Enqueue(T item)
        {
            lock (sync)
            {
                queue.Enqueue(item);
                Monitor.Pulse(sync);
            }
        }

        internal bool Dequeue(out T result)
        {
            lock (sync)
            {
                while (!completed && queue.Count == 0)
                    Monitor.Wait(sync);

                if (queue.Count > 0)
                {
                    result = queue.Dequeue();
                    return true;
                }
            }

            result = default(T);
            return false;
        }

        internal T[] DequeueAll()
        {
            lock (sync)
            {
                if (queue.Count == 0)
                    return null;
                else
                {
                    T[] ret = queue.ToArray();
                    queue.Clear();
                    return ret;
                }
            }
        }

        internal void SetCompleted()
        {
            lock (sync)
            {
                completed = true;
                Monitor.PulseAll(sync);
            }
        }
    }

    /// <summary>
    /// Atomic storage of one generic item. Set never blocks. Get blocks while the storage is empty.
    /// </summary>
    internal sealed class Atomic<T>
    {
        T slot;
        readonly object sync = new object();
        bool set;

        internal void Set(T item)
        {
            lock (sync)
            {
                slot = item;
                set = true;
                Monitor.Pulse(sync);
            }
        }

        internal T Get()
        {
            lock (sync)
            {
                while (!set)
                    Monitor.Wait(sync);

                set = false;
                return slot;
            }
        }
    }

    /// <summary>
    /// A dictionary that maintains insertion order. Inspired by Java's LinkedHashMap.
    /// This implementation is very minimal.
    /// </summary>
    internal sealed class LinkedHashMap<K, V>
    {
        readonly Dictionary<K, Node> map;
        readonly Node head;
        Node spare;

        internal LinkedHashMap(int capacity)
        {
            map = new Dictionary<K, Node>(capacity);
            head = new Node();
            head.prev = head;
            head.next = head;
        }

        internal int Count => map.Count;
        internal bool ContainsKey(K key) => map.ContainsKey(key);
        internal V Eldest => head.next.val;
        //internal K EldestKey => head.next.key;

        internal V this[K key]
        {
            get { return map[key].val; }

            set
            {
                if (map.TryGetValue(key, out Node n))
                    n.val = value;
                else
                    Add(key, value);
            }
        }

        internal void Add(K key, V val)
        {
            Node n = CreateNode(key, val);
            map.Add(key, n);
            n.prev = head.prev;
            n.next = head;
            head.prev.next = n;
            head.prev = n;
        }

        internal bool TryGetValue(K key, out V val)
        {
            if (map.TryGetValue(key, out Node n))
            {
                val = n.val;
                return true;
            }

            val = default(V);
            return false;
        }

        internal void Reinsert(K key)
        {
            if (map.TryGetValue(key, out Node n))
            {
                n.prev.next = n.next;
                n.next.prev = n.prev;
                n.prev = head.prev;
                n.next = head;
                head.prev.next = n;
                head.prev = n;
            }
        }

        internal V Remove(K key)
        {
            if (map.TryGetValue(key, out Node n))
            {
                map.Remove(key);
                V ret = n.val;
                n.prev.next = n.next;
                n.next.prev = n.prev;
                AddSpare(n);
                return ret;
            }

            return default(V);
        }

        internal void RemoveEldest()
        {
            Node n = head.next;
            map.Remove(n.key);
            head.next = n.next;
            n.next.prev = head;
            AddSpare(n);
        }

        internal void Clear()
        {
            while(Count > 0)
            {
                RemoveEldest();
                spare = null;
            }
        }

        Node CreateNode(K key, V val)
        {
            Node n = spare;

            if (n == null)
                n = new Node();
            else
                spare = n.next;

            n.key = key; n.val = val;
            return n;
        }

        void AddSpare(Node n)
        {
            n.key = default(K); n.val = default(V); n.prev = null; n.next = spare;
            spare = n;
        }

        sealed class Node
        {
            internal K key;
            internal V val;
            internal Node prev, next;
        }
    }

    public abstract class Instance<T>
    {
        private static T inst;

        internal static T instance
        {
            get => inst;
            set => inst = value;
        }

        internal static bool HasInstance => inst != null;

        internal static T Create()
        {
            if (inst == null)
                inst = (T) Activator.CreateInstance(typeof(T), true);

            return inst;
        }
    }
}
