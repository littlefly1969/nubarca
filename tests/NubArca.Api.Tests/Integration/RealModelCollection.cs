namespace NubArca.Api.Tests.Integration;

/// The real-model lanes, run ONE AT A TIME.
///
/// xUnit parallelises across classes, and these load gigabytes of ONNX weights
/// each: the private-text regression loads `multilingual-e5-small`, the visual
/// lanes load both SigLIP2 towers, and the Phase-0 lane loads them alongside a
/// candidate's stored vectors. Run together they do not merely get slow — the
/// runtime's parallel reductions are not bitwise deterministic across thread
/// pressure, and a borderline paraphrase case in the text lane flips rank
/// depending on what else is competing for the pool.
///
/// Serialising them is the honest fix. The alternative — loosening the
/// assertion until it survives the interference — would delete the one thing
/// that lane measures, which is whether a real embedding model actually finds a
/// paraphrase the lexical path misses.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RealModelCollection
{
    public const string Name = "RealModel";
}
