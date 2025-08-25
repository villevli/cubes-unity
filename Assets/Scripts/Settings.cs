using UnityEngine;
using UnityEngine.InputSystem;

namespace Cubes
{
    /// <summary>
    /// Manages game settings.
    /// </summary>
    public static class Settings
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        public static void OnSubsystemRegistration()
        {
            // By default Unity runs at 30 fps on mobile platforms. Increase that to 60
            if (!Application.isEditor && Application.isMobilePlatform)
            {
                Application.targetFrameRate = 60;
            }

            // Fix issue where input actions are not registered when domain reload is disabled
            InputSystem.actions.Enable();
        }
    }
}
