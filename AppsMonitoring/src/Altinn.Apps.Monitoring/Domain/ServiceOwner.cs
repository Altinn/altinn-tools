using System.Diagnostics.CodeAnalysis;

namespace Altinn.Apps.Monitoring.Domain;

/// <summary>
/// E.g "skd" "brg"
/// </summary>
public readonly struct ServiceOwner : IEquatable<ServiceOwner>
{
    private readonly string _value;

    public readonly string Value => _value;

    private ServiceOwner(string value) => _value = value;

    public static ServiceOwner Parse(string serviceOwner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceOwner, nameof(serviceOwner));

        for (int i = 0; i < serviceOwner.Length; i++)
        {
            if (!char.IsLetter(serviceOwner[i]) || !char.IsLower(serviceOwner[i]))
            {
                throw new ArgumentException(
                    $"Service owner must only contain lowercase letters. Got: '{serviceOwner}'",
                    nameof(serviceOwner)
                );
            }
        }

        return new ServiceOwner(serviceOwner);
    }

    public bool Equals(ServiceOwner other) => _value.Equals(other._value, StringComparison.Ordinal);

    public override bool Equals([NotNullWhen(true)] object? obj) => obj is ServiceOwner other && Equals(other);

    public override int GetHashCode() => _value.GetHashCode();

    public override string ToString() => _value;

    public static bool operator ==(ServiceOwner left, ServiceOwner right) => left.Equals(right);

    public static bool operator !=(ServiceOwner left, ServiceOwner right) => !left.Equals(right);
}
