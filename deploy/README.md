# Weave Fleet — Infrastructure Deployment Guide

## Deployment Contracts

| Contract | Requirement | Reference Implementation |
|----------|-------------|--------------------------|
| Single-node host | Linux VM, 2 GB+ RAM, .NET 10 runtime, harness binary, git | AWS Lightsail, Ubuntu 22.04 LTS |
| TLS reverse proxy | HTTPS termination with auto-renewable certificate, proxy to localhost app port | Caddy with automatic HTTPS |
| Process supervision | Auto-restart, journal/structured logging, low-privilege service account, resource limits | systemd unit file |
| Persistent key ring | Directory for ASP.NET Core Data Protection XML keys, survives restarts/deploys | `/opt/fleet/data/keys/` |
| Runtime confinement | Harness processes cannot read Fleet DB, key ring, deployment secrets, or other users' workspaces | Restricted service account / filesystem ACLs |

---

## Quick Start (Automated)

The only manual step is provisioning the Lightsail instance. Everything else is automated.

### 1. Provision Host

1. Create Lightsail instance: Ubuntu 22.04 LTS, 2 GB+ RAM
2. Attach a static IP
3. Open security group ports: **80** (HTTP/ACME), **443** (HTTPS), **22** (SSH)
4. Create or upload an SSH key pair
5. Point your DNS A record to the static IP

### 2. Get the Host Public Key

Obtain the server's SSH host public key **once** and store it as a GitHub Secret. This pins the key and prevents MITM attacks:

```bash
ssh-keyscan <static-ip>
# Copy the full line, e.g.: 1.2.3.4 ecdsa-sha2-nistp256 AAAA...
```

### 3. Configure GitHub Secrets

Go to **Settings → Secrets and variables → Actions** and add:

| Secret | Description | How to obtain |
|--------|-------------|---------------|
| `FLEET_SSH_KEY` | Private SSH key for deploy user | Your Lightsail `.pem` key contents |
| `FLEET_SSH_HOST_PUBLIC_KEY` | Server's SSH host public key line | `ssh-keyscan <host>` output |
| `FLEET_HOST` | SSH target | `ubuntu@<static-ip>` |
| `FLEET_DOMAIN` | Domain name | `fleet.example.com` |
| `FLEET_AUTH_AUTHORITY` | OIDC issuer URL | From your OIDC provider (e.g. Clerk) |
| `FLEET_AUTH_CLIENT_ID` | OIDC client ID | From your OIDC provider |
| `FLEET_AUTH_CLIENT_SECRET` | OIDC client secret | From your OIDC provider |

### 4. Run the Bootstrap Workflow

Go to **Actions → Bootstrap → Run workflow** and provide:
- **host**: `ubuntu@<static-ip>`
- **domain**: `fleet.example.com`
- **host_public_key**: The full line from `ssh-keyscan` (e.g. `1.2.3.4 ecdsa-sha2-nistp256 AAAA...`)

