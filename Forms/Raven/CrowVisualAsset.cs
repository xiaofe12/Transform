using System;
using System.Collections.Generic;
using UnityEngine;
using UnityTransform = UnityEngine.Transform;

namespace ImRaven;

// The native Resources prefab is available in Airport. Copy only its render
// hierarchy, native particle leaves and animation-driven SFX_PlayOneShot components.
// Never instantiate VoiceThief, PhotonView or cleanup/voice-stealing scripts.
internal static class CrowVisualAsset
{
    private const float FeatherScale = 0.4f;
    private static GameObject _template;
    private static float _retryAt;
    private static bool _loggedFailure;

    internal static bool EnsureTemplate()
    {
        if (_template != null) return true;
        if (Time.unscaledTime < _retryAt) return false;
        _retryAt = Time.unscaledTime + 10f;
        GameObject root = null;
        try
        {
            GameObject prefab = Resources.Load<GameObject>("RavenVoiceThief");
            Animator source = prefab != null ? prefab.GetComponentInChildren<Animator>(true) : null;
            if (source == null || source.runtimeAnimatorController == null)
                throw new InvalidOperationException("Native RavenVoiceThief model/controller is unavailable.");
            bool hasFlight = false, hasIdle = false;
            foreach (AnimationClip clip in source.runtimeAnimatorController.animationClips)
            {
                if (clip != null && clip.name == "RavenFlyAway") hasFlight = true;
                if (clip != null && clip.name == "RavenIdle") hasIdle = true;
            }
            if (!hasFlight || !hasIdle || source.transform.Find("Armature") == null)
                throw new InvalidOperationException("Native Raven flight/idle animation or armature is unavailable.");
            root = new GameObject("Transform.Crow.Template");
            root.SetActive(false);
            UnityEngine.Object.DontDestroyOnLoad(root);
            var nodes = new Dictionary<UnityTransform, UnityTransform>();
            CopyHierarchy(source.transform, root.transform, nodes);
            // The template root uses local model coordinates, not the prefab's
            // perch/world offset; the controller owns its position and facing.
            UnityTransform model = nodes[source.transform];
            model.localPosition = Vector3.zero;
            model.localRotation = Quaternion.identity;
            int meshes = 0;
            foreach (SkinnedMeshRenderer original in source.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (original.sharedMesh == null) continue;
                var renderer = nodes[original.transform].gameObject.AddComponent<SkinnedMeshRenderer>();
                renderer.sharedMesh = original.sharedMesh;
                renderer.sharedMaterials = original.sharedMaterials;
                var bones = original.bones;
                for (int i = 0; i < bones.Length; i++) bones[i] = nodes[bones[i]];
                renderer.bones = bones;
                renderer.rootBone = original.rootBone != null ? nodes[original.rootBone] : model;
                renderer.localBounds = original.localBounds;
                renderer.updateWhenOffscreen = true;
                renderer.shadowCastingMode = original.shadowCastingMode;
                renderer.receiveShadows = original.receiveShadows;
                meshes++;
            }
            if (meshes == 0) throw new InvalidOperationException("Native Raven has no skinned mesh.");
            // Idle/FlyAway animate these components' m_Enabled curves, rather
            // than calling animation events or playing child AudioSources.
            // Preserve the exact script type, transform path and serialized SFX
            // references so the native animator and SFX mixer drive the sounds.
            foreach (SFX_PlayOneShot original in source.GetComponentsInChildren<SFX_PlayOneShot>(true))
            {
                var sound = nodes[original.transform].gameObject.AddComponent<SFX_PlayOneShot>();
                sound.playOnStart = original.playOnStart;
                sound.playOnEnable = original.playOnEnable;
                sound.followTransform = original.followTransform;
                sound.stopOnDisable = original.stopOnDisable;
                sound.sfx = original.sfx;
                sound.sfxs = original.sfxs;
                sound.enabled = original.enabled;
            }
            var animator = model.gameObject.AddComponent<Animator>();
            animator.avatar = source.avatar;
            animator.runtimeAnimatorController = source.runtimeAnimatorController;
            animator.applyRootMotion = false;
            animator.fireEvents = false;
            animator.speed = 0f;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            root.AddComponent<CrowFlightPresentation>();
            _template = root;
            _loggedFailure = false;
            RavenPlugin.Log?.LogInfo("[Raven] Loaded original Raven mesh, materials, animations, feather particles and idle/takeoff SFX from game Resources; no AI or network prefab spawned.");
            return true;
        }
        catch (Exception ex)
        {
            if (root != null) UnityEngine.Object.Destroy(root);
            if (!_loggedFailure) RavenPlugin.Log?.LogError("[Raven] Native model load failed: " + ex);
            _loggedFailure = true;
            return false;
        }
    }

