using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FlyAI
{
    // The fly. On the Raspberry Pi (backend/fly.py) all of its behaviour runs remotely, and this class draws it and reports
    // what only the game knows (light, flashlight, room events, clear flight paths). If the Pi is not reachable, the same
    // behaviour runs here against a local brain.
    //
    // The fruit fly brain (brain/server.py) decides what it does; the game only supplies what the fly
    // "sees" and forces where it may land.
    //
    //   Input   Looming (0-1) and which eye sees it, from: the swatter, the player walking at it, the
    //           flashlight, and passing shadows. Sent to the brain at 20 Hz.
    //   Output  Escape strength and jump side, from the connectome simulation.
    //             strength >= EscapeThreshold  -> takes off; the side sets the first turn, the strength sets
    //                                             speed and how far it flies
    //             strength >= FleeThreshold    -> walks away from the threat, toward the side the brain chose
    //   Game    Perching, walking along tables (between landing spots), flying routes, and where it lands.
    //
    // Without a connection the fly does not react at all: there is no stand-in.
    public class FlyAgent : MonoBehaviour
    {
        public static FlyAgent Current { get; private set; }

        // Tuning.
        const float BodyScale = 1f;            // Raise to make the fly easier to see.
        const float EscapeThreshold = 0.4f;    // Brain strength that makes the fly take off.
        const float FleeThreshold = 0.12f;     // Brain strength that makes it walk away.
        const float KillPadding = 0.06f;       // Added to the swatter radius: the fly counts as hit this close to the impact.
        const float LiftFromSurface = 0.012f;
        const float WalkSpeed = 0.1f;          // Metres per second while strolling.
        const float MinVision = 0.12f;         // How much it still sees in total darkness.
        const float FullVisionLight = 0.35f;   // Light level at which it sees everything.
        const float NeighbourRadius = 0.6f;    // Landing spots this close on one surface are connected for walking.

        enum State { Perched, Walking, Flying, Dead }

        struct Spot
        {
            public Vector3 position;
            public Vector3 normal;
        }

        static FlyBrainClient client;
        public static FlyBrainClient BrainClient => client;

        readonly List<Spot> spots = new List<Spot>();
        readonly List<Vector3> route = new List<Vector3>();
        List<int>[] neighbours;
        State state = State.Perched;
        int spotIndex;
        Transform model;
        Transform wingLeft;
        Transform wingRight;
        Transform[] legs;
        Quaternion[] legBase;
        Transform head;
        AudioSource buzz;
        AudioLowPassFilter muffle;
        Light glow;
        Texture2D glowTexture;
        Camera viewCamera;
        PlayerController player;

        // Perch and walk.
        Vector3 perchNormal = Vector3.up;
        Vector3 perchHeading = Vector3.forward;
        float perchTimer;
        float groomUntil;
        float cooldownUntil;
        int walkTarget = -1;
        int walkPrevious = -1;
        int walkStepsLeft;
        float fleeUntil;
        string fleeSide = "right";

        // Flight.
        int routeIndex;
        int landingSpot;
        Vector3 landingPoint;
        Vector3 landingNormal = Vector3.up;
        Vector3 velocity;
        float flightSpeed = 1.5f;
        float flightAgility = 5f;
        float flightStarted;
        float wobbleSeed;
        bool escaping;
        float ceilingHeight = 2.5f;

        // Stimulus (what the brain is shown).
        Vector3 lastThreat;
        float lastDistance = -1f;
        float closingSpeed;
        float lastBodyDistance = -1f;
        float bodyClosing;
        float swingThreat;
        float swingThreatUntil;
        float nextAmbient;
        float ambientUntil;
        float ambientValue;
        string ambientEye = "left";
        Vector3 ambientPoint;
        float looming;
        string eye = "center";
        string source = "none";
        float lightLevel;
        float vision = 1f;
        float flashlightOnFly;
        float soundValue;
        float soundUntil;
        string soundEar = "center";
        float pulseValue;
        float pulseUntil;
        Vector3 pulsePoint;
        string pulseLabel = "";
        float previousPower = 1f;
        bool lightningWasOn;
        bool lightningRight;
        float nextSurgeFlip;
        bool surgeRight;

        // Brain output, for the panel.
        float lastStrength;
        float lastDecisionAt = -10f;
        string lastDecisionSide = "-";
        int[] lastSpikes;
        string lastAction = "waiting";

        // Marker and panel.
        float beacon;
        float flightGlow;
        float landedAt;
        float nextChirp;
        float chirpUntil;
        bool showStatus = true;

        // Personality: rolled for each fly. It only scales what the fly senses and how it moves; the brain still decides.
        public string PersonalityName { get; private set; } = "Steady";
        float skittish = 1f;    // Scales every stimulus sent to the brain.
        float restless = 1f;    // How often it gets startled, and how little it grooms.
        float nightSight = 1f;  // How well it still sees in the dark.
        float pace = 1f;        // Walking and flying speed.
        float hearing = 1f;     // Scales how loud sounds are to it.
        GUIStyle statusStyle;

        // Remote mode: the Pi runs the fly and this follows it.
        bool remote;
        public bool IsRemote => remote;
        BackendMessage appliedFly;
        bool netPlaced;
        Vector3 netPosition;
        Vector3 netUp = Vector3.up;
        Vector3 netForward = Vector3.forward;
        Vector3 netVelocity;
        float netAt;
        float nextSenses;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Current = null;
            client?.Dispose();
            client = null;
        }

        static bool IsFurniture(string kind) =>
            kind == "table" || kind == "seat" || kind == "ledge" || kind == "storage" || kind == "bed" || kind == "object";

        static List<Spot> CollectSpots(RoomData room, System.Func<LandingSpotData, bool> accept)
        {
            var list = new List<Spot>();
            if (room.landingSpots == null) return list;
            foreach (var spot in room.landingSpots)
                if (spot != null && spot.position != null && spot.position.Length == 3 && spot.normal != null && spot.normal.Length == 3 && accept(spot))
                    list.Add(new Spot { position = RoomLoader.ToUnity(spot.position), normal = RoomLoader.ToUnity(spot.normal).normalized });
            return list;
        }

        public static FlyAgent Spawn(Transform parent, RoomData room)
        {
            // The fly lands on furniture, not on the ceiling, walls or floor. Other surfaces are only a fallback.
            var found = CollectSpots(room, spot => IsFurniture(spot.surfaceKind));
            if (found.Count < 2) found = CollectSpots(room, spot => spot.surfaceKind != "ceiling");
            if (found.Count < 2)
            {
                Debug.LogWarning("[FlyAI] The scan has fewer than 2 landing spots, so there is no fly.");
                return null;
            }

            var go = new GameObject("Fly");
            go.transform.SetParent(parent, false);
            var agent = go.AddComponent<FlyAgent>();
            agent.Init(found, parent.GetComponent<RoomAssets>(), room.ceilingHeight);
            agent.SendRoomToPi(RoomLoader.ToUnity(room.playerStart.position) + Vector3.up * 1.6f);
            return agent;
        }

        void RollPersonality()
        {
            // The difficulty picks which side of each trait the fly can land on (see Difficulty).
            skittish = Difficulty.Roll(0.6f, 0.9f, 1.2f, 1.6f);
            restless = Difficulty.Roll(0.5f, 0.8f, 1.4f, 1.9f);
            nightSight = Difficulty.Roll(0.6f, 0.9f, 1.5f, 2.2f);
            pace = Difficulty.Roll(0.65f, 0.9f, 1.25f, 1.6f);
            hearing = Difficulty.Roll(0.5f, 0.85f, 1.3f, 1.8f);
            var traits = new List<string>();
            if (skittish > 1.18f) traits.Add("Jittery"); else if (skittish < 0.9f) traits.Add("Calm");
            if (restless > 1.35f) traits.Add("Restless"); else if (restless < 0.8f) traits.Add("Lazy");
            if (nightSight > 1.45f) traits.Add("Night owl"); else if (nightSight < 0.9f) traits.Add("Day flyer");
            if (pace > 1.2f) traits.Add("Speedy"); else if (pace < 0.9f) traits.Add("Slow");
            if (hearing > 1.25f) traits.Add("Sharp ears"); else if (hearing < 0.85f) traits.Add("Hard of hearing");
            if (traits.Count > 1) { var keep = traits[Random.Range(0, traits.Count)]; traits.Remove(keep); traits.Insert(0, keep); }
            PersonalityName = traits.Count == 0 ? "Steady" : string.Join(" & ", traits.GetRange(0, Mathf.Min(2, traits.Count)));
        }

        // Called when the round's countdown ends.
        public void OnRoundStart()
        {
            if (remote)
            {
                BackendClient.StartHunt();
                return;
            }
            RollPersonality(); // The level loads behind the menu, so this picks up a difficulty changed after that.
            nextAmbient = Time.time + Random.Range(4f, 8f) / restless;
            cooldownUntil = Time.time + 0.5f;
            client?.Reset(); // A fresh fly: it is not used to anything yet.
        }

        void Init(List<Spot> landing, RoomAssets assets, float ceiling)
        {
            remote = BackendClient.Connected;
            BackendClient.Driving = remote;
            RollPersonality();
            ceilingHeight = ceiling > 1f ? ceiling : 2.5f;
            spots.AddRange(landing);
            BuildGraph();
            BuildModel(assets);
            Perch(Random.Range(0, spots.Count), Random.insideUnitSphere);
            perchTimer = Random.Range(1.5f, 4f);
            nextAmbient = Time.time + Random.Range(4f, 8f);

            buzz = gameObject.AddComponent<AudioSource>();
            buzz.clip = FlySounds.Get("fly_buzz");
            buzz.loop = true;
            buzz.spatialBlend = 1f;
            buzz.spread = 0f;
            buzz.rolloffMode = AudioRolloffMode.Linear;
            buzz.minDistance = 0.35f;
            buzz.maxDistance = 14f;
            buzz.dopplerLevel = 0f;
            buzz.volume = 0f;
            buzz.Play();
            muffle = gameObject.AddComponent<AudioLowPassFilter>();
            muffle.cutoffFrequency = 22000f;

            glow = new GameObject("Fly Glow").AddComponent<Light>();
            glow.transform.SetParent(transform, false);
            glow.type = LightType.Point;
            glow.color = new Color(0.45f, 0.75f, 1f);
            glow.range = 1.6f;
            glow.intensity = 0f;
            glow.shadows = LightShadows.None;
            glowTexture = MakeGlow();

            if (client == null && !remote)
            {
                var url = FlyBrainClient.ResolveUrl();
                if (url != null)
                {
                    client = new FlyBrainClient(url);
                    Debug.Log($"[FlyAI] Fly brain: connecting to {url}");
                }
                else Debug.LogWarning("[FlyAI] Fly brain is switched off (FLYAI_BRAIN=off), so the fly will not react.");
            }
            Current = this;
            GameRound.Begin(transform.parent, this);
        }

        // The Pi has no copy of the room's walls, so it gets points in the air and which pairs have a clear line of flight.
        void SendRoomToPi(Vector3 head)
        {
            const int MaxAirPoints = 40;
            Physics.SyncTransforms();
            var air = new List<Vector3>();
            var step = Mathf.Max(1, spots.Count / MaxAirPoints);
            for (var i = 0; i < spots.Count && air.Count < MaxAirPoints; i += step)
                air.Add(ClampToAir(spots[i].position + Vector3.up * 0.9f));

            var links = new List<string>();
            var row = new char[air.Count];
            for (var a = 0; a < air.Count; a++)
            {
                for (var b = 0; b < air.Count; b++) row[b] = a != b && !Physics.Linecast(air[a], air[b]) ? '1' : '0';
                links.Add(new string(row));
            }

            // For each spot, the nearest air point it can fly straight up to.
            var spotAir = new List<int>(spots.Count);
            var order = new List<int>(air.Count);
            for (var a = 0; a < air.Count; a++) order.Add(a);
            foreach (var spot in spots)
            {
                var from = spot.position + spot.normal * 0.15f;
                order.Sort((a, b) => (air[a] - from).sqrMagnitude.CompareTo((air[b] - from).sqrMagnitude));
                var chosen = order[0];
                for (var k = 0; k < Mathf.Min(6, order.Count); k++)
                    if (!Physics.Linecast(from, air[order[k]]))
                    {
                        chosen = order[k];
                        break;
                    }
                spotAir.Add(chosen);
            }

            var pairs = new List<(Vector3 position, Vector3 normal)>(spots.Count);
            foreach (var spot in spots) pairs.Add((spot.position, spot.normal));
            BackendClient.SendRoom(pairs, head, ceilingHeight, air, links, spotAir);
        }

        // Landing spots on the same surface (same plane, close together) are joined so the fly can walk between them.
        void BuildGraph()
        {
            neighbours = new List<int>[spots.Count];
            for (var i = 0; i < spots.Count; i++)
            {
                neighbours[i] = new List<int>();
                for (var j = 0; j < spots.Count; j++)
                {
                    if (i == j) continue;
                    var delta = spots[j].position - spots[i].position;
                    if (delta.sqrMagnitude > NeighbourRadius * NeighbourRadius) continue;
                    if (Vector3.Dot(spots[i].normal, spots[j].normal) < 0.95f) continue;
                    if (Mathf.Abs(Vector3.Dot(delta, spots[i].normal)) > 0.04f) continue;
                    neighbours[i].Add(j);
                }
            }
        }

        void OnDestroy()
        {
            if (Current == this) Current = null;
            UiHit.Clear("brain");
            if (glowTexture != null) Destroy(glowTexture);
        }

        // Called by the player when a swing starts.
        public void OnSwing(Vector3 origin, Vector3 direction, float radius, float reach, float strength)
        {
            if (state == State.Dead || remote) return; // The Pi gets the swing itself.
            // A swing aimed at the fly is a big looming shape; one aimed elsewhere barely registers.
            var toFly = transform.position - origin;
            swingThreat = toFly.magnitude < reach + 0.6f
                ? Mathf.InverseLerp(28f, 4f, Vector3.Angle(direction, toFly)) * (0.7f + 0.3f * strength)
                : 0f;
            swingThreatUntil = Time.time + 0.3f;
        }

        void Update()
        {
            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard.jKey.wasPressedThisFrame) showStatus = !showStatus;

            if (player == null) player = FindFirstObjectByType<PlayerController>();
            if (player == null || state == State.Dead) return;

            var dt = Time.deltaTime;
            var round = GameRound.Current;
            if (remote)
            {
                UpdateRemote(dt, round);
                if (state == State.Dead) return;
                Animate();
                UpdateLocator(dt);
                if (RoomTension.Current != null)
                    RoomTension.Current.SetFlyDistance(Vector3.Distance(transform.position, player.Head.position));
                return;
            }
            if (round == null || round.FlyActive)
            {
                UpdateStimulus(dt);
                UpdateBrain();
                if (state == State.Dead) return;
            }
            else
            {
                // Countdown or round over: the fly goes about its business and the brain is shown nothing.
                looming = 0f;
                if (client != null && client.Connected) { client.SetLooming(0f, "center"); client.SetSound(0f, "center"); }
            }

            switch (state)
            {
                case State.Perched: UpdatePerched(dt); break;
                case State.Walking: UpdateWalking(dt); break;
                case State.Flying: UpdateFlying(dt); break;
            }
            Animate();
            UpdateLocator(dt);

            if (RoomTension.Current != null)
                RoomTension.Current.SetFlyDistance(Vector3.Distance(transform.position, player.Head.position));
        }

        // --- Following the fly on the Pi -----------------------------------------------------------------

        void UpdateRemote(float dt, GameRound round)
        {
            if (round == null || round.FlyActive) SendSenses();
            var m = BackendClient.LastFly;
            if (m != null && !ReferenceEquals(m, appliedFly))
            {
                appliedFly = m;
                ApplyFly(m, round);
                if (state == State.Dead) return;
            }
            if (!netPlaced) return;

            // Follow smoothly between the Pi's 30 updates a second, leading a little while it flies.
            var flying = state == State.Flying;
            var target = flying ? netPosition + netVelocity * Mathf.Min(Time.unscaledTime - netAt, 0.08f) : netPosition;
            transform.position = Vector3.Lerp(transform.position, target, 1f - Mathf.Exp(-(flying ? 28f : 20f) * dt));
            if (netForward.sqrMagnitude > 1e-4f)
                transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(netForward, netUp), 1f - Mathf.Exp(-18f * dt));
            perchNormal = netUp;

            if (flying)
            {
                buzz.pitch = escaping ? 1.3f : 1f;
                buzz.volume = Mathf.MoveTowards(buzz.volume, 1f, dt * 10f);
            }
            else ChirpQuietly(dt);
        }

        void ApplyFly(BackendMessage m, GameRound round)
        {
            if (m.pos == null || m.pos.Length != 3) return;
            netPosition = new Vector3(m.pos[0], m.pos[1], m.pos[2]);
            if (m.heading != null && m.heading.Length == 3) netForward = new Vector3(m.heading[0], m.heading[1], m.heading[2]);
            if (m.up != null && m.up.Length == 3) netUp = new Vector3(m.up[0], m.up[1], m.up[2]);
            netVelocity = m.vel != null && m.vel.Length == 3 ? new Vector3(m.vel[0], m.vel[1], m.vel[2]) : Vector3.zero;
            netAt = Time.unscaledTime;
            if (!netPlaced)
            {
                netPlaced = true;
                transform.position = netPosition;
                if (netForward.sqrMagnitude > 1e-4f) transform.rotation = Quaternion.LookRotation(netForward, netUp);
            }

            if (m.state == "dead")
            {
                if (m.phase == "won" && round != null && round.FlyActive) Die();
                return;
            }
            var next = m.state == "walking" ? State.Walking : m.state == "flying" || m.state == "fleeing" ? State.Flying : State.Perched;
            if (state == State.Flying && next != State.Flying)
            {
                landedAt = Time.time;
                nextChirp = Time.time + 1.2f;
            }
            state = next;
            escaping = m.state == "fleeing";
            groomUntil = m.grooming ? Time.time + 0.1f : 0f;
            fleeUntil = m.hurrying ? Time.time + 0.1f : 0f;
            if (!string.IsNullOrEmpty(m.personality)) PersonalityName = m.personality;
            looming = m.looming;
            if (!string.IsNullOrEmpty(m.eye)) eye = m.eye;
            if (!string.IsNullOrEmpty(m.source)) source = m.source;
            if (!string.IsNullOrEmpty(m.action)) lastAction = m.action;
        }

        // What only the game can know, 20 times a second.
        void SendSenses()
        {
            if (Time.unscaledTime < nextSenses) return;
            nextSenses = Time.unscaledTime + 0.05f;
            var traceStart = NetTrace.NowMs;
            var position = transform.position;
            var swatterPosition = player.SwatterTransform.position;
            var beam = FlashlightAt(position);
            flashlightOnFly = beam;
            lightLevel = RoomLightAt(position) + beam;
            vision = Mathf.Clamp01(Mathf.Max(lightLevel * 0.8f, RoomLightAt(swatterPosition)) / FullVisionLight);
            if (NetTrace.On)
            {
                // The light sums cast a ray from every lamp, twice: their cost and what they add up to.
                var rigs = RoomAtmosphere.Current != null ? RoomAtmosphere.Current.Rigs.Count : 0;
                NetTrace.Peak("senses_ms", NetTrace.NowMs - traceStart);
                NetTrace.Event($"SENSES lamps={rigs} light={lightLevel:0.00} beam={beam:0.00} vision={vision:0.00} took={NetTrace.NowMs - traceStart:0.00}ms");
            }
            var events = RoomEvents.Current;
            BackendClient.SendSenses(vision, beam, events != null ? events.Surge : 0f, events != null ? events.LightningLevel : 0f,
                events != null ? events.HeadlightLevel : 0f, events != null ? events.Power : 1f, player.Sneaking, swatterPosition,
                player.Flashlight != null ? player.Flashlight.transform.position : player.Head.position);
        }

        // A decision from the brain on the Pi, for the panel and the neural view.
        public void FeedBrain(BackendMessage m)
        {
            lastStrength = m.strength;
            lastDecisionSide = m.side;
            lastDecisionAt = Time.time;
            if (m.spikes != null && m.spikes.Length > 0) lastSpikes = m.spikes;
            NeuralView.Current?.Feed(m.active != null && m.active.Length > 0 ? m.active : null, m.spikes);
        }

        // --- What the fly sees --------------------------------------------------------------------------

        // Turns everything the fly can see or hear into one looming value (0-1) and the eye it arrives in.
        // Visual threats are scaled by how well it can see (the lighting); sounds and events are not.
        void UpdateStimulus(float dt)
        {
            var position = transform.position;
            var events = RoomEvents.Current;
            var swatterPosition = player.SwatterTransform.position;

            // How well it sees: light on the fly itself (lamps and the flashlight beam), or light on the threat.
            var beam = FlashlightAt(position);
            lightLevel = RoomLightAt(position) + beam;
            flashlightOnFly = beam;
            var lightOnThreat = RoomLightAt(swatterPosition);
            vision = Mathf.Lerp(Mathf.Min(0.5f, MinVision * nightSight), 1f, Mathf.Clamp01(Mathf.Max(lightLevel * 0.8f, lightOnThreat) / FullVisionLight));

            var best = 0f;
            source = "none";
            var threat = lastThreat;
            void Consider(float value, string label, Vector3 point)
            {
                if (value <= best) return;
                best = value;
                source = label;
                threat = point;
            }
            Vector3 SidePoint(bool right) => position + transform.right * (right ? 0.6f : -0.6f) + Vector3.up * 0.2f;

            // Swatter: how close, how fast it closes in, a swing in progress, or held up and aimed at it.
            var distance = Vector3.Distance(swatterPosition, position);
            if (lastDistance >= 0f && dt > 0f) closingSpeed = Mathf.Lerp(closingSpeed, (lastDistance - distance) / dt, Mathf.Clamp01(dt * 12f));
            lastDistance = distance;
            var stealth = player.Sneaking ? 0.35f : 1f; // Sneaking (hold Ctrl) makes the approach less noticeable.
            var swatter = (Mathf.InverseLerp(2.2f, 0.4f, distance) * 0.45f + Mathf.Clamp01(closingSpeed / 3f) * Mathf.InverseLerp(2.6f, 0.3f, distance) * 0.9f) * stealth;
            var aimCamera = GetCamera();
            if (aimCamera != null)
            {
                var toFly = position - aimCamera.transform.position;
                if (toFly.magnitude < 2f)
                    swatter = Mathf.Max(swatter, Mathf.InverseLerp(14f, 3f, Vector3.Angle(aimCamera.transform.forward, toFly)) * Mathf.InverseLerp(2f, 0.5f, toFly.magnitude) * 0.3f * stealth);
            }
            if (Time.time < swingThreatUntil) swatter = Mathf.Max(swatter, swingThreat);
            Consider(swatter * vision, "swatter", swatterPosition);

            // The player walking at it.
            var bodyDistance = Vector3.Distance(player.Head.position, position);
            if (lastBodyDistance >= 0f && dt > 0f) bodyClosing = Mathf.Lerp(bodyClosing, (lastBodyDistance - bodyDistance) / dt, Mathf.Clamp01(dt * 8f));
            lastBodyDistance = bodyDistance;
            var body = Mathf.InverseLerp(1.6f, 0.3f, bodyDistance) * 0.3f + Mathf.Clamp01(bodyClosing / 2.5f) * Mathf.InverseLerp(2f, 0.3f, bodyDistance) * 0.7f;
            Consider(body * stealth * vision, "player", player.Head.position);

            // The flashlight beam on it unsettles it, and makes it restless.
            if (beam > 0.02f && player.Flashlight != null) Consider(beam * 0.4f, "flashlight", player.Flashlight.transform.position);

            // A passing shadow: a short pulse in one eye, now and then, more often in the beam.
            if (beam > 0.2f) nextAmbient -= dt * beam * 2f;
            if (state != State.Flying && Time.time >= nextAmbient)
            {
                nextAmbient = Time.time + Random.Range(6f, 14f) / restless;
                ambientUntil = Time.time + 0.3f;
                ambientValue = Random.Range(0.6f, 1f);
                ambientPoint = SidePoint(Random.value < 0.5f);
            }
            if (Time.time < ambientUntil) Consider(ambientValue * Mathf.Lerp(0.4f, 1f, vision), "shadow", ambientPoint);

            // Room events.
            if (events != null)
            {
                // A power surge: the room blazes up and the fly goes wild, flicking between eyes.
                if (events.Surge > 0.15f)
                {
                    if (Time.time >= nextSurgeFlip)
                    {
                        surgeRight = !surgeRight;
                        nextSurgeFlip = Time.time + 0.12f;
                    }
                    Consider(events.Surge, "light surge", SidePoint(surgeRight));
                }
                // Lightning flashes through the window.
                if (events.LightningLevel > 0.05f)
                {
                    if (!lightningWasOn) lightningRight = Random.value < 0.5f;
                    lightningWasOn = true;
                    Consider(Mathf.Min(1f, events.LightningLevel * 1.2f), "lightning", SidePoint(lightningRight));
                }
                else lightningWasOn = false;
                // Headlights sweeping the room.
                if (events.HeadlightLevel > 0.1f) Consider(events.HeadlightLevel * 0.3f, "headlights", SidePoint(Mathf.Sin(Time.time * 1.4f) > 0f));
                // The lights cutting out.
                if (previousPower > 0.6f && events.Power <= 0.6f) Pulse(0.55f, 0.3f, SidePoint(Random.value < 0.5f), "blackout");
                previousPower = events.Power;
            }

            // Sounds (see HearNoise) and other short pulses.
            if (Time.time < pulseUntil) Consider(pulseValue, pulseLabel, pulsePoint);

            looming = Mathf.Clamp01(best * skittish);
            lastThreat = threat;
            var local = transform.InverseTransformDirection(threat - position);
            var across = local.x / Mathf.Max(0.001f, (threat - position).magnitude);
            eye = Mathf.Abs(across) < 0.2f ? "center" : across > 0f ? "right" : "left";
        }

        void Pulse(float value, float seconds, Vector3 point, string label)
        {
            if (Time.time < pulseUntil && value <= pulseValue) return;
            pulseValue = value;
            pulseUntil = Time.time + seconds;
            pulsePoint = point;
            pulseLabel = label;
        }

        // A loud sound near the fly (glass breaking, a swing landing). The brain only has eyes, so the game turns
        // the sound into a looming pulse from the direction it came from; the brain still decides what the fly does.
        public static void Noise(Vector3 point, float loudness, float range = 8f)
        {
            if (Current != null) Current.HearNoise(point, loudness, range);
        }

        public void HearNoise(Vector3 point, float loudness, float range = 8f)
        {
            if (state == State.Dead) return;
            // On the Pi the fly's own hearing is applied there.
            var value = Mathf.Clamp01(loudness * (remote ? 1f : hearing) * Mathf.InverseLerp(range, 0.4f, Vector3.Distance(point, transform.position)));
            if (value < (remote ? 0.02f : 0.05f)) return;
            // The ears get it, not the eyes: the side is which ear the sound is nearer to.
            var local = transform.InverseTransformDirection(point - transform.position);
            var across = local.x / Mathf.Max(0.001f, (point - transform.position).magnitude);
            soundEar = Mathf.Abs(across) < 0.2f ? "center" : across > 0f ? "right" : "left";
            if (remote)
            {
                BackendClient.SendSound(value, soundEar);
                return;
            }
            if (value > soundValue || Time.time >= soundUntil) soundValue = value;
            soundUntil = Time.time + 0.35f;
        }

        // --- Light --------------------------------------------------------------------------------------

        // Light from the room's lamps at a point: nearer, brighter and unblocked counts for more. Blackouts and
        // surges already change the lamps' brightness, so they come through here.
        float RoomLightAt(Vector3 point)
        {
            var total = 0.05f; // Faint ambient.
            var atmosphere = RoomAtmosphere.Current;
            if (atmosphere != null)
                foreach (var rig in atmosphere.Rigs)
                {
                    var lamp = rig.main;
                    if (lamp == null || !lamp.enabled || !lamp.gameObject.activeInHierarchy || rig.mainBase < 0.001f) continue;
                    var distance = Vector3.Distance(lamp.transform.position, point);
                    if (distance >= lamp.range) continue;
                    var falloff = 1f - distance / lamp.range;
                    falloff *= falloff;
                    if (Physics.Linecast(lamp.transform.position, point)) falloff *= 0.35f; // Walls and furniture hide most of it.
                    total += falloff * Mathf.Min(lamp.intensity / rig.mainBase, 3f) * 0.6f;
                }
            var events = RoomEvents.Current;
            if (events != null) total += Mathf.Max(events.LightningLevel, events.HeadlightLevel * 0.4f) * 0.8f;
            return total;
        }

        // How strongly the player's flashlight beam is on a point (0 if outside it or blocked).
        float FlashlightAt(Vector3 point)
        {
            var light = player.Flashlight;
            if (light == null || !light.enabled) return 0f;
            var to = point - light.transform.position;
            var distance = to.magnitude;
            if (distance >= light.range || distance < 0.01f) return 0f;
            var angle = Vector3.Angle(light.transform.forward, to);
            var half = light.spotAngle * 0.5f;
            if (angle >= half) return 0f;
            if (Physics.Linecast(light.transform.position, point, out var hit) && hit.distance < distance - 0.06f) return 0f;
            var cone = 1f - Mathf.InverseLerp(light.innerSpotAngle * 0.5f, half, angle);
            return cone * Mathf.Clamp01((1f - distance / light.range) * 1.6f) * Mathf.Clamp01(light.intensity / 5f);
        }

        // --- The brain decides -------------------------------------------------------------------------

        void UpdateBrain()
        {
            if (client == null || !client.Connected)
            {
                lastAction = "brain offline: not reacting";
                return;
            }
            client.SetLooming(looming, eye);
            client.SetSound(Time.time < soundUntil ? soundValue : 0f, soundEar);
            if (!client.TryGetEscape(out var decision)) return;

            lastStrength = decision.strength;
            lastDecisionSide = decision.side;
            lastDecisionAt = Time.time;
            if (decision.spikes != null) lastSpikes = decision.spikes;
            NeuralView.Current?.Feed(decision.active, decision.spikes);
            if (state == State.Flying || Time.time < cooldownUntil) return;

            if (decision.strength >= EscapeThreshold)
            {
                lastAction = $"TAKE OFF ({decision.side})";
                TakeOff(decision.strength, decision.side);
            }
            else if (decision.strength >= FleeThreshold)
            {
                lastAction = $"walking away ({decision.side})";
                fleeUntil = Time.time + 0.6f;
                fleeSide = decision.side;
                if (state == State.Perched) StartWalk(Random.Range(2, 5));
            }
        }

        // Called by the player when a swing lands. The swatter's hit zone is where its imprint goes: the line from
        // the player to the impact point (or to full reach in open air), as wide as the swatter. The fly is hit
        // if it is inside it at that moment, so it cannot dodge once the swing has started.
        public bool TrySwat(Vector3 origin, Vector3 direction, float radius, float length)
        {
            if (state == State.Dead) return false;
            var closest = ClosestOnSegment(origin, origin + direction * length, transform.position);
            if (Vector3.Distance(closest, transform.position) > radius + KillPadding) return false;
            if (remote) BackendClient.SendSwat();
            Die();
            return true;
        }

        static Vector3 ClosestOnSegment(Vector3 a, Vector3 b, Vector3 p)
        {
            var ab = b - a;
            var t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / Mathf.Max(0.0001f, ab.sqrMagnitude));
            return a + ab * t;
        }

        void Die()
        {
            state = State.Dead;
            buzz.Stop();
            glow.intensity = 0f;
            beacon = 0f;
            flightGlow = 0f;
            model.localScale = new Vector3(1.7f, 0.12f, 1.5f) * BodyScale;
            wingLeft.gameObject.SetActive(false);
            wingRight.gameObject.SetActive(false);
            foreach (var leg in legs) leg.gameObject.SetActive(false);
            var splat = FlySounds.Get("fly_splat") ?? FlySounds.Get("impact_squish");
            AudioSource.PlayClipAtPoint(splat, transform.position, 0.8f);
            if (GameRound.Current != null) GameRound.Current.Win(transform.position);
            else player.Announce("You got the fly!  (R to reload the room)");
        }

        // --- Perching and walking ------------------------------------------------------------------------

        void UpdatePerched(float dt)
        {
            ChirpQuietly(dt);
            perchTimer -= dt;
            if (perchTimer > 0f) return;

            if (Random.value < 0.35f / restless || neighbours[spotIndex].Count == 0)
            {
                // Turn in place and groom.
                perchHeading = Quaternion.AngleAxis(Random.Range(-110f, 110f), perchNormal) * perchHeading;
                groomUntil = Time.time + Random.Range(0.8f, 2.2f);
                perchTimer = Random.Range(1.5f, 4f);
            }
            else StartWalk(Random.Range(3, 10));
        }

        // A short quiet buzz every few seconds, so a perched fly can still be located by ear.
        void ChirpQuietly(float dt)
        {
            if (Time.time >= nextChirp)
            {
                nextChirp = Time.time + Random.Range(3f, 6f);
                chirpUntil = Time.time + 0.45f;
            }
            buzz.pitch = 0.9f;
            buzz.volume = Mathf.MoveTowards(buzz.volume, Time.time < chirpUntil ? 0.55f : 0f, dt * 8f);
        }

        void StartWalk(int steps)
        {
            if (neighbours[spotIndex].Count == 0) return;
            walkStepsLeft = steps;
            walkPrevious = -1;
            state = State.Walking;
            groomUntil = 0f;
            ChooseNextStep();
        }

        // Picks the next landing spot to walk to: keeps going roughly the same way, or flees when the brain says so.
        void ChooseNextStep()
        {
            var options = neighbours[spotIndex];
            var fleeing = Time.time < fleeUntil;
            var wanted = perchHeading;
            if (fleeing)
            {
                var away = Vector3.ProjectOnPlane(transform.position - lastThreat, perchNormal);
                if (away.sqrMagnitude < 1e-4f) away = perchHeading;
                wanted = Quaternion.AngleAxis(fleeSide == "left" ? -50f : 50f, perchNormal) * away.normalized;
            }

            var best = -1;
            var bestScore = float.MinValue;
            foreach (var candidate in options)
            {
                var step = Vector3.ProjectOnPlane(spots[candidate].position - spots[spotIndex].position, perchNormal);
                if (step.sqrMagnitude < 1e-6f) continue;
                var score = Vector3.Dot(step.normalized, wanted.normalized) + Random.value * (fleeing ? 0.2f : 0.9f);
                if (candidate == walkPrevious) score -= 1.2f; // Do not go straight back.
                if (score > bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }
            if (best < 0)
            {
                state = State.Perched;
                perchTimer = Random.Range(1f, 3f);
                return;
            }
            walkTarget = best;
        }

        void UpdateWalking(float dt)
        {
            ChirpQuietly(dt);
            var fleeing = Time.time < fleeUntil;
            var target = spots[walkTarget].position + spots[walkTarget].normal * LiftFromSurface;
            var toTarget = target - transform.position;
            var distance = toTarget.magnitude;
            var direction = distance > 1e-5f ? toTarget / distance : perchHeading;

            // Turn toward the next spot first, then walk; a fleeing fly is quicker on both.
            var turnRate = fleeing ? 9f : 4f;
            var tangent = Vector3.ProjectOnPlane(direction, perchNormal);
            if (tangent.sqrMagnitude > 1e-6f) perchHeading = Vector3.RotateTowards(perchHeading, tangent.normalized, turnRate * dt, 0f);
            var facing = Mathf.Clamp01(Vector3.Dot(perchHeading, tangent.normalized) * 1.4f - 0.1f);
            var speed = WalkSpeed * pace * (fleeing ? 3.5f : 1f) * (0.6f + 0.4f * Mathf.PerlinNoise(Time.time * 1.5f, 4f)) * facing;
            transform.position += direction * Mathf.Min(speed * dt, distance);
            Pose(perchNormal, perchHeading);

            if (distance > 0.012f) return;
            walkPrevious = spotIndex;
            spotIndex = walkTarget;
            perchNormal = spots[spotIndex].normal;
            walkStepsLeft--;
            if (walkStepsLeft <= 0)
            {
                state = State.Perched;
                perchTimer = Random.Range(1.5f, 4f);
                landedAt = Time.time;
                return;
            }
            ChooseNextStep();
        }

        void Perch(int index, Vector3 heading)
        {
            spotIndex = index;
            var spot = spots[index];
            perchNormal = spot.normal;
            perchHeading = heading.sqrMagnitude > 0.001f ? heading : Vector3.forward;
            transform.position = spot.position + spot.normal * LiftFromSurface;
            Pose(perchNormal, perchHeading);
            state = State.Perched;
            escaping = false;
            landedAt = Time.time;
            nextChirp = Time.time + 1.2f;
        }

        void Pose(Vector3 up, Vector3 heading)
        {
            var forward = Vector3.ProjectOnPlane(heading, up);
            if (forward.sqrMagnitude < 0.0001f) forward = Vector3.ProjectOnPlane(Vector3.forward, up);
            if (forward.sqrMagnitude < 0.0001f) forward = Vector3.ProjectOnPlane(Vector3.right, up);
            transform.rotation = Quaternion.LookRotation(forward.normalized, up);
        }

        // --- Flight (started by the brain) --------------------------------------------------------------

        // The brain's side sets the first turn; its strength sets speed and how many legs the flight has.
        // The flight crosses the room. The game only forces the landing: a furniture spot far from here
        // and away from the threat.
        void TakeOff(float strength, string side)
        {
            var sideDirection = transform.right * (side == "left" ? -1f : 1f);
            var away = transform.position - lastThreat;
            if (away.sqrMagnitude < 1e-4f) away = -transform.forward;
            away.Normalize();
            var burst = (sideDirection * 0.6f + away * 0.8f + Vector3.up * 0.5f).normalized;

            var minTravel = Mathf.Lerp(1.5f, 3f, strength);
            var chosen = -1;
            var bestScore = float.MinValue;
            for (var attempt = 0; attempt < 24; attempt++)
            {
                var candidate = Random.Range(0, spots.Count);
                if (candidate == spotIndex) continue;
                var travel = Vector3.Distance(spots[candidate].position, transform.position);
                var score = Mathf.Min(travel, minTravel * 1.6f) + Random.value * 0.6f;
                if (travel < minTravel) score -= 3f;
                if (Vector3.Distance(spots[candidate].position, lastThreat) < 1f) score -= 5f;
                if (score > bestScore)
                {
                    bestScore = score;
                    chosen = candidate;
                }
            }
            if (chosen < 0) return;

            route.Clear();
            var first = ClampToAir(transform.position + burst * Mathf.Lerp(0.8f, 2f, strength));
            route.Add(Physics.Linecast(transform.position + Vector3.up * 0.05f, first) ? ClampToAir(transform.position + Vector3.up * 0.7f + burst * 0.3f) : first);

            // Sharp, quick turns across the room, like a real fly's flight.
            var previous = route[0];
            var legs = 1 + Mathf.RoundToInt(strength * 3f);
            for (var i = 0; i < legs; i++)
                for (var attempt = 0; attempt < 8; attempt++)
                {
                    var spot = Random.Range(0, spots.Count);
                    if (Vector3.Distance(spots[spot].position, previous) < 1.2f) continue;
                    var point = ClampToAir(spots[spot].position + Vector3.up * Random.Range(0.5f, 1.2f) + Random.insideUnitSphere * 0.3f);
                    if (Physics.Linecast(previous, point)) continue;
                    route.Add(point);
                    previous = point;
                    break;
                }
            // Come in from above the landing spot.
            route.Add(ClampToAir(spots[chosen].position + Vector3.up * 0.9f));

            BeginFlight(chosen, (2f + 3.5f * strength) * pace, 14f, true);
            velocity = burst * flightSpeed * 0.7f;
        }

        Vector3 ClampToAir(Vector3 point)
        {
            point.y = Mathf.Clamp(point.y, 0.45f, Mathf.Max(0.8f, ceilingHeight - 0.45f));
            return point;
        }

        void BeginFlight(int landing, float speed, float agility, bool escape)
        {
            landingSpot = landing;
            landingNormal = spots[landing].normal;
            landingPoint = spots[landing].position + landingNormal * LiftFromSurface;
            route.Add(landingPoint + landingNormal * 0.3f);
            route.Add(landingPoint);
            routeIndex = 0;
            flightSpeed = speed;
            flightAgility = agility;
            flightStarted = Time.time;
            wobbleSeed = Random.value * 10f;
            escaping = escape;
            state = State.Flying;
            groomUntil = 0f;
            walkTarget = -1;
            cooldownUntil = Time.time + 0.4f;
        }

        void UpdateFlying(float dt)
        {
            var target = route[routeIndex];
            var last = routeIndex == route.Count - 1;
            var toTarget = target - transform.position;
            var distance = toTarget.magnitude;
            var speed = last ? Mathf.Lerp(0.3f, flightSpeed, Mathf.Clamp01(distance / 0.6f)) : flightSpeed;
            var desired = toTarget / Mathf.Max(0.001f, distance) * speed;
            // Buzzing wander, fading out as it lands.
            var wander = last ? Mathf.Clamp01(distance / 0.6f) : 1f;
            desired += (Vector3.Cross(desired.normalized, Vector3.up) * Mathf.Sin(Time.time * 7f + wobbleSeed) * 0.35f
                        + Vector3.up * Mathf.Sin(Time.time * 5f + wobbleSeed * 2f) * 0.25f) * wander;
            velocity = Vector3.MoveTowards(velocity, desired, dt * flightAgility);
            transform.position += velocity * dt;
            if (!last && distance < 0.25f) routeIndex++;

            var settle = last ? Mathf.InverseLerp(0.5f, 0.05f, distance) : 0f;
            Pose(Vector3.Slerp(Vector3.up, landingNormal, settle), velocity.sqrMagnitude > 1e-4f ? velocity : transform.forward);
            buzz.pitch = escaping ? 1.3f : 1f;
            buzz.volume = Mathf.MoveTowards(buzz.volume, 1f, dt * 10f);

            if ((last && distance < 0.04f) || Time.time - flightStarted > 40f)
            {
                Perch(landingSpot, transform.forward);
                perchTimer = Random.Range(1.5f, 4f);
                cooldownUntil = Time.time + 0.8f; // Settle before it can bolt again.
            }
        }

        // --- Model -------------------------------------------------------------------------------------

        void BuildModel(RoomAssets assets)
        {
            model = new GameObject("Model").transform;
            model.SetParent(transform, false);
            model.localScale = Vector3.one * BodyScale;

            var body = Own(assets, RoomVisuals.LitMaterial(new Color(0.07f, 0.065f, 0.06f), 0.55f));
            var thoraxMaterial = Own(assets, RoomVisuals.LitMaterial(new Color(0.12f, 0.11f, 0.1f), 0.5f));
            var eyeMaterial = Own(assets, RoomVisuals.LitMaterial(new Color(0.55f, 0.04f, 0.03f), 0.9f));
            var wingMaterial = RoomProps.ClearSurface(new Color(0.8f, 0.85f, 0.9f, 0.3f), assets);
            var legMaterial = Own(assets, RoomVisuals.LitMaterial(new Color(0.05f, 0.05f, 0.045f), 0.3f));

            Part(PrimitiveType.Sphere, "Thorax", model, Vector3.zero, new Vector3(0.016f, 0.015f, 0.021f), thoraxMaterial);
            Part(PrimitiveType.Sphere, "Abdomen", model, new Vector3(0f, -0.001f, -0.022f), new Vector3(0.014f, 0.013f, 0.025f), body);
            head = new GameObject("Head").transform;
            head.SetParent(model, false);
            head.localPosition = new Vector3(0f, 0.001f, 0.019f);
            Part(PrimitiveType.Sphere, "Skull", head, Vector3.zero, new Vector3(0.011f, 0.011f, 0.01f), thoraxMaterial);
            Part(PrimitiveType.Sphere, "Eye L", head, new Vector3(-0.0045f, 0.0008f, 0.0016f), new Vector3(0.0065f, 0.0075f, 0.0065f), eyeMaterial);
            Part(PrimitiveType.Sphere, "Eye R", head, new Vector3(0.0045f, 0.0008f, 0.0016f), new Vector3(0.0065f, 0.0075f, 0.0065f), eyeMaterial);

            wingLeft = Wing("Wing L", -1f, wingMaterial);
            wingRight = Wing("Wing R", 1f, wingMaterial);

            legs = new Transform[6];
            legBase = new Quaternion[6];
            for (var i = 0; i < 6; i++)
            {
                var side = i % 2 == 0 ? -1f : 1f;
                var row = i / 2; // 0 front, 1 middle, 2 hind
                var pivot = new GameObject($"Leg {i}").transform;
                pivot.SetParent(model, false);
                pivot.localPosition = new Vector3(side * 0.005f, -0.005f, 0.008f - row * 0.008f);
                legBase[i] = Quaternion.Euler(0f, side * (row == 0 ? 25f : 5f + row * 12f), side * 38f);
                pivot.localRotation = legBase[i];
                Part(PrimitiveType.Cylinder, "Segment", pivot, new Vector3(0f, -0.0075f, 0f), new Vector3(0.0014f, 0.0075f, 0.0014f), legMaterial);
                legs[i] = pivot;
            }
        }

        Transform Wing(string name, float side, Material material)
        {
            var pivot = new GameObject(name).transform;
            pivot.SetParent(model, false);
            pivot.localPosition = new Vector3(side * 0.005f, 0.008f, 0.004f);
            var blade = Part(PrimitiveType.Sphere, "Blade", pivot, new Vector3(side * 0.0085f, 0f, -0.012f), new Vector3(0.012f, 0.0008f, 0.034f), material);
            blade.localRotation = Quaternion.Euler(0f, side * -12f, 0f);
            return pivot;
        }

        static Transform Part(PrimitiveType type, string name, Transform parent, Vector3 position, Vector3 scale, Material material)
        {
            var part = GameObject.CreatePrimitive(type);
            part.name = name;
            Destroy(part.GetComponent<Collider>());
            part.transform.SetParent(parent, false);
            part.transform.localPosition = position;
            part.transform.localScale = scale;
            var renderer = part.GetComponent<Renderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return part.transform;
        }

        static Material Own(RoomAssets assets, Material material) => assets != null ? assets.Keep(material) : material;

        void Animate()
        {
            if (state == State.Flying)
            {
                // Wings blur through a wide arc; legs tuck up.
                var flap = Mathf.Sin(Time.time * 190f);
                wingLeft.localRotation = Quaternion.Euler(0f, 0f, 40f + flap * 38f);
                wingRight.localRotation = Quaternion.Euler(0f, 0f, -40f - flap * 38f);
                for (var i = 0; i < legs.Length; i++) legs[i].localRotation = Quaternion.Euler(-40f, 0f, 0f) * legBase[i];
                return;
            }

            // Wings folded back.
            wingLeft.localRotation = Quaternion.Euler(0f, 0f, 6f);
            wingRight.localRotation = Quaternion.Euler(0f, 0f, -6f);

            if (state == State.Walking)
            {
                // Tripod gait: front-left, middle-right and hind-left step together.
                var phase = Time.time * (Time.time < fleeUntil ? 34f : 15f);
                for (var i = 0; i < legs.Length; i++)
                {
                    var group = (i / 2 + i % 2) % 2;
                    legs[i].localRotation = Quaternion.Euler(Mathf.Sin(phase + group * Mathf.PI) * 24f, 0f, 0f) * legBase[i];
                }
            }
            else
            {
                var grooming = Time.time < groomUntil;
                var rub = grooming ? Mathf.Sin(Time.time * 22f) * 22f : 0f;
                for (var i = 0; i < legs.Length; i++) legs[i].localRotation = legBase[i];
                legs[0].localRotation = Quaternion.Euler(-30f + rub, 0f, 0f) * legBase[0];
                legs[1].localRotation = Quaternion.Euler(-30f - rub, 0f, 0f) * legBase[1];
            }
            head.localRotation = Quaternion.Euler(Mathf.PerlinNoise(Time.time * 1.7f, 3f) * 14f - 7f, (Mathf.PerlinNoise(Time.time * 1.3f, 9f) - 0.5f) * 30f, 0f);
        }

        // --- Finding the fly ---------------------------------------------------------------------------

        const float BeaconDelay = 1f;     // Seconds after landing before the blue marker starts to appear.
        const float BeaconRamp = 7f;      // Seconds for it to reach full brightness.

        Camera GetCamera()
        {
            if (viewCamera == null) viewCamera = Camera.main != null ? Camera.main : FindFirstObjectByType<Camera>();
            return viewCamera;
        }

        void UpdateLocator(float dt)
        {
            // Sound behind the player is muffled, which makes the direction easy to tell.
            var cameraTransform = player.Head;
            var toFly = (transform.position - cameraTransform.position).normalized;
            var facing = Vector3.Dot(cameraTransform.forward, toFly); // 1 ahead, -1 behind
            var cutoff = Mathf.Lerp(1100f, 22000f, Mathf.InverseLerp(-1f, 0.35f, facing));
            muffle.cutoffFrequency = Mathf.Lerp(muffle.cutoffFrequency, cutoff, Mathf.Clamp01(dt * 14f));

            // Marker: dark while it flies, then slowly lights up once it has landed.
            var target = state != State.Flying ? Mathf.Clamp01((Time.time - landedAt - BeaconDelay) / BeaconRamp) : 0f;
            beacon = target > beacon ? Mathf.MoveTowards(beacon, target, dt / BeaconRamp) : Mathf.MoveTowards(beacon, target, dt * 3f);
            // A smaller, steady glow while it flies, so it can be followed in the dark.
            flightGlow = Mathf.MoveTowards(flightGlow, state == State.Flying ? 1f : 0f, dt * 6f);
            glow.intensity = Mathf.Max(beacon * 1.6f, flightGlow * 1f);
            glow.transform.position = transform.position + perchNormal * 0.06f;
        }

        static Texture2D MakeGlow()
        {
            const int size = 64;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, name = "Fly Marker" };
            var pixels = new Color32[size * size];
            for (var y = 0; y < size; y++)
                for (var x = 0; x < size; x++)
                {
                    var d = new Vector2(x - size / 2f + 0.5f, y - size / 2f + 0.5f).magnitude / (size / 2f);
                    var ring = Mathf.Exp(-Mathf.Pow((d - 0.62f) / 0.1f, 2f));
                    var core = Mathf.Exp(-d * d * 7f);
                    var alpha = Mathf.Clamp01(Mathf.Max(core, ring * 0.9f)) * Mathf.Clamp01((1f - d) * 6f);
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)(alpha * 255f));
                }
            texture.SetPixels32(pixels);
            texture.Apply();
            return texture;
        }

        void DrawMarker()
        {
            var alpha = Mathf.Max(beacon, flightGlow * 0.5f);
            if (alpha <= 0.01f || state == State.Dead) return;
            var camera = GetCamera();
            if (camera == null) return;

            var screen = camera.WorldToScreenPoint(transform.position + perchNormal * 0.05f);
            var point = new Vector2(screen.x, Screen.height - screen.y);
            var centre = new Vector2(Screen.width, Screen.height) * 0.5f;
            if (screen.z < 0f) point = centre * 2f - point; // Behind the camera the projection is mirrored.
            const float margin = 46f;
            var halfSize = centre - new Vector2(margin, margin);
            var offset = point - centre;
            var onScreen = screen.z > 0f && Mathf.Abs(offset.x) < halfSize.x && Mathf.Abs(offset.y) < halfSize.y;
            if (!onScreen)
            {
                var scale = Mathf.Min(halfSize.x / Mathf.Max(0.01f, Mathf.Abs(offset.x)), halfSize.y / Mathf.Max(0.01f, Mathf.Abs(offset.y)));
                point = centre + offset * scale;
            }

            var pulse = 0.82f + 0.18f * Mathf.Sin(Time.time * 3f);
            var size = onScreen ? 64f : 52f;
            var previousColor = GUI.color;
            GUI.color = new Color(0.55f, 0.82f, 1f, alpha * pulse);
            GUI.DrawTexture(new Rect(point.x - size / 2f, point.y - size / 2f, size, size), glowTexture);
            if (!onScreen)
            {
                var angle = Mathf.Atan2(offset.y, offset.x) * Mathf.Rad2Deg;
                var matrix = GUI.matrix;
                GUIUtility.RotateAroundPivot(angle, point);
                GUI.Label(new Rect(point.x + 14f, point.y - 15f, 30f, 30f), ">", new GUIStyle(GUI.skin.label) { fontSize = 28, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter });
                GUI.matrix = matrix;
            }
            GUI.color = previousColor;
        }

        // --- Brain panel (top right, so it never overlaps the player's HUD) ------------------------------

        static int Spike(int[] spikes, int index) => spikes != null && index < spikes.Length ? spikes[index] : 0;

        // How used to each kind of input the fly has got (100% = fresh, lower = it reacts less).
        string HabituationLine()
        {
            var e = remote ? BackendClient.Efficacy : client?.Efficacy;
            if (e == null) return "";
            string P(float v) => v < 0.97f ? $"<color=#FFC857>{Mathf.RoundToInt(v * 100f)}%</color>" : "100%";
            return $"<b>Used to it</b>  eyes L {P(e[0])} R {P(e[1])}   ears L {P(e[2])} R {P(e[3])}";
        }

        void OnGUI()
        {
            if (MainMenu.Active) return;
            DrawMarker();
            // The brain panel's toggle. Press Esc to free the mouse, then click it (J works any time).
            var toggle = new Rect(Screen.width - 166f, 12f, 150f, 24f);
            UiHit.Set("brain", toggle);
            if (GUI.Button(toggle, showStatus ? "Fly brain  [hide]" : "Fly brain  [show]")) showStatus = !showStatus;
            if (!showStatus) return;
            if (statusStyle == null)
                statusStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, richText = true, wordWrap = false, normal = { textColor = new Color(0.86f, 0.9f, 0.86f) } };
                statusStyle.hover.textColor = statusStyle.active.textColor = statusStyle.focused.textColor = statusStyle.normal.textColor;

            var online = remote ? BackendClient.Connected : client != null && client.Connected;
            var brainText = remote ? BackendClient.BrainInfo : client?.BrainInfo;
            var info = string.IsNullOrEmpty(brainText) ? "connected" : brainText;
            var header = online ? $"<color=#7CFF9B>FLY BRAIN  {info}</color>"
                       : client == null && !remote ? "<color=#FF7C7C>FLY BRAIN  switched off - fly not reacting</color>"
                       : "<color=#FF7C7C>FLY BRAIN  OFFLINE - fly not reacting</color>";
            var fresh = Time.time - lastDecisionAt < 0.5f;
            var strength = fresh ? lastStrength : 0f;
            var spikes = fresh ? lastSpikes : null;

            var lines = new[]
            {
                header,
                remote ? (online ? "running on the Raspberry Pi" : "Raspberry Pi link lost, reconnecting") : online ? client.url : "start brain/server.py, or set FLYAI_BRAIN",
                "",
                $"<b>Stimulus</b>   looming {looming:0.00}   eye {eye}   from {source}",
                $"<b>Light</b>       at fly {lightLevel:0.00}   vision {vision:0.00}   flashlight on fly {flashlightOnFly:0.00}",
                $"<b>Brain out</b>  strength {strength:0.00}   jumps {(fresh ? lastDecisionSide : "-")}",
                HabituationLine(),
                $"Eye inputs     LPLC2 L{Spike(spikes, 0)} R{Spike(spikes, 1)}    LC4 L{Spike(spikes, 2)} R{Spike(spikes, 3)}",
                $"Giant Fiber    L{Spike(spikes, 9)} R{Spike(spikes, 10)}   (DNp01)",
                $"Escape DNs     02: {Spike(spikes, 11)}/{Spike(spikes, 12)}   04: {Spike(spikes, 13)}/{Spike(spikes, 14)}   11: {Spike(spikes, 17)}/{Spike(spikes, 18)}",
                "",
                $"<b>Fly</b>         {state}   last action: {lastAction}",
                $"<b>Personality</b>  {PersonalityName}",
                "[J] or the button hides this",
            };

            const float lineHeight = 19f;
            var width = 470f;
            var height = lines.Length * lineHeight + 14f;
            var panel = new Rect(Screen.width - width - 16f, 42f, width, height);
            var previous = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.62f);
            GUI.DrawTexture(panel, Texture2D.whiteTexture);
            GUI.color = previous;
            for (var i = 0; i < lines.Length; i++)
                GUI.Label(new Rect(panel.x + 10f, panel.y + 7f + i * lineHeight, width - 20f, lineHeight), lines[i], statusStyle);
        }
    }
}
