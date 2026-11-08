# Deploy NovaKart API to Azure Container Apps — Develop

The API uses .NET 10, Azure Container Apps Consumption, private images in Azure Container Registry (ACR), and external Neon PostgreSQL. GitHub Actions validates pull requests and deploys `main` through OIDC. No local image build, Azure client password, registry administrator password, or deployed database secret in GitHub is required.

```text
PR -> build + migration check + PostgreSQL integration tests + Docker build
main -> validation -> build/push commit image -> Container Apps revision -> smoke checks
API -> Neon PostgreSQL over TLS
```

## 1. Prepare GitHub Actions first

1. Enable GitHub Actions for the repository. The API workflow is `.github/workflows/deploy-api.yml`.
2. In Settings -> Secrets and variables -> Actions -> Variables, create the **repository** variable `API_DEPLOY_ENABLED=false`. Leaving it unset also disables deployment. Do not put this switch only in an environment: the job condition is evaluated before environment variables become available.
3. Merge the Dockerfile, workflow, and API health changes into `main`. Validation runs without Azure credentials, using disposable PostgreSQL 15 containers. Deployment stays disabled until the Azure setup below is complete.
4. Create Settings -> Environments -> `develop` (lowercase, matching the workflow). Restrict deployment branches to `main` where your GitHub plan supports it. Leave required reviewers disabled for automatic deployment. The workflow independently restricts deployment to `main`.
5. Allow the actions used by the workflow in your organization/repository Actions policy.

Optional local validation from the repository root:

```bash
dotnet tool restore
dotnet restore apps/api/NovaKart.slnx
dotnet build apps/api/NovaKart.slnx -c Release --no-restore
dotnet test apps/api/tests/NovaKart.WebApi.IntegrationTests -c Release --no-build
docker build --platform linux/amd64 -f apps/api/Dockerfile -t novakart-api:local apps/api
```

Docker is needed locally for Testcontainers, but GitHub runners already provide it. The Docker context is `apps/api`, not the repository root. The runtime image uses the non-root .NET user, listens on HTTP 8080, and excludes local `.env` files. The hosted runtime uses the exact custom environment name `Develop`, which is distinct from .NET's built-in `Development`. Only local `Development` loads `.env` files and redirects HTTP in the application; Azure ingress enforces HTTPS for the hosted Develop API.

## 2. Check the Azure student subscription

1. Activate **Azure for Students**, then open Azure portal -> Subscriptions. Select the active student subscription; record subscription ID and tenant ID. Students Starter is a different, limited offer.
2. Check remaining credit, expiry, supported regions, and free-service eligibility. The published offer includes $100 for 12 months, renewable while eligible; it is not a monthly $100 allowance.
3. In Cost Management, create a small monthly budget (for example $5) and email alerts at 50%, 80%, and 100%. Alerts do not stop resources. Keep student spending protection enabled.
4. Confirm your user can create resources and assign roles. University tenant restrictions may require an administrator's help. The two managed identities below avoid requiring an Entra app registration.
5. Pick a supported region close to your users, initially `eastus` if available. Keep ACR and Container Apps in that region. If provisioning reports region or quota restrictions, choose another supported region before proceeding.

Consumption includes monthly free amounts, but excess compute, requests, logs, registry storage, and network usage can consume credit. Set minimum replicas to zero. Cold starts are an accepted tradeoff. Check actual subscription costs after deployment rather than assuming the whole stack is free.

## 3. Create Neon PostgreSQL

1. Sign up at Neon and explicitly select the Free plan.
2. Create project `novakart`, PostgreSQL 15, in a region near your Azure region. Select its deployment branch.
3. Create database `novakart` and a dedicated application role that owns the database/schema and can create/alter its objects. The database must exist, but its schema can be empty.
4. Open Connect, select that database and role, and disable connection pooling to obtain the **direct hostname**. Startup migrations use this connection, so use the direct endpoint initially.
5. Convert the credentials to Npgsql format:

```text
Host=<direct-hostname>;Port=5432;Database=novakart;Username=<role>;Password=<password>;SSL Mode=VerifyFull;Channel Binding=Require;Timeout=30;Command Timeout=60
```

Use the actual password, not a URI-encoded password. Quote/escape connection-string values containing semicolons using Npgsql connection-string rules. Do not commit this value or use it for CI tests. Keep TLS certificate verification enabled.

Check the current Neon Free plan's compute, storage, transfer, and restore limits. Neon can sleep when idle; database wake-up adds latency. Free-plan database restore capabilities are limited: export valuable data separately before destructive changes. Do not deploy PostgreSQL as an ephemeral sidecar inside the API Container App.

## 4. Create Azure resources (portal plus Cloud Shell)

