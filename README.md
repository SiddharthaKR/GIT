# PrivateGit — C# internal Git dashboard

An ASP.NET Core 10 Razor Pages application for a small, trusted internal team. Browse repositories, branches, version tags, paginated commit history and historical text files. Open commits for green/red unified diffs with line numbers and exact text line totals, or compare two snapshots. Administrators create bare repositories. The default deployment is a Linux central server with Linux clients using OpenSSH. Windows clients can be added later, optionally. There are no cloud services, CDN assets, external fonts, Node build, NuGet package dependencies, or internet requirements at runtime.

**Per-user, per-repository permissions.** For every repository, an administrator gives each user *No access*, *Read* (clone, pull, browse) or *Read & write* (also push). The same permission is enforced in the dashboard and over SSH. Each SSH key is linked to exactly one user, and a permission gate (`git-serve`, run by OpenSSH `ForceCommand`) checks every clone, fetch and push. Admins have full access. Users without a grant cannot see that a repository exists.

**Hidden backup of main:** every push to main is also copied, with timestamped snapshots, into a backup area no user can see. It can optionally be copied to a NAS share too. Admins restore it with `admin.sh backup-list` / `backup-restore`, and `PrivateGit__MainBackup=false` turns it off.

For our lab network (Ubuntu 22.04, servers addressed by IP, no IT certificate, corporate proxy), follow **[OFFICE-SETUP.md](OFFICE-SETUP.md)**. Read [SECURITY.md](SECURITY.md) for the exact access model and limitations. This does not promise “complete security.”

## Guided installation (recommended)

See [EASY-SETUP.md](EASY-SETUP.md) for a plain-language walkthrough and [ARCHITECTURE.md](ARCHITECTURE.md) for the system design. Extract the Linux deployment ZIP on a fresh Ubuntu 22.04 or 24.04 server and run `sudo bash install.sh`. The wizard configures application/SSH identities, directories, HTTPS, client-network restrictions, the SSH permission gate and the systemd service, then creates the first administrator interactively. The server can be addressed by a fixed IP (e.g. `172.20.1.126`) or an internal DNS name. HTTPS uses either a private PrivateGit CA created on the server or an IT-issued certificate; the installer checks the certificate chain/key match and package architecture. It can install missing OS packages from configured apt repositories during setup; use `--offline` to prohibit downloads. The application makes no external API/cloud calls during normal operation.

Firewall access to 443/22 from approved clients is still required. DNS and an IT certificate are optional; `request-certificate.sh` generates a local CSR if IT will sign one. After installation: `sudo bash /opt/privategit/admin.sh user-add NAME User` creates a person, `sudo bash /opt/privategit/add-client.sh NAME /absolute/path/key.pub` links their SSH key, and the dashboard's **Access** tab (or `admin.sh access-grant REPO NAME read|write`) grants repositories. Unrelated existing identities/installation paths are refused; use the manual guide below with IT if the server already has a `git` account. Installer-owned reruns preserve state and back up configuration/application files; account/repository backups remain a separate responsibility.

## Files

