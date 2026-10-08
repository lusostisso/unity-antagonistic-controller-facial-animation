using System;
using System.Diagnostics;
using System.Threading;
using NetMQ;
using NetMQ.Sockets;

public enum VisionRequestStatus
{
    Response,
    Timeout,
    Error,
    Cancelled
}

public sealed class VisionRequestResult
{
    public long RequestId { get; }
    public VisionRequestStatus Status { get; }
    public string Json { get; }
    public string Error { get; }

    public VisionRequestResult(long requestId, VisionRequestStatus status, string json = null, string error = null)
    {
        RequestId = requestId;
        Status = status;
        Json = json;
        Error = error;
    }
}

public sealed class VisionRequester : RunAbleThread
{
    private sealed class Request
    {
        public long Id;
        public string Address;
        public string Objective;
        public byte[] Image;
        public long Deadline;
        public CancellationToken Cancellation;
    }

    private readonly object gate = new object();
    private Request pending;
    private VisionRequestResult completion;
    private bool busy;

    public bool IsRunning => Running;
    public bool IsBusy { get { lock (gate) return busy; } }

    protected override IDisposable AcquireLifetime() => NetMQRuntime.Acquire(Stop);

    public bool TrySend(long id, byte[] image, string objective, string address, float timeoutSeconds, CancellationToken cancellation)
    {
        if (image == null || image.Length == 0) throw new ArgumentException("A imagem está vazia.", nameof(image));
        if (string.IsNullOrWhiteSpace(address)) throw new ArgumentException("O endereço do servidor está vazio.", nameof(address));
        if (float.IsNaN(timeoutSeconds) || float.IsInfinity(timeoutSeconds) || timeoutSeconds <= 0 || timeoutSeconds > 86400)
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));

        lock (gate)
        {
            if (!Running || busy || Failure != null) return false;
            pending = new Request
            {
                Id = id,
                Image = (byte[])image.Clone(),
                Objective = objective ?? string.Empty,
                Address = address,
                Deadline = Stopwatch.GetTimestamp() + (long)(timeoutSeconds * Stopwatch.Frequency),
                Cancellation = cancellation
            };
            busy = true;
            return true;
        }
    }

    public bool TryGetResult(out VisionRequestResult result)
    {
        lock (gate)
        {
            result = completion;
            if (result == null) return false;
            completion = null;
            busy = false;
            return true;
        }
    }

    protected override void Run()
    {
        while (Running)
        {
            Request request;
            lock (gate)
            {
                request = pending;
                pending = null;
            }
            if (request == null)
            {
                Thread.Sleep(5);
                continue;
            }

            VisionRequestResult result = Exchange(request);
            lock (gate) completion = result;
        }
    }

    private VisionRequestResult Exchange(Request request)
    {
        try
        {
            if (!CanContinue(request)) return Interrupted(request);
            using (var socket = new RequestSocket())
            {
                socket.Options.Linger = TimeSpan.Zero;
                socket.Options.Identity = Guid.NewGuid().ToByteArray();
                socket.Connect(request.Address);

                var message = new NetMQMessage();
                message.Append(request.Objective);
                message.Append(request.Image);
                bool sent = false;
                while (CanContinue(request) && !sent)
                    sent = socket.TrySendMultipartMessage(PollTime(request), message);
                if (!sent || !CanContinue(request)) return Interrupted(request);

                NetMQMessage reply = null;
                bool received = false;
                while (CanContinue(request) && !received)
                    received = socket.TryReceiveMultipartMessage(PollTime(request), ref reply);
                if (!received || !CanContinue(request)) return Interrupted(request);
                if (reply == null || reply.FrameCount != 1)
                    return new VisionRequestResult(request.Id, VisionRequestStatus.Error, error: "Esperado um único frame JSON na resposta.");

                return new VisionRequestResult(request.Id, VisionRequestStatus.Response, reply[0].ConvertToString());
            }
        }
        catch (Exception exception)
        {
            return new VisionRequestResult(request.Id, VisionRequestStatus.Error,
                error: exception.GetType().Name + ": " + exception.Message);
        }
    }

    private bool CanContinue(Request request)
    {
        return Running && !request.Cancellation.IsCancellationRequested && Stopwatch.GetTimestamp() < request.Deadline;
    }

    private VisionRequestResult Interrupted(Request request)
    {
        bool cancelled = !Running || request.Cancellation.IsCancellationRequested;
        return new VisionRequestResult(request.Id, cancelled ? VisionRequestStatus.Cancelled : VisionRequestStatus.Timeout,
            error: cancelled ? "Pedido cancelado." : "O servidor não respondeu dentro do prazo.");
    }

    private static TimeSpan PollTime(Request request)
    {
        double milliseconds = (request.Deadline - Stopwatch.GetTimestamp()) * 1000.0 / Stopwatch.Frequency;
        return TimeSpan.FromMilliseconds(Math.Max(0, Math.Min(50, milliseconds)));
    }
}