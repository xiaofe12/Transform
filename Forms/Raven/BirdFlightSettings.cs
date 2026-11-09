using BepInEx.Configuration;
using UnityEngine;

namespace ImRaven;

// Both birds use the same flight/controller implementation, with separate live settings.
internal sealed class BirdFlightSettings
{
    internal ConfigEntry<KeyCode> UpKey, DownKey, SprintKey;
    internal ConfigEntry<float> MovementSpeed, VerticalSpeed, SprintMultiplier, CameraDistance, CameraHeight, CameraFov;

    internal static BirdFlightSettings CreateCrow(ConfigFile config) => new BirdFlightSettings
    {
        UpKey = config.Bind("Crow Controls", "UpKey", KeyCode.Space, "Hold to rise in Raven form."),
        DownKey = config.Bind("Crow Controls", "DownKey", KeyCode.LeftControl, "Hold to descend in Raven form."),
        SprintKey = config.Bind("Crow Controls", "SprintKey", KeyCode.LeftShift, "Hold to boost in Raven form."),
        MovementSpeed = Bind(config, "Crow", "MovementSpeed", 10f, 0f, 40f, "Raven flight speed."),
        VerticalSpeed = Bind(config, "Crow", "VerticalSpeed", 7f, 0f, 30f, "Raven ascent/descent speed."),
        SprintMultiplier = Bind(config, "Crow", "SprintMultiplier", 1.8f, 1f, 5f, "Raven boost multiplier."),
        CameraDistance = Bind(config, "Crow Camera", "Distance", 5f, 3f, 15f, "Raven third-person camera distance."),
        CameraHeight = Bind(config, "Crow Camera", "Height", 1.5f, 0.3f, 8f, "Raven camera height."),
        CameraFov = Bind(config, "Crow Camera", "Fov", 80f, 60f, 110f, "Raven camera field of view.")
    };

    private static ConfigEntry<float> Bind(ConfigFile config, string section, string key,
        float value, float min, float max, string description)
        => config.Bind(section, key, value, new ConfigDescription(description, new AcceptableValueRange<float>(min, max)));
}
