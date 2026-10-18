# PrivateGit in our office: setup guide

This guide covers our lab network: Ubuntu 22.04 servers at 172.20.1.x (.126, .157, .158), different Linux users on each machine, and a corporate proxy. There is no internal DNS and no IT-issued certificate. It works the same on Ubuntu 24.04.

## What you get

| Item | How it works |
|---|---|
| One web dashboard | `https://172.20.1.126` (the server's IP; no DNS needed). Sign in with a password and a 6-digit authenticator code. |
| Git over SSH | `git clone git@172.20.1.126:project.git` from any approved machine. |
| Per-user, per-repository permissions | For each repository, each user has **No access**, **Read** (clone, pull, browse) or **Read & write** (also push). The same rule applies on the web and over SSH. |
| Admins | Create repositories and manage permissions. They always have full access. |
| No cloud | Nothing leaves the LAN. No external APIs, CDN, telemetry, DNS or IT certificate are needed. |

## 1. Choose the server for a project

Any of the three servers can host PrivateGit. Pick **one server per project**:

- **.126 (recommended default):** stable, always on.
- **.157 / .158:** these are DPU lab machines (OVS changes, firmware flashing, reboots). Use them only if a project should live next to that hardware.

You can install PrivateGit on more than one server. Each installation has its own repositories, users and permissions. Clients add one SSH config entry per server (section 6).

Before installing, check the chosen server:

```bash
lsb_release -d                                   # Ubuntu 22.04 or 24.04
getent passwd git gitdash; getent group gitrepos # should print nothing (installer refuses existing ones)
sudo ss -ltnp | grep -E ':(443|5080)\b'          # nothing, or nginx only, on 443; nothing on 5080
```

If another program (for example a C# web server) already uses port 443, move it to another port or use a different server for PrivateGit.

## 2. Build the package (Windows PC, once per version)

The app is a single self-contained Linux executable. The server needs no .NET install.

```powershell
cd C:\Users\siddh\Documents\Personal\Study\GIT_LOCAL
./scripts/test.ps1                      # optional: runs the app test suite
./scripts/publish.ps1 -Runtime linux-x64
```

Copy `artifacts\privategit-linux-x64.zip` to the server, for example with `scp` to `~/`.

## 3. Install (on the server)

```bash
mkdir privategit-setup && unzip privategit-linux-x64.zip -d privategit-setup
cd privategit-setup
bash install.sh --check        # read-only prerequisite check
sudo bash scripts/test-ssh-gate.sh ./PrivateGit   # optional self-test on THIS server (temporary users + private sshd on 127.0.0.1:2299, all removed afterwards)
sudo bash install.sh
```

If apt needs the corporate proxy, it uses the proxy configured for apt on that server (for example `/etc/apt/apt.conf.d/*proxy*`). `--offline` forbids downloads.

Answers for our network:

| Question | Answer |
|---|---|
| Server address | `172.20.1.126` (detected automatically; just press Enter) |
| Allowed client networks | `172.20.1.0/24`, or only the three machines: `172.20.1.126/32 172.20.1.157/32 172.20.1.158/32` |
| HTTPS certificate | `1` = private PrivateGit CA (no IT needed) |
| First administrator | your username, e.g. `siddhartha` |

You then set a 16+ character password and add the displayed secret to an authenticator app (Microsoft Authenticator, Google Authenticator, 2FAS, KeePassXC…). Type the secret by hand; no QR service is used.

## 4. Trust the private CA and bypass the proxy (each client, once)

**Linux clients (.126, .157, .158, others):**

```bash
scp ADMIN@172.20.1.126:/opt/privategit/privategit-ca.crt .
sudo cp privategit-ca.crt /usr/local/share/ca-certificates/privategit-ca.crt
sudo update-ca-certificates
# Corporate proxy: never send the Git server through it (same idea as the vLLM endpoint).
echo 'export no_proxy="$no_proxy,172.20.1.126" NO_PROXY="$NO_PROXY,172.20.1.126"' >> ~/.bashrc
```

**Windows browser:** open `privategit-ca.crt`, then choose *Install Certificate → Local Machine → Trusted Root Certification Authorities*. Add `172.20.1.126` to the proxy bypass list (*Settings → Network → Proxy → "Don't use the proxy server for these addresses"*). Firefox has its own certificate store; import the CA there as well.

SSH (port 22) is not affected by the HTTP proxy.

## 5. Add people, link their keys, grant access

On the server, use the two helpers (both need `sudo`):

```bash
# a) Dashboard account. "User" sees nothing until granted; "Admin" sees everything.
sudo bash /opt/privategit/admin.sh user-add alice User

# b) Link each of Alice's SSH public keys (one per machine she works on).
sudo bash /opt/privategit/add-client.sh alice /tmp/alice-126.pub
sudo bash /opt/privategit/add-client.sh alice /tmp/alice-157.pub --from 172.20.1.157/32   # key only usable from .157

# c) Permissions (or use the dashboard: repository → Access tab)
sudo bash /opt/privategit/admin.sh access-grant fpga-rtl alice write
sudo bash /opt/privategit/admin.sh access-grant dpu-demo alice read
sudo bash /opt/privategit/admin.sh access-list
```

Create repositories in the dashboard (Admin → "Create a repository"). It then opens the repository's Access page so you can grant people right away.

Example permission plan:

| Repository | siddhartha (Admin) | alice | bob | trainee1 |
|---|---|---|---|---|
| fpga-rtl | full | Read & write | Read | none |
| dpu-demo | full | Read | Read & write | Read |
| trainee1-sandbox | full | none | none | Read & write |

## 6. Each person: SSH key and clone (Linux)

Run this as **your own Linux user** on each machine you use:

```bash
ssh-keygen -t ed25519 -f ~/.ssh/id_ed25519_privategit -C "$USER@$(hostname)"
cat ~/.ssh/id_ed25519_privategit.pub      # send ONLY this .pub line to the admin
```

Then add this to `~/.ssh/config`, and run `chmod 600 ~/.ssh/config`:

```sshconfig
Host privategit 172.20.1.126
    HostName 172.20.1.126
    User git
    IdentityFile ~/.ssh/id_ed25519_privategit
    IdentitiesOnly yes

# Only if a second installation runs on .158:
Host privategit-158 172.20.1.158
    HostName 172.20.1.158
    User git
    IdentityFile ~/.ssh/id_ed25519_privategit
    IdentitiesOnly yes
```

```bash
ssh privategit            # prints "Hi alice. Interactive shells are disabled…" = key works
git clone privategit:fpga-rtl.git
git push                  # works only with Read & write
```

On the first connection, check the server fingerprint against what `install.sh` or `add-client.sh` printed.

Several Linux users on the same machine each make **their own** key. A key belongs to exactly one person, and the helper refuses to link one key to two users.

## 7. Working together: conflicts and project templates

PrivateGit adds nothing to Git here: conflicts are handled by plain Git on each person's machine, and the server only protects history.

- **The server accepts a push only if it builds on the latest version.** If someone pushed first, you get `rejected (fetch first)` and nothing is overwritten. Then `git pull`, fix any conflict, and `git push` again.
- **Different lines, even in the same file: merged automatically.** Only edits to the *same lines* produce a conflict. Edit the marked lines, then `git add FILE` and `git commit`.
- **Force-push is refused by the server**, so nobody can rewrite or wipe out someone else's history.
- **Deleting branches — your own only:** the server records who pushed each new branch ("created by alice" on the dashboard). Its creator can delete it with `git push origin --delete feature-x` or the **Delete** button on *Branches & tags*, as long as they still have *Read & write*. Nobody else can, except an admin, who can delete any branch. The main branch is always protected, even from admins, and tags (version labels) cannot be deleted. Branches created before this feature have no recorded creator, so only an admin can delete them.

Simple team rules:

| Rule | Why |
|---|---|
| Work on `main`; branch only for something long or risky | No branch management overhead |
| `git pull` before starting; commit and push small changes often | Small differences → rare, easy conflicts |
| Once per person: `git config --global pull.rebase true` | Unpushed commits are placed on top of the latest version; straight history |
| Never commit generated outputs (`.bit`, `.dcp`, `*.runs/`, `build/`) | They cannot be merged and are rebuilt anyway |
| Vivado: keep the project as Tcl (`write_project_tcl`, `write_bd_tcl`) + sources | Text merges cleanly; project files do not |
| One owner at a time for block designs (`.bd`) and IP (`.xci`) | Git cannot merge these safely; the template makes Git stop and ask instead of guessing |

**Templates.** Copy them into every new repository, before the first commit. They are in `templates/` in this folder and on the server in `/opt/privategit/templates/`:

| File | Copy as | For |
|---|---|---|
| `gitignore-fpga` | `.gitignore` | Vivado, Vitis (classic and unified), Vitis HLS, PetaLinux, xsim/Questa/Verilator/cocotb, MATLAB |
| `gitignore-doca-c` | `.gitignore` | DOCA / DPDK / C / C++ / CUDA (meson, CMake, make) |
| `gitattributes` | `.gitattributes` | All projects: LF line endings for Linux and Windows, binaries never merged, `.bd/.xci/.xpr` never auto-merged |

```bash
git clone privategit:fpga-rtl.git && cd fpga-rtl
scp ADMIN@172.20.1.126:/opt/privategit/templates/gitignore-fpga .gitignore
scp ADMIN@172.20.1.126:/opt/privategit/templates/gitattributes .gitattributes
git add .gitignore .gitattributes && git commit -m "Add Git templates" && git push
```

Existing repository that already tracks build outputs: after adding `.gitignore`, run `git rm -r --cached proj.runs proj.cache` (the folders that should not be tracked), then commit. This stops tracking them without deleting your local copies.

Deleted a branch by mistake? Every deletion message and log line includes the branch's last commit ID. The admin can restore it (for roughly two weeks, before Git's cleanup removes unreferenced commits):
`sudo -u gitdash git --git-dir=/srv/privategit/repositories/REPO.git branch old-branch COMMIT_ID`
To forbid all branch deletion, set `PrivateGit__AllowBranchDelete=false` in `/etc/privategit/environment` and restart the dashboard (`sudo systemctl restart privategit`).

## 8. Day-to-day cheat sheet

| Task | Command (on the server) |
|---|---|
| List users | `sudo bash /opt/privategit/admin.sh user-list` |
| New password / lost authenticator / change role | `sudo bash /opt/privategit/admin.sh user-reset alice User` |
| Remove a person completely | `sudo bash /opt/privategit/admin.sh user-delete alice` (also removes keys and permissions) |
| Who has which key | `sudo bash /opt/privategit/add-client.sh --list` |
| Remove all keys of a person | `sudo bash /opt/privategit/add-client.sh --revoke alice` |
| Grant / change / remove access | `admin.sh access-grant REPO USER read\|write`, `admin.sh access-revoke REPO USER` |
| See permissions | `admin.sh access-list [REPO]` |
| Who pushed/cloned/deleted what (SSH) | `journalctl -t privategit-ssh` |
| Hidden backups of main | `admin.sh backup-list REPO`, `admin.sh backup-restore REPO [SNAPSHOT]`, `admin.sh backup-now` (section 11) |
| Restore a deleted branch | `sudo -u gitdash git --git-dir=/srv/privategit/repositories/REPO.git branch NAME COMMIT_ID` (commit ID from the deletion message or log) |
| Dashboard log | `journalctl -u privategit` |

Permission changes apply immediately, with no restart. `user-*` commands briefly stop and restart the dashboard automatically.

## 9. Messages users may see

| Message | Meaning / fix |
|---|---|
| `This SSH key is not linked to a PrivateGit user` | The admin hasn't run `add-client.sh USER key.pub` for this key yet (or it is an old shared key from the previous version). |
| `Repository 'x' not found or alice has no access` | Wrong name, or no Read grant. Missing and forbidden repositories deliberately look the same. |
| `alice has read-only access to x` | Ask an admin for Read & write. |
| `the main branch (main) cannot be deleted` | Intended: the main branch is protected. |
| `tag v1.0 is a version label and cannot be deleted` | Intended: ask an admin if a tag really must go. |
| `branch x was created by alice. Only its creator or an administrator can delete it.` | Intended: ask alice or an admin. |
| `Permission denied (publickey)` | The key isn't linked, was revoked, or is used from a machine outside its `--from` limit. |
| Browser certificate warning | The private CA isn't trusted on this client yet (step 4). |
| Browser can't connect / proxy error page | Add the server IP to the proxy bypass list (step 4). |

## 10. Upgrading from the previous PrivateGit version

Rerun `sudo bash install.sh` from the new ZIP. Accounts and repositories are kept. After the upgrade:

- Old shared SSH keys (not linked to a user) are **refused**. Re-link each one with `add-client.sh USER key.pub`, which replaces the old line. `add-client.sh --list` shows how many old lines are left.
- Old `Reader` accounts become `User` accounts with **no access** until you grant repositories.
- The git account's shell changes from `git-shell` to the PrivateGit gate (`ForceCommand`). Only clone, fetch, pull and push of permitted repositories are possible.

## 11. Backups

### Automatic hidden backup of `main` (on by default)

Every push that changes a repository's main branch also copies `main` into a separate backup repository in `/srv/privategit/backups/REPO.git`. It records a timestamped snapshot each time, so **every past version of main stays recoverable**.

- **Hidden:** it doesn't appear in the dashboard. The SSH gate serves only `/srv/privategit/repositories`, so no clone URL reaches it (not even an admin's), and normal Linux users cannot list the folder (`root:gitrepos 2770`).
- **Harmless if it fails:** if the backup can't be written, the push still succeeds with a warning, and the failure is logged (`journalctl -t privategit-ssh`).
- **Turn it off:** set `PrivateGit__MainBackup=false` in `/etc/privategit/environment`. This takes effect on the next push, with no restart. Existing backups are kept. Turn it back on with `true`, then run `admin.sh backup-now`.

