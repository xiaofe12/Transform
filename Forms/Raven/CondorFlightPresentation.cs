using UnityEngine;

namespace ImRaven;

internal sealed class CondorFlightPresentation : MonoBehaviour
{
    private readonly CondorFlightState _state = new CondorFlightState();
    private Animator _animator;
    private Animation _animation;
    private string _playing;
    private bool _ready;

    internal CondorFlightMode Apply(Vector3 velocity, float deltaTime, bool carrying)
    {
        if (!_ready)
        {
            _animator = GetComponentInChildren<Animator>(true);
            _animation = GetComponentInChildren<Animation>(true);
            if (_animator != null) _animator.applyRootMotion = false;
            _ready = true;
        }
        CondorFlightMode mode = _state.Step(velocity, deltaTime, carrying);
        if (_animator != null)
        {
            _animator.SetBool("Dive", mode == CondorFlightMode.Dive);
            _animator.SetBool("Flap", mode == CondorFlightMode.Flap);
        }
        if (_animation != null)
        {
            // The game-file loader provides the original Fly/Flap clips. A dive
            // uses the Fly pose and pitches the visual in the controller.
            string clip = mode == CondorFlightMode.Flap ? "CondorFlap" : "CondorFly";
            if (_playing != clip || !_animation.IsPlaying(clip))
            {
                _animation.CrossFade(clip, 0.15f);
                _playing = clip;
            }
        }
        return mode;
    }
}
