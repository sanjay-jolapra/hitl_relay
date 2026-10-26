using HitlRelay.Core;
using HitlRelay.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HitlRelay.Tests;

public class KeywordFlowTests
{
    [Fact]
    public async Task ButtonClick_ResumesWithKeyOutputs()
    {
        using var h = new Harness();
        var req = await h.CreateAsync(Harness.KeywordConfig());

        Assert.Equal("Approve order A-7?", h.Channels.Sent.Single().Prompt); // template rendered
        Assert.Equal(WorkflowRunStatus.Waiting, (await h.LoadRunAsync("run-1")).Status);

        var result = await h.ReplyAsync(Harness.Button(req, "approve"));

        Assert.Equal(ReplyStatus.Resumed, result.Status);
        var resumed = Assert.Single(h.Resumer.Resumed);
        Assert.Equal("node-1", resumed.NodeId);
        Assert.Equal("approve", resumed.Outputs["response.value"]);
        Assert.Equal("Approve", resumed.Outputs["response.label"]);
        Assert.Equal("slack", resumed.Outputs["response.channel"]);
        Assert.Equal("Priya", resumed.Outputs["response.responder"]);
        Assert.Equal(HitlRequestStatus.Answered, (await h.LoadAsync(req.Id)).Status);
        Assert.Contains(h.Channels.Acks, a => a.Status == HitlOutcomeStatus.Answered); // original message updated
    }

    [Theory]
    [InlineData("  YES ", "approve")]
    [InlineData("Reject", "reject")]
    [InlineData("no", "reject")]
    public async Task TypedText_CaseInsensitive_Synonyms_Resume(string typed, string expected)
    {
        using var h = new Harness();
        var req = await h.CreateAsync(Harness.KeywordConfig());
        var result = await h.ReplyAsync(Harness.Typed(req, typed));
        Assert.Equal(ReplyStatus.Resumed, result.Status);
        Assert.Equal(expected, h.Resumer.Resumed.Single().Outputs["response.value"]);
    }

    [Fact]
    public async Task InvalidKeyword_RePromptsAndKeepsWaiting_ThenAppliesTimeoutRuleAfterRetries()
    {
        using var h = new Harness();
        var cfg = Harness.KeywordConfig();
        cfg.MaxRetries = 2;
        var req = await h.CreateAsync(cfg);

        var first = await h.ReplyAsync(Harness.Typed(req, "maybe", "e1"));
        Assert.Equal(ReplyStatus.Invalid, first.Status);
        Assert.Contains("Approve, Reject", h.Channels.Texts.Last().Text); // valid options listed
        Assert.Empty(h.Resumer.Resumed);
        Assert.Equal(HitlRequestStatus.Pending, (await h.LoadAsync(req.Id)).Status);

        await h.ReplyAsync(Harness.Typed(req, "perhaps", "e2"));
        var third = await h.ReplyAsync(Harness.Typed(req, "hmm", "e3"));

        Assert.Equal(ReplyStatus.TimedOut, third.Status); // retries exhausted -> OnTimeout=Fail
        Assert.Empty(h.Resumer.Resumed);
        Assert.Single(h.Resumer.Failed);
        Assert.Equal(HitlRequestStatus.TimedOut, (await h.LoadAsync(req.Id)).Status);
    }

    [Fact]
    public async Task SecondReply_IsAlreadyAnswered_ChangesNothing()
    {
        using var h = new Harness();
        var req = await h.CreateAsync(Harness.KeywordConfig());

        await h.ReplyAsync(Harness.Button(req, "approve", "e1"));
        var second = await h.ReplyAsync(Harness.Button(req, "reject", "e2"));

        Assert.Equal(ReplyStatus.AlreadyAnswered, second.Status);
        Assert.Contains("already answered", h.Channels.Texts.Last().Text);
        Assert.Single(h.Resumer.Resumed);
        Assert.Equal("approve", h.Resumer.Resumed[0].Outputs["response.value"]);
        Assert.Equal(1, await h.QueryAsync(db => db.Responses.CountAsync()));
    }

    [Fact]
    public async Task SimultaneousReplies_ResumeExactlyOnce()
    {
        using var h = new Harness();
        var req = await h.CreateAsync(Harness.KeywordConfig());

        var results = await Task.WhenAll(
            h.ReplyAsync(Harness.Button(req, "approve", "e1")),
            h.ReplyAsync(Harness.Button(req, "reject", "e2")));

        Assert.Equal(1, results.Count(r => r.Status == ReplyStatus.Resumed));
        Assert.Single(h.Resumer.Resumed);
        Assert.Equal(1, await h.QueryAsync(db => db.Responses.CountAsync()));
    }

