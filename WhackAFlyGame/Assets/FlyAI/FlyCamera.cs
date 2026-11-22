using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace FlyAI
{
    // Test camera. Hold right mouse to look, WASD to move, Q/E down/up, Shift for speed.
    // Any time: R = reveal every placement (object outlines, props and lights, fly landing spots, sound sources),
    // T = trigger the next room event (headlights, lightning, blackout, power surge, then round again).
    // In the player's view (not this camera, where Q/E fly): E = reset the round, Q = quit the game, M = main menu.
    public class FlyCamera : MonoBehaviour
    {
        public float speed = 3f;
        public float lookSensitivity = 0.12f;
        public bool flightEnabled = true;

        float yaw;
        float pitch;
        RoomLoader loader;

        void Start()
        {
            loader = FindFirstObjectByType<RoomLoader>();
            SyncRotation();
        }

        public void SyncRotation()
        {
            var angles = transform.eulerAngles;
            yaw = angles.y;
            pitch = angles.x > 180f ? angles.x - 360f : angles.x;
        }

        void Update()
        {
            ReadInput(out var move, out var look, out var looking, out var fast, out var reveal, out var nextEvent,
                      out var resetRound, out var quit, out var menu);

            if (flightEnabled)
            {
                if (looking)
                {
                    yaw += look.x * lookSensitivity;
                    pitch = Mathf.Clamp(pitch - look.y * lookSensitivity, -89f, 89f);
                    transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
                }

                var velocity = transform.right * move.x + Vector3.up * move.y + transform.forward * move.z;
                transform.position += Vector3.ClampMagnitude(velocity, 1f) * (speed * (fast ? 2.5f : 1f) * Time.unscaledDeltaTime);
            }

            if (MainMenu.Active) return;
            if (nextEvent && RoomEvents.Current != null)
            {
                var name = RoomEvents.Current.TriggerNext();
                var player = FindFirstObjectByType<PlayerController>();
                if (player != null) player.ShowMessage(name != null ? $"Event: {name}" : "Event: wait for the last one to finish");
            }
            if (reveal && loader != null) loader.ToggleReveal();
            if (flightEnabled) return;   // flying this camera, Q and E move it down and up
            if (resetRound && loader != null) loader.ReloadScan();   // the same as Play again: a new round with a new fly
            else if (menu) MainMenu.Open();
            else if (quit) MainMenu.Quit();
        }

        static void ReadInput(out Vector3 move, out Vector2 look, out bool looking, out bool fast, out bool reveal, out bool nextEvent,
                              out bool resetRound, out bool quit, out bool menu)
        {
            move = Vector3.zero;
            look = Vector2.zero;
            looking = fast = reveal = nextEvent = resetRound = quit = menu = false;

#if ENABLE_INPUT_SYSTEM
            var keyboard = Keyboard.current;
            var mouse = Mouse.current;
            if (keyboard != null && !Leaderboard.TypingName)
            {
                move.x = (keyboard.dKey.isPressed ? 1 : 0) - (keyboard.aKey.isPressed ? 1 : 0);
                move.y = (keyboard.eKey.isPressed ? 1 : 0) - (keyboard.qKey.isPressed ? 1 : 0);
                move.z = (keyboard.wKey.isPressed ? 1 : 0) - (keyboard.sKey.isPressed ? 1 : 0);
                fast = keyboard.leftShiftKey.isPressed;
                reveal = keyboard.rKey.wasPressedThisFrame;
                nextEvent = keyboard.tKey.wasPressedThisFrame;
                resetRound = keyboard.eKey.wasPressedThisFrame;
                quit = keyboard.qKey.wasPressedThisFrame;
                menu = keyboard.mKey.wasPressedThisFrame;
            }
            if (mouse != null)
            {
                looking = mouse.rightButton.isPressed;
                look = mouse.delta.ReadValue();
            }
#else
            move.x = (Input.GetKey(KeyCode.D) ? 1 : 0) - (Input.GetKey(KeyCode.A) ? 1 : 0);
            move.y = (Input.GetKey(KeyCode.E) ? 1 : 0) - (Input.GetKey(KeyCode.Q) ? 1 : 0);
            move.z = (Input.GetKey(KeyCode.W) ? 1 : 0) - (Input.GetKey(KeyCode.S) ? 1 : 0);
            fast = Input.GetKey(KeyCode.LeftShift);
            reveal = Input.GetKeyDown(KeyCode.R);
            nextEvent = Input.GetKeyDown(KeyCode.T);
            resetRound = Input.GetKeyDown(KeyCode.E);
            quit = Input.GetKeyDown(KeyCode.Q);
            menu = Input.GetKeyDown(KeyCode.M);
            looking = Input.GetMouseButton(1);
            look = new Vector2(Input.GetAxisRaw("Mouse X"), Input.GetAxisRaw("Mouse Y")) * 20f;
#endif
        }
    }
}
