# BTM Pizza Notification Service Constitution

## Core Principles

### I. iOS Payload Contract Is Authoritative

The BTM Pizza iOS app (`com.nayakkarthik.BTMPizza`) defines the push payload; this service conforms to it.

- Every payload MUST pass `IosContract.Check` before it is sent. A payload that fails it is a bug and
  MUST NOT reach APNs.
- The set of notification types (`NotificationTypes.All`) MUST match the types the shipped app
  understands. Adding, renaming or removing a type MUST be coordinated with an app release, because
  the app shows unknown types as a generic bell.
- Payload limits are fixed: title ≤ 50, body ≤ 150, kicker ≤ 25 user-visible characters; whole
  payload ≤ 4096 UTF-8 bytes; `aps.thread-id` equals `type`; no `aps.sound` (the app decides the
  sound); no `aps.category`.

Rationale: the app is released through the App Store and cannot be patched alongside the service, so
a contract mismatch reaches customers' phones and stays there.

### II. Validate Up Front, Report Every Problem

- Input from HTTP and RabbitMQ MUST go through the same validation (`SendRequestValidator`).
- Validation MUST collect and return every problem at once (HTTP `422` with the full list), not
  just the first one.
- Unknown fields MUST be rejected, not ignored.
- RabbitMQ messages that are malformed or fail validation MUST be dead-lettered, not retried.
- Misconfiguration (credentials, database) MUST fail at startup, not on the first send. Missing
  APNs credentials are the one exception: the service starts, dry runs work, and real sends return
  `503`.

Rationale: callers are other services and an admin form; one complete error list is the fastest
way for them to fix a request, and retrying a bad message can never succeed.

### III. Self-Contained Tests (NON-NEGOTIABLE)

- `dotnet test` MUST pass before any change merges to `main`; Jenkins enforces this on every build.
- Tests MUST NOT need Apple, RabbitMQ or network access. They use a temp credentials file, a temp
  SQLite database and the fake HTTP/2 APNs server on loopback.
- Every change to validation, payload building or the iOS contract MUST include a test that fails
  without the change.
- Every new notification type MUST have a sample in `Samples.cs` that passes
  `send-test --all-types --dry-run`.

Rationale: the Jenkins agent has no Apple or RabbitMQ access, and a regression in payload shape is
only visible on a real phone otherwise.

### IV. Secrets Stay Out of Git and Images

- `config/credentials.json`, `.p8` keys and `data/` MUST stay gitignored and excluded from the
  Docker image.
- In the cluster, credentials MUST come from a mounted Secret at `/app/config`.
- Logs MUST NOT print the admin API key, APNs signing keys or RabbitMQ passwords; connection URLs
  are redacted before logging.
- The send API MUST require `Authorization: Bearer <admin_api_key>`.

Rationale: the send API can push to every customer's phone; a leaked key is a brand incident.

### V. Every Send Is Traceable

- Every send, including dry runs, MUST get a fresh lowercase UUID `notification_id`.
- Real sends MUST be written to `notification_log` with type, target count, and success, failure
  and removed-token counts.
- Tokens that APNs answers with `410 Unregistered` or `400 BadDeviceToken` MUST be deleted.
- Each token MUST be sent to the APNs host that matches its registered environment (sandbox or
  production).

Rationale: without a log there is no way to answer "did this customer get the offer?", and dead
tokens otherwise accumulate and inflate failure counts.

## Technology & Deployment Constraints

- Stack: ASP.NET Core on .NET 9 (`global.json`), SQLite via `Microsoft.Data.Sqlite`,
  `RabbitMQ.Client`, xUnit. New runtime dependencies MUST be justified in the feature plan.
- APNs: HTTP/2 with token-based (.p8) auth, `apns-push-type: alert`,
  `apns-topic: com.nayakkarthik.BTMPizza`.
- HTTP and RabbitMQ (`btm.notifications` / `notification.send`) MUST accept the same JSON body.
- Images MUST be tagged `<short-sha>-<build>` and never `latest`. Deployment happens only through
  the Jenkins → deployment repo (`charts/notificationservice/values.yaml`) → Argo CD flow; no manual
  `kubectl` changes to the running service.
- `image_url` MUST be https, percent-encoded, and return `200` with an image content type.

## Development Workflow

- New features follow Spec Kit: `/speckit-specify` → `/speckit-plan` → `/speckit-tasks` →
  `/speckit-implement`. Plans MUST include a check against this constitution.
- Changes that touch the iOS contract or notification types MUST state in the spec whether an app
  update is needed and in what order the service and app ship.
- Changes to the request API or RabbitMQ message MUST stay backward compatible, or the spec MUST
  list the affected publishers and the migration.
- `README.md` MUST be updated in the same change as any API, configuration or deployment change.

## Governance

This constitution takes precedence over other conventions in this repository. Feature plans and code
reviews MUST check compliance with it, and any deviation MUST be written down with its reason in the
plan's complexity tracking.

Amendments are made by editing this file through `/speckit-constitution`, in a commit that explains
the change. Versions follow semantic versioning: MAJOR for removing or redefining a principle, MINOR
for a new principle or section or materially expanded guidance, PATCH for wording and clarifications.

**Version**: 1.0.0 | **Ratified**: 2026-10-20 | **Last Amended**: 2026-10-20
