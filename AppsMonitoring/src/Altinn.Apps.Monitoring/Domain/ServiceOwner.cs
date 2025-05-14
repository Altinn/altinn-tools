using System.Diagnostics.CodeAnalysis;

namespace Altinn.Apps.Monitoring.Domain;

/// <summary>
/// E.g "skd" "brg"
/// </summary>
internal readonly struct ServiceOwner : IEquatable<ServiceOwner>
{
    public readonly string Value { get; }
    public readonly string? ExtId { get; }

    private ServiceOwner(string value, string? extId)
    {
        Value = value;
        ExtId = extId;
    }

    public static ServiceOwner Parse(string serviceOwner, string? extId = null)
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

        return new ServiceOwner(serviceOwner, extId);
    }

    public bool Equals(ServiceOwner other) => Value.Equals(other.Value, StringComparison.Ordinal);

    public override bool Equals([NotNullWhen(true)] object? obj) => obj is ServiceOwner other && Equals(other);

    public override int GetHashCode() => Value.GetHashCode(StringComparison.Ordinal);

    public override string ToString() => Value;

    public static bool operator ==(ServiceOwner left, ServiceOwner right) => left.Equals(right);

    public static bool operator !=(ServiceOwner left, ServiceOwner right) => !left.Equals(right);
}
