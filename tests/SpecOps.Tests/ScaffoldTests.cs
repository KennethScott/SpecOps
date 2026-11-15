namespace SpecOps.Tests;

public class ScaffoldTests
{
    [Fact]
    public void WebAssemblyLoads()
    {
        // Placeholder until Phase 1 adds real tests for the runner, validation and authorization.
        Assert.NotNull(typeof(Program).Assembly);
    }
}
