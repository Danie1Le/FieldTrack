# The cloud demo runs on AWS, created for a session and destroyed afterwards

The spec first named Azure as the cloud target, on the Azure for Students credit (no card, services stop when the credit runs out). That plan failed at sign-up: Azure for Students could not verify the school account (#8). The demo deploys to AWS instead, paid only from the AWS account's credits (#12).

The $0 rule still holds. Every AWS resource is paid from credits, never from a card charge. A budget alert on spend after credits fires only if real money would be charged. The demo is applied for a session, recorded, and destroyed; nothing runs between sessions. Before anything is applied, check the account plan (Free or Paid) and the remaining credit.

## Details

- **Broker:** AWS IoT Core in the cloud, Mosquitto locally. Devices and the ingest worker connect with X.509 client certificates created by Terraform. IoT Core delivers at most 100 msg/s to one subscriber connection and ADR 0005 allows one ingest worker, so the cloud runs at demo rate. Load benchmarks stay local.
- **Processes:** one ECS Fargate service runs the API with the ingest worker inside, one replica, so SignalR needs no backplane. The simulator is a second Fargate service at demo rate.
- **Database:** RDS for PostgreSQL `db.t4g.micro` with PostGIS, single-AZ, 20 GB gp3, no final snapshot.
- **Front door:** CloudFront is the single HTTPS address. The Angular app is served from S3. `/api` and `/hubs` go through an Application Load Balancer to Fargate, so there is no CORS and SignalR WebSockets work.
- **Secrets:** SSM Parameter Store, standard tier (free), read by the task role.
- **Observability:** the AWS Distro for OpenTelemetry collector runs as a sidecar and sends metrics to CloudWatch and traces to X-Ray. Both need signed requests, so the sidecar exists on AWS only. Logs are kept 1 day.
- **Terraform:** two stacks, state in S3, run from GitHub Actions through OIDC.
  - `bootstrap`: the S3 state bucket, the GitHub OIDC provider and role, and the budget alert. Applied once from the laptop and kept.
  - `demo`: everything else. Applied and destroyed per session from manual GitHub Actions workflows. `terraform plan` runs on PRs.
- **Images:** published to GHCR, as before.
- **Cost guardrails:** public subnets with no NAT gateway, one ALB and only while a session runs, destroy after every session.

## Considered Options

- **Stay on Azure:** the school account cannot be verified, so there is no student credit and no card-free path.
- **Azure on a paid account:** breaks the $0 rule.
- **Mosquitto on Fargate instead of IoT Core:** keeps one broker everywhere, but needs a Network Load Balancer or public IP plus broker TLS and credentials to manage. IoT Core certificates are created by Terraform and cost nothing while idle.
- **API on Lambda or App Runner:** scales to zero, but the ingest worker and SignalR need a long-running process.
- **Aurora Serverless or Multi-AZ RDS:** more availability than a session demo needs, at a higher price.
- **NAT gateway for private subnets:** the usual layout, but a NAT gateway bills by the hour, so tasks and the database sit in public subnets behind security groups.

## Consequences

- The cloud cannot reproduce the 2,000 msg/s load benchmarks; those numbers come from the local stack only.
- The MQTT client must support both Mosquitto (local) and IoT Core (client certificate).
- Credits are a budget, not a cap. The budget alert is the only guard against a charge, so a session that is not destroyed can spend real money once credits run out.
- Credit terms and prices change. Re-check them before each deploy.
