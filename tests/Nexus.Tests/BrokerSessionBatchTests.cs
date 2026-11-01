using Nexus.Broker;
using Nexus.Core;
using Xunit;

namespace Nexus.Tests;

[Trait("Category", "Unit")]
public sealed class BrokerSessionBatchTests
{
    private static List<ActionRequest> Many(int n) => Enumerable.Range(0, n).Select(i => new ActionRequest($"a{i}@1")).ToList();

    private static BrokerCallResult Ok(IReadOnlyList<ActionRequest> chunk) =>
        new(true, new BrokerResponse(Guid.NewGuid(), true, Results: chunk.Select(c => new ActionResultDto(c.ActionId, "Verified")).ToList(), RunId: Guid.NewGuid()), "ok");

    [Fact]
    public void Ninety_four_actions_become_two_batches_with_all_results_and_runs()
    {
        var sizes = new List<int>();
        var r = BrokerSession.Batched(Many(94), c => { sizes.Add(c.Count); return Ok(c); });
        Assert.Equal([64, 30], sizes);
        Assert.True(r.Ok);
        Assert.Equal(94, r.Response!.Results!.Count);
        Assert.Equal(2, r.AllRunIds.Count);
    }

    [Fact]
    public void Small_request_is_sent_as_is()
    {
        var calls = 0;
        var r = BrokerSession.Batched(Many(10), c => { calls++; return Ok(c); });
        Assert.Equal(1, calls);
        Assert.Single(r.AllRunIds);
    }

    [Fact]
    public void Stops_at_first_failed_batch()
    {
        var calls = 0;
        var r = BrokerSession.Batched(Many(200), c => ++calls == 2 ? new BrokerCallResult(true, BrokerResponse.Fail(Guid.NewGuid(), "x", "falhou"), "falhou") : Ok(c));
        Assert.Equal(2, calls);
        Assert.False(r.Ok);
        Assert.Contains("não foram enviados", r.Message, StringComparison.Ordinal);
        Assert.Single(r.AllRunIds);
    }
}
