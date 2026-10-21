// Minimal UnityEngine stubs used ONLY by tools/csharp-headless.
//
// The package's Unity-facing files (MonoBehaviour, Microphone, Debug, attributes) cannot be
// compiled without the engine assemblies. This file declares just enough surface for the
// headless harness to type-check those files and catch syntax/typo errors on a workstation
// with no Unity editor.
//
// It is NOT a behavioural emulation and is never shipped: it lives under tools/ and is not
// part of the UPM package or its .asmdef.

using System;
using System.Collections;

namespace UnityEngine
{
    public class Object
    {
    }

    public class Component : Object
    {
        public Transform transform;
    }

    public class Behaviour : Component
    {
        public bool enabled;
    }

    public class MonoBehaviour : Behaviour
    {
        public T GetComponentInChildren<T>() where T : class
        {
            return null;
        }

        public Coroutine StartCoroutine(IEnumerator routine)
        {
            return new Coroutine();
        }

        public void StopCoroutine(Coroutine routine)
        {
        }
    }

    public sealed class Coroutine
    {
    }

    public sealed class Transform
    {
    }

    public sealed class WaitForSecondsRealtime
    {
        public WaitForSecondsRealtime(float seconds)
        {
        }
    }

    public static class Debug
    {
        public static void Log(object message)
        {
        }

        public static void LogWarning(object message)
        {
        }

        public static void LogError(object message)
        {
        }

        public static void LogException(Exception exception)
        {
        }
    }

    public static class Mathf
    {
        public static int Clamp(int value, int min, int max)
        {
            return value < min ? min : (value > max ? max : value);
        }
    }

    public static class Input
    {
        public static bool GetKeyDown(KeyCode key)
        {
            return false;
        }

        public static bool GetKeyUp(KeyCode key)
        {
            return false;
        }
    }

    public enum KeyCode
    {
        Space = 32
    }

    public sealed class AudioClip : Object
    {
        public int samples;
        public int frequency;

        public bool GetData(float[] data, int offsetSamples)
        {
            return false;
        }
    }

    public static class Microphone
    {
        public static string[] devices => Array.Empty<string>();

        public static void GetDeviceCaps(string deviceName, out int minFreq, out int maxFreq)
        {
            minFreq = 0;
            maxFreq = 0;
        }

        public static AudioClip Start(string deviceName, bool loop, int lengthSec, int frequency)
        {
            return null;
        }

        public static void End(string deviceName)
        {
        }

        public static int GetPosition(string deviceName)
        {
            return 0;
        }

        public static bool IsRecording(string deviceName)
        {
            return false;
        }
    }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class SerializeField : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class HeaderAttribute : Attribute
    {
        public HeaderAttribute(string header)
        {
        }
    }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class TooltipAttribute : Attribute
    {
        public TooltipAttribute(string tooltip)
        {
        }
    }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class RangeAttribute : Attribute
    {
        public RangeAttribute(float min, float max)
        {
        }
    }

    [AttributeUsage(AttributeTargets.Class)]
    public sealed class AddComponentMenu : Attribute
    {
        public AddComponentMenu(string menuName)
        {
        }
    }
}

namespace UnityEngine.Events
{
    public class UnityEvent
    {
        public void Invoke()
        {
        }
    }

    public class UnityEvent<T>
    {
        public void Invoke(T value)
        {
        }
    }
}

namespace UnityEngine
{
    public static class Application
    {
        public static string streamingAssetsPath => string.Empty;
    }
}

namespace UnityEngine.UI
{
    public class Text : UnityEngine.Object
    {
        public string text;
    }
}

namespace UnityEngine.InputSystem
{
    public sealed class InputActionReference
    {
        public InputAction action => null;
    }

    public sealed class InputAction
    {
        public event Action<CallbackContext> started;
        public event Action<CallbackContext> canceled;

        public void Enable()
        {
        }

        public void Disable()
        {
        }

        public struct CallbackContext
        {
        }
    }
}
