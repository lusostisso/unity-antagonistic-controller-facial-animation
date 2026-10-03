using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using NetMQ;
using NetMQ.Sockets;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;

/// <summary>Captures one historical frame and delivers all completions on Unity's main thread.</summary>
[DisallowMultipleComponent]
public class VisionStreamer : MonoBehaviour
{
    public Camera visionCamera;
    [Min(1)] public int imageWidth = 512;
    [Min(1)] public int imageHeight = 512;
    public string serverAddress = "tcp://localhost:5555";
    public string currentObjective = "navigate safely down the street";
    public RawImage telaDeDebug;
    [Min(0.1f)] public float captureTimeoutSeconds = 5f;
    [Min(0.1f)] public float requestTimeoutSeconds = 120f;

    // Keep the old serialized values ONLY for migration by the scene setup command.
    [SerializeField, HideInInspector] private FaceController faceController;
    [SerializeField, HideInInspector] private float sendIntervalSeconds = 1f;
    [SerializeField, HideInInspector] private float holdDurationSeconds = 5f;
    [SerializeField, HideInInspector] private float returnToCenterWaitSeconds = 1f;
    public FaceController LegacyFaceController => faceController;
    public float LegacySendInterval => sendIntervalSeconds;
    public float LegacyHoldDuration => holdDurationSeconds;
    public float LegacyRecenterWait => returnToCenterWaitSeconds;

    public event Action<long, int, Camera> CapturePrepared;
    public event Action<long, int, string> ResponseReceived;
    public event Action<long, int, string> RequestFailed;
    public bool IsBusy => activeRequestId != 0;
    public bool IsReady => isActiveAndEnabled && workerRunning && renderTexture != null;
    public int Generation => generation;

    private sealed class Request
    {
        public readonly long Id;
        public readonly int Generation;
        public readonly byte[] Image;
        public readonly string Address, Objective;
        public readonly double Deadline;
        public readonly CancellationToken Cancellation;

        public Request(long id, int generation, byte[] image, string address, string objective,
            double deadline, CancellationToken cancellation)
        {
            Id = id; Generation = generation; Image = image; Address = address;
            Objective = objective; Deadline = deadline; Cancellation = cancellation;
        }
    }

    private sealed class Completion
    {
        public long Id;
        public int Generation;
        public string Json, Error;
    }

    private readonly object gate = new object();
    private readonly Queue<Completion> completions = new Queue<Completion>();
    private readonly AutoResetEvent workAvailable = new AutoResetEvent(false);
    private Thread networkThread;
    private volatile bool workerRunning;
    private volatile bool destroyRequested;
    private Request pendingRequest;
    private CancellationTokenSource activeCancellation;
    private Texture2D texture2D;
    private RenderTexture renderTexture, previousTarget;
    private Texture previousDebugTexture;
    private Camera ownedCamera;
    private RawImage ownedDebug;
    private float previousAspect;
    private int generation, renderedFrame = -1;
    private long nextRequestId, activeRequestId;
    private bool captureArmed, captureStarted;
    private double captureDeadline, responseDeadline;
    private string lastWorkerError;
    private static double Now => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;

    private void OnEnable()
    {
        generation++;
        if (networkThread != null && networkThread.IsAlive)
        {
            Debug.LogError("[VisionStreamer] Previous network thread has not stopped; disable and retry.", this);
            return;
        }
        try
        {
            ValidateCamera();
            ownedCamera = visionCamera;
            previousTarget = ownedCamera.targetTexture;
            previousAspect = ownedCamera.aspect;
            texture2D = new Texture2D(imageWidth, imageHeight, TextureFormat.RGB24, false);
            renderTexture = new RenderTexture(imageWidth, imageHeight, 24) { name = "VLM live frame" };
            if (!renderTexture.Create()) throw new InvalidOperationException("Cannot create capture RenderTexture.");
            ownedCamera.targetTexture = renderTexture;
            ownedCamera.aspect = (float)imageWidth / imageHeight;
            ownedDebug = telaDeDebug;
            if (ownedDebug != null)
            {
                previousDebugTexture = ownedDebug.texture;
                ownedDebug.texture = renderTexture;
            }
            lock (gate) { pendingRequest = null; completions.Clear(); }
            lastWorkerError = null;
            workerRunning = true;
            networkThread = new Thread(NetworkLoop) { IsBackground = true, Name = "VLM NetMQ" };
            networkThread.Start();
            RenderPipelineManager.beginCameraRendering += BeginCameraRendering;
            RenderPipelineManager.endCameraRendering += EndCameraRendering;
        }
        catch (Exception exception)
        {
            workerRunning = false;
            ReleaseTextures();
            Debug.LogError($"[VisionStreamer] {exception.Message}", this);
        }
    }

