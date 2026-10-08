using System;
using System.Threading;
using NetMQ;
using NetMQ.Sockets;

public class InferenceRequester : RunAbleThread
{
    private readonly object gate = new object();
    private byte[] pendingInput;
    private bool requestPending;

    private Action<byte[]> onOutputReceived;
    private Action<Exception> onFail;

    private int failCount = 0;
    public volatile bool NeedReset = false;

    private int failThreshold = 3;
    private string socketID;

    private static readonly TimeSpan PollTimeout = TimeSpan.FromMilliseconds(50);

    public InferenceRequester(string socketID) : base()
    {
        this.socketID = socketID;
    }

    protected override IDisposable AcquireLifetime() => NetMQRuntime.Acquire(Stop);

    protected override void Run()
    {
        using (RequestSocket client = new RequestSocket())
        {
            client.Options.Linger = TimeSpan.Zero;
            client.Connect("tcp://localhost:" + socketID);

            while (Running)
            {
                byte[] input;
                Action<byte[]> onSuccess;
                Action<Exception> onError;
                lock (gate)
                {
                    input = pendingInput;
                    pendingInput = null;
                    onSuccess = onOutputReceived;
                    onError = onFail;
                }
                if (input == null)
                {
                    Thread.Sleep(1);
                    continue;
                }

                try
                {
                    // Send and receive belong to THIS thread, and neither waits indefinitely.
                    bool sent = false;
                    while (Running && !sent) sent = client.TrySendFrame(PollTimeout, input);
                    if (!Running) break;
                    byte[] outputBytes = null;
                    bool received = false;
                    while (Running && !received)
                        received = client.TryReceiveFrameBytes(PollTimeout, out outputBytes);
                    if (!Running) break;
                    onSuccess?.Invoke(outputBytes);
                }
                catch (Exception exception)
                {
                    NeedReset = true;
                    onError?.Invoke(exception);
                    break;
                }
                finally { lock (gate) requestPending = false; }
            }
        }
        // RunAbleThread releases the shared context AFTER this socket has been disposed.
    }

    public void SendInput(byte[] input)
    {
        try
        {
            if (!Running) throw new InvalidOperationException("Inference worker is not running.");
            if (input == null) throw new ArgumentNullException(nameof(input));
            var byteArray = new byte[input.Length];
            Buffer.BlockCopy(input, 0, byteArray, 0, byteArray.Length);
            lock (gate)
            {
                if (requestPending) throw new InvalidOperationException("An inference request is already pending.");
                requestPending = true;
                pendingInput = byteArray;
            }
            failCount = 0;
        }
        catch (Exception e)
        {
            onFail?.Invoke(e);
            failCount++;
            if (failCount >= failThreshold)
            {
                NeedReset = true;
                failCount = 0;
            }
        }
    }

    public void SetOnOutputReceivedListener(Action<byte[]> onOutputReceived, Action<Exception> fallback)
    {
        lock (gate)
        {
            this.onOutputReceived = onOutputReceived;
            onFail = fallback;
        }
    }
}