    [Fact]
    public async Task DuplicateWebhookDelivery_IsIgnored()
    {
        using var h = new Harness();
        var req = await h.CreateAsync(Harness.KeywordConfig());
        var reply = Harness.Button(req, "approve", "same-event");

        Assert.Equal(ReplyStatus.Resumed, (await h.ReplyAsync(reply)).Status);
        Assert.Equal(ReplyStatus.Duplicate, (await h.ReplyAsync(reply)).Status);
        Assert.Single(h.Resumer.Resumed);
        Assert.Empty(h.Channels.Texts); // a duplicate does not even trigger an "already answered" message
    }

    [Fact]
    public async Task AllowedResponders_RejectOthersWithNeutralMessage()
    {
        using var h = new Harness();
        var cfg = Harness.KeywordConfig();
        cfg.AllowedResponders = new List<string> { "boss@example.com" };
        var req = await h.CreateAsync(cfg);

        var denied = await h.ReplyAsync(Harness.Button(req, "approve", "e1"));
        Assert.Equal(ReplyStatus.NotAllowed, denied.Status);
        Assert.Empty(h.Resumer.Resumed);

        var ok = Harness.Button(req, "approve", "e2") with { ResponderEmail = "Boss@Example.com" };
        Assert.Equal(ReplyStatus.Resumed, (await h.ReplyAsync(ok)).Status);
    }
}

public class OpenEndedFlowTests
{
    [Fact]
    public async Task FirstValidTextReply_ResumesWithText()
    {
        using var h = new Harness();
        var req = await h.CreateAsync(Harness.OpenConfig());
        var result = await h.ReplyAsync(Harness.Typed(req, "Leave at reception"));

        Assert.Equal(ReplyStatus.Resumed, result.Status);
        Assert.Equal("Leave at reception", h.Resumer.Resumed.Single().Outputs["response.text"]);
    }

    [Fact]
    public async Task InvalidReply_RePromptsWithCustomMessage_CountsAgainstRetries()
    {
        using var h = new Harness();
        var cfg = Harness.OpenConfig();
        cfg.MinLength = 10;
        cfg.ValidationMessage = "Please write at least 10 characters.";
        var req = await h.CreateAsync(cfg);

        var bad = await h.ReplyAsync(Harness.Typed(req, "short", "e1"));
        Assert.Equal(ReplyStatus.Invalid, bad.Status);
        Assert.Equal("Please write at least 10 characters.", h.Channels.Texts.Last().Text);
        Assert.Equal(1, (await h.LoadAsync(req.Id)).RetryCount);

        Assert.Equal(ReplyStatus.Resumed, (await h.ReplyAsync(Harness.Typed(req, "long enough text", "e2"))).Status);
    }
}

public class CorrelationTests
{
    [Fact]
    public async Task ConversationFallback_MatchesLatestPendingRequest()
    {
        using var h = new Harness();
        var req = await h.CreateAsync(Harness.KeywordConfig());

        // Rule 3: no token, no thread reference, only the conversation (the WhatsApp-style fallback).
        var reply = new InboundReply
        {
            Channel = ChannelKeys.Slack,
            ExternalConversationId = req.ExternalConversationId,
            ExternalMessageId = "e1",
            ResponderId = "U123",
            Text = "ok",
        };
        Assert.Equal(ReplyStatus.Resumed, (await h.ReplyAsync(reply)).Status);
    }

    [Fact]
    public async Task TokenWinsOverThreadReference()
    {
        using var h = new Harness();
        var first = await h.CreateAsync(Harness.KeywordConfig(), "run-1");
        var second = await h.CreateAsync(Harness.KeywordConfig(), "run-2");

        // Token points at the first request even though the thread reference points at the second.
        var reply = Harness.Button(first, "approve") with { ExternalThreadId = second.ExternalMessageId };
        await h.ReplyAsync(reply);

        Assert.Equal(HitlRequestStatus.Answered, (await h.LoadAsync(first.Id)).Status);
        Assert.Equal(HitlRequestStatus.Pending, (await h.LoadAsync(second.Id)).Status);
    }

