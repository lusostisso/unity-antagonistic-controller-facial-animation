using System;
using System.Threading;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;

[DisallowMultipleComponent]
public class VisionStreamer : MonoBehaviour
{
    public const float RequestTimeoutSeconds = 10f;

    public Camera visionCamera;
    [Min(1)]
    public int imageWidth = 512;
    [Min(1)]
    public int imageHeight = 512;
    public string serverAddress = "tcp://localhost:5555";
    public string currentObjective = "navigate safely down the street";
    public RawImage telaDeDebug;
    [Range(1, 100)]
    public int jpegQuality = 75;
    [Min(0.1f)]
    public float captureTimeoutSeconds = 5f;

    public event Action<long, Camera> CapturePrepared;

    public long ActiveRequestId { get; private set; }
    public bool IsBusy => ActiveRequestId != 0 || completion != null || (requester != null && requester.IsBusy);
    public bool IsReady => isActiveAndEnabled && requester != null && requester.IsRunning && requester.Failure == null;
    public string LastError { get; private set; }

    private Texture2D texture2D;
    private RenderTexture renderTexture;
    private Camera configuredCamera;
    private RenderTexture previousTarget;
    private float previousAspect;
    private RawImage configuredDebugImage;
    private Texture previousDebugTexture;
    private VisionRequester requester;
    private VisionRequestResult completion;
    private CancellationTokenSource cancellation;
    private long nextRequestId;
    private bool captureArmed;
    private bool capturePrepared;
    private int preparedFrame;
    private double captureDeadline;
    private string pendingAddress;
    private string pendingObjective;

    private void OnEnable()
    {
        try
        {
            if (!StopRequester()) throw new InvalidOperationException("O worker anterior ainda não terminou.");
            ValidateCamera();
            configuredCamera = visionCamera;
            previousTarget = configuredCamera.targetTexture;
            previousAspect = configuredCamera.aspect;
            renderTexture = new RenderTexture(Mathf.Max(1, imageWidth), Mathf.Max(1, imageHeight), 24);
            if (!renderTexture.Create()) throw new InvalidOperationException("Não foi possível criar a textura de captura.");
            texture2D = new Texture2D(renderTexture.width, renderTexture.height, TextureFormat.RGB24, false);
            configuredCamera.targetTexture = renderTexture;
            configuredCamera.aspect = (float)renderTexture.width / renderTexture.height;
            configuredDebugImage = telaDeDebug;
            if (configuredDebugImage != null)
            {
                previousDebugTexture = configuredDebugImage.texture;
                configuredDebugImage.texture = renderTexture;
            }

            requester = new VisionRequester();
            requester.Start();
            RenderPipelineManager.beginCameraRendering += BeginCameraRendering;
            RenderPipelineManager.endCameraRendering += EndCameraRendering;
            Camera.onPreCull += BeginBuiltinCamera;
            Camera.onPostRender += EndBuiltinCamera;
            LastError = null;
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
            Debug.LogError("[VisionStreamer] " + LastError, this);
            enabled = false;
        }
    }

    public bool TryCaptureAndSend()
    {
        PumpResults();
        if (!IsReady || IsBusy) return false;
        try
        {
            ValidateCamera();
            if (visionCamera != configuredCamera || configuredCamera.targetTexture != renderTexture)
                throw new InvalidOperationException("A câmera/textura mudou; reative o VisionStreamer.");
            if (!configuredCamera.isActiveAndEnabled)
                throw new InvalidOperationException("A câmera de captura está desativada.");
            if (string.IsNullOrWhiteSpace(serverAddress)) throw new InvalidOperationException("Endereço do servidor vazio.");

            cancellation = new CancellationTokenSource();
            ActiveRequestId = ++nextRequestId;
            pendingAddress = serverAddress;
            pendingObjective = currentObjective;
            captureDeadline = Time.realtimeSinceStartupAsDouble + SafeTimeout(captureTimeoutSeconds, 5f);
            captureArmed = true;
            capturePrepared = false;
            LastError = null;
            return true;
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
            Debug.LogWarning("[VisionStreamer] " + LastError, this);
            return false;
        }
    }

    public bool TryGetResult(out VisionRequestResult result)
    {
        PumpResults();
        result = completion;
        if (result == null) return false;
        completion = null;
        return true;
    }

    public void CancelRequest(long requestId)
    {
        if (completion != null && completion.RequestId == requestId) completion = null;
        if (ActiveRequestId != requestId || requestId == 0) return;
        cancellation?.Cancel();
        Complete(new VisionRequestResult(requestId, VisionRequestStatus.Cancelled, error: "Pedido cancelado."));
    }

    private void Update() => PumpResults();

    private void PumpResults()
    {
        if (requester != null && requester.TryGetResult(out VisionRequestResult result))
        {
            if (ActiveRequestId == result.RequestId) Complete(result);
        }
        if (ActiveRequestId == 0) return;
        if (!IsReady)
        {
            cancellation?.Cancel();
            Complete(new VisionRequestResult(ActiveRequestId, VisionRequestStatus.Error,
                error: requester?.Failure?.Message ?? "O transporte foi interrompido."));
        }
        else if (captureArmed && Time.realtimeSinceStartupAsDouble >= captureDeadline)
        {
            cancellation?.Cancel();
            Complete(new VisionRequestResult(ActiveRequestId, VisionRequestStatus.Timeout,
                error: "A câmera não produziu um frame dentro do prazo de captura."));
        }
    }

