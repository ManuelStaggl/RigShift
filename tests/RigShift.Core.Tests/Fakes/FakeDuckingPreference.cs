using RigShift.Core.Abstractions;

namespace RigShift.Core.Tests.Fakes;

/// <summary>Remembers the ducking value like the registry would; <c>null</c> is a missing value.</summary>
internal sealed class FakeDuckingPreference : IDuckingPreference
{
    public bool Fail { get; set; }

    public int? Value { get; set; } = CommunicationsDucking.ReduceBy50Percent;

    public List<int?> Written { get; } = [];

    public int? Read() => Fail ? throw new UnauthorizedAccessException("registry denied") : Value;

    public void Write(int? value)
    {
        if (Fail)
        {
            throw new UnauthorizedAccessException("registry denied");
        }

        Value = value;
        Written.Add(value);
    }
}
