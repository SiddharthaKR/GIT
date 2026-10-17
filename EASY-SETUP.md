# PrivateGit: the easy setup guide

Start with **one central Linux server and one Linux client**. The central server stores your repositories and serves the dashboard. Additional Linux clients can be added after this first test works. Windows is optional and is not required for installation, operation or testing.

> **Our lab network (172.20.1.x, no DNS, no IT certificate)?** Use [OFFICE-SETUP.md](OFFICE-SETUP.md) instead; it is the short version for that environment.

The installer is for **Ubuntu 22.04 or 24.04 with systemd**. The supplied ZIP is for **x64**; build the ARM64 package if needed. Run the installer from an extracted **deployment ZIP**, not the source ZIP.

## What you need — once

1. **An address for the server**: either a fixed IP such as `172.20.1.126` (no DNS needed) or a name such as `git.internal` pointing to it in company DNS. This is the address you will type into your browser.
2. **The allowed network**, such as `192.168.1.0/24`, or individual machine addresses such as `192.168.1.126/32`. Use your actual company addresses. These decide who may reach the dashboard and the Git SSH account.
3. **HTTPS**: either let the installer create a **private PrivateGit CA** (option 1; you then install `privategit-ca.crt` on each client once), or use a certificate from IT for that name/IP with its chain and CA bundle (option 2). Clients must trust whichever CA signed the certificate.
4. **Firewall access** to this server on TCP 443 (dashboard) and TCP 22 (Git/administration), restricted to approved company clients/VPNs. The installer does not replace the company's firewall rules or alter administrator SSH policy.

Only for option 2, if IT needs a certificate request, run this on the Linux server before installation:

```bash
bash request-certificate.sh git.internal
```

Give IT **only** `privategit-certificate/server.csr`. Keep `privkey.pem` private on the server. IT returns a signed full-chain PEM certificate and a CA bundle. The helper generates the request locally; it does not contact an outside service or generate an untrusted self-signed server certificate.

## Install

Copy `privategit-linux-x64.zip` to the central server with your usual transfer tool. The ZIP is already built; you do not need a Windows machine or a .NET SDK to install it. For example, from a machine with SSH tools:

```bash
scp /path/to/privategit-linux-x64.zip administrator@SERVER-IP:~/
```

**On the central Linux server:** log in using your administrator account, then:

```bash
mkdir privategit-setup
unzip privategit-linux-x64.zip -d privategit-setup
cd privategit-setup
sudo bash install.sh
```

If `unzip` is missing, install it from your approved package source first. The installer can install its other missing system packages with apt after asking you. It uses the server's configured repositories, which can be company mirrors. This setup download is separate from the application's normal operation.

The installer asks for:

| Question | Example / explanation |
|---|---|
| Server address | `172.20.1.126` (detected) or `git.internal` |
| Allowed networks | `172.20.1.0/24`, or single machines such as `172.20.1.157/32`; separate several with spaces |
| HTTPS certificate | `1` private PrivateGit CA, or `2` IT certificate files |
| Certificate / key / CA bundle (option 2 only) | `/home/administrator/fullchain.pem`, `.../privkey.pem`, CA bundle from IT |
| First admin username | `alice` |

You review the settings and type `INSTALL`. The installer creates the service accounts, directories, application configuration, HTTPS site and Git SSH policy. It then asks you to create a **16-character-or-longer password** and shows a secret to enter into an authenticator app. Each sign-in needs your password plus its six-digit code. Choose an authenticator that works locally; cloud synchronization is not needed by PrivateGit.

Existing installer-owned account and repository data is retained when rerunning. Reruns do not reset existing credentials or automatically enroll a new administrator. Unrelated pre-existing `git`, `gitdash`, `gitrepos` identities or installation locations are refused for IT review. If installation fails, configuration is restored and the dashboard is left stopped; fix the reported issue and rerun from the extracted setup directory. See `README.md` for manual recovery/administration.

To check prerequisites without installing:

```bash
bash install.sh --check
```

If company policy requires **zero setup downloads**, preinstall the dependencies through IT, then use:

```bash
sudo bash install.sh --offline
```

## Open the dashboard

Visit **`https://172.20.1.126`** (or the address you entered) from an allowed computer. If the company proxy is set in your browser, add that address to the proxy bypass list. Sign in, then create a repository such as `project`. The dashboard then opens its **Access** tab: choose *Read* or *Read & write* for each user who should use it. Users see only repositories granted to them; admins see all.

Create more people on the server with `sudo bash /opt/privategit/admin.sh user-add NAME User` (each gets their own password + authenticator).

