namespace Androidx.Health.Connect.Client.Units;

// The .NET binding generator produced these ten unit value classes (Kotlin classes implementing
// Comparable<Self>) without a working `IComparable.CompareTo(object)` implementation - each
// fails to compile with "does not implement interface member 'IComparable.CompareTo(Object)'".
// This is a binding-generator gap, not something fixable via ScaleBridge.csproj or a metadata
// transform (unlike the packages excluded in Transforms/Metadata.xml, these classes are exactly
// the ones this app needs - Mass in particular, via Mass.Kilograms(...) in HealthConnectWriter.cs
// - so they can't simply be removed).
//
// Because every one of the generated binding classes here is `partial`, the standard fix is to
// complete the missing interface member in an ordinary C# file like this one, rather than via
// binding metadata. ScaleBridge never actually calls CompareTo on any of these types (it only
// constructs a Mass value and passes it straight into WeightRecord), so each stub below only
// needs to satisfy the compiler, not implement real comparison semantics - see
// docs/PROTOCOL_CONFIRMATION.md for the fuller explanation.
//
// If a future connect-client version fixes this in the binding itself, these partial
// declarations become harmless no-ops (redeclaring an already-satisfied interface member is not
// an error) rather than something that needs removing.

public partial class Mass : IComparable
{
    int IComparable.CompareTo(object? obj) =>
        throw new NotSupportedException($"{nameof(Mass)}.CompareTo is not used by ScaleBridge.");
}

public partial class Length : IComparable
{
    int IComparable.CompareTo(object? obj) =>
        throw new NotSupportedException($"{nameof(Length)}.CompareTo is not used by ScaleBridge.");
}

public partial class Energy : IComparable
{
    int IComparable.CompareTo(object? obj) =>
        throw new NotSupportedException($"{nameof(Energy)}.CompareTo is not used by ScaleBridge.");
}

public partial class Power : IComparable
{
    int IComparable.CompareTo(object? obj) =>
        throw new NotSupportedException($"{nameof(Power)}.CompareTo is not used by ScaleBridge.");
}

public partial class Pressure : IComparable
{
    int IComparable.CompareTo(object? obj) =>
        throw new NotSupportedException($"{nameof(Pressure)}.CompareTo is not used by ScaleBridge.");
}

public partial class Percentage : IComparable
{
    int IComparable.CompareTo(object? obj) =>
        throw new NotSupportedException($"{nameof(Percentage)}.CompareTo is not used by ScaleBridge.");
}

public partial class Temperature : IComparable
{
    int IComparable.CompareTo(object? obj) =>
        throw new NotSupportedException($"{nameof(Temperature)}.CompareTo is not used by ScaleBridge.");
}

public partial class Velocity : IComparable
{
    int IComparable.CompareTo(object? obj) =>
        throw new NotSupportedException($"{nameof(Velocity)}.CompareTo is not used by ScaleBridge.");
}

public partial class Volume : IComparable
{
    int IComparable.CompareTo(object? obj) =>
        throw new NotSupportedException($"{nameof(Volume)}.CompareTo is not used by ScaleBridge.");
}

public partial class BloodGlucose : IComparable
{
    int IComparable.CompareTo(object? obj) =>
        throw new NotSupportedException($"{nameof(BloodGlucose)}.CompareTo is not used by ScaleBridge.");
}
