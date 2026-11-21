using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FlyAI
{
    public struct SwingResult
    {
        public float time;
        public float strength;
        public bool hit;
        public string target;
        public RoomPartKind kind;
        public int damage;
        public Vector3 point;
    }

    // First-person player: tank movement, swatter aim, flashlight, swings and the damage bill.
    // Tab switches to the free debug camera and back.
    [RequireComponent(typeof(CharacterController))]
    public class PlayerController : MonoBehaviour
    {
        const float EyeHeight = 1.6f;
        const float BodyHeight = 1.75f;
        const float BodyRadius = 0.25f;
        const float MaxAimYaw = 28f;
        const float MaxAimPitch = 84f;
        const float ViewPitchShare = 0.65f;
        const float SwingDuration = 0.18f;
        const float SwingCooldown = 0.35f;
        const float GroundClearance = 0.03f;
        const float MaxMoveStep = 0.05f;
        const float StepLength = 0.74f;
        const float SneakSpeedScale = 0.4f;
        const int SwatterGrowthStep = 1000;
        const float BaseSwatReach = 2f;
        const float BaseSwatRadius = 0.12f;
        const float SneakStepVolume = 0.05f;
        const float HitStopSeconds = 0.055f;
        const float PopupSeconds = 1.1f;
        const int WindowCost = 2500;

        public float walkSpeed = 2.2f;
        public float turnSpeed = 110f;
        public float swatReach = 2f;
        public float swatRadius = 0.12f;
        public float aimLookback = 0.15f;

        public event Action<SwingResult> Swung;
        public int DamageBill { get; private set; }
        public float SwatterScale { get; private set; } = 1f;
        string swatterGrewNote = "";
        public bool Sneaking { get; private set; }
        public Light Flashlight { get; private set; }
        public Transform Head => head;
        public Transform SwatterTransform => swatter;
        // Every $1000 of damage makes the swatter 10% bigger than the original: reach, width and the model.
        void UpdateSwatterSize(bool announce)
        {
            var scale = 1f + 0.1f * (DamageBill / SwatterGrowthStep);
            if (Mathf.Approximately(scale, SwatterScale) && announce) return;
            var grew = scale > SwatterScale;
            SwatterScale = scale;
            swatReach = BaseSwatReach * scale;
            swatRadius = BaseSwatRadius * scale;
            if (swatter != null) swatter.localScale = Vector3.one * scale;
            if (announce && grew) swatterGrewNote = $"  The swatter grew! Now {Mathf.RoundToInt(scale * 100f)}%";
        }

        public void ToggleFlashlight()
        {
            var on = !Flashlight.enabled;
            Flashlight.enabled = on;
            var fx = Flashlight.GetComponent<FlashlightFx>();
            if (fx != null) fx.enabled = on;
            foreach (Transform child in Flashlight.transform) child.gameObject.SetActive(on); // The visible beam.
            playerAudio.PlayOneShot(FlySounds.Get("impact_tech"), 0.2f);
            message = on ? "Flashlight on" : "Flashlight off";
        }

        public void Announce(string text) => message = text;

        readonly Queue<(float time, Quaternion aim)> aimHistory = new Queue<(float, Quaternion)>();
        readonly RaycastHit[] hits = new RaycastHit[48];

        ISwatterInput input = new KeyboardMouseSwatter();
        CharacterController body;
        Camera view;
        FlyCamera debugCamera;
        Transform head;
        Transform aimPivot;
        Transform swatter;
        bool playing = true;
        float aimYaw;
        float aimPitch;
        List<Vector3> roomOutline;
        Vector3 safePosition;
        float swingStartedAt = -10f;
        string message = "Find the fly";
        AudioSource playerAudio;
        Coroutine hitStop;
        float shake;
        float stepDistance;
        int stepIndex;
        GUIStyle popupStyle;
        readonly List<DamagePopup> popups = new List<DamagePopup>();

        struct DamagePopup
        {
            public Vector3 point;
            public string text;
            public float born;
        }

        public static PlayerController GetOrCreate(Camera camera)
        {
            var player = FindFirstObjectByType<PlayerController>();
            if (player == null)
            {
                player = new GameObject("Player").AddComponent<PlayerController>();
                player.Build(camera);
            }
            return player;
        }

        void Build(Camera camera)
        {
            view = camera;
            debugCamera = camera.GetComponent<FlyCamera>();
            body = GetComponent<CharacterController>();
            body.height = BodyHeight;
            body.radius = BodyRadius;
            body.center = Vector3.up * (BodyHeight / 2f);
            body.stepOffset = 0f;
            body.skinWidth = 0.02f;

            head = new GameObject("Head").transform;
            head.SetParent(transform, false);
            head.localPosition = Vector3.up * EyeHeight;

            aimPivot = new GameObject("Aim").transform;
            aimPivot.SetParent(head, false);

            var flashlight = new GameObject("Flashlight").AddComponent<Light>();
            flashlight.transform.SetParent(aimPivot, false);
            Flashlight = flashlight;
            flashlight.type = LightType.Spot;
            flashlight.range = 9f;
            flashlight.spotAngle = 45f;
            flashlight.innerSpotAngle = 22f;
            flashlight.intensity = 5f;
            flashlight.color = new Color(1f, 0.95f, 0.84f);
            flashlight.shadows = LightShadows.Soft;
            flashlight.shadowStrength = 0.9f;
            flashlight.shadowNearPlane = 0.1f;
            flashlight.gameObject.AddComponent<FlashlightFx>().Setup(flashlight);

            playerAudio = gameObject.AddComponent<AudioSource>();
            playerAudio.playOnAwake = false;
            playerAudio.spatialBlend = 0f;

            swatter = new GameObject("Swatter").transform;
            swatter.SetParent(aimPivot, false);
            swatter.localPosition = new Vector3(0.22f, -0.2f, 0.42f);
            SwatterModel.Build(swatter);
            SetPlaying(true);
        }

        void OnDisable()
        {
            Time.timeScale = 1f;
        }

        // position is the eye position from room.json.
        public void Spawn(Vector3 eyePosition, Quaternion rotation, List<Vector3> outline)
        {
            roomOutline = outline;
            safePosition = new Vector3(eyePosition.x, GroundClearance, eyePosition.z);
            body.enabled = false;
            transform.SetPositionAndRotation(safePosition, rotation);
            body.enabled = true;
            Physics.SyncTransforms();
            aimYaw = aimPitch = 0f;
            aimHistory.Clear();
            DamageBill = 0;
            UpdateSwatterSize(false);
            message = "Find the fly";
            ApplyAim();
        }

        void SetPlaying(bool value)
        {
            playing = value;
            if (debugCamera != null) debugCamera.flightEnabled = !value;
            if (value)
            {
                view.transform.SetParent(head, false);
                view.transform.localPosition = Vector3.zero;
                view.transform.localRotation = Quaternion.identity;
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
            else
            {
                view.transform.SetParent(null, true);
                if (debugCamera != null) debugCamera.SyncRotation();
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            if (swatter != null) swatter.gameObject.SetActive(value);
        }

        void Update()
        {
            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard.tabKey.wasPressedThisFrame) SetPlaying(!playing);
            if (!playing) return;
            KeepInRoom();

            if (Cursor.lockState != CursorLockMode.Locked)
            {
                shake = 0f;
                UpdateShake();
                var mouse = Mouse.current;
                if (mouse != null && mouse.leftButton.wasPressedThisFrame)
                {
                    Cursor.lockState = CursorLockMode.Locked;
                    Cursor.visible = false;
                }
                return;
            }

            var frame = input.Read();
            if (frame.quickTurn) transform.Rotate(0f, 180f, 0f);
            transform.Rotate(0f, frame.turn * turnSpeed * Time.deltaTime, 0f);

            // Hold Left Ctrl to sneak: slow and quiet, and the fly notices you less.
            Sneaking = Keyboard.current != null && Keyboard.current.leftCtrlKey.isPressed;
            if (Keyboard.current != null && Keyboard.current.qKey.wasPressedThisFrame) ToggleFlashlight();
            Walk(transform.forward * (frame.forward * walkSpeed * (Sneaking ? SneakSpeedScale : 1f)), Time.deltaTime);

            if (frame.recenter)
            {
                aimYaw = aimPitch = 0f;
                message = "Aim recentered";
            }
            aimYaw = Mathf.Clamp(aimYaw + frame.aimDelta.x, -MaxAimYaw, MaxAimYaw);
            aimPitch = Mathf.Clamp(aimPitch + frame.aimDelta.y, -MaxAimPitch, MaxAimPitch);
            ApplyAim();
            RecordAim();

            if (frame.ping)
            {
                if (RoomAtmosphere.Current != null) RoomAtmosphere.Current.Ping();
                message = "Ping";
            }
            if (frame.swing && Time.time - swingStartedAt >= SwingCooldown) Swing(frame.swingStrength);
            AnimateSwatter();
        }

        // Rooms have one floor level, so the player never needs gravity. Long frames
        // from shader compiling are capped so they cannot launch the player either.
        public void Walk(Vector3 velocity, float deltaTime)
        {
            velocity.y = 0f;
            var before = transform.position;
            body.Move(velocity * Mathf.Min(deltaTime, MaxMoveStep));
            KeepInRoom();

            var moved = transform.position - before;
            moved.y = 0f;
            stepDistance += moved.magnitude;
            if (stepDistance >= StepLength)
            {
                stepDistance = 0f;
                stepIndex = (stepIndex + 1) % 3;
                playerAudio.PlayOneShot(FlySounds.Get($"footstep_{stepIndex}"), Sneaking ? SneakStepVolume : 0.24f);
            }
        }

        void KeepInRoom()
        {
            var position = transform.position;
            if (roomOutline != null && !InsideRoom(position) && InsideRoom(safePosition))
            {
                MoveTo(safePosition);
                return;
            }
            if (Mathf.Abs(position.y - GroundClearance) > 0.001f)
            {
                position.y = GroundClearance;
                MoveTo(position);
            }
            safePosition = position;
        }

        void MoveTo(Vector3 position)
        {
            body.enabled = false;
            transform.position = position;
            body.enabled = true;
            Physics.SyncTransforms();
        }

        bool InsideRoom(Vector3 point)
        {
            var inside = false;
            for (int i = 0, j = roomOutline.Count - 1; i < roomOutline.Count; j = i++)
            {
                var a = roomOutline[i];
                var b = roomOutline[j];
                if ((a.z > point.z) != (b.z > point.z) && point.x < (b.x - a.x) * (point.z - a.z) / (b.z - a.z) + a.x)
                    inside = !inside;
            }
            return inside;
        }

        void ApplyAim()
        {
            var viewPitch = aimPitch * ViewPitchShare;
            head.localRotation = Quaternion.Euler(-viewPitch, 0f, 0f);
            aimPivot.localRotation = Quaternion.Euler(-(aimPitch - viewPitch), aimYaw, 0f);
        }

        void RecordAim()
        {
            // Stored relative to the body, so turning does not drag the delayed aim behind.
            aimHistory.Enqueue((Time.time, Quaternion.Inverse(transform.rotation) * aimPivot.rotation));
            while (aimHistory.Count > 1 && Time.time - aimHistory.Peek().time > aimLookback + 0.1f) aimHistory.Dequeue();
        }

        // Uses the aim from just before the swing so the wrist whip does not throw it off.
        Quaternion AimBeforeSwing()
        {
            var chosen = Quaternion.Inverse(transform.rotation) * aimPivot.rotation;
            var found = false;
            foreach (var entry in aimHistory)
            {
                if (Time.time - entry.time < aimLookback) break;
                chosen = entry.aim;
                found = true;
            }
            if (!found && aimHistory.Count > 0) chosen = aimHistory.Peek().aim;
            return transform.rotation * chosen;
        }

        public void Swing(float strength)
        {
            swingStartedAt = Time.time;
            playerAudio.PlayOneShot(FlySounds.Get("swing_whoosh"), Mathf.Lerp(0.3f, 0.6f, strength));
            var direction = AimBeforeSwing() * Vector3.forward;
            FlyAgent.Current?.OnSwing(head.position, direction, swatRadius, swatReach, strength);
            var result = new SwingResult { time = Time.time, strength = strength, target = "nothing" };

            var count = Physics.SphereCastNonAlloc(head.position, swatRadius, direction, hits, swatReach, ~0, QueryTriggerInteraction.Ignore);
            var nearest = float.MaxValue;
            var nearestBoundary = float.MaxValue;
            RoomPart part = null;
            RoomPart boundary = null;
            var hitNormal = -direction;
            var boundaryPoint = Vector3.zero;
            var boundaryNormal = -direction;
            for (var i = 0; i < count; i++)
            {
                var candidate = hits[i];
                if (candidate.collider == body || candidate.distance <= 0f) continue;
                var roomPart = candidate.collider.GetComponent<RoomPart>();
                if (roomPart == null) continue;
                if (roomPart.kind == RoomPartKind.Boundary)
                {
                    if (candidate.distance >= nearestBoundary) continue;
                    nearestBoundary = candidate.distance;
                    boundary = roomPart;
                    boundaryPoint = candidate.point;
                    boundaryNormal = candidate.normal;
                    continue;
                }
                if (candidate.distance >= nearest) continue;
                nearest = candidate.distance;
                part = roomPart;
                result.point = candidate.point;
                hitNormal = candidate.normal;
            }
            if (part == null && boundary != null)
            {
                // Gaps in a scan's walls are still solid, so they can take a mark.
                part = boundary;
                result.point = boundaryPoint;
                hitNormal = boundaryNormal;
            }

            if (part != null)
            {
                result.hit = true;
                result.kind = part.kind;
                result.target = part.label;
                var window = part.GetComponent<WindowBreak>();
                var prop = part.GetComponent<PropDamage>();
                if (window != null) window.Smash(strength);
                else SwatMarks.Stamp(part, head.position, direction, result.point, hitNormal, view.transform.up, swatReach);
                if (prop != null) prop.Hit(result.point, direction, strength);
                PropDamage.ShakeNearby(result.point, strength, prop);

                // Every hit is billed. Imprints stay free and unlimited.
                var cost = window != null ? WindowCost : Mathf.RoundToInt(DamageCost(part) * strength);
                result.damage = cost;
                DamageBill += result.damage;
                UpdateSwatterSize(true);
                message = result.damage > 0 ? $"Hit {part.label}: -${result.damage}" : $"Hit {part.label}";
                message += swatterGrewNote;
                swatterGrewNote = "";
                if (result.damage > 0)
                    popups.Add(new DamagePopup { point = result.point, text = $"-${result.damage}", born = Time.unscaledTime });

                var impact = window != null ? "window_smash" : prop != null ? prop.ImpactSound : ImpactFor(part);
                playerAudio.PlayOneShot(FlySounds.Get(impact), window != null ? 1f : Mathf.Lerp(0.5f, 0.95f, strength));
                shake = window != null ? 1.25f : Mathf.Lerp(0.55f, 1f, strength);
                if (hitStop != null) StopCoroutine(hitStop);
                hitStop = StartCoroutine(HitStop());
            }
            else
            {
                message = "Whiff";
            }
            // Every swing makes a sound the fly can hear: a thud where it lands, or a whoosh if it misses.
            FlyAgent.Current?.HearNoise(result.hit ? result.point : head.position + direction, result.hit ? Mathf.Lerp(0.35f, 0.7f, strength) : 0.2f);
            Swung?.Invoke(result);
        }

        System.Collections.IEnumerator HitStop()
        {
            Time.timeScale = 0.06f;
            yield return new WaitForSecondsRealtime(HitStopSeconds);
            Time.timeScale = 1f;
            hitStop = null;
        }

        static string ImpactFor(RoomPart part)
        {
            switch (part.kind)
            {
                case RoomPartKind.Wall:
                case RoomPartKind.Boundary:
                case RoomPartKind.Ceiling: return "impact_wall";
                case RoomPartKind.Floor: return "impact_wood";
            }
            switch (part.label)
            {
                case "television":
                case "tv": return "impact_tech";
                case "refrigerator":
                case "fridge":
                case "oven":
                case "stove":
                case "dishwasher":
                case "washerdryer":
                case "washer":
                case "sink": return "impact_metal";
                case "sofa":
                case "couch":
                case "bed": return "impact_soft";
                case "toilet":
                case "bathtub": return "impact_ceramic";
                case "window": return "impact_glass";
                default: return "impact_wood";
            }
        }

        void UpdateShake()
        {
            if (shake <= 0.001f)
            {
                view.transform.localPosition = Vector3.zero;
                view.transform.localRotation = Quaternion.identity;
                shake = 0f;
                return;
            }
            var now = Time.unscaledTime * 46f;
            var amount = shake * shake;
            view.transform.localPosition = new Vector3(Mathf.PerlinNoise(now, 1.3f) - 0.5f, Mathf.PerlinNoise(2.7f, now) - 0.5f, 0f) * (0.035f * amount);
            view.transform.localRotation = Quaternion.Euler((Mathf.PerlinNoise(now, 5.1f) - 0.5f) * 3.2f * amount,
                (Mathf.PerlinNoise(7.4f, now) - 0.5f) * 3.2f * amount, (Mathf.PerlinNoise(now, 9.6f) - 0.5f) * 2f * amount);
            shake = Mathf.MoveTowards(shake, 0f, Time.unscaledDeltaTime * 4.2f);
        }

        static int DamageCost(RoomPart part)
        {
            switch (part.kind)
            {
                case RoomPartKind.Wall: return 20;
                case RoomPartKind.Floor:
                case RoomPartKind.Ceiling:
                case RoomPartKind.Boundary: return 0;
            }
            switch (part.label)
            {
                case "television":
                case "tv": return 450;
                case "laptop": return 400;
                case "phone": return 250;
                case "airpods": return 90;
                case "refrigerator":
                case "fridge": return 300;
                case "oven":
                case "stove":
                case "dishwasher":
                case "washerdryer":
                case "washer": return 250;
                case "sofa":
                case "couch":
                case "bed": return 180;
                case "table":
                case "desk": return 120;
                case "storage":
                case "cabinet":
                case "dresser": return 90;
                case "chair":
                case "stool": return 50;
                default: return 40;
            }
        }

        void AnimateSwatter()
        {
            var t = (Time.time - swingStartedAt) / SwingDuration;
            var angle = -20f;
            if (t < 1f) angle = Mathf.Lerp(-20f, 70f, Mathf.Sin(t * Mathf.PI));
            swatter.localRotation = Quaternion.Euler(angle, 0f, -8f);
            UpdateShake();
        }

        void DrawPopups()
        {
            if (popupStyle == null)
                popupStyle = new GUIStyle(GUI.skin.label) { fontSize = 26, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };

            for (var i = popups.Count - 1; i >= 0; i--)
            {
                var age = (Time.unscaledTime - popups[i].born) / PopupSeconds;
                if (age >= 1f)
                {
                    popups.RemoveAt(i);
                    continue;
                }
                var screen = view.WorldToScreenPoint(popups[i].point + Vector3.up * (0.08f + age * 0.34f));
                if (screen.z <= 0f) continue;
                var box = new Rect(screen.x - 80f, Screen.height - screen.y - 20f, 160f, 40f);
                var alpha = 1f - age * age;
                popupStyle.normal.textColor = new Color(0f, 0f, 0f, alpha * 0.8f);
                GUI.Label(new Rect(box.x + 2f, box.y + 2f, box.width, box.height), popups[i].text, popupStyle);
                popupStyle.normal.textColor = new Color(1f, 0.26f, 0.18f, alpha);
                GUI.Label(box, popups[i].text, popupStyle);
            }
        }

        void OnGUI()
        {
            if (!playing)
            {
                GUI.Label(new Rect(16, 12, 500, 24), "Debug camera. Tab returns to the player.");
                return;
            }

            DrawPopups();
            GUI.Label(new Rect(16, 12, 300, 24), $"Damage bill: ${DamageBill}");
            GUI.Label(new Rect(16, 34, 400, 24), message);
            GUI.Label(new Rect(16, Screen.height - 34, 900, 24),
                "W/S walk  A/D turn  Hold Ctrl sneak  Q flashlight  Mouse aim  Click swing  Space ping  Space x2 turn 180  Hold Space recenter  Tab debug camera");

            var target = view.WorldToScreenPoint(aimPivot.position + aimPivot.forward * swatReach);
            if (target.z > 0f) GUI.Label(new Rect(target.x - 5f, Screen.height - target.y - 11f, 20f, 22f), "+");
        }
    }
}