    private void BeginCameraRendering(ScriptableRenderContext context, Camera camera) => PrepareCapture(camera);
    private void EndCameraRendering(ScriptableRenderContext context, Camera camera) => SendCapturedFrame(camera);

    private void BeginBuiltinCamera(Camera camera)
    {
        if (GraphicsSettings.currentRenderPipeline == null) PrepareCapture(camera);
    }

    private void EndBuiltinCamera(Camera camera)
    {
        if (GraphicsSettings.currentRenderPipeline == null) SendCapturedFrame(camera);
    }

    private void PrepareCapture(Camera camera)
    {
        if (!captureArmed || capturePrepared || camera != configuredCamera) return;
        try
        {
            camera.aspect = (float)renderTexture.width / renderTexture.height;
            preparedFrame = Time.frameCount;
            CapturePrepared?.Invoke(ActiveRequestId, camera);
            capturePrepared = true;
        }
        catch (Exception exception)
        {
            Complete(new VisionRequestResult(ActiveRequestId, VisionRequestStatus.Error,
                error: "Falha ao preparar o frame: " + exception.Message));
        }
    }

    private void SendCapturedFrame(Camera camera)
    {
        if (!captureArmed || !capturePrepared || camera != configuredCamera || preparedFrame != Time.frameCount) return;
        try
        {
            if (camera.targetTexture != renderTexture) throw new InvalidOperationException("A textura da câmera foi alterada durante a captura.");
            RenderTexture previous = RenderTexture.active;
            byte[] bytes;
            try
            {
                RenderTexture.active = renderTexture;
                texture2D.ReadPixels(new Rect(0, 0, renderTexture.width, renderTexture.height), 0, 0, false);
                texture2D.Apply(false, false);
                bytes = texture2D.EncodeToJPG(Mathf.Clamp(jpegQuality, 1, 100));
            }
            finally { RenderTexture.active = previous; }

            if (!requester.TrySend(ActiveRequestId, bytes, pendingObjective, pendingAddress, RequestTimeoutSeconds, cancellation.Token))
                throw new InvalidOperationException("O worker não aceitou o frame.");
            captureArmed = false;
            capturePrepared = false;
        }
        catch (Exception exception)
        {
            Complete(new VisionRequestResult(ActiveRequestId, VisionRequestStatus.Error,
                error: "Falha ao capturar/enviar o frame: " + exception.Message));
        }
    }

    private void Complete(VisionRequestResult result)
    {
        captureArmed = false;
        capturePrepared = false;
        ActiveRequestId = 0;
        cancellation?.Dispose();
        cancellation = null;
        completion = result;
        LastError = result.Error;
    }

    private void ValidateCamera()
    {
        if (visionCamera == null) throw new InvalidOperationException("Atribua a câmera no Inspector.");
        if (visionCamera.stereoEnabled) throw new InvalidOperationException("Use uma câmera de captura sem stereo/XR.");
        Rect rect = visionCamera.rect;
        if (rect != new Rect(0, 0, 1, 1)) throw new InvalidOperationException("A câmera de captura deve usar o viewport completo.");
        UniversalAdditionalCameraData data = visionCamera.GetComponent<UniversalAdditionalCameraData>();
        if (data != null && (data.renderType != CameraRenderType.Base || (data.cameraStack != null && data.cameraStack.Count != 0)))
            throw new InvalidOperationException("Use uma câmera Base dedicada, sem câmeras na stack.");
    }

    private static float SafeTimeout(float seconds, float fallback)
    {
        return float.IsNaN(seconds) || float.IsInfinity(seconds) || seconds <= 0
            ? fallback : Mathf.Min(seconds, 86400f);
    }

    private bool StopRequester()
    {
        if (requester == null) return true;
        try
        {
            requester.Stop();
            requester = null;
            return true;
        }
        catch (Exception exception)
        {
            Debug.LogError("[VisionStreamer] Falha ao encerrar o worker: " + exception.Message, this);
            return false;
        }
    }

    private void Shutdown()
    {
        RenderPipelineManager.beginCameraRendering -= BeginCameraRendering;
        RenderPipelineManager.endCameraRendering -= EndCameraRendering;
        Camera.onPreCull -= BeginBuiltinCamera;
        Camera.onPostRender -= EndBuiltinCamera;
        cancellation?.Cancel();
        StopRequester();
        cancellation?.Dispose();
        cancellation = null;
        captureArmed = false;
        capturePrepared = false;
        ActiveRequestId = 0;
        completion = null;

        if (configuredCamera != null && configuredCamera.targetTexture == renderTexture)
        {
            configuredCamera.targetTexture = previousTarget;
            configuredCamera.aspect = previousAspect;
        }
        if (configuredDebugImage != null && configuredDebugImage.texture == renderTexture)
            configuredDebugImage.texture = previousDebugTexture;
        if (texture2D != null) Destroy(texture2D);
        if (renderTexture != null)
        {
            renderTexture.Release();
            Destroy(renderTexture);
        }
        texture2D = null;
        renderTexture = null;
        configuredCamera = null;
        configuredDebugImage = null;
    }

    private void OnDisable() => Shutdown();
    private void OnDestroy() => Shutdown();
    private void OnApplicationQuit() => Shutdown();
}