using System;

namespace FlyAI
{
    [Serializable]
    public class LatestScan
    {
        public string id;
        public string glb;
        public string room;
    }

    [Serializable]
    public class RoomData
    {
        public FloorData floor;
        public float ceilingHeight;
        public WallData[] walls;
        public ObjectData[] objects;
        public LandingSpotData[] landingSpots;
        public PropData[] props;
        public LightData[] lights;
        public SoundAnchorData[] soundAnchors;
        public WindowData window;
        public DoorData[] doors;
        public WallDecorData[] wallDecor;
        public PlayerStartData playerStart;
        public string layoutSource;
    }

    [Serializable]
    public class DoorData
    {
        public string source;
        public string wall;
        public float[] center;
        public float[] normal;
        public float[] along;
        public float width;
        public float height;
    }

    [Serializable]
    public class WallDecorData
    {
        public string id;
        public string type;
        public string wall;
        public float[] center;
        public float[] normal;
        public float width;
        public float height;
    }

    [Serializable]
    public class WindowData
    {
        public string source;
        public string wall;
        public float[] center;
        public float[] normal;
        public float[] along;
        public float width;
        public float height;
    }

    [Serializable]
    public class LandingSpotData
    {
        public string id;
        public float[] position;
        public float[] normal;
        public string surface;
        public string surfaceKind;
    }

    [Serializable]
    public class PropData
    {
        public string id;
        public string type;
        public float[] position;
        public float rotationY;
        public string surface;
        public string surfaceKind;
        public float radius;
        public float height;
    }

    [Serializable]
    public class LightData
    {
        public string id;
        public string type;
        public float[] position;
        public float[] mount;
        public float[] normal;
        public string surface;
        public string surfaceKind;
        public float intensity;
        public float range;
        public string color;
    }

    [Serializable]
    public class SoundAnchorData
    {
        public string id;
        public string sound;
        public float[] position;
        public float[] normal;
        public string source;
        public string prop;
        public string sourceObject;
        public string surfaceKind;
        public float radius;
        public float height;
        public float volume;
        public float range;
        public bool loop;
    }

    [Serializable]
    public class FloorData
    {
        // JsonUtility cannot read nested arrays, so this is x0,z0,x1,z1...
        public float[] polygonFlat;
        public float area;
    }

    [Serializable]
    public class WallData
    {
        public string node;
        public float[] start;
        public float[] end;
        public float height;
        public float thickness;
    }

    [Serializable]
    public class ObjectData
    {
        public string label;
        public string node;
        public float[] position;
        public float[] size;
        public float rotationY;
        public string color;
    }

    [Serializable]
    public class PlayerStartData
    {
        public float[] position;
        public float yaw;
    }
}
