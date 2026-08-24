using Photon.Pun;
using UnityEngine;

namespace ImScoutmaster;

public sealed class ControlledScoutmasterVisualSync : MonoBehaviour
{
	private Character _character;
	private PhotonView _view;
	private bool _remoteInterpolationApplied;

	public void Initialize(Character character, PhotonView view)
	{
		_character = character != null ? character : GetComponent<Character>();
		_view = view != null ? view : GetComponent<PhotonView>();
		_remoteInterpolationApplied = false;
	}

	private void Awake()
	{
		Initialize(_character, _view);
	}

	private void Update()
	{
		if (_character == null)
		{
			_character = GetComponent<Character>();
		}
		if (_character != null)
		{
			Plugin.EnsureControlledScoutmasterRegistered(_character);
			ApplyRemoteRagdollInterpolation();
		}
	}

	private void ApplyRemoteRagdollInterpolation()
	{
		if (_remoteInterpolationApplied || _character?.refs?.ragdoll?.partList == null)
		{
			return;
		}
		if (_view == null)
		{
			_view = GetComponent<PhotonView>();
		}
		if (_view == null || _view.IsMine)
		{
			return;
		}

		bool touchedAny = false;
		foreach (Bodypart part in _character.refs.ragdoll.partList)
		{
			Rigidbody rig = part != null ? part.Rig : null;
			if (rig == null)
			{
				continue;
			}
			try
			{
				if (rig.interpolation != RigidbodyInterpolation.Interpolate)
				{
					rig.interpolation = RigidbodyInterpolation.Interpolate;
				}
				touchedAny = true;
			}
			catch
			{
			}
		}

		_remoteInterpolationApplied = touchedAny;
	}

	private void OnDestroy()
	{
		Plugin.UnregisterControlledScoutmasterInstance(_character, _view);
	}
}
