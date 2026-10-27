using HitlRelay.Channels.Slack;
using HitlRelay.Models;
using Xunit;

namespace HitlRelay.Tests;

public class SlackChannelTests
{
    [Theory]
    [InlineData("order-", "A-1001", "order-a-1001")]
    [InlineData("order-", "A/1001 Rev.2", "order-a-1001-rev-2")]   // case folded, punctuation and spaces become hyphens
    [InlineData("", "__x__", "x")]
    public void ChannelNamesAreSanitised(string prefix, string key, string expected) =>
        Assert.Equal(expected, SlackChannelProvisioner.ChannelName(prefix, key));

    [Fact]
    public void ChannelNamesAreCappedAt80Characters() =>
        Assert.True(SlackChannelProvisioner.ChannelName("order-", new string('x', 300)).Length <= 80);

    private static HitlNodeConfig ChannelModeConfig()
    {
        var cfg = Harness.KeywordConfig(ChannelKeys.Slack);
        cfg.Recipient = "";
        cfg.SlackChannelKey = "{{order.id}}";
        return cfg;
    }

    [Fact]
    public async Task ChannelKeyIsRenderedFromVariables_AndRecipientIsOptional()
    {
        using var h = new Harness();
        var cfg = ChannelModeConfig();
        var req = await h.CreateAsync(cfg);

        Assert.Equal("A-7", req.Config.SlackChannelKey);
        Assert.Equal("channel:A-7", req.Recipient);
        Assert.Equal("{{order.id}}", cfg.SlackChannelKey);   // caller's config untouched
    }

    [Fact]
    public async Task EachRequestForTheSameOrderIsItsOwnPost()
    {
        using var h = new Harness();
        var first = await h.CreateAsync(ChannelModeConfig(), "run-1");
        var second = await h.CreateAsync(ChannelModeConfig(), "run-2");

        Assert.Equal(first.Config.SlackChannelKey, second.Config.SlackChannelKey);
        Assert.Equal(2, h.Channels.Sent.Count);
        Assert.NotEqual(first.ExternalMessageId, second.ExternalMessageId);
    }

    [Fact]
    public async Task SlackKeyIsRejectedOnOtherChannels()
    {
        using var h = new Harness();
        var cfg = Harness.KeywordConfig(ChannelKeys.Teams);
        cfg.SlackChannelKey = "A-7";
        await Assert.ThrowsAsync<ArgumentException>(() => h.CreateAsync(cfg));
    }
}
