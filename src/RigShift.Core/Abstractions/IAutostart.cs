namespace RigShift.Core.Abstractions;

/// <summary>Start with Windows. Windows implementation: HKCU <c>Run</c> key with <c>--minimized</c>.</summary>
public interface IAutostart
{
    bool IsEnabled { get; }

    void SetEnabled(bool enabled);
}