    [Fact]
    public async Task UnmatchedMessage_IsIgnored_NeverResumes()
    {
        using var h = new Harness();
        await h.CreateAsync(Harness.KeywordConfig());

        var stray = new InboundReply
        {
            Channel = ChannelKeys.Slack,
            ExternalConversationId = "some-other-channel",
            ExternalMessageId = "e1",
            ResponderId = "U999",
            Text = "approve",
        };
        Assert.Equal(ReplyStatus.Unmatched, (await h.ReplyAsync(stray)).Status);
        Assert.Empty(h.Resumer.Resumed);

        var unknownToken = Harness.Button(new HitlRequest { CorrelationToken = "forged" }, "approve", "e2");
        Assert.Equal(ReplyStatus.Unmatched, (await h.ReplyAsync(unknownToken)).Status);
        Assert.Empty(h.Resumer.Resumed);
    }

    [Fact]
    public async Task WhatsApp_AllowsOnlyOneOpenRequestPerPhoneNumber()
    {
        using var h = new Harness();
        var cfg = Harness.KeywordConfig(ChannelKeys.WhatsApp);
        cfg.Recipient = "+15551234567";
        await h.CreateAsync(cfg, "run-1");

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.CreateAsync(cfg, "run-2"));
    }
}

public class TimeoutAndSchedulerTests
{
    private static HitlNodeConfig Cfg(HitlTimeoutAction action)
    {
        var c = Harness.KeywordConfig();
        c.Timeout = TimeSpan.FromHours(1);
        c.OnTimeout = action;
        return c;
    }

    [Fact]
    public async Task Fail_FailsTheNode()
    {
        using var h = new Harness();
        var req = await h.CreateAsync(Cfg(HitlTimeoutAction.Fail));
        h.Clock.Advance(TimeSpan.FromHours(2));
        await h.RunSchedulerAsync();

        Assert.Equal(HitlRequestStatus.TimedOut, (await h.LoadAsync(req.Id)).Status);
        Assert.Single(h.Resumer.Failed);
        Assert.Equal(WorkflowRunStatus.Failed, (await h.LoadRunAsync("run-1")).Status);
    }

    [Fact]
    public async Task ContinueWithDefault_ResumesWithDefaultValue()
    {
        using var h = new Harness();
        var cfg = Cfg(HitlTimeoutAction.ContinueWithDefault);
        cfg.DefaultValue = "reject";
        await h.CreateAsync(cfg);
        h.Clock.Advance(TimeSpan.FromHours(2));
        await h.RunSchedulerAsync();

        var outputs = Assert.Single(h.Resumer.Resumed).Outputs;
        Assert.Equal("reject", outputs["response.value"]);
        Assert.Equal("system:timeout", outputs["response.responder"]);
    }

    [Fact]
    public async Task Escalate_SendsToFallback_AndFallbackAnswerResumes()
    {
        using var h = new Harness();
        var cfg = Cfg(HitlTimeoutAction.Escalate);
        cfg.FallbackChannel = ChannelKeys.Telegram;
        cfg.FallbackRecipient = "999";
        var original = await h.CreateAsync(cfg);

        h.Clock.Advance(TimeSpan.FromHours(2));
        await h.RunSchedulerAsync();

        Assert.Equal(HitlRequestStatus.TimedOut, (await h.LoadAsync(original.Id)).Status);
        Assert.Empty(h.Resumer.Resumed);
        Assert.Equal(WorkflowRunStatus.Waiting, (await h.LoadRunAsync("run-1")).Status); // still waiting for the fallback person
        var escalated = await h.QueryAsync(db => db.Requests.AsNoTracking().FirstAsync(r => r.EscalatedFromRequestId == original.Id));
        Assert.Equal("telegram", escalated.Channel);
        Assert.Equal("999", escalated.Recipient);

        var reply = Harness.Button(escalated, "approve", "e1", ChannelKeys.Telegram);
        Assert.Equal(ReplyStatus.Resumed, (await h.ReplyAsync(reply)).Status);
        Assert.Single(h.Resumer.Resumed);
    }

    [Fact]
    public async Task Reminder_ExtendsTimeout_ThenFailsWhenExhausted()
    {
        using var h = new Harness();
        var cfg = Cfg(HitlTimeoutAction.Reminder);
        cfg.MaxTimeoutReminders = 1;
        var req = await h.CreateAsync(cfg);

        h.Clock.Advance(TimeSpan.FromHours(2));
        await h.RunSchedulerAsync();
        var extended = await h.LoadAsync(req.Id);
        Assert.Equal(HitlRequestStatus.Pending, extended.Status);
        Assert.Equal(1, extended.ReminderCount);
        Assert.Contains(h.Channels.Texts, t => t.Text.Contains("Reminder"));

        h.Clock.Advance(TimeSpan.FromHours(2));
        await h.RunSchedulerAsync();
        Assert.Equal(HitlRequestStatus.TimedOut, (await h.LoadAsync(req.Id)).Status);
        Assert.Single(h.Resumer.Failed);
    }

