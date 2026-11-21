using UnityEngine;

namespace FlyAI
{
    public enum RoomPartKind
    {
        Furniture,
        Wall,
        Floor,
        Ceiling,
        Boundary,
    }

    // Tells a swing what it hit. Added to each room collider when a scan loads.
    public class RoomPart : MonoBehaviour
    {
        public RoomPartKind kind;
        public string label;
    }
}