    private static void CopyHierarchy(UnityTransform source, UnityTransform parent, Dictionary<UnityTransform, UnityTransform> nodes)
    {
        if (source.GetComponent<ParticleSystem>() != null)
        {
            // Clone only a self-contained native particle leaf. This preserves
            // every module, renderer setting, material and texture without
            // copying the Raven's gameplay or voice-stealing components.
            if (source.childCount != 0) throw new InvalidOperationException("Native Raven particle hierarchy changed.");
            foreach (Component component in source.GetComponents<Component>())
                if (!(component is UnityTransform) && !(component is ParticleSystem) && !(component is ParticleSystemRenderer))
                    throw new InvalidOperationException("Unexpected component on native Raven particle leaf.");
            GameObject effect = UnityEngine.Object.Instantiate(source.gameObject, parent, false);
            effect.name = source.name;
            // Use an explicit 2/5 of the original effect. Hierarchy scaling
            // also applies the model's ~0.095 scale, making feathers too small.
            effect.transform.localScale = source.localScale * FeatherScale;
            var particleMain = effect.GetComponent<ParticleSystem>().main;
            particleMain.scalingMode = ParticleSystemScalingMode.Local;
            nodes.Add(source, effect.transform);
            return;
        }
        var copy = new GameObject(source.name).transform;
        copy.SetParent(parent, false);
        copy.gameObject.layer = source.gameObject.layer;
        // Feathers starts inactive; the native FlyAway m_IsActive curve enables
        // it once. Preserving this prevents particles on initial ground entry.
        copy.gameObject.SetActive(source.gameObject.activeSelf);
        copy.localPosition = source.localPosition;
        copy.localRotation = source.localRotation;
        copy.localScale = source.localScale;
        nodes.Add(source, copy);
        foreach (UnityTransform child in source) CopyHierarchy(child, copy, nodes);
    }

    internal static GameObject Create(Vector3 position, Quaternion rotation)
    {
        if (!EnsureTemplate()) return null;
        GameObject visual = UnityEngine.Object.Instantiate(_template, position, rotation);
        visual.name = "Transform.Crow.Visual";
        visual.SetActive(true);
        return visual;
    }

    internal static void Clear()
    {
        if (_template != null) UnityEngine.Object.Destroy(_template);
        _template = null;
        _retryAt = 0f;
        _loggedFailure = false;
    }
}

internal sealed class CrowFlightPresentation : MonoBehaviour
{
    private Animator _animator;
    private UnityTransform _armature;
    private Vector3 _armaturePosition;
    private Vector3 _armatureScale;
    private float _duration = 1f;
    private readonly CondorFlightState _state = new CondorFlightState();
    private readonly CrowFlightCycle _cycle = new CrowFlightCycle();
    private float _idlePhase;
    private float _idleDuration = 1f;

    internal CondorFlightMode Apply(Vector3 velocity, float deltaTime, bool grounded)
    {
        if (_animator == null)
        {
            _animator = GetComponentInChildren<Animator>(true);
            if (_animator == null) return CondorFlightMode.Flap;
            _armature = _animator.transform.Find("Armature");
            if (_armature != null)
            {
                _armaturePosition = _armature.localPosition;
                _armatureScale = _armature.localScale;
            }
            foreach (AnimationClip clip in _animator.runtimeAnimatorController.animationClips)
            {
                if (clip != null && clip.name == "RavenFlyAway") _duration = Mathf.Max(0.1f, clip.length);
                if (clip != null && clip.name == "RavenIdle") _idleDuration = Mathf.Max(0.1f, clip.length);
            }
            _animator.fireEvents = false;
            _animator.applyRootMotion = false;
            _animator.speed = 0f;
            // Match the native RandomizeAnimatorParameter without cloning an
            // additional script or assigning one shared phase to the template.
            _animator.SetFloat("RandomParameter", UnityEngine.Random.value);
        }
        CondorFlightMode mode = _state.Step(velocity, deltaTime, false);
        if (grounded)
        {
            _idlePhase = Mathf.Repeat(_idlePhase + Mathf.Max(0f, deltaTime) / _idleDuration, 1f);
            _animator.Play("Base Layer.RavenIdle", 0, _idlePhase);
        }
        else _animator.Play("Base Layer.FlyAway", 0, _cycle.Step(deltaTime, _duration, mode == CondorFlightMode.Flap));
        _animator.Update(0f);
        // Native takeoff animates Armature.localPosition directly, so disabling
        // Animator root motion alone cannot stop its 10+ metre visual departure.
        if (!grounded && _armature != null)
        {
            _armature.localPosition = _armaturePosition;
            _armature.localScale = _armatureScale;
        }
        return mode;
    }
}