    [Fact]
    public async Task ReminderInterval_SendsPeriodicReminders_BeforeExpiry()
    {
        using var h = new Harness();
        var cfg = Cfg(HitlTimeoutAction.Fail);
        cfg.Timeout = TimeSpan.FromHours(4);
        cfg.ReminderInterval = TimeSpan.FromHours(1);
        await h.CreateAsync(cfg);

        h.Clock.Advance(TimeSpan.FromMinutes(61));
        await h.RunSchedulerAsync();
        Assert.Single(h.Channels.Texts);

        await h.RunSchedulerAsync(); // nothing new is due yet
        Assert.Single(h.Channels.Texts);
    }
}

public class DurabilityAndFailureTests
{
    [Fact]
    public async Task ReplyAfterRestart_StillResumesTheRun()
    {
        HitlRequest req;
        string dbFile;
        using (var before = new Harness())
        {
            dbFile = before.DbFile;
            req = await before.CreateAsync(Harness.KeywordConfig());
            // Keep the file: Harness.Dispose deletes it, so copy it aside while the first "process" is alive.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Copy(dbFile, dbFile + ".keep", overwrite: true);
        }

        using var after = new Harness(dbFile + ".keep"); // brand-new service provider, same database file
        var result = await after.ReplyAsync(Harness.Button(req, "approve"));

        Assert.Equal(ReplyStatus.Resumed, result.Status);
        Assert.Single(after.Resumer.Resumed);
    }

    [Fact]
    public async Task ResumeFailure_MarksRunResumeFailed_DoesNotReAsk_AndCanBeRetried()
    {
        using var h = new Harness();
        var req = await h.CreateAsync(Harness.KeywordConfig());
        h.Resumer.ThrowOnResume = true;

        var result = await h.ReplyAsync(Harness.Button(req, "approve"));

        Assert.Equal(ReplyStatus.Resumed, result.Status); // the person's answer is safely recorded
        Assert.Equal(HitlRequestStatus.Answered, (await h.LoadAsync(req.Id)).Status);
        Assert.Equal(WorkflowRunStatus.ResumeFailed, (await h.LoadRunAsync("run-1")).Status);
        Assert.Empty(h.Channels.Texts); // nobody was asked to answer again

        h.Resumer.ThrowOnResume = false;
        using var scope = h.Provider.CreateScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<HitlCompletion>().RetryResumeAsync("run-1", CancellationToken.None));
        // Outputs come back from the database as JSON values, hence ToString().
        Assert.Equal("approve", Assert.Single(h.Resumer.Resumed).Outputs["response.value"]?.ToString());
    }

    [Fact]
    public async Task SendFailure_RetriesThreeTimes_ThenFailsTheNode()
    {
        using var h = new Harness();
        h.Channels.FailNextSends = 5;

        await Assert.ThrowsAsync<HitlSendException>(() => h.CreateAsync(Harness.KeywordConfig()));

        Assert.Equal(2, h.Channels.FailNextSends); // exactly 3 attempts were made
        Assert.Equal(WorkflowRunStatus.Failed, (await h.LoadRunAsync("run-1")).Status);
    }

    [Fact]
    public async Task TransientSendFailure_IsRetriedAndSucceeds()
    {
        using var h = new Harness();
        h.Channels.FailNextSends = 2;
        var req = await h.CreateAsync(Harness.KeywordConfig());
        Assert.NotNull((await h.LoadAsync(req.Id)).ExternalMessageId);
    }

    [Fact]
    public async Task UnreachableRecipient_FailsFast_WithoutRetries()
    {
        using var h = new Harness();
        h.Channels.Unreachable = true;
        var ex = await Assert.ThrowsAsync<HitlSendException>(() => h.CreateAsync(Harness.KeywordConfig()));
        Assert.Contains("not reachable", ex.Message);
        Assert.DoesNotContain(await h.QueryAsync(db => db.AuditEvents.Select(a => a.EventType).ToListAsync()), t => t == "send_retry");
    }

    [Fact]
    public async Task EveryStepIsAudited()
    {
        using var h = new Harness();
        var req = await h.CreateAsync(Harness.KeywordConfig());
        await h.ReplyAsync(Harness.Button(req, "approve"));

        var events = await h.QueryAsync(db => db.AuditEvents.Where(a => a.RequestId == req.Id).Select(a => a.EventType).ToListAsync());
        Assert.Contains("created", events);
        Assert.Contains("sent", events);
        Assert.Contains("reply_received", events);
        Assert.Contains("answered", events);
    }
}
