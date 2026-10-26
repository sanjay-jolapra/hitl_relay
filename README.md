# HITL Relay: Human-in-the-Loop module

A workflow node that pauses a run, asks a person a question on **Microsoft Teams, Slack, WhatsApp, Telegram or Email**,
captures the reply and resumes the run with the reply as data. .NET 8 / ASP.NET Core / EF Core.

> **Status:** written against the requirements spec but **not yet compiled or run** (the authoring environment had no .NET SDK).
> Run `dotnet build` and `dotnet test` first and expect to fix a few compile errors.

## Layout

```
src/HitlRelay/
  Core/        IHitlChannelAdapter, HitlService, HitlReplyHandler, HitlCompletion, HitlScheduler, InboundQueue, helpers
  Models/      entities, node config, shared models (InboundReply, SendResult, HitlOutcome)
  Data/        HitlDbContext
  Channels/    Teams, Slack, WhatsApp, Telegram, Email adapters (+ Email token/sender/cleaner)
  Controllers/ webhook endpoints, /api/hitl/*, /demo/*
  Sample/      SampleWorkflowEngine: Start -> HITL -> branch on response.value / echo response.text
tests/HitlRelay.Tests/   keyword matching, correlation, double reply, timeouts, restart, forged webhooks
```

## Run

```bash
dotnet test
cd src/HitlRelay
# secrets: use user-secrets or environment variables, never commit them (Channels__Slack__BotToken=...)
ASPNETCORE_ENVIRONMENT=Development dotnet run
```

The database (SQLite, `hitl.db`) is created on first start with `EnsureCreated`. For production switch the provider in
`AddHitlCore` and generate a migration: `dotnet ef migrations add Initial --project src/HitlRelay`.
Persist the ASP.NET Data Protection key ring (it encrypts `HitlChannelConnection.ConfigJson`).

Every webhook needs a public HTTPS URL (`Hitl:BaseUrl`); use a tunnel such as `ngrok http 5000` while developing.

### Try the sample workflow (Development only)

```bash
curl -X POST localhost:5000/demo/start -H 'Content-Type: application/json' \
  -d '{"channel":"telegram","recipient":"123456789","mode":"keyword"}'
curl localhost:5000/demo/runs/<runId>      # shows the run log and status
curl localhost:5000/api/hitl/requests/<requestId>
```

`mode` is `keyword` (Approve/Reject) or `open`. Add `fallbackChannel`/`fallbackRecipient` to use the Escalate timeout rule.
Outside Development set `Hitl:ApiKey` and send it as `X-Api-Key` (the `/api/hitl/*` and `/demo/*` endpoints are otherwise closed).

## Using the node from your own engine

```csharp
var request = await hitlService.CreateAsync(config, workflowRunId, nodeId, variables);   // returns once sent; run = Waiting
```

Implement `IWorkflowResumer` (`ResumeAsync`, plus `FailAsync` for the timeout/send-failure error branch) and register it instead
of `AddSampleWorkflow()`. Outputs: `response.value`, `response.label`, `response.text`, `response.responder`,
`response.channel`, `response.receivedAt` (plus `response.timedOut` when the default value was applied).

## Channel setup

| Channel | Endpoint(s) | Verification |
|---|---|---|
| Teams | `POST /api/messages` | Bot Framework JWT (CloudAdapter, answers 401) |
| Slack | `POST /slack/events`, `POST /slack/interactivity` | `X-Slack-Signature` HMAC + 5-minute timestamp check |
| WhatsApp | `GET/POST /whatsapp/webhook` | verify-token handshake, `X-Hub-Signature-256` HMAC |
| Telegram | `POST /telegram/webhook` | `X-Telegram-Bot-Api-Secret-Token` |
| Email | `GET/POST /hitl/email/respond`, `POST /hitl/email/inbound` | signed expiring link token; shared-secret header for inbound mail |

Config keys live in `appsettings.json` under `Channels:*`; the template lists every key.

### Telegram
1. Talk to **@BotFather**, `/newbot`, copy the token into `Channels:Telegram:BotToken`.
2. Pick a random `WebhookSecret`. Set `Channels:Telegram:AutoRegisterWebhook=true` (registers `{BaseUrl}/telegram/webhook`), or call
   `setWebhook` yourself with `secret_token`.
3. **The recipient is a numeric `chat_id`.** The person must open the bot and press **Start** once; until then Telegram refuses
   to message them and the node fails fast with a clear error. Find a chat id by messaging the bot and reading `message.chat.id`
   from `getUpdates` (before the webhook is set) or from the audit log.

### Slack
1. Create a Slack app. Bot scopes: `chat:write`, `im:write`, `im:history` (add `channels:history` for channel threads).
2. Event Subscriptions: Request URL `{BaseUrl}/slack/events` (the `url_verification` challenge is answered automatically);
   subscribe to `message.im` (and `message.channels` if used).
3. Interactivity: Request URL `{BaseUrl}/slack/interactivity`.
4. Put the Bot User OAuth Token in `BotToken` and the Signing Secret in `SigningSecret`. Recipient = user ID (DM) or channel ID.
   Keyword answers are Block Kit buttons; open-ended answers are thread replies (or any message in a DM).

