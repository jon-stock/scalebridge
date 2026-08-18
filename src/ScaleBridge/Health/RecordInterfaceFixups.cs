namespace Androidx.Health.Connect.Client.Records;

// Kotlin's `Record` is a simple marker interface with a single member (`val metadata: Metadata`)
// that every concrete record type (including WeightRecord) already implements via its
// constructor. Confirmed against the real AndroidX source
// (androidx.health.connect.client.records.Record.kt) that no other members are involved.
//
// Despite that, the generated `WeightRecord` binding doesn't compile as an `IRecord` ("cannot
// convert from WeightRecord to IRecord" in HealthConnectWriter.cs) - it's simply missing the
// `IRecord` interface declaration somewhere in its binding (either directly, or via a broken
// intermediate interface further up the real Kotlin hierarchy, e.g. InstantaneousRecord). This
// declares the interface directly on WeightRecord instead. No member stubs are needed: since
// WeightRecord already has a public `Metadata` member from its constructor (the same kind of
// "implicit interface satisfaction" C# uses elsewhere), this partial declaration only needs to
// state the relationship - see docs/PROTOCOL_CONFIRMATION.md for the fuller explanation and what
// to check if this needs more than that (e.g. if `Metadata` turns out not to already exist under
// that exact name/type, the compiler will name the missing member explicitly).
public partial class WeightRecord : IRecord
{
}
