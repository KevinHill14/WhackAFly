using UnityEngine;

namespace FlyAI
{
    // Easy, Medium or Hard: which kinds of fly personality can be rolled.
    // Easy flies are slower and calmer, hard flies are faster and more sensitive, medium is a mix of both.
    public static class Difficulty
    {
        public const int Easy = 0, Medium = 1, Hard = 2;
        const string Key = "FlyAI.Difficulty";
        public static readonly string[] Names = { "Easy", "Medium", "Hard" };

        public static int Level
        {
            get
            {
                try { return Mathf.Clamp(PlayerPrefs.GetInt(Key, Medium), Easy, Hard); }
                catch (System.Exception) { return Medium; }
            }
            set
            {
                try { PlayerPrefs.SetInt(Key, Mathf.Clamp(value, Easy, Hard)); PlayerPrefs.Save(); }
                catch (System.Exception) { }
            }
        }

        public static string Name => Names[Level];

        // A trait value: easy flies take the low range, hard flies the high range, medium picks either at random.
        public static float Roll(float easyMin, float easyMax, float hardMin, float hardMax)
        {
            var hard = Level == Hard || (Level == Medium && Random.value < 0.5f);
            return hard ? Random.Range(hardMin, hardMax) : Random.Range(easyMin, easyMax);
        }
    }
}