Use these example names; the ACR name must be globally unique and alphanumeric:

| Resource                   | Name                      |
| -------------------------- | ------------------------- |
| Resource group             | `rg-novakart-develop`     |
| Container Apps environment | `cae-novakart-develop`    |
| Container App              | `ca-novakart-api`         |
| Registry                   | `novakart<unique-suffix>` |
| Image pull identity        | `id-novakart-acrpull`     |
| GitHub deployment identity | `id-novakart-github`      |

### Register providers

Open Cloud Shell -> Bash (a session without persistent storage is sufficient). Run:

```bash
az account set --subscription '<subscription-id>'
az provider register --namespace Microsoft.App --wait
az provider register --namespace Microsoft.ContainerRegistry --wait
az provider register --namespace Microsoft.ManagedIdentity --wait
az provider register --namespace Microsoft.OperationalInsights --wait
```

### Resource group and registry

1. Resource groups -> Create: select your student subscription, `rg-novakart-develop`, and chosen region.
2. Container registries -> Create: use the same group/region and a unique registry name.
3. Check your subscription's free services before selecting the SKU. The published student offer currently lists a Standard registry for eligible new customers for 12 months. Use Standard if covered; otherwise select Basic to reduce ongoing cost.
4. Select **RBAC Registry Permissions**, not the repository ABAC permissions mode, for the `AcrPull`/`AcrPush` assignments below.
5. Keep registry administrator credentials disabled. Allow public network access for GitHub-hosted runners.
6. Copy the registry's actual login server from Overview; do not assume its hostname.

### Managed identities and image permissions

1. Managed Identities -> Create: create both identities in the resource group and region.
2. Registry -> Access control (IAM) -> Add role assignment: assign `AcrPull` to `id-novakart-acrpull` and `AcrPush` to `id-novakart-github`.
3. Check ARM audience token authentication:

```bash
az acr config authentication-as-arm show --registry '<registry-name>'
# Run only if disabled:
az acr config authentication-as-arm update --registry '<registry-name>' --status enabled
```

### Container Apps environment and bootstrap application

1. Container Apps -> Create: choose the subscription/group, application `ca-novakart-api`, and create environment `cae-novakart-develop` in the same region.
2. Use the standard workload-profiles environment with the **Consumption** profile. Do not add Dedicated profiles.
3. Use default networking. No VNet, NAT gateway, private endpoint, or database firewall changes are needed for this public TLS Neon connection.
4. Enable Azure Monitor logs with a Log Analytics workspace. Choose the minimum supported retention and a small daily ingestion cap (for example 0.1 GB). Reaching the cap reduces diagnostics; it does not cap all Azure charges.
5. Keep portal-generated GitHub continuous deployment disabled.
6. Use Azure's public quickstart image temporarily. Leave ingress disabled. This creates the app before the private API image exists.
7. Set CPU `0.25`, memory `0.5 GiB`, minimum replicas `0`, maximum replicas `1`, and single-revision mode. If the portal creates an HTTP scaling rule, use concurrency 10.
8. After creation, Identity -> User assigned -> Add `id-novakart-acrpull`.
9. Copy this identity's **resource ID** and configure registry authentication in Cloud Shell:

```bash
az extension add --name containerapp --upgrade --yes
az containerapp registry set   --name ca-novakart-api   --resource-group rg-novakart-develop   --server '<actual-acr-login-server>'   --identity '<acrpull-identity-resource-id>'
```

### Secrets and application configuration

1. Container App -> Secrets -> Add: name `db-connection-string`, value your private Neon Npgsql connection string.
2. Containers -> Edit and deploy -> Environment variables: set the following, preserving the image-pull identity:

| Variable                             | Value                                      |
| ------------------------------------ | ------------------------------------------ |
| `ASPNETCORE_ENVIRONMENT`             | `Develop`                                  |
| `DOTNET_ENVIRONMENT`                 | `Develop`                                  |
| `ASPNETCORE_HTTP_PORTS`              | `8080`                                     |
| `DB_CONNECTION_STRING`               | Secret reference to `db-connection-string` |
| `Database__ApplyMigrationsOnStartup` | `true`                                     |

3. Keep port 8080 for the real API. Do not configure a container startup command: the image has an entrypoint.
4. Before enabling the first API deployment, open Containers -> Edit and deploy -> Health probes and configure the following probes on the main container:

