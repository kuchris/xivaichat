using System.Numerics;
using System.Reflection;
using Dalamud.Interface.Windowing;

namespace XivAiChat;

internal static class WindowCompat
{
    public static void ApplySizeConstraints(Window window, Vector2 minimumSize, Vector2 maximumSize)
    {
        var property = window.GetType().GetProperty("SizeConstraints", BindingFlags.Instance | BindingFlags.Public);
        if (property is null)
        {
            return;
        }

        var constraintType = property.PropertyType;
        var constraintValue = Activator.CreateInstance(constraintType);
        if (constraintValue is null)
        {
            return;
        }

        constraintType.GetProperty("MinimumSize", BindingFlags.Instance | BindingFlags.Public)
            ?.SetValue(constraintValue, minimumSize);
        constraintType.GetProperty("MaximumSize", BindingFlags.Instance | BindingFlags.Public)
            ?.SetValue(constraintValue, maximumSize);
        property.SetValue(window, constraintValue);
    }
}
