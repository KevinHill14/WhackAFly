using System.Collections.Generic;
using UnityEngine;

namespace FlyAI
{
    // Lets the swatter's thumbstick work the menus: up and down step through the items (PLAY is highlighted first) and
    // pressing the stick selects the highlighted one. The mouse keeps working as usual.
    //
    // Unity's GUI buttons ignore clicks made in code, so each menu control also calls Hit. A press is matched to the
    // highlighted control while the menu is drawn and fires in the next layout pass; the repaint after that is skipped,
    // because the action may have changed what is on screen.
    public static class MenuCursor
    {
        const float PushAt = 0.55f;         // How far the stick must go to count as up or down.
        const float ReleaseAt = 0.3f;       // It must come back this far before the next single step.
        const float RepeatDelay = 0.45f;    // Holding it: wait this long, then keep stepping.
        const float RepeatEvery = 0.2f;

        public static bool RequireFocus = true;
        public static int Focus { get; private set; } = -1;

        static readonly List<bool> drawing = new List<bool>();  // Which controls are enabled, this repaint.
        static readonly List<bool> controls = new List<bool>(); // The same, from the last full repaint.
        static bool pressPending;
        static int controlIndex;
        static int armed = -1;
        static bool skipRepaint;
        static int screen = -1;
        static int heldDirection;
        static float nextRepeat;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            RequireFocus = true;
            screen = -1;
            controls.Clear();
            Reset();
        }

        // Back to the default item: PLAY on the main menu.
        public static void Reset()
        {
            Focus = -1;
            pressPending = false;
            armed = -1;
            skipRepaint = false;
            heldDirection = 0;
        }

        // Called every frame while the menu is open.
        public static void Tick()
        {
            if (RequireFocus && !Application.isFocused)
            {
                ImuReceiver.TakePress();
                return;
            }
            var y = ImuReceiver.LiveStick.y * ImuSwatter.StickForwardSign;
            var direction = y > PushAt ? -1 : y < -PushAt ? 1 : 0;   // Up the screen is toward the first item.
            if (direction == 0)
            {
                if (Mathf.Abs(y) < ReleaseAt) heldDirection = 0;
            }
            else if (direction != heldDirection)
            {
                heldDirection = direction;
                nextRepeat = Time.unscaledTime + RepeatDelay;
                Step(direction);
            }
            else if (Time.unscaledTime >= nextRepeat)
            {
                nextRepeat = Time.unscaledTime + RepeatEvery;
                Step(direction);
            }
            if (ImuReceiver.TakePress()) pressPending = true;
        }

        // Moves to the next enabled item, stopping at the ends of the list.
        static void Step(int direction)
        {
            for (var i = Focus + direction; i >= 0 && i < controls.Count; i += direction)
                if (controls[i])
                {
                    Focus = i;
                    return;
                }
        }

        // First thing in the menu's OnGUI. Returns true if this repaint must draw only the background.
        public static bool Begin(Event e, int currentScreen)
        {
            if (currentScreen != screen)
            {
                screen = currentScreen;
                Reset();
            }
            if (e.type == EventType.Layout || e.type == EventType.Repaint) controlIndex = 0;
            if (e.type != EventType.Repaint) return false;
            drawing.Clear();
            if (!skipRepaint) return false;
            skipRepaint = false;
            return true;
        }

        // After each menu control, with its rectangle. True when the stick's press selected it.
        // preferred marks the item highlighted first on this screen.
        public static bool Hit(Rect rect, bool preferred = false)
        {
            var e = Event.current;
            if (e.type != EventType.Layout && e.type != EventType.Repaint) return false;
            var index = controlIndex++;
            if (e.type == EventType.Layout)
            {
                if (armed != index) return false;
                armed = -1;
                skipRepaint = true;
                return true;
            }
            drawing.Add(GUI.enabled);
            if (preferred && Focus < 0) Focus = index;
            if (index != Focus) return false;
            if (ImuReceiver.IsLive) // Only show the highlight when there is a stick to move it.
            {
                var ring = new Rect(rect.x - 4f, rect.y - 4f, rect.width + 8f, rect.height + 8f);
                GUI.DrawTexture(ring, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, MenuStyle.Accent, 3f, 16f);
            }
            if (pressPending && GUI.enabled)
            {
                pressPending = false;
                armed = index;
            }
            return false;
        }

        // Last thing in the menu's OnGUI.
        public static void End()
        {
            if (Event.current.type != EventType.Repaint) return;
            pressPending = false;
            controls.Clear();
            controls.AddRange(drawing);
            if (Focus >= controls.Count) Focus = controls.Count - 1;
            if (Focus < 0 && controls.Count > 0) Focus = 0; // A screen with no preferred item starts at the top.
        }
    }
}
