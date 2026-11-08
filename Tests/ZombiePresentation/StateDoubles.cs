// UI identity doubles only; camera math uses the installed Unity managed structs.
public class Character
{
    public static Character localCharacter;
    public InputState input = new InputState();
    public Voice voice;
}
public class InputState { public bool pauseWasPressed; }
public class Voice { public bool transmitting; }
public static class MainCameraMovement
{
    public static Character specCharacter { get; private set; }
    public static void Observe(Character value) { specCharacter = value; }
}
namespace ImZombie
{
    public static class ZombieController
    {
        public static Character ActiveZombieCharacter;
        public static Character ParkedPlayerCharacter;
    }
}
