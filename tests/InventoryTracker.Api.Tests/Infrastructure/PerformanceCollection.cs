namespace InventoryTracker.Api.Tests.Infrastructure;

/// <summary>
/// The xUnit collection for the PRD section 5 performance tests (100,000 movements, 1,000 orders, 500 items; 95% of
/// requests within 1 second). Parallel running is turned off for it, so its tests measure the app on its own and not
/// the rest of the suite competing for the same processor.
/// </summary>
/// <remarks>
/// <para>
/// xUnit puts every test class in a <em>collection</em>. By default each class is its own collection, and collections
/// run in parallel with each other. A class joins this one with <c>[Collection(nameof(PerformanceCollection))]</c>.
/// <c>DisableParallelization = true</c> makes xUnit run the collection only after every parallel collection has
/// finished, with nothing else running beside it; the classes inside it run one after another.
/// </para>
/// <para>
/// Why (T-020, a follow-up from T-016): under full-suite parallel load, <c>GET /stock</c> once measured a p95 of
/// 2.7 seconds against the 1 second target, while it passed alone and on a rerun. The time went to the other tests, not
/// to the app. The targets and assertions in the tests are unchanged.
/// </para>
/// </remarks>
[CollectionDefinition(nameof(PerformanceCollection), DisableParallelization = true)]
public class PerformanceCollection;
