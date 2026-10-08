using System;
using UnityEngine;

public class InferenceClient : MonoBehaviour
{
    private InferenceRequester inferenceRequester;
    public string socketID = "5555";

    private void OnEnable() => InitializeServer();

    private void Update()
    {
        if (inferenceRequester != null && (inferenceRequester.NeedReset || inferenceRequester.Failure != null))
        {
            ResetServer();
        }
    }

    public void InitializeServer()
    {
        if (inferenceRequester != null) return;
        inferenceRequester = new InferenceRequester(socketID);
        try { inferenceRequester.Start(); }
        catch
        {
            inferenceRequester = null;
            throw;
        }
    }

    public void Infer(byte[] input, Action<byte[]> onOutputReceived, Action<Exception> fallback)
    {
        if (inferenceRequester == null)
        {
            fallback?.Invoke(new InvalidOperationException("InferenceClient is disabled."));
            return;
        }
        inferenceRequester.SetOnOutputReceivedListener(onOutputReceived, fallback);
        inferenceRequester.SendInput(input);
    }

    private void ResetServer()
    {
        Debug.Log("NetMQ socket crash detected - resetting");
        StopServer();
        if (inferenceRequester == null && isActiveAndEnabled) InitializeServer();
    }

    private void StopServer()
    {
        if (inferenceRequester == null) return;
        try
        {
            inferenceRequester.Stop();
            inferenceRequester = null;
        }
        catch (Exception exception) { Debug.LogException(exception, this); }
    }

    private void OnDisable() => StopServer();
    private void OnDestroy() => StopServer();
    private void OnApplicationQuit() => StopServer();
}