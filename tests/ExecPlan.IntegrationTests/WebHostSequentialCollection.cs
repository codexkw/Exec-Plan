namespace ExecPlan.IntegrationTests;

/// <summary>
/// Serializes every <see cref="TestAppFactory"/>-hosted test class into a single non-parallel xUnit
/// collection. <see cref="TestAppFactory"/> sets/unsets PROCESS-GLOBAL environment variables
/// (<c>Database__Provider</c> / <c>Jwt__*</c>) in its ctor/<c>Dispose</c>; running two such factories in
/// parallel lets one factory's <c>Dispose</c> (which nulls those vars) race another factory's host build
/// (which reads them at build time) — a flaky wrong-provider / signing-key mismatch. Grouping every
/// <c>IClassFixture&lt;TestAppFactory&gt;</c> class into this one collection makes their fixtures'
/// lifecycles run one at a time. Classes that only use <c>SqliteFixture</c> (no env-var mutation) are NOT
/// annotated and stay parallel. This is additive test infrastructure only — no production behavior changes.
/// </summary>
[CollectionDefinition("WebHostSequential", DisableParallelization = true)]
public sealed class WebHostSequentialCollection
{
}
