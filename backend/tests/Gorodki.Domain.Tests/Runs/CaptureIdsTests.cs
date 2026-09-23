using Gorodki.Domain.Runs;

namespace Gorodki.Domain.Tests.Runs;

public sealed class CaptureIdsTests
{
    /// <summary>Эталоны посчитаны независимо: <c>uuid.uuid5(namespace, run.bytes + end_seq.to_bytes(4, "big"))</c> в Python.</summary>
    [Theory]
    [InlineData(0, "2e19b697-e397-532d-a6e6-7c0fc1940226")]
    [InlineData(1, "624661fe-a660-59b4-be57-f29da86d1ebc")]
    [InlineData(499, "2cba2ab6-1fb3-5f35-b170-727e301a521c")]
    [InlineData(28_800, "cdbf2d12-778f-5edb-a95a-4232180db333")]
    public void Capture_id_matches_the_reference_uuid5(int endSeq, string expected)
    {
        var run = new Guid("01999a1b-2c3d-7e4f-8a5b-6c7d8e9f0a1b");

        Assert.Equal(new Guid(expected), CaptureIds.For(run, endSeq));
    }

    [Fact]
    public void Repeated_claim_gets_the_same_id_and_another_loop_another()
    {
        var run = Guid.CreateVersion7();

        Assert.Equal(CaptureIds.For(run, 120), CaptureIds.For(run, 120));
        Assert.NotEqual(CaptureIds.For(run, 120), CaptureIds.For(run, 121));
        Assert.Equal(5, CaptureIds.For(run, 120).Version);
    }
}