| Probe     | Protocol | Port   | Path           | Initial delay (seconds) | Interval (seconds) | Timeout (seconds) | Failure threshold |
| --------- | -------- | ------ | -------------- | ----------------------- | ------------------ | ----------------- | ----------------- |
| Startup   | TCP      | `8080` | Not applicable | `1`                     | `1`                | `3`               | `240`             |
| Liveness  | HTTP     | `8080` | `/health/live` | `3`                     | `30`               | `5`               | `3`               |
| Readiness | HTTP     | `8080` | `/health`      | `3`                     | `30`               | `5`               | `3`               |

5. Keep the success threshold at `1` and save/deploy the configuration. Startup allows approximately four minutes for migrations. Liveness checks the running process; readiness additionally checks PostgreSQL, so database outages do not trigger process restarts.
6. The temporary quickstart image may become unhealthy with these API-specific probes. This is expected until the first workflow release replaces it with the API image. Do not use the bootstrap image's health as the API acceptance check.

Runtime variables, probes, secret references, and scaling are configured once in Azure and maintained there. The workflow updates the image and HTTPS ingress; it does not recreate these settings or require a Python helper or generated deployment file. When changing these settings later, edit and deploy the Azure configuration before the next release.

## 5. Connect GitHub with OIDC

1. Open `id-novakart-github` -> Federated credentials -> Add credential -> GitHub Actions scenario.
2. Enter the exact GitHub owner/repository. Entity type: **Environment**; environment: `develop` (lowercase, matching the workflow).
3. Verify issuer `https://token.actions.githubusercontent.com`, audience `api://AzureADTokenExchange`, subject `repo:<owner>/<repository>:environment:develop`. The subject must match the token GitHub issues for this workflow.
4. Record the deployment identity's **client ID**, tenant ID, and subscription ID. Client ID is different from resource ID and principal/object ID.
5. On the Container App -> IAM, assign **Container Apps Contributor** to `id-novakart-github`, scoped to this app. Confirm its registry `AcrPush` role. Wait several minutes for propagation. The workflow updates existing infrastructure and does not assign roles.
6. In GitHub Settings -> Environments -> develop -> Environment variables, add:

| Variable                   | Value                                |
| -------------------------- | ------------------------------------ |
| `AZURE_CLIENT_ID`          | GitHub deployment identity client ID |
| `AZURE_TENANT_ID`          | Directory tenant ID                  |
| `AZURE_SUBSCRIPTION_ID`    | Student subscription ID              |
| `AZURE_RESOURCE_GROUP`     | `rg-novakart-develop`                |
| `AZURE_CONTAINER_APP_NAME` | `ca-novakart-api`                    |
| `AZURE_ACR_NAME`           | Registry resource name               |
| `AZURE_ACR_LOGIN_SERVER`   | Actual registry login server         |

These identifiers are not passwords. The API workflow no longer uses `AZURE_WEBAPP_NAME` or a manually configured `API_BASE_URL`. No database password goes into GitHub. Set both .NET runtime environment variables to `Develop` in Azure as shown above; image updates preserve these values rather than enforcing them on every release. The GitHub environment is lowercase `develop`, while the custom .NET runtime name remains `Develop`. If federation was already configured for another environment or spelling, update its subject and configure the matching GitHub environment before deploying; editing this repository does not rename existing Azure resources or GitHub settings.

## 6. First release and acceptance checks

1. Confirm Azure has the runtime variables, secret reference, probes, and scaling settings from section 4, then change the **repository** variable `API_DEPLOY_ENABLED` to `true`.
2. Actions -> API - validate and deploy -> Run workflow -> select `main`.
3. Validation must pass before deployment. Confirm ACR contains `novakart-api:<full-commit-sha>`.
4. Deployment updates the image using `az containerapp update --image`, preserving Azure-configured runtime variables, secret references, probes, and scaling. It also enables external ingress to port 8080 and disables insecure HTTP. The initial image/ingress transition can briefly be unavailable.
5. It waits up to approximately eight minutes for the exact new revision to become healthy, then performs retried HTTP checks. The job summary records the image, revision, and generated HTTPS origin even when a later check fails.
6. Open the URL from that summary and verify:

```bash
curl --fail 'https://<app-hostname>/health/live'
curl --fail 'https://<app-hostname>/health'
curl --fail 'https://<app-hostname>/api/v1/products?pageSize=1'
```

All must return 200. An empty product list is valid; no development seed SQL runs. `/health` checks database connectivity; `/health/live` checks HTTP responsiveness without PostgreSQL. Hosted Develop Scalar/OpenAPI is intentionally disabled. No browser CORS policy is added; current frontend API calls are intended to originate on the Next.js server.

7. In the deployed revision, confirm the image matches the workflow commit and the configured runtime variables, secret references, probes, and scaling settings were retained. Check logs for successful migration completion. Restart the API and verify migrations are not reapplied and data persists. Allow scale-to-zero, then verify a new request wakes the service successfully.
8. For frontend integration, set its server-side `API_URL` to `https://<app-hostname>/api/v1` and redeploy the frontend separately.
9. Review Azure costs and Neon usage after the first day and again after several days. Avoid frequent external uptime checks, which can keep compute awake.

