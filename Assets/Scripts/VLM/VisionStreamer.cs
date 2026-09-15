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

    private Texture2D texture2D;
    private RenderTexture renderTexture;
    
    private Thread networkThread;
    private bool isRunning = true;
    private byte[] imageToSend = null;
    private readonly object lockObject = new object();

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

        StartCoroutine(CaptureLoop());
    }

    IEnumerator CaptureLoop()
    {
        WaitForSeconds waitTime = new WaitForSeconds(sendIntervalSeconds);
        Debug.Log("[CaptureLoop] Iniciado com sucesso.");

        while (isRunning)
        {
            yield return new WaitForEndOfFrame();

            try
            {
                if (visionCamera != null)
                {
                    // Força a leitura da imagem
                    RenderTexture.active = renderTexture;
                    texture2D.ReadPixels(new Rect(0, 0, imageWidth, imageHeight), 0, 0);
                    texture2D.Apply();
                    RenderTexture.active = null;

                    byte[] bytes = texture2D.EncodeToJPG(75);
                    
                    if (bytes != null && bytes.Length > 0)
                    {
                        Debug.Log($"[CaptureLoop] Frame capturado com sucesso! Tamanho: {bytes.Length} bytes.");
                        lock (lockObject)
                        {
                            imageToSend = bytes;
                        }
                    }
                    else
                    {
                        Debug.LogWarning("[CaptureLoop] Frame capturado, mas os bytes estão vazios (imagem preta ou nula).");
                    }
                }
            }
            catch (Exception e)
            {
                // Se der erro de URP ou memória, ele avisa no console em vez de morrer em silêncio
                Debug.LogError($"[CaptureLoop] Erro fatal ao tentar capturar: {e.Message}\n{e.StackTrace}");
            }

            yield return waitTime;
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