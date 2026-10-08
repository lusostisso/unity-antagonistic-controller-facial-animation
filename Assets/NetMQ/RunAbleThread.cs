using System;
using System.Threading;

/// <summary>
///     The superclass that you should derive from. It provides Start() and Stop() method and Running property.
///     It will start the thread to run Run() when you call Start().
/// </summary>
public abstract class RunAbleThread
{
    private readonly Thread _runnerThread;
    private volatile bool running;
    private bool started;
    private IDisposable lifetime;
    private Exception failure;
    public Exception Failure => Volatile.Read(ref failure);

    protected RunAbleThread()
    {
        // we need to create a thread instead of calling Run() directly because it would block unity
        // from doing other tasks like drawing game scenes
        _runnerThread = new Thread(RunWithCleanup) { IsBackground = true, Name = GetType().Name };
    }

    protected bool Running => running;
    protected virtual IDisposable AcquireLifetime() => null;

    /// <summary>
    /// This method will get called when you call Start(). Programmer must implement this method while making sure that
    /// this method terminates in a finite time. You can use Running property (which will be set to false when Stop() is
    /// called) to determine when you should stop the method.
    /// </summary>
    protected abstract void Run();

    public void Start()
    {
        if (started) throw new InvalidOperationException("Create a new worker instead of restarting a thread.");
        lifetime = AcquireLifetime();
        running = true;
        try
        {
            _runnerThread.Start();
            started = true;
        }
        catch
        {
            running = false;
            lifetime?.Dispose();
            throw;
        }
    }

    public void Stop()
    {
        running = false;
        if (!started || Thread.CurrentThread == _runnerThread) return;
        if (_runnerThread.IsAlive && !_runnerThread.Join(3000))
            throw new TimeoutException("NetMQ worker did not stop within 3 seconds.");
    }

    private void RunWithCleanup()
    {
        try { Run(); }
        catch (Exception exception) { Volatile.Write(ref failure, exception); }
        finally
        {
            running = false;
            try { lifetime?.Dispose(); }
            catch (Exception exception) { Volatile.Write(ref failure, exception); }
        }
    }
}