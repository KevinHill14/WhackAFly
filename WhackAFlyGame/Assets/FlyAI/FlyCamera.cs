using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace FlyAI
{
    // Test camera. Hold right mouse to look, WASD to move, Q/E down/up, Shift for speed.
    // F = fog on/off, B = object outlines, M = props and lights, G = fly landing spots,
    // V = sound sources, L = rule-based vs AI layout, C = ceiling, R = reload scan,
    // N = test blackout, U = test power surge, H = test passing headlights, K = test lightning,
    // hold T = preview fly-nearby tension.
    public class FlyCamera : MonoBehaviour
    {
        public float speed = 3f;
        public float lookSensitivity = 0.12f;
        public bool flightEnabled = true;

        float yaw;
        float pitch;
        RoomLoader loader;

        struct Commands
        {
            public bool fog, boxes, markers, landing, sound, layout, ceiling, reload, blackout, surge, headlights, lightning, tension;
        }

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
            ReadInput(out var move, out var look, out var looking, out var fast, out var commands);

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

            if (commands.fog) RoomAtmosphere.ToggleFog();
            if (RoomTension.Current != null) RoomTension.Current.SetDebugHeld(commands.tension);
            if (RoomEvents.Current != null)
            {
                if (commands.blackout) RoomEvents.Current.TriggerBlackout();
                if (commands.surge) RoomEvents.Current.TriggerSurge();
                if (commands.lightning) RoomEvents.Current.TriggerLightning();
                if (commands.headlights) RoomEvents.Current.TriggerHeadlights();
            }
            if (loader == null) return;
            if (commands.boxes) loader.ToggleDebugBoxes();
            if (commands.markers) loader.TogglePlacementMarkers();
            if (commands.landing) loader.ToggleLandingMarkers();
            if (commands.sound) loader.ToggleSoundMarkers();
            if (commands.layout) loader.ToggleLayout();
            if (commands.ceiling) loader.ToggleCeiling();
            if (commands.reload) loader.ReloadScan();
        }

        static void ReadInput(out Vector3 move, out Vector2 look, out bool looking, out bool fast, out Commands commands)
        {
            move = Vector3.zero;
            look = Vector2.zero;
            looking = fast = false;
            commands = default;

#if ENABLE_INPUT_SYSTEM
            var keyboard = Keyboard.current;
            var mouse = Mouse.current;
            if (keyboard != null)
            {
                move.x = (keyboard.dKey.isPressed ? 1 : 0) - (keyboard.aKey.isPressed ? 1 : 0);
                move.y = (keyboard.eKey.isPressed ? 1 : 0) - (keyboard.qKey.isPressed ? 1 : 0);
                move.z = (keyboard.wKey.isPressed ? 1 : 0) - (keyboard.sKey.isPressed ? 1 : 0);
                fast = keyboard.leftShiftKey.isPressed;
                commands.boxes = keyboard.bKey.wasPressedThisFrame;
                commands.markers = keyboard.mKey.wasPressedThisFrame;
                commands.fog = keyboard.fKey.wasPressedThisFrame;
                commands.blackout = keyboard.nKey.wasPressedThisFrame;
                commands.surge = keyboard.uKey.wasPressedThisFrame;
                commands.lightning = keyboard.kKey.wasPressedThisFrame;
                commands.headlights = keyboard.hKey.wasPressedThisFrame;
                commands.tension = keyboard.tKey.isPressed;
                commands.landing = keyboard.gKey.wasPressedThisFrame;
                commands.sound = keyboard.vKey.wasPressedThisFrame;
                commands.layout = keyboard.lKey.wasPressedThisFrame;
                commands.ceiling = keyboard.cKey.wasPressedThisFrame;
                commands.reload = keyboard.rKey.wasPressedThisFrame;
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
            commands.boxes = Input.GetKeyDown(KeyCode.B);
            commands.markers = Input.GetKeyDown(KeyCode.M);
            commands.fog = Input.GetKeyDown(KeyCode.F);
            commands.blackout = Input.GetKeyDown(KeyCode.N);
            commands.surge = Input.GetKeyDown(KeyCode.U);
            commands.lightning = Input.GetKeyDown(KeyCode.K);
            commands.headlights = Input.GetKeyDown(KeyCode.H);
            commands.tension = Input.GetKey(KeyCode.T);
            commands.landing = Input.GetKeyDown(KeyCode.G);
            commands.sound = Input.GetKeyDown(KeyCode.V);
            commands.layout = Input.GetKeyDown(KeyCode.L);
            commands.ceiling = Input.GetKeyDown(KeyCode.C);
            commands.reload = Input.GetKeyDown(KeyCode.R);
            looking = Input.GetMouseButton(1);
            look = new Vector2(Input.GetAxisRaw("Mouse X"), Input.GetAxisRaw("Mouse Y")) * 20f;
#endif
        }
    }
}
