namespace AiAssistant.Core.ValueObjects;

/// <summary>
/// Sequential schema version value object. Downgrade writes are strictly prohibited.
/// </summary>
public readonly record struct SchemaVersion(int Number, string Name) : IComparable<SchemaVersion>
{
    public static readonly SchemaVersion Empty = new(0, "Uninitialized");
    public static readonly SchemaVersion Current = new(1, "S1_InitialEncryptedCoreSchema");

    public int CompareTo(SchemaVersion other) => Number.CompareTo(other.Number);

    public bool IsNewerThan(SchemaVersion other) => Number > other.Number;

    public static bool operator <(SchemaVersion left, SchemaVersion right) => left.CompareTo(right) < 0;
    public static bool operator <=(SchemaVersion left, SchemaVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >(SchemaVersion left, SchemaVersion right) => left.CompareTo(right) > 0;
    public static bool operator >=(SchemaVersion left, SchemaVersion right) => left.CompareTo(right) >= 0;
}