Admin commands (on the server):

| Task | Command |
|---|---|
| List snapshots of main (newest first) | `sudo bash /opt/privategit/admin.sh backup-list fpga-rtl` |
| Get an older main back **as a new branch** (main is never changed) | `sudo bash /opt/privategit/admin.sh backup-restore fpga-rtl 20261017T112424Z-d59a695d` → creates `restored-main-<time>`; review it, then merge |
| Repository deleted or damaged: recreate it from the latest backup | Move the damaged folder away, then `sudo bash /opt/privategit/admin.sh backup-restore fpga-rtl` (permissions apply again automatically) |
| Back up all repositories now (also done by `install.sh`) | `sudo bash /opt/privategit/admin.sh backup-now` |

The backup covers **main only**, as you asked. Other branches, branch-creator records, user accounts and permissions are not in it. For those, also keep the full server backup below.

### Optional: a copy of `main` on the Windows NAS

The NAS gets a **copy** of each repository's `main`, written in the background after every push to `main`. The live repositories stay on the Linux server. Git's locking isn't reliable over Windows file shares, and an offline NAS would otherwise block everyone's pushes.

**The NAS can never block anything else:**

| If the NAS is… | Effect |
|---|---|
| Off, asleep, unreachable or slow | Pushes, clones, the dashboard and the server backup carry on normally. The NAS copy runs fully detached, at most one per repository at a time, with a hard 10-minute limit, and failures are just logged (`journalctl -t privategit-ssh`) |
| Unreachable during `admin.sh backup-now` | Skipped after a 10-second check; server backups are still done |
| Unreachable during install or upgrade | Not touched at all (the installer backs up to the server only) |
| Unreachable at boot | The mount options `nofail,x-systemd.automount,soft` let the server start normally |