This single workflow:
1. Installs .NET 10, Caddy, opencode on the server
2. Templates and installs the Caddyfile with your domain
3. Installs the systemd unit and provisions `fleet.env` securely
4. Enables fleet (but does not start it — app doesn't exist yet)
5. Starts Caddy (begins TLS provisioning)
6. Validates infrastructure
7. Builds and deploys the application
8. Starts fleet and runs post-deploy validation

After the workflow succeeds:
- `curl https://<domain>/healthz` → 200
- `curl https://<domain>/api/config/client` → 401 (confirms auth is enforced)

### 5. Ongoing Deploys

Push to `main` → CI runs → on success, deploy triggers automatically.

---

## Manual Bootstrap

For operators who prefer the command line over GitHub Actions:

```bash
# Step 0: Pin the server's host public key (required — no TOFU)
# Run once; stores the key so SSH enforces it on all future connections
ssh-keyscan <static-ip> >> ~/.ssh/known_hosts

# Step 1: Set required variables
export FLEET_HOST=ubuntu@<static-ip>
export FLEET_DOMAIN=fleet.example.com
export Fleet__Auth__Authority=https://your-issuer.example.com
export Fleet__Auth__ClientId=your-client-id
export Fleet__Auth__ClientSecret=your-client-secret
# SSH_OPTS must include the private key AND StrictHostKeyChecking=yes
export SSH_OPTS="-i ~/.ssh/your-key.pem -o StrictHostKeyChecking=yes"

# Step 2: Bootstrap infrastructure
bash deploy/bootstrap.sh

# Step 3: Deploy application (first deploy)
bash deploy/deploy.sh
```

`bootstrap.sh` installs all dependencies and configures the server.
`deploy.sh` builds the application locally and delivers it to the server.

> **Important**: `SSH_OPTS` must include `-o StrictHostKeyChecking=yes` together with a pre-populated `~/.ssh/known_hosts` entry (step 0). Without this, SSH may fall back to interactive host-key prompts or TOFU. In GitHub Actions, the `setup-ssh` action handles this automatically.

---

## Deploying

### Automatic (recommended)

Push to `main`. The deploy workflow triggers automatically after the CI workflow (`test`, `client`, `e2e` jobs) succeeds.

### Manual dispatch

Go to **Actions → Deploy → Run workflow**. The workflow runs immediately (CI gate bypassed — operator takes responsibility).

### Local

```bash
FLEET_HOST=ubuntu@<host> \
FLEET_DOMAIN=<domain> \
SSH_OPTS="-i ~/.ssh/your-key.pem -o StrictHostKeyChecking=yes" \
bash deploy/deploy.sh
```

### Skip rebuild (deploy existing artifacts)

```bash
SKIP_BUILD=1 \
FLEET_HOST=ubuntu@<host> \
FLEET_DOMAIN=<domain> \
SSH_OPTS="-i ~/.ssh/your-key.pem -o StrictHostKeyChecking=yes" \
bash deploy/deploy.sh
```

`SKIP_BUILD=1` requires `publish/` to already contain built artifacts. Fails with a clear error if empty.

---

## Release Management

Each deploy creates a timestamped release directory:

```
/opt/fleet/releases/
  20260411-143022/          ← prior release (preserved)
  20260411-150515/          ← current release
/opt/fleet/app              ← symlink → current release
```

The `current` symlink is atomically swapped on each deploy. The last 3 releases are kept; older releases are pruned automatically.

`deploy.sh` verifies release readiness in two phases:

1. **Local app health** on the host via `http://127.0.0.1:8080/healthz`
2. **External HTTPS health** via `https://<domain>/healthz`

**On health check failure**: `deploy.sh` auto-rolls back to the previous release and dumps the last 50 journal lines for debugging. External health failures also dump the last 50 Caddy journal lines to help diagnose TLS / reverse-proxy readiness issues.

---

## Rollback

### Automatic (on failed deploy)

If the local app health check or the external HTTPS health check fails, `deploy.sh` automatically repoints the symlink to the previous release and restarts the service.

### Manual rollback

```bash
ROLLBACK=1 \
FLEET_HOST=ubuntu@<host> \
SSH_OPTS="-i ~/.ssh/your-key.pem -o StrictHostKeyChecking=yes" \
bash deploy/deploy.sh
```

### Manual symlink swap (SSH)

```bash
ssh ubuntu@<host>
sudo systemctl stop fleet
sudo ln -sfn /opt/fleet/releases/<timestamp> /opt/fleet/app
sudo systemctl start fleet
```

> **Database migrations**: SQLite migrations are forward-only. If a migration has destructive effects, restore from backup before rolling back the binary.

---

## SSH Host Key Pinning

SSH connections use a **pre-pinned** host public key. There is no trust-on-first-use (TOFU) and no `StrictHostKeyChecking=accept-new`.

### Obtain the key (one-time setup)

```bash
ssh-keyscan <static-ip>
# Store the full output line in FLEET_SSH_HOST_PUBLIC_KEY GitHub Secret
```

### Update after server reprovisioning

If the Lightsail instance is replaced, the host key changes and all deploys will fail with a key mismatch error. To update:

1. Run `ssh-keyscan <new-host>` to get the new key
2. Update the `FLEET_SSH_HOST_PUBLIC_KEY` GitHub Secret
3. For bootstrap, provide the new key as the `host_public_key` dispatch input

---

## Task 7: Runtime Confinement Model

Weave Fleet uses a service account–based confinement model:

### Fleet Service Account (`fleet` user)
- Owns `/opt/fleet/` (app, data, keys) and `/data/workspaces/`
- Runs the Fleet backend process
- Cannot escalate privileges (`NoNewPrivileges=yes`)
- No capability set (`CapabilityBoundingSet=`)

### Harness Process Confinement

Harness child processes (opencode, claude, etc.) are spawned by the Fleet service account. To prevent harness processes from reading Fleet DB, key ring, and deployment secrets:

**Option A — Dedicated runtime user (recommended)**:
1. Create a restricted user: `sudo useradd --system --no-create-home --shell /bin/false fleet-runtime`
2. Configure harness processes to run as `fleet-runtime` via systemd transient units or `su -s /bin/sh fleet-runtime -c "opencode ..."`
3. Apply filesystem ACLs:
   ```bash
   # Fleet DB — deny fleet-runtime read
   setfacl -m u:fleet-runtime:--- /opt/fleet/data/weave-fleet.db
   # Key ring — deny fleet-runtime read
   setfacl -m u:fleet-runtime:--- /opt/fleet/data/keys/
   # Deployment secrets — deny fleet-runtime read
   setfacl -m u:fleet-runtime:--- /opt/fleet/data/fleet.env
   ```

**Option B — Workspace-scoped ACLs**:
Each user workspace under `/data/workspaces/` is accessible only to the fleet service account. Harness processes write only to their assigned workspace subdirectory.

---

## Backup, Restore

### Backup

**Manual backup**:
```bash
# Stop service, backup SQLite DB and Data Protection keys
sudo systemctl stop fleet
sudo cp /opt/fleet/data/weave-fleet.db /opt/fleet/backups/weave-fleet-$(date +%Y%m%d).db
sudo tar czf /opt/fleet/backups/keys-$(date +%Y%m%d).tar.gz /opt/fleet/data/keys/
sudo systemctl start fleet
```

**Online backup (WAL mode)**:
```bash
sqlite3 /opt/fleet/data/weave-fleet.db ".backup /opt/fleet/backups/weave-fleet-$(date +%Y%m%d).db"
```

### Restore

```bash
sudo systemctl stop fleet
sudo cp /opt/fleet/backups/weave-fleet-<DATE>.db /opt/fleet/data/weave-fleet.db
sudo chown fleet:fleet /opt/fleet/data/weave-fleet.db
sudo tar xzf /opt/fleet/backups/keys-<DATE>.tar.gz -C /
sudo chown -R fleet:fleet /opt/fleet/data/keys/
sudo chmod 700 /opt/fleet/data/keys/
sudo systemctl start fleet
```

> **WARNING**: Restoring keys from a different date than the DB backup will cause credentials encrypted with newer keys to be unreadable. Always restore DB and keys from the same backup set.

---

## Environment Variables

`fleet.env` is provisioned automatically by `bootstrap.sh`. It is stored at `/opt/fleet/data/fleet.env` with permissions `600`, owned by `fleet:fleet`.

| Variable | Description |
|----------|-------------|
| `Fleet__Auth__Enabled` | `true` — enables OIDC authentication |
| `Fleet__Auth__Authority` | OIDC issuer URL |
| `Fleet__Auth__ClientId` | OIDC client ID |
| `Fleet__Auth__ClientSecret` | OIDC client secret |
| `Fleet__Auth__AllowedOrigins__0` | `https://<FLEET_DOMAIN>` |
| `Fleet__Cloud__Enabled` | `true` — enables managed workspaces |
| `Fleet__Cloud__WorkspaceRoot` | `/data/workspaces` |
| `Fleet__DataProtection__KeyPath` | `/opt/fleet/data/keys` |
| `Fleet__DatabasePath` | `/opt/fleet/data/weave-fleet.db` |

`ASPNETCORE_ENVIRONMENT` and `ASPNETCORE_URLS` are set in `fleet.service` via `Environment=` directives and are NOT in `fleet.env`.

---

## Post-Deploy Smoke Test Checklist

After every deploy:
- [ ] `curl -sf https://<domain>/healthz` → 200
- [ ] `curl -sf https://<domain>/readyz` → 200
- [ ] `curl -so /dev/null -w '%{http_code}' https://<domain>/api/config/client` → 401 (auth enforced)
- [ ] Sign in via OIDC → redirects correctly, session cookie set
- [ ] `GET /api/user/me` → 200 with valid user object
- [ ] `sudo journalctl -u fleet -n 20` → no ERROR or FATAL lines

---

## Troubleshooting

**TLS cert delay**: Caddy provisioning can take time after DNS propagates. `deploy.sh` now verifies local app health first, then retries the external HTTPS health check for up to 120s. If TLS is still not ready, wait and retry.

**Service won't start**: Check `sudo journalctl -u fleet -n 50`. Common causes: missing `fleet.env`, wrong binary path (symlink issue), or missing .NET runtime.

**Auth misconfigured**: `curl https://<domain>/api/config/client` should return 401. If it returns 200, auth is disabled — check that `Fleet__Auth__Enabled=true` is in `fleet.env`.

**Host key mismatch**: Deploys fail with "WARNING: REMOTE HOST IDENTIFICATION HAS CHANGED". Update `FLEET_SSH_HOST_PUBLIC_KEY` secret with the new key from `ssh-keyscan <host>`.

**Manual service check**:
```bash
ssh ubuntu@<host>
sudo systemctl is-active fleet          # should be: active
sudo systemctl is-enabled fleet         # should be: enabled
sudo journalctl -u fleet -f             # live logs
ls -la /opt/fleet/app                   # should be a symlink
readlink /opt/fleet/app                 # should point to a release dir
ls /opt/fleet/releases/                 # timestamped release dirs
```