Subsequent merges to `main` deploy automatically. Other branches do not deploy. Deployments are serialized, but concurrency can replace older pending jobs; it does not guarantee releasing every intermediate commit.

## 7. Migrations, diagnosis, rollback, and retirement

### Database migrations

From the repository root, generate migrations against a **local** connection string:

```bash
dotnet tool restore
dotnet ef migrations add DescribeChange --project apps/api/NovaKart.Infraestructure --startup-project apps/api/NovaKart.WebApi --output-dir Persistence/Migrations
```

Set `DB_CONNECTION_STRING` locally for the design-time factory. Commit the migration, designer, and model snapshot. CI rejects model changes missing a migration. The hosted Develop API uses compiled migrations and does not need the EF CLI.

Startup migrations run before the API listens. Failure stops startup. Runtime credentials therefore need schema modification privileges. Even with maximum replicas one, revisions may overlap during a release: keep migrations compatible with the old code, review destructive changes, and export valuable data before those changes. A separate migration job and a restricted runtime role are future improvements.

### Troubleshooting

- OIDC: check exact environment subject, client/tenant/subscription IDs, Actions policy, role scopes, and propagation.
- Image pull: check `AcrPull`, RBAC registry mode, ARM authentication, registry hostname, image tag, and assigned pull identity.
- Startup: use Container Apps Log stream and system logs; check connection string syntax, TLS, Neon role permissions, migration failures, memory, port 8080, and probes.
- Readiness: PostgreSQL sleep or connection failure can temporarily return 503; liveness should remain 200 once the application is running.
- A failed smoke check does not automatically roll back. Inspect the live revision and logs before retrying. Never print secrets to diagnose a failure.

### Rollback

Keep several known-good image tags. Set the app image back to a known-good commit:

```bash
az containerapp update   --name ca-novakart-api   --resource-group rg-novakart-develop   --image '<acr-login-server>/novakart-api:<known-good-sha>'
```

Repeat health/product checks, then revert or fix the bad commit before the next deployment. Application rollback does not reverse schema migrations; prefer a corrective forward migration. Database restore requires a separately reviewed recovery action.

### Maintenance and cleanup

Rotate the Neon password in the Azure secret, then restart the active revision so containers receive the new value. Remove old ACR tags while preserving known-good releases. Track student renewal and expiring free registry eligibility. Retiring the app requires deleting its Azure resource group and separately deleting its Neon project. Verify any separately located Log Analytics workspace is also removed if no longer needed.

## References

- [Container Apps GitHub deployment](https://learn.microsoft.com/en-us/azure/container-apps/github-actions)
- [Managed identity image pulls](https://learn.microsoft.com/en-us/azure/container-apps/managed-identity-image-pull)
- [GitHub OIDC with Azure](https://learn.microsoft.com/en-us/azure/developer/github/connect-from-azure-openid-connect)
- [Container Apps probes](https://learn.microsoft.com/en-us/azure/container-apps/health-probes)
- [Azure for Students and free services](https://azure.microsoft.com/en-us/free/students/)
- [Container Apps pricing](https://azure.microsoft.com/en-us/pricing/details/container-apps/)
- [Neon .NET connections](https://neon.com/docs/guides/dotnet-npgsql)
- [Neon plan limits](https://neon.com/docs/introduction/plans)

---

## Frontend: optional Develop integration

The API workflow deploys only the API. If you connect a Vercel frontend to this Develop API, use a Vercel Preview deployment and set its Preview variables:

| Name                   | Value                                                             |
| ---------------------- | ----------------------------------------------------------------- |
| `API_URL`              | Develop API HTTPS origin plus `/api/v1`, without a trailing slash |
| `AUTH_SECRET`          | A private generated secret for this environment                   |
| `AUTH_URL`             | Stable HTTPS origin assigned to the frontend deployment           |
| `NEXT_PUBLIC_SITE_URL` | Same frontend HTTPS origin                                        |

Keep `API_URL` server-side. For this monorepo, select Next.js, root `apps/web`, and include source files outside the root for shared packages. The root package manager is pnpm 9.0.0. Frontend deployment automation is separate from this API workflow; do not expect this workflow to publish Vercel previews.

## Current limits

Product listings currently use fixtures, `API_MODE` does not implement a switch, and Auth.js has no configured login providers. Deployment does not add live catalog integration or login. Browser CORS, additional environments, custom domains, and zero-downtime schema changes require separate work.