- `PrivateGit/`: application (C#, Razor, local CSS). `Services/AccessStore.cs` = permissions, `Services/GitServe.cs` = SSH permission gate.
- `PrivateGit.Tests/`: dependency-free security/integration test runner using real Git and HTTP.
- `deploy/`: Linux systemd, Nginx, SSH, and environment configuration templates.
- `install.sh`, `admin.sh` (users + permissions), `add-client.sh` (SSH keys per user), `request-certificate.sh`.
- `scripts/publish.ps1`: transferable self-contained Linux package builder.
- `scripts/test-installer.sh` (non-root script checks) and `scripts/test-ssh-gate.sh` (root, Linux: real sshd + Git end-to-end permission test).

## Optional development on Windows (not needed for Linux installation)

Install the .NET **10 SDK** and Git for Windows. If using the SDK installed locally during development, replace `dotnet` with `..\.dotnet\dotnet.exe` from the PrivateGit directory.

```powershell
cd PrivateGit
$env:ASPNETCORE_ENVIRONMENT = 'Development'
dotnet run -- user-add alice Admin
# Enter and confirm a unique password, then enroll the displayed TOTP secret
# in an offline-capable authenticator. Do not put secrets in logs or source.
dotnet run --no-launch-profile --urls http://127.0.0.1:5080
```

Open http://127.0.0.1:5080. No demo account is installed. Development HTTP is allowed only for this local workflow; do not expose development mode to your network. The default clone address is intentionally a placeholder until configured.

From the workspace root, run tests:

```powershell
./scripts/test.ps1
```

The runner builds the application, creates disposable accounts/repos, starts a loopback test server, checks security and normal workflows, then removes its fixtures. It does not change your application accounts. Tests must run from the workspace root; the script does this for you.

## Build the transferable package

On your development PC, from the workspace root:

```powershell
./scripts/publish.ps1 -Runtime linux-x64
# Or: ./scripts/publish.ps1 -Runtime linux-arm64
# Only when Windows client instructions are requested:
# ./scripts/publish.ps1 -Runtime linux-x64 -IncludeWindowsClientGuide
```

Transfer `artifacts/privategit-linux-x64.zip` to your server. Self-contained publishing downloads Microsoft's target runtime pack during the build; the server does not need a .NET installation. It still needs an OS supported by .NET 10, Git, OpenSSH, Nginx and native .NET OS dependencies. Ubuntu 22.04 or 24.04 x64 is the intended deployment target (no `libicu` needed: the app uses invariant globalization); use the appropriate binary for ARM64. Do not transfer `.dotnet`, `.venv`, test data, development App_Data, or development credentials.

To build on Linux with the .NET 10 SDK instead:

```bash
dotnet build PrivateGit.Tests/PrivateGit.Tests.csproj -c Release
dotnet PrivateGit.Tests/bin/Release/net10.0/PrivateGit.Tests.dll
dotnet publish PrivateGit/PrivateGit.csproj -c Release -r linux-x64 \
  --self-contained true -p:PublishSingleFile=true -p:DebugType=None -o artifacts/linux-x64
```

## Linux server deployment (fresh dedicated installation)

These are administrator commands for the **central server**, not your development PC. They assume `/opt/privategit` is a new application location and that `gitdash`, `git`, and `gitrepos` are new identities. If those already exist, inspect their purpose and adapt rather than replacing existing identities or SSH configuration.

### 1. Install dependencies, identities and directories

```bash
sudo apt update
sudo apt install -y git openssh-server nginx unzip ca-certificates
sudo groupadd --system gitrepos
sudo useradd --system --gid gitrepos --home-dir /var/lib/privategit \
  --shell /usr/sbin/nologin gitdash
# --disabled-password leaves public-key login available on Ubuntu.
sudo adduser --disabled-password --gecos '' --ingroup gitrepos git
# sshd runs the PrivateGit gate (ForceCommand) through this shell; nothing else can run.
sudo usermod --shell /bin/sh git
sudo install -d -o root -g root -m 0755 /opt/privategit /etc/privategit
sudo install -d -o gitdash -g gitrepos -m 0700 /var/lib/privategit
sudo install -d -o root -g gitrepos -m 0750 /srv/privategit
sudo install -d -o gitdash -g gitrepos -m 2770 /srv/privategit/repositories
sudo install -d -o gitdash -g gitrepos -m 2750 /srv/privategit/access
sudo install -d -o root -g root -m 0700 /etc/privategit/tls
sudo systemctl enable --now ssh
```

### 2. Extract the application and configure it

Substitute the actual archive location:

```bash
sudo unzip /path/to/privategit-linux-x64.zip -d /opt/privategit
sudo chown -R root:root /opt/privategit
sudo chmod 0755 /opt/privategit/PrivateGit
sudo install -o root -g gitrepos -m 0640 \
  /opt/privategit/deploy/environment.example /etc/privategit/environment
sudoedit /etc/privategit/environment
```

Set `AllowedHosts` to the exact address people use: an internal DNS name such as `git.internal`, or the server's fixed IP such as `172.20.1.126`. Set the clone address accordingly. The app refuses placeholder production hosts/clone addresses and binds Kestrel only to `127.0.0.1:5080`. It rejects requests that do not arrive as HTTPS through the trusted local reverse proxy.

### 3. Create your administrator before starting the service

Use the same data path as the service. The CLI takes passwords interactively and never via command-line arguments.

```bash
sudo bash /opt/privategit/admin.sh user-add alice Admin
```

`admin.sh` runs the app as `gitdash` with `/etc/privategit/environment` (and pauses the dashboard for `user-*` commands). Enroll the displayed TOTP seed in your authenticator (SHA1, six digits, 30 seconds); no external account is needed. Keep clocks synchronized. Enter the seed manually; no external QR service is used. Store your password securely. The authentication store and app key directory are created only on your server.

### 4. Configure HTTPS and start the dashboard

Obtain a server certificate signed by your internal CA, including `git.internal` as a DNS SAN. Install the chain as `/etc/privategit/tls/fullchain.pem` and private key as `/etc/privategit/tls/privkey.pem`; keep the key root-owned mode 0600. Install the CA trust on every client you connect. Do not disable TLS verification or use HTTP for login.

```bash
sudo cp /opt/privategit/deploy/nginx.conf /etc/nginx/sites-available/privategit
sudoedit /etc/nginx/sites-available/privategit
# Replace hostname, certificate paths and network allowlist.
# If other virtual hosts exist, retain their intended configuration.
sudo ln -s /etc/nginx/sites-available/privategit /etc/nginx/sites-enabled/privategit
sudo nginx -t
sudo cp /opt/privategit/deploy/privategit.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now privategit
sudo systemctl reload nginx
sudo systemctl status privategit --no-pager
```

Set host/network firewall rules allowing TCP 443 and 22 only from approved client and administrator addresses; allow TCP 80 only if you need the HTTPS redirect. Never expose port 5080. Review existing firewall rules before enabling changes so you retain administrator access. The Nginx LAN allowlist supplements the firewall. Verify `nginx -T` has no alternate virtual host forwarding to this app without these controls.

Open `https://git.internal` from an allowed client, sign in with password + TOTP, and create `project`. No server console `git init` is needed for repositories created in the dashboard.

### 5. Authorize Linux Git clients

Create a unique SSH key under each Linux client identity. Install `git openssh-client` through the client's approved package source, then run:

```bash
mkdir -p ~/.ssh
ssh-keygen -t ed25519 -f ~/.ssh/id_ed25519_internal_git -C "my-client-internal-git"
cat ~/.ssh/id_ed25519_internal_git.pub
```

Copy only the `.pub` content to the administrator. On the central server, link it to that person's dashboard account (create the account first):

```bash
sudo install -o root -g gitrepos -m 0640 /dev/null /etc/privategit/authorized_keys   # once
sudo bash /opt/privategit/add-client.sh alice /path/alice.pub [--from 172.20.1.157/32]
sudo bash /opt/privategit/admin.sh access-grant project alice write
```

`add-client.sh` writes lines such as:

```text
from="172.20.1.0/24",restrict ssh-ed25519 AAAA... privategit:alice alice@126
from="172.20.1.157/32",restrict ssh-ed25519 AAAA... privategit:bob bob@157
```

The `privategit:USER` marker links the key to one person. The SSH gate reads which key actually authenticated (`ExposeAuthInfo`), maps it to the user, and allows `git-upload-pack` only with Read and `git-receive-pack` only with Read & write on that repository. Keys without a marker (the earlier shared-key format) are refused. A key can belong to only one user. For a deployment machine, create a separate `User` account with Read on the repositories it needs.

```bash
sudo cp /opt/privategit/deploy/sshd-privategit.conf /etc/ssh/sshd_config.d/60-privategit.conf
sudo sshd -t
# Inspect the effective match policy before reloading (substitute an actual IP).
sudo sshd -T -C user=git,host=git.internal,addr=192.168.1.126
sudo systemctl reload ssh
sudo ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub
```

On each client, add to `~/.ssh/config`:

```sshconfig
Host internal-git git.internal
    HostName git.internal
    User git
    IdentityFile ~/.ssh/id_ed25519_internal_git
    IdentitiesOnly yes
```

Run `chmod 700 ~/.ssh && chmod 600 ~/.ssh/config`. Verify the first SSH host-key prompt matches the fingerprint obtained through a trusted administrator channel.

```bash
git ls-remote internal-git:/srv/privategit/repositories/project.git
git clone internal-git:/srv/privategit/repositories/project.git
cd project
git config user.name "Your Name"
git config user.email "you@example.internal"
# Add project files (after configuring an appropriate .gitignore).
git add .
git commit -m "Initial commit"
git push -u origin main
```

If your Git client chose a different local branch after cloning an empty repository, use `git branch -M main` before the first push. Other clients clone the same URL; normal `git pull --ff-only` and `git push` work. A push updates the remote; clients must pull to receive changes. Interactive SSH shell login being rejected is expected.

An existing local project can use:

```bash
git remote add internal git@git.internal:/srv/privategit/repositories/project.git
git push -u internal HEAD:main
git push internal --tags
```

## Account administration and operations

### Optional Windows clients

First verify the Linux server/client setup using the Linux-only test in `EASY-SETUP.md`. Windows clients can be added later without changing the server: each gets Git, a unique SSH key and company CA trust. The separate `WINDOWS-CLIENT.md` guide is included only when publishing with `-IncludeWindowsClientGuide`; the default Linux deployment ZIP excludes it. This does not install a Windows server or require Windows components on Linux.

### Using history, versions and diffs

- **Files & history:** select a branch or tag; click a commit ID or message to open its changes. Use Older/Newer for history (20 commits per page, up to 1,000 pages). Opening a file or directory filters the history to that exact path. Renamed paths are not followed automatically.
- **Commit details:** full message, author, committer, authored/committed timestamps, parent links, changed files, additions/deletions, mode changes and green/red code rows with old/new line numbers. An initial commit compares against an empty tree. Merge commits default to the first parent; choose another parent for a different comparison.
- **Before / After:** open that file at the old/new commit. Deleted files have a Before link, new files an After link. File navigation and history pagination stay pinned to the resolved commit, so a branch moving does not silently change the snapshot you are inspecting.
- **Branches & tags:** shows who created each branch. A branch can be deleted here (or with `git push origin --delete NAME`) by its creator, while they have write access, or by an admin. The main branch is protected and tags cannot be deleted. Browse up to 200 branches and 200 directly commit-targeted lightweight/annotated tags. Tags are version labels stored in Git, not a separate release-asset publishing system. Create tags in your Git client with `git tag -a v1.0.0 -m 'Version 1.0.0'` and `git push origin refs/tags/v1.0.0`.
- **Compare changes:** use `branch:main`, `branch:feature`, `tag:v1.0.0` or full commit hashes. The comparison shows the difference between those two snapshots, not a three-dot/merge-base pull-request diff. Branch/tag prefixes resolve ambiguous names safely. Arbitrary Git revision expressions are rejected.
- **Limits:** diffs list at most 200 files, patches are bounded to 2 MiB of Git output, and each file preview to 256 KiB / 5,000 lines. Oversized output gets an explicit warning; use local Git for the full patch. Binary changes have no inline code diff. Rename detection is disabled, so a rename appears as a deletion and an addition. Source and commit/tag text are escaped and never executed.
- **Timestamps:** Git author/committer dates and UTC offsets are shown as stored in Git. They are contributor-controlled metadata, not independently recorded server push times. Annotated tags use the tagger date; lightweight tags use the commit date.

### Managing accounts

Stop the app first because account storage intentionally permits one process at a time:

```bash
`admin.sh` pauses the dashboard automatically for account changes, because account storage intentionally permits one process at a time:

```bash
sudo bash /opt/privategit/admin.sh user-add bob User      # Admin or User
sudo bash /opt/privategit/admin.sh user-reset alice Admin # new password + TOTP; also changes role
sudo bash /opt/privategit/admin.sh user-delete bob        # also removes permissions and SSH keys
sudo bash /opt/privategit/admin.sh user-list
sudo bash /opt/privategit/admin.sh access-grant REPO USER read|write
sudo bash /opt/privategit/admin.sh access-revoke REPO USER
sudo bash /opt/privategit/admin.sh access-list [REPO]
sudo bash /opt/privategit/add-client.sh --list | --revoke USER
```

`user-reset` rotates both password and authenticator seed and revokes existing sessions. Permission commands work while the dashboard runs and apply immediately to web and SSH. `Reader` accounts from earlier versions are treated as `User`. There is no emailed recovery or web-based account bootstrap.

Check `journalctl -u privategit` (logins, repository creation, permission changes), `journalctl -t privategit-ssh` (every allowed/denied SSH clone/fetch/push with user, repository and client IP), SSH logs, and Nginx logs. Configure log retention; authentication secrets are not logged by the app. This is not a tamper-proof per-ref audit trail.

Back up `/var/lib/privategit`, `/srv/privategit` (repositories and `access/access.json`), `/etc/privategit`, and your service configuration. Quiesce **both the dashboard and SSH Git writes** or use a coordinated filesystem snapshot; stopping the dashboard alone does not stop pushes. Encrypt backups, restrict access and test restoration. Set storage quotas because Git clients can grow repositories. Use `git fsck --full` in a maintenance window as an integrity check, not as a backup substitute.

For updates, publish a new package with a patched .NET 10 SDK/runtime pack, stop the service, replace **application files only** with root-owned files, retain `/var/lib` and `/srv` state, and restart. Review configuration changes separately. No auto-update or cloud connection runs on the server.

## Verification limits

The included tests exercise password/TOTP, lockout, authentication, CSRF, per-repository permissions (web and a real OpenSSH end-to-end test), traversal and command-injection rejection, XSS escaping, bare repository creation, and Git browsing against a real local Git process. Linux systemd permissions, firewall, Nginx, SSH and TLS must be validated on your actual server before production use. This is a small-team initial implementation, not an independently audited forge.
