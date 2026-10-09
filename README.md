# BTM Pizza Notification Service

ASP.NET Core (.NET 9) API that sends push notifications to the BTM Pizza iOS app (`com.nayakkarthik.BTMPizza`). It uses APNs over HTTP/2 with token-based (.p8) auth. It takes send requests over HTTP and from RabbitMQ.

```
src/BtmPizza.Notifications/        the API (also contains the send-test CLI)
tests/BtmPizza.Notifications.Tests xUnit tests, including a full send against a fake HTTP/2 APNs server
config/credentials.json            secrets (gitignored); see credentials.example.json
data/notifications.db              SQLite: devices and send log (created on first run)
```

## Setup

1. `cp config/credentials.example.json config/credentials.json`
2. Set `admin_api_key` to a long random string. Callers of the send API need it.
3. Set `rabbitmq.url`, e.g. `amqp://user:pass@localhost:5672/`. Percent-encode special characters in the password, e.g. `@` becomes `%40`.
4. Once you have the Apple credentials, fill in `apns.team_id` and `apns.key_id`, and put the `.p8` file at `apns.private_key_path`. `config/*.p8` is gitignored.
5. Start the service with `dotnet run --project src/BtmPizza.Notifications`. It runs on `http://localhost:3000`.

While the APNs values are still placeholders, the service starts and device registration works. Real sends are refused with `503`, and dry runs (`"dry_run": true`) still return the built payload.

To use a credentials file somewhere else, set `CredentialsPath`. To move the database, set `DbPath`. Both can be set as environment variables or in `appsettings.json`.

## API

### `POST /devices`, called by the app

```json
{ "device_token": "<hex token>", "platform": "ios", "user_id": "user-123",
  "environment": "sandbox", "device_id": "<identifierForVendor>" }
```

`environment` and `device_id` are optional.

- `environment`: `sandbox` for development builds, `production` for TestFlight and App Store builds. It defaults to `apns.default_environment`. Each token is sent to the matching APNs host. This matters because a sandbox token sent to the production host comes back as `BadDeviceToken`, and the service then deletes that token.
- `device_id`: lets the service replace a device's old token when it changes. Without it, the token itself identifies the device.

### `POST /notifications/send`, needs `Authorization: Bearer <admin_api_key>`

```json
{ "type": "OFFER", "title": "Two mediums for ₹599", "body": "Today only at BTM Layout",
  "kicker": "Weekend offer", "article": ["Paragraph 1", "Paragraph 2"],
  "image_url": "https://s3.flavourslane.com/notificationimages/banner.jpeg",
  "target": { "user_ids": ["user-123"] }, "dry_run": false }
```

- `target` can also be `{ "all": true }`.
- Validation failures return `422` and list every problem.
- Every built payload is also checked against the iOS contract before it is sent.
- Tokens that get `410` or `400 BadDeviceToken` from APNs are deleted.
- Each send is logged and stored in the `notification_log` table.

## RabbitMQ

| | |
|---|---|
| Exchange | `btm.notifications` (topic, durable) |
| Routing key | `notification.send` |
| Queue (created by the service) | `notification-service.send` |
| Dead-letter queue | `notification-service.send.dlq` |

The message body is the same JSON as `POST /notifications/send`. Publish it with `content_type: application/json` and delivery mode 2 (persistent).

- If the message is processed, it is acked.
- If the message is invalid JSON, fails validation, or arrives while the Apple credentials are still placeholders, it goes to the dead-letter queue. It is not retried.

Debug logging is on by default for the `BtmPizza` category. It prints every incoming message and the payload built from it. To turn it off, set `Logging:LogLevel:BtmPizza` to `Information`, or set the environment variable `Logging__LogLevel__BtmPizza=Information`.

## Test notifications

- Admin form: open `http://localhost:3000/admin`.
- CLI:
  ```sh
  dotnet run --project src/BtmPizza.Notifications -- send-test --type OFFER --user user-123
  dotnet run --project src/BtmPizza.Notifications -- send-test --all-types --all-devices --dry-run
  ```

## Tests

Run `dotnet test`.

## Docker, CI and deployment

- **`Dockerfile`:** a multi-stage build that serves on port 8080 inside the container. `config/` and `data/` are excluded from the image (see `.dockerignore`). In the cluster, `credentials.json` is mounted from a Secret at `/app/config`, and the database lives on a volume at `/app/data`.
- **`Jenkinsfile`:** the same flow as AuthService and Gateway:
  1. Build and test.
  2. Push `ghcr.io/btmpizza/notificationservice:<sha>-<build>`.
  3. Bump `charts/notificationservice/values.yaml` in [AuthService-deployment](https://github.com/BTMPizza/AuthService-deployment).
  4. Validate the chart, then commit and push.
  5. Wait for the Argo app `notification-service` to be Synced and Healthy.
- **Deployment:** the Helm chart, the Argo Application and the one-time cluster setup are in the deployment repo's README, under "NotificationService".

In the cluster, `rabbitmq.url` must use the server's IP, e.g. `amqp://user:pass@192.168.1.24:5672/`. `localhost` inside a pod refers to the pod itself.