    private void ValidateCamera()
    {
        if (visionCamera == null) throw new InvalidOperationException("Assign a vision camera.");
        if (imageWidth <= 0 || imageHeight <= 0) throw new InvalidOperationException("Image dimensions must be positive.");
        if (visionCamera.stereoEnabled) throw new InvalidOperationException("Use a non-stereo capture camera.");
        if (visionCamera.rect != new Rect(0, 0, 1, 1))
            throw new InvalidOperationException("The capture camera must use a full viewport (0,0,1,1).");
        if (visionCamera.TryGetComponent(out UniversalAdditionalCameraData data) &&
            (data.renderType != CameraRenderType.Base || (data.cameraStack != null && data.cameraStack.Count != 0)))
            throw new InvalidOperationException("Use a dedicated URP Base camera without overlay cameras.");
    }

    private void Start()
    {
        if (CapturePrepared == null)
            Debug.LogWarning("[VisionStreamer] No attention bridge is configured. Select VisionCharacter and use Tools > VLM > Configure selected VisionStreamer before Play.", this);
    }

    public bool RequestCapture(out long requestId, out int requestGeneration)
    {
        requestId = 0; requestGeneration = generation;
        if (!IsReady || IsBusy) return false;
        activeRequestId = ++nextRequestId;
        requestId = activeRequestId;
        activeCancellation = new CancellationTokenSource();
        captureDeadline = Now + SafeTimeout(captureTimeoutSeconds, 5);
        captureArmed = true;
        captureStarted = false;
        return true;
    }

    public void CancelRequest(long id, int requestGeneration)
    {
        if (id == activeRequestId && requestGeneration == generation)
            Finish(null, "Request cancelled.");
    }

    private void BeginCameraRendering(ScriptableRenderContext context, Camera camera)
    {
        if (!captureArmed || captureStarted || camera != ownedCamera) return;
        try
        {
            ValidateCamera();
            if (camera.targetTexture != renderTexture) throw new InvalidOperationException("Capture target was changed.");
            captureStarted = true;
            renderedFrame = Time.frameCount;
            CapturePrepared?.Invoke(activeRequestId, generation, camera);
        }
        catch (Exception exception) { Finish(null, "Snapshot: " + exception.Message); }
    }

    private void EndCameraRendering(ScriptableRenderContext context, Camera camera)
    {
        if (!captureArmed || !captureStarted || camera != ownedCamera || renderedFrame != Time.frameCount) return;
        RenderTexture previousActive = RenderTexture.active;
        try
        {
            if (camera.targetTexture != renderTexture) throw new InvalidOperationException("Capture target was changed.");
            RenderTexture.active = renderTexture;
            texture2D.ReadPixels(new Rect(0, 0, renderTexture.width, renderTexture.height), 0, 0, false);
            texture2D.Apply(false);
            byte[] image = texture2D.EncodeToJPG(75);
            if (image == null || image.Length == 0) throw new InvalidOperationException("Empty JPEG.");
            responseDeadline = Now + SafeTimeout(requestTimeoutSeconds, 120);
            var request = new Request(activeRequestId, generation, image, serverAddress, currentObjective ?? "",
                responseDeadline, activeCancellation.Token);
            captureArmed = false;
            lock (gate) { pendingRequest = request; }
            workAvailable.Set();
        }
        catch (Exception exception) { Finish(null, "Capture: " + exception.Message); }
        finally { RenderTexture.active = previousActive; }
    }