When the NAS comes back, the next push to `main` (or `admin.sh backup-now`) brings its copy up to date.

1. On the NAS, create a share (or a folder in an existing share), e.g. `\\NAS\GitBackup`, and an account that can write to it.
2. On the Git server, mount it for the git accounts:

   ```bash
   sudo apt install -y cifs-utils
   sudo install -m 0600 /dev/null /etc/privategit/nas-credentials
   sudoedit /etc/privategit/nas-credentials        # username=NASUSER  password=...  domain=WORKGROUP
   sudo mkdir -p /mnt/privategit-nas
   echo '//NAS-IP/GitBackup /mnt/privategit-nas cifs credentials=/etc/privategit/nas-credentials,uid=git,gid=gitrepos,file_mode=0660,dir_mode=0770,vers=3.0,soft,_netdev,nofail,x-systemd.automount,x-systemd.mount-timeout=20 0 0' | sudo tee -a /etc/fstab
   sudo systemctl daemon-reload && sudo mount /mnt/privategit-nas
   sudo -u git touch /mnt/privategit-nas/.test && sudo -u gitdash rm /mnt/privategit-nas/.test && echo "NAS writable"
   ```
3. Turn it on in `/etc/privategit/environment`, choosing all projects (`*`) or only some:

   ```text
   PrivateGit__NasBackupDirectory=/mnt/privategit-nas
   PrivateGit__NasBackupRepositories=fpga-rtl,dpu-demo
   ```

   Then make the first copy: `sudo bash /opt/privategit/admin.sh backup-now`. Leaving `PrivateGit__NasBackupDirectory` empty means no NAS copy. Upgrades keep these settings.
