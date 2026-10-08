using System;
using System.Collections.Generic;
using AsyncIO;
using NetMQ;

/// <summary>Owns NetMQ's global context until the last socket-owning worker has exited.</summary>
public static class NetMQRuntime
{
    private static readonly object gate = new object();
    private static readonly HashSet<Lease> clients = new HashSet<Lease>();
    private static bool shuttingDown;

    private sealed class Lease : IDisposable
    {
        public readonly Action Stop;
        public Lease(Action stop) { Stop = stop; }
        public void Dispose() => Release(this);
    }

    public static int ActiveClientCount
    {
        get { lock (gate) return clients.Count; }
    }

    // Acquire before starting the worker; dispose on that worker AFTER all its sockets.
    public static IDisposable Acquire(Action stop)
    {
        if (stop == null) throw new ArgumentNullException(nameof(stop));
        lock (gate)
        {
            if (shuttingDown) throw new InvalidOperationException("NetMQ is shutting down.");
            if (clients.Count == 0) ForceDotNet.Force();
            var lease = new Lease(stop);
            clients.Add(lease);
            return lease;
        }
    }

    private static void Release(Lease lease)
    {
        lock (gate)
        {
            if (!clients.Remove(lease)) return;
            // Socket.Dispose is asynchronous. Cleanup also joins the internal I/O/reaper threads.
            // Holding gate prevents a new worker from opening a context during this cleanup.
            if (clients.Count == 0) NetMQConfig.Cleanup(false);
        }
    }

    /// <summary>Called on Unity's main thread before stopping Play, unloading scripts or quitting.</summary>
    public static void ShutdownAll()
    {
        Lease[] snapshot;
        lock (gate)
        {
            if (shuttingDown) return;
            shuttingDown = true;
            snapshot = new Lease[clients.Count];
            clients.CopyTo(snapshot);
        }
        var errors = new List<Exception>();
        try
        {
            // Never hold gate while joining: each worker needs it to release its lease.
            foreach (Lease client in snapshot)
            {
                try { client.Stop(); }
                catch (Exception exception) { errors.Add(exception); }
            }
        }
        finally
        {
            lock (gate)
            {
                shuttingDown = false;
                if (clients.Count != 0)
                    errors.Add(new TimeoutException($"{clients.Count} NetMQ worker(s) did not stop."));
            }
        }
        if (errors.Count != 0)
            throw new AggregateException("NetMQ shutdown did not finish cleanly.", errors);
    }
}