using System;
using System.Collections;
using System.Threading;
using UnityEngine;
using NetMQ;
using NetMQ.Sockets;
using UnityEngine.UI;

public class VisionStreamer : MonoBehaviour
{
    public Camera visionCamera;
    public int imageWidth = 512;
    public int imageHeight = 512;
    public float sendIntervalSeconds = 1.0f;
    public string serverAddress = "tcp://localhost:5555";
    //public string currentObjective = "choose the best path to survive";
    public string currentObjective = "navigate safely down the street";

    public RawImage telaDeDebug;

    [Header("Gaze Control")]
    public FaceController faceController;
    public float turnAngleDegrees = 45f;
    public float slightTurnAngleDegrees = 20f;
    public float holdDurationSeconds = 5f;
    public float returnToCenterWaitSeconds = 1f;

    private Texture2D texture2D;
    private RenderTexture renderTexture;
    
    private Thread networkThread;
    private bool isRunning = true;
    private byte[] imageToSend = null;
    private readonly object lockObject = new object();
    private string receivedDirection = null;
    private bool hasNewDirection = false;

    void Start()
    {
        Debug.Log("[IA Visual] Iniciando sistema de visão...");
        
        texture2D = new Texture2D(imageWidth, imageHeight, TextureFormat.RGB24, false);
        renderTexture = new RenderTexture(imageWidth, imageHeight, 24);
        
        if (telaDeDebug != null) telaDeDebug.texture = renderTexture;

        if (visionCamera != null)
        {
            visionCamera.targetTexture = renderTexture;
            Debug.Log("[IA Visual] Câmera conectada à textura virtual.");
        }
        else
        {
            Debug.LogError("[IA Visual] ERRO: Nenhuma câmera foi atribuída no Inspector!");
        }

        networkThread = new Thread(NetworkLoop);
        networkThread.Start();

        StartCoroutine(GazeCycleLoop());
    }

    // Cycle: re-center -> capture frame -> wait for server's direction -> turn -> hold -> repeat
    IEnumerator GazeCycleLoop()
    {
        Debug.Log("[GazeCycleLoop] Started.");

        while (isRunning)
        {
            RecenterGaze();
            yield return new WaitForSeconds(returnToCenterWaitSeconds);

            yield return new WaitForEndOfFrame();
            byte[] bytes = CaptureFrame();
            if (bytes == null)
            {
                yield return new WaitForSeconds(sendIntervalSeconds);
                continue;
            }

            lock (lockObject)
            {
                imageToSend = bytes;
                hasNewDirection = false;
                receivedDirection = null;
            }
            Debug.Log("[GazeCycleLoop] Frame captured, waiting for the server's direction...");

            string direction = null;
            while (isRunning && direction == null)
            {
                lock (lockObject)
                {
                    if (hasNewDirection) direction = receivedDirection;
                }
                if (direction == null) yield return null;
            }
            if (!isRunning) yield break;

            Debug.Log($"[GazeCycleLoop] Turning head towards: {direction}");
            TurnGazeTo(direction);
            yield return new WaitForSeconds(holdDurationSeconds);

            yield return new WaitForSeconds(sendIntervalSeconds);
        }
    }

    private byte[] CaptureFrame()
    {
        if (visionCamera == null) return null;

        try
        {
            // Força a leitura da imagem
            RenderTexture.active = renderTexture;
            texture2D.ReadPixels(new Rect(0, 0, imageWidth, imageHeight), 0, 0);
            texture2D.Apply();
            RenderTexture.active = null;

            byte[] bytes = texture2D.EncodeToJPG(75);
            if (bytes == null || bytes.Length == 0)
            {
                Debug.LogWarning("[GazeCycleLoop] Frame captured, but bytes are empty (black or null image).");
                return null;
            }

            Debug.Log($"[GazeCycleLoop] Frame captured successfully! Size: {bytes.Length} bytes.");
            return bytes;
        }
        catch (Exception e)
        {
            // Se der erro de URP ou memória, ele avisa no console em vez de morrer em silêncio
            Debug.LogError($"[GazeCycleLoop] Fatal error while capturing: {e.Message}\n{e.StackTrace}");
            return null;
        }
    }

    private void RecenterGaze()
    {
        if (faceController == null) return;
        faceController.SetManualGazeDirection(faceController.InitialNeckForward);
    }

    private void TurnGazeTo(string direction)
    {
        if (faceController == null) return;
        float yaw = DirectionToYaw(direction);
        Vector3 targetDirection = Quaternion.Euler(0f, yaw, 0f) * faceController.InitialNeckForward;
        faceController.SetManualGazeDirection(targetDirection);
    }

    private float DirectionToYaw(string direction)
    {
        switch (direction)
        {
            case "LEFT": return -turnAngleDegrees;
            case "SLIGHT_LEFT": return -slightTurnAngleDegrees;
            case "SLIGHT_RIGHT": return slightTurnAngleDegrees;
            case "RIGHT": return turnAngleDegrees;
            case "CENTER":
                return 0f;
            default:
                Debug.LogWarning($"[GazeCycleLoop] Unknown direction '{direction}', keeping head centered.");
                return 0f;
        }
    }

    void NetworkLoop()
    {
        AsyncIO.ForceDotNet.Force();
        Debug.Log("[NetworkLoop] Iniciado com sucesso.");

        while (isRunning)
        {
            try
            {
                // Se houver um timeout, recriamos o socket do zero para evitar curto-circuito
                using (var clientSocket = new RequestSocket())
                {
                    clientSocket.Connect(serverAddress);
                    bool isSocketValid = true;

                    while (isRunning && isSocketValid)
                    {
                        byte[] bytesToSend = null;
                        
                        lock (lockObject)
                        {
                            if (imageToSend != null)
                            {
                                bytesToSend = imageToSend;
                                imageToSend = null; 
                            }
                        }

                        if (bytesToSend != null)
                        {
                            Debug.Log("[NetworkLoop] Despachando frame para o Python...");
                            clientSocket.SendMoreFrame(currentObjective);
                            clientSocket.SendFrame(bytesToSend);

                            bool received = false;
                            string response = null;
                            
                            Debug.Log("[NetworkLoop] Aguardando modelo...");
                            for (int i = 0; i < 150 && isRunning; i++) // Timeout de 15 segundos
                            {
                                if (clientSocket.TryReceiveFrameString(TimeSpan.FromMilliseconds(100), out response))
                                {
                                    received = true;
                                    break;
                                }
                            }

                            if (received)
                            {
                                Debug.Log($"[NetworkLoop] >>> Direção recebida com sucesso: {response} <<<");
                                lock (lockObject)
                                {
                                    receivedDirection = response;
                                    hasNewDirection = true;
                                }
                            }
                            else if (isRunning)
                            {
                                Debug.LogWarning("[NetworkLoop] TIMEOUT! O Python demorou mais de 15s. Resetando socket para não travar...");
                                isSocketValid = false; // Quebra o while interno e recria o socket limpo
                            }
                        }
                        else
                        {
                            Thread.Sleep(10);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[NetworkLoop] Erro de rede: {e.Message}");
                Thread.Sleep(1000); // Aguarda um pouco antes de tentar reconectar
            }
        }
        
        NetMQConfig.Cleanup();
        Debug.Log("[NetworkLoop] Desligado.");
    }

    void OnDestroy()
    {
        isRunning = false;
        
        if (visionCamera != null)
            visionCamera.targetTexture = null;

        if (renderTexture != null)
            renderTexture.Release();
        
        if (networkThread != null && networkThread.IsAlive)
            networkThread.Join(1000);
    }
}