using Nexus.Broker;
using Xunit;

namespace Nexus.Tests;

[Trait("Category", "Unit")]
public sealed class LocalBrokerLevelTests
{
    [Theory]
    [InlineData(BrokerLevel.User, false, BrokerLevel.User)]
    [InlineData(BrokerLevel.Admin, false, BrokerLevel.Admin)]
    [InlineData(BrokerLevel.User, true, BrokerLevel.Admin)] // interface elevada: diário protegido em %ProgramData%
    [InlineData(BrokerLevel.Admin, true, BrokerLevel.Admin)]
    public void Elevated_process_always_uses_admin_broker(BrokerLevel requested, bool elevated, BrokerLevel expected) =>
        Assert.Equal(expected, LocalBroker.EffectiveLevel(requested, elevated));
}
