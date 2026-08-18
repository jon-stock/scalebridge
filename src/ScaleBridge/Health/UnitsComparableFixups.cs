namespace Androidx.Health.Connect.Client.Units;

// The .NET binding generator produced these ten unit value classes (Kotlin classes implementing
// Comparable<Self>) without a working `Java.Lang.IComparable.CompareTo(Java.Lang.Object)`
// implementation - each fails to compile with "does not implement interface member
// 'IComparable.CompareTo(Object)'". This is a binding-generator gap, not something fixable via
// ScaleBridge.csproj or a metadata transform (unlike the packages excluded in
// Transforms/Metadata.xml, these classes are exactly the ones this app needs - Mass in
// particular, via Mass.Kilograms(...) in HealthConnectWriter.cs - so they can't simply be
// removed).
//
// IMPORTANT: the missing interface here is Java.Lang.IComparable (the bound `java.lang.Comparable`
// interface, whose CompareTo takes a Java.Lang.Object), NOT System.IComparable (the BCL
// interface, whose CompareTo takes a plain object). A first attempt at this fix implemented
// System.IComparable, which compiled but did not resolve the error at all, since it wasn't the
// interface the compiler was actually complaining about - confirmed by the identical error
// persisting unchanged on the next build.
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

public partial class Mass : Java.Lang.IComparable
{
    public int CompareTo(Java.Lang.Object? obj) =>
        throw new NotSupportedException($"{nameof(Mass)}.CompareTo is not used by ScaleBridge.");
}

public partial class Length : Java.Lang.IComparable
{
    public int CompareTo(Java.Lang.Object? obj) =>
        throw new NotSupportedException($"{nameof(Length)}.CompareTo is not used by ScaleBridge.");
}

public partial class Energy : Java.Lang.IComparable
{
    public int CompareTo(Java.Lang.Object? obj) =>
        throw new NotSupportedException($"{nameof(Energy)}.CompareTo is not used by ScaleBridge.");
}

public partial class Power : Java.Lang.IComparable
{
    public int CompareTo(Java.Lang.Object? obj) =>
        throw new NotSupportedException($"{nameof(Power)}.CompareTo is not used by ScaleBridge.");
}

public partial class Pressure : Java.Lang.IComparable
{
    public int CompareTo(Java.Lang.Object? obj) =>
        throw new NotSupportedException($"{nameof(Pressure)}.CompareTo is not used by ScaleBridge.");
}

public partial class Percentage : Java.Lang.IComparable
{
    public int CompareTo(Java.Lang.Object? obj) =>
        throw new NotSupportedException($"{nameof(Percentage)}.CompareTo is not used by ScaleBridge.");
}

public partial class Temperature : Java.Lang.IComparable
{
    public int CompareTo(Java.Lang.Object? obj) =>
        throw new NotSupportedException($"{nameof(Temperature)}.CompareTo is not used by ScaleBridge.");
}

public partial class Velocity : Java.Lang.IComparable
{
    public int CompareTo(Java.Lang.Object? obj) =>
        throw new NotSupportedException($"{nameof(Velocity)}.CompareTo is not used by ScaleBridge.");
}

public partial class Volume : Java.Lang.IComparable
{
    public int CompareTo(Java.Lang.Object? obj) =>
        throw new NotSupportedException($"{nameof(Volume)}.CompareTo is not used by ScaleBridge.");
}

public partial class BloodGlucose : Java.Lang.IComparable
{
    public int CompareTo(Java.Lang.Object? obj) =>
        throw new NotSupportedException($"{nameof(BloodGlucose)}.CompareTo is not used by ScaleBridge.");
}