Creating a repository means making an empty home for a project's Git history. Your client then pushes its code into that home.

## Connect your first Linux client

Open a terminal on a Linux client. On an Ubuntu/Debian client, install these if missing:

```bash
sudo apt update
sudo apt install -y git openssh-client
```

Then generate that client's SSH key:

```bash
mkdir -p ~/.ssh
ssh-keygen -t ed25519 -f ~/.ssh/id_ed25519_internal_git -C "my-company-client"
```

This creates two files: the **private key** stays on that client; the file ending in **`.pub`** is safe to give the server administrator. Use a separate key for each machine.

Transfer that `.pub` file to the central server. There, link it to the person's dashboard account with one command:

```bash
sudo bash /opt/privategit/add-client.sh alice /absolute/path/to/alice.pub
```

The helper validates the public key, links it to `alice`, restricts it to your approved networks (or one machine with `--from 172.20.1.157/32`) and prints the SSH configuration to put in the client's `~/.ssh/config`. Alice can then clone repositories where she has Read and push where she has Read & write. Copy that configuration. Compare the server fingerprint printed by the helper with the client's first connection prompt.

Then, on the client:

```bash
git clone git@git.internal:/srv/privategit/repositories/project.git
cd project
git config user.name "Your Name"
git config user.email "you@example.internal"
```

Use the dashboard's clone URL if you chose another hostname or repository name. A client SSH key gives Git access; it does not create a dashboard login.

On the Linux client, protect the SSH configuration with `chmod 700 ~/.ssh` and `chmod 600 ~/.ssh/config`.

## First Linux-only test

In the dashboard, create a **new empty** repository named `linux-test`. On the Linux client, after authorizing its key and setting up `~/.ssh/config`:

```bash
git clone git@git.internal:/srv/privategit/repositories/linux-test.git linux-test-check
cd linux-test-check
git config user.name "Linux Test"
git config user.email "linux-test@example.internal"
git switch -c main
printf 'Linux test version 1\n' > connection-test.txt
git add connection-test.txt
git commit -m "First Linux test"
git push -u origin main

printf 'Linux test version 2\n' > connection-test.txt
git add connection-test.txt
git commit -m "Check the code diff"
git push
```

Refresh the dashboard, open `linux-test`, and click **Check the code diff**. You should see version 1 removed in red and version 2 added in green. This checks Linux SSH push, stored commits, dashboard history and code diffs without needing Windows. If Git says the empty clone already has a local `main` branch, skip `git switch -c main` and continue on that branch.

To test pulling, clone the same test repository on a second Linux client, or in a separate working directory, then push another test commit from the first clone and run `git pull --ff-only` in the second.

## Normal daily use

```bash
git pull --ff-only
# Work on your files.
git add .
git commit -m "Explain what changed"
git push
```

For the first commit in an empty repository, use `git push -u origin main`. If your local branch has a different name, rename it with `git branch -M main` first. Configure `.gitignore` before adding files so credentials, generated files and private keys do not enter Git.

Refresh the dashboard after a push. Click a commit for green/red code changes; open a file for its history; use **Branches & tags** for versions and **Compare changes** to inspect two versions. Other machines receive changes when they pull.

For an existing local Git project, instead of cloning into it:

```bash
git remote add internal git@git.internal:/srv/privategit/repositories/project.git
git push -u internal HEAD:main
git push internal --tags
```

## Where everything lives

| Directory | What it means |
|---|---|
| `/opt/privategit` | The installed program — replace this when upgrading |
| `/srv/privategit/repositories` | Your source-code repositories — preserve and back up |
| `/var/lib/privategit` | Dashboard accounts and login-protection keys — preserve and back up |
| `/etc/privategit` | Settings, HTTPS keys, approved networks and authorized client keys |

The service starts automatically after reboot. To check it:

```bash
sudo systemctl status privategit --no-pager
sudo journalctl -u privategit -n 50 --no-pager
```

No external API or cloud service is used by the installed app. Package installation/updates may use your configured package repositories during setup/maintenance. Firewall/DNS/time services and any OS-wide updates remain company IT's responsibility.

## Windows later — optional

You can add a Windows Git client later using its own SSH key. No server reinstallation or Windows software on the central server is needed. Windows client instructions are a separate optional guide, excluded from the default Linux deployment package. There is no Windows-server installer in this package.

**Access model:** per user and per repository (none / read / read & write), enforced on the web and over SSH. Admins have full access. This is a small-team tool, not an audited forge. Read `SECURITY.md` and have IT validate the Linux deployment. Installer configuration backups are not source-code backups; back up repository and account state separately while coordinating SSH writes.
