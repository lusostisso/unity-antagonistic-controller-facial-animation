using System;
using UnityEditor;
using UnityEngine;

/// <summary>Stops workers while their managed domain is still valid, not during Mono's unload.</summary>
[InitializeOnLoad]
public static class NetMQEditorLifecycle
{
    static NetMQEditorLifecycle()
    {
        AssemblyReloadEvents.beforeAssemblyReload += Shutdown;
        EditorApplication.playModeStateChanged += OnPlayModeChanged;
        EditorApplication.quitting += Shutdown;
    }

    private static void OnPlayModeChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.ExitingPlayMode) Shutdown();
    }

    private static void Shutdown()
    {
        try { NetMQRuntime.ShutdownAll(); }
        catch (Exception exception) { Debug.LogException(exception); }
    }
}