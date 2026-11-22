using UnityEngine;
using UnityEngine.InputSystem;

namespace FlyAI
{
    public struct SwatterFrame
    {
        public float forward;
        public float turn;
        public Vector2 aimDelta;
        public bool swing;
        public float swingStrength;
        // Which way the real swing went, in degrees of aim (x = right, y = up). The swatter sweeps that far along it
        // and hits the first thing in the way. Zero means the classic chop straight along the aim (mouse, keyboard).
        public Vector2 swingStroke;
        public bool ping;
        public bool quickTurn;
        public bool recenter;
    }

    // The real swatter will implement this too, so the player code does not change.
    public interface ISwatterInput
    {
        SwatterFrame Read();
    }

    // Keyboard and mouse as before, plus swings from the real swatter. The ESP32 sends
    // events to the backend over Wi-Fi and the backend forwards them to this client.
    public class NetSwatter : ISwatterInput
    {
        const float MinRate = 4f;     // rad/s, same as SWING_THRESHOLD in the firmware
        const float FullRate = 12f;   // rad/s that counts as a full-strength swing
        readonly ISwatterInput fallback;

        public NetSwatter(ISwatterInput fallback) => this.fallback = fallback;

        public SwatterFrame Read()
        {
            var frame = fallback.Read();
            while (BackendClient.TakeSwatterEvent(out var e))
            {
                if (e.evt != "swing") continue;
                frame.swing = true;
                frame.swingStrength = Mathf.Lerp(0.4f, 1f, e.strength > 0f ? Mathf.Clamp01(e.strength) : Mathf.InverseLerp(MinRate, FullRate, e.rate));
                frame.swingStroke = ImuSwatter.StrokeFor(e.axis, e.dir, frame.swingStrength);
            }
            return frame;
        }
    }

    // W/S walk, A/D turn, mouse aims, left click swings.
    // Space acts like the stick click: tap = ping, double tap = 180, hold = recenter.
    public class KeyboardMouseSwatter : ISwatterInput
    {
        const float DoubleTapWindow = 0.3f;
        const float HoldTime = 0.6f;
        const float MouseSensitivity = 0.08f;

        float pressedAt;
        float lastTapAt = -10f;
        bool holdFired;
        bool pingPending;
        bool secondTap;

        public SwatterFrame Read()
        {
            var frame = new SwatterFrame();
            var keyboard = Keyboard.current;
            var mouse = Mouse.current;
            var now = Time.unscaledTime;

            if (keyboard != null)
            {
                frame.forward = (keyboard.wKey.isPressed ? 1f : 0f) - (keyboard.sKey.isPressed ? 1f : 0f);
                frame.turn = (keyboard.dKey.isPressed ? 1f : 0f) - (keyboard.aKey.isPressed ? 1f : 0f);

                var space = keyboard.spaceKey;
                if (space.wasPressedThisFrame)
                {
                    pressedAt = now;
                    holdFired = false;
                    secondTap = pingPending;
                    pingPending = false;
                }
                if (space.isPressed && !holdFired && now - pressedAt >= HoldTime)
                {
                    holdFired = true;
                    secondTap = false;
                    pingPending = false;
                    frame.recenter = true;
                }
                if (space.wasReleasedThisFrame && !holdFired)
                {
                    if (secondTap)
                    {
                        frame.quickTurn = true;
                        secondTap = false;
                        lastTapAt = -10f;
                    }
                    else
                    {
                        lastTapAt = now;
                        pingPending = true;
                    }
                }
                if (pingPending && now - lastTapAt >= DoubleTapWindow)
                {
                    pingPending = false;
                    frame.ping = true;
                }
            }

            if (mouse != null)
            {
                var delta = mouse.delta.ReadValue();
                frame.aimDelta = delta * MouseSensitivity;
                if (mouse.leftButton.wasPressedThisFrame)
                {
                    frame.swing = true;
                    frame.swingStrength = Mathf.Clamp01(0.55f + delta.magnitude * 0.01f);
                }
            }
            return frame;
        }
    }
}