    private void Update()
    {
        while (true)
        {
            Completion completion;
            lock (gate)
            {
                if (completions.Count == 0) break;
                completion = completions.Dequeue();
            }
            if (completion.Id == activeRequestId && completion.Generation == generation)
                Finish(completion.Json, completion.Error);
        }
        if (!IsBusy) return;
        if (!workerRunning) Finish(null, lastWorkerError ?? "Network worker stopped.");
        else if (captureArmed && Now >= captureDeadline) Finish(null, "Camera did not complete a render before the capture deadline.");
        else if (!captureArmed && Now >= responseDeadline) Finish(null, "Server response timed out.");
    }

    private void Finish(string json, string error)
    {
        long id = activeRequestId;
        if (id == 0) return;
        activeRequestId = 0;
        captureArmed = captureStarted = false;
        activeCancellation?.Cancel();
        activeCancellation?.Dispose();
        activeCancellation = null;
        if (error == null) ResponseReceived?.Invoke(id, generation, json);
        else RequestFailed?.Invoke(id, generation, error);
    }

    private void NetworkLoop()
    {
        // No Unity APIs or UnityEngine.Object references are used on this thread.
        try
        {
            AsyncIO.ForceDotNet.Force();
            while (workerRunning)
            {
                Request request;
                lock (gate) { request = pendingRequest; pendingRequest = null; }
                if (request == null) { workAvailable.WaitOne(50); continue; }
                if (request.Cancellation.IsCancellationRequested) continue;
                var result = new Completion { Id = request.Id, Generation = request.Generation };
                try
                {
                    // A fresh identity/socket per request also isolates replies from expired requests.
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
                        if (!sent) throw new TimeoutException("Request send timed out or was cancelled.");
                        NetMQMessage reply = null;
                        bool received = false;
                        while (CanContinue(request) && !received)
                            received = socket.TryReceiveMultipartMessage(PollTime(request), ref reply);
                        if (!received) throw new TimeoutException("Server response timed out or was cancelled.");
                        if (reply.FrameCount != 1) throw new InvalidOperationException("Expected one JSON reply frame.");
                        result.Json = reply[0].ConvertToString();
                    }
                }
                catch (Exception exception) { result.Error = exception.GetType().Name + ": " + exception.Message; }
                if (!request.Cancellation.IsCancellationRequested)
                    lock (gate) { completions.Enqueue(result); }
            }
        }
        catch (Exception exception) { lastWorkerError = exception.GetType().Name + ": " + exception.Message; }
        finally
        {
            workerRunning = false;
            if (destroyRequested) workAvailable.Dispose();
        }
        // Do not call global NetMQConfig.Cleanup here: other clients may still own sockets.
    }

    private bool CanContinue(Request request) => workerRunning && !request.Cancellation.IsCancellationRequested && Now < request.Deadline;
    private static TimeSpan PollTime(Request request) => TimeSpan.FromMilliseconds(Math.Max(0, Math.Min(50, (request.Deadline - Now) * 1000)));
    private static double SafeTimeout(float value, double fallback) => float.IsNaN(value) || float.IsInfinity(value) || value <= 0 ? fallback : value;

    private void OnDisable()
    {
        RenderPipelineManager.beginCameraRendering -= BeginCameraRendering;
        RenderPipelineManager.endCameraRendering -= EndCameraRendering;
        workerRunning = false;
        Finish(null, "VisionStreamer disabled.");
        generation++;
        lock (gate) { pendingRequest = null; completions.Clear(); }
        workAvailable.Set();
        if (networkThread != null && networkThread.IsAlive && !networkThread.Join(1000))
            Debug.LogWarning("[VisionStreamer] Network thread is still stopping; restart is blocked until it exits.", this);
        ReleaseTextures();
    }

    private void ReleaseTextures()
    {
        if (ownedCamera != null && ownedCamera.targetTexture == renderTexture)
        {
            ownedCamera.targetTexture = previousTarget;
            ownedCamera.aspect = previousAspect;
        }
        if (ownedDebug != null && ownedDebug.texture == renderTexture) ownedDebug.texture = previousDebugTexture;
        if (renderTexture != null) { renderTexture.Release(); Destroy(renderTexture); }
        if (texture2D != null) Destroy(texture2D);
        renderTexture = null; texture2D = null; ownedCamera = null; ownedDebug = null;
    }

    private void OnDestroy()
    {
        destroyRequested = true;
        if (networkThread == null || !networkThread.IsAlive) workAvailable.Dispose();
        // If a worker is still exiting, it disposes the signal in its finally block.
    }
}