4. To restore from the NAS instead of the server, add `--nas`, e.g. `admin.sh backup-list fpga-rtl --nas` or `admin.sh backup-restore fpga-rtl --nas`.
5. To read main directly on Windows from the NAS copy (read-only use), install Git for Windows:

   ```powershell
   git config --global --add safe.directory "%(prefix)///NAS/GitBackup/fpga-rtl.git"
   git clone \\NAS\GitBackup\fpga-rtl.git
   ```

   Don't push into the NAS copy. The next push to the server would overwrite it. Changes always go through the Git server.

Anyone with access to that NAS share can read the copies, so limit the share to administrators.

### Full server backup (recommended in addition)

Back up `/var/lib/privategit` (accounts, TOTP secrets, cookie keys), `/srv/privategit` (repositories, `access/access.json`, hidden backups) and `/etc/privategit` (config, TLS, CA, SSH keys). Stop the dashboard **and** block pushes (or use a filesystem snapshot) while copying. Keep backups encrypted and root-only.

## 12. Turning off or uninstalling

Two scripts, both in `/opt/privategit` (and in the package folder). Each asks you to type a word before changing anything.

| Type | Command | Keeps data? | Undo |
|---|---|---|---|
| **Turn off** | `sudo bash /opt/privategit/uninstall-disable.sh` (type `DISABLE`) | Yes: repositories, accounts, keys, backups, settings | `sudo bash /opt/privategit/uninstall-disable.sh --undo` |
| **Remove completely** | `sudo bash /opt/privategit/uninstall-remove.sh` (type `REMOVE`) | Exported first to `~/privategit-export-<time>.tgz` (root-only) | Reinstall; restore repositories from the export |

- *Turn off* stops the dashboard, removes the PrivateGit nginx site, parks the git account's SSH rules and locks the git account. Nobody can clone or push.
- *Remove* deletes only what `install.sh` created. `--no-export` skips the export (data is lost).
- Neither touches your users, your SSH login, other nginx sites, the nginx package, system certificates or NAS copies. If nginx/SSH would not validate afterwards, the change is undone automatically.