### WhatsApp (Cloud API)
1. Meta developer app with the WhatsApp product; copy the permanent token, Phone Number ID and App Secret.
2. Webhook: callback `{BaseUrl}/whatsapp/webhook`, verify token = `VerifyToken`, subscribe to `messages`.
3. **24-hour rule:** the first message to a person outside the 24-hour window must be a pre-approved template. Set
   `WhatsAppTemplate` (name, language, body variables) on the node. Template approval takes time. For keyword mode, define up to
   3 quick-reply buttons in the template. Without a template, free-form messages work only inside the window, and Meta's
   re-engagement error is reported as "recipient unreachable".
4. Keyword UI: reply buttons (up to 3), list message (up to 10), numbered text beyond that.
5. **One open request per phone number** (across workflows) is enforced, because WhatsApp has no threads: a reply is matched
   by `context.id` when present, otherwise to that number's single open request.

### Microsoft Teams
1. Azure Bot (single-tenant) registration; messaging endpoint `{BaseUrl}/api/messages`; add the Teams channel.
2. Fill `Channels:Teams` (`MicrosoftAppId`, `MicrosoftAppPassword`, `MicrosoftAppTenantId`, `MicrosoftAppType`).
3. Package a Teams app manifest for the bot (scope `personal`) and install it for each user. The bot stores each user's
   `ConversationReference` (encrypted) on install or first message; **proactive messaging fails for users who never installed it.**
   Recipient = the user's AAD object id or Teams user id (`29:...`).
4. Keyword mode sends an Adaptive Card with `Action.Submit` buttons whose data is `{"hitlToken": "...", "key": "approve"}`;
   after the answer the card is replaced by the outcome (`UpdateActivityAsync`).

### Email
* `Provider`: `Smtp` (MailKit; set `Smtp:*`) or `Graph` (client credentials, `Mail.Send` application permission; set `Graph:*`).
* Set `LinkSigningKey` (32+ random characters) and `Hitl:BaseUrl`.
* Keyword mode: one button-styled link per keyword. The link opens a confirmation page and only the **POST** records the
  answer, so mail scanners that prefetch links cannot answer for the person.
* Open-ended: link to a hosted response page. Optional reply-by-email: set `InboundAddressPattern` (for example
  `hitl+{token}@example.com`), point your inbound-parse provider at `POST /hitl/email/inbound` with header
  `X-Hitl-Inbound-Secret`, JSON body `{messageId, from, fromName, to[], inReplyTo, references, text}`. Replies are matched by the
  plus-address token or `In-Reply-To`; quoted text and signatures are stripped.

## Behaviour notes and assumptions (where the spec left room)

* `IHitlChannelAdapter` has extra members beyond section 4: `SendTextAsync` (re-prompts, reminders, "already answered"),
  `ParseInboundAll` (WhatsApp batches) and `AcknowledgeInboundAsync` (Telegram `answerCallbackQuery`); the last two are default interface methods.
* `IWorkflowResumer` has a second method, `FailAsync`, so the host can take the error branch on timeout/send failure.
* Timeout rules: `Fail` fails the node; `ContinueWithDefault` resumes with the default; `Escalate` closes the request and sends
  a new one to the fallback target (the run keeps waiting, one escalation only); `Reminder` re-arms the timeout up to
  `MaxTimeoutReminders` (default 3), then fails. `ReminderInterval` sends periodic reminders before expiry.
* Invalid replies: each re-prompts; after `MaxRetries` (default 2) more invalid replies the timeout rule is applied.
* A bare number selects the keyword at that position on every channel (the WhatsApp numbered fallback).
* A request whose send fails after 3 attempts is stored as `Cancelled`, the run is `Failed`, and `HitlSendException` is thrown.
* Webhooks answer `200` immediately and hand work to an in-process queue. Idempotency uses a `ProcessedInbound` ledger plus the
  optimistic-concurrency check on `Status`. Multi-instance deployments need a shared database (not SQLite) and ideally a durable queue.
* Slack's optional modal flow is not implemented (thread replies and DMs cover open-ended answers). IMAP/Graph mailbox polling for
  reply-by-email is not implemented (inbound webhook only). Attachments and multi-approver chains are a later phase, as in the spec.
* `/api/hitl/runs/{runId}/retry-resume` re-calls the resumer for a run stuck in `ResumeFailed`, without asking the person again.

## Spec acceptance criteria and where they are tested

| Criterion | Test |
|---|---|
| Button/link resumes with the right `response.value` | `ButtonClick_ResumesWithKeyOutputs` |
| Typed keyword (case, synonym) resumes | `TypedText_CaseInsensitive_Synonyms_Resume` |
| Open-ended resumes with `response.text` | `FirstValidTextReply_ResumesWithText` |
| Invalid keyword re-prompts, does not resume | `InvalidKeyword_RePrompts...` |
| Second reply changes nothing | `SecondReply_IsAlreadyAnswered...`, `SimultaneousReplies_ResumeExactlyOnce` |
| Timeout rules | `TimeoutAndSchedulerTests` |
| Forged webhook rejected | `ForgedWebhookTests` (adapters return false, controllers answer 401) |
| Survives restart | `ReplyAfterRestart_StillResumesTheRun` |

Channel send/parse code against the real vendor APIs is not covered by automated tests; verify each channel manually with the setup above.
