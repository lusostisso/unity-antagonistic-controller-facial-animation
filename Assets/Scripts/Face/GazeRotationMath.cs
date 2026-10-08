using System;
using UnityEngine;

/// <summary>Pitch/yaw about a saved neutral frame. No world-up LookRotation, roll or absolute Euler clamping.</summary>
public static class GazeRotationMath
{
    // x = pitch (positive down), y = yaw (positive right), both in degrees.
    public static Vector2 Angles(Vector3 localDirection)
    {
        double horizontal = Math.Sqrt((double)localDirection.x * localDirection.x + (double)localDirection.z * localDirection.z);
        if (horizontal < 1e-10 && Math.Abs(localDirection.y) < 1e-10) return Vector2.zero;
        return new Vector2((float)(-Math.Atan2(localDirection.y, horizontal) * 180 / Math.PI),
            (float)(Math.Atan2(localDirection.x, localDirection.z) * 180 / Math.PI));
    }

    public static Quaternion Inverse(Quaternion rotation)
    {
        float length = rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w;
        if (length < 1e-10f) return Quaternion.identity;
        return new Quaternion(-rotation.x / length, -rotation.y / length, -rotation.z / length, rotation.w / length);
    }

    public static Vector2 Clamp(Vector2 angles, float up, float down, float yaw) => new Vector2(
        Mathf.Clamp(angles.x, -Mathf.Max(0, up), Mathf.Max(0, down)),
        Mathf.Clamp(angles.y, -Mathf.Max(0, yaw), Mathf.Max(0, yaw)));

    public static Vector2 Step(Vector2 current, Vector2 desired, float maxDegrees)
    {
        if (maxDegrees <= 0) return current;
        Vector2 delta = desired - current;
        float distance = (float)Math.Sqrt((double)delta.x * delta.x + (double)delta.y * delta.y);
        return distance <= maxDegrees || distance < 1e-6f ? desired : current + delta * (maxDegrees / distance);
    }

    public static Quaternion LocalRotation(Quaternion neutral, Vector2 angles)
    {
        double pitch = angles.x * Math.PI / 360, yaw = angles.y * Math.PI / 360;
        var pitchRotation = new Quaternion((float)Math.Sin(pitch), 0, 0, (float)Math.Cos(pitch));
        var yawRotation = new Quaternion(0, (float)Math.Sin(yaw), 0, (float)Math.Cos(yaw));
        return neutral * yawRotation * pitchRotation;
    }
}