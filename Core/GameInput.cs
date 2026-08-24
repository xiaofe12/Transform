using UnityEngine;

namespace Transform.Core;

internal static class GameInput
{
    internal static Vector2 Move()
    {
        Vector2 input = Vector2.zero;
        if (CharacterInput.action_move != null) input += CharacterInput.action_move.ReadValue<Vector2>();
        if (CharacterInput.action_moveForward != null && CharacterInput.action_moveForward.IsPressed()) input += Vector2.up;
        if (CharacterInput.action_moveBackward != null && CharacterInput.action_moveBackward.IsPressed()) input -= Vector2.up;
        if (CharacterInput.action_moveRight != null && CharacterInput.action_moveRight.IsPressed()) input += Vector2.right;
        if (CharacterInput.action_moveLeft != null && CharacterInput.action_moveLeft.IsPressed()) input -= Vector2.right;
        return Vector2.ClampMagnitude(input, 1f);
    }

    internal static Vector2 Look()
    {
        return CharacterInput.action_look != null
            ? CharacterInput.action_look.ReadValue<Vector2>()
            : new Vector2(Input.GetAxis("Mouse X"), Input.GetAxis("Mouse Y")) * 10f;
    }

    internal static bool JumpHeld(KeyCode fallback)
    {
        return ActionHeld(CharacterInput.action_jump) || KeyHeld(fallback);
    }

    internal static bool JumpPressed(KeyCode fallback)
    {
        return ActionPressed(CharacterInput.action_jump) || KeyPressed(fallback);
    }

    internal static bool SprintHeld(KeyCode fallback)
    {
        return ActionHeld(CharacterInput.action_sprint)
               || ActionPressed(CharacterInput.action_sprintToggle)
               || KeyHeld(fallback)
               || Input.GetKey(KeyCode.LeftShift)
               || Input.GetKey(KeyCode.RightShift);
    }

    internal static bool SprintPressed(KeyCode fallback)
    {
        return ActionPressed(CharacterInput.action_sprint)
               || ActionPressed(CharacterInput.action_sprintToggle)
               || KeyPressed(fallback)
               || Input.GetKeyDown(KeyCode.LeftShift)
               || Input.GetKeyDown(KeyCode.RightShift);
    }

    internal static bool CrouchHeld(KeyCode fallback)
    {
        return ActionHeld(CharacterInput.action_crouch)
               || ActionPressed(CharacterInput.action_crouchToggle)
               || KeyHeld(fallback);
    }

    internal static bool UsePrimaryHeld(KeyCode fallback)
    {
        return ActionHeld(CharacterInput.action_usePrimary) || KeyHeld(fallback);
    }

    internal static bool UsePrimaryPressed(KeyCode fallback)
    {
        return ActionPressed(CharacterInput.action_usePrimary) || KeyPressed(fallback);
    }

    internal static bool UsePrimaryReleased(KeyCode fallback)
    {
        return ActionReleased(CharacterInput.action_usePrimary) || KeyReleased(fallback);
    }

    internal static bool UseSecondaryHeld(KeyCode fallback)
    {
        return ActionHeld(CharacterInput.action_useSecondary) || KeyHeld(fallback);
    }

    internal static bool UseSecondaryPressed(KeyCode fallback)
    {
        return ActionPressed(CharacterInput.action_useSecondary) || KeyPressed(fallback);
    }

    internal static bool UseSecondaryReleased(KeyCode fallback)
    {
        return ActionReleased(CharacterInput.action_useSecondary) || KeyReleased(fallback);
    }

    private static bool ActionHeld(UnityEngine.InputSystem.InputAction action)
    {
        return action != null && action.IsPressed();
    }

    private static bool ActionPressed(UnityEngine.InputSystem.InputAction action)
    {
        return action != null && action.WasPressedThisFrame();
    }

    private static bool ActionReleased(UnityEngine.InputSystem.InputAction action)
    {
        return action != null && action.WasReleasedThisFrame();
    }

    private static bool KeyHeld(KeyCode key)
    {
        return key != KeyCode.None && Input.GetKey(key);
    }

    private static bool KeyPressed(KeyCode key)
    {
        return key != KeyCode.None && Input.GetKeyDown(key);
    }

    private static bool KeyReleased(KeyCode key)
    {
        return key != KeyCode.None && Input.GetKeyUp(key);
    }
}
