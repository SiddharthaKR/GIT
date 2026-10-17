# PrivateGit on .126: step-by-step check (GUI first)

Server: 172.20.1.126 (Ubuntu 22.04). Laptop: Windows with a browser.
The terminal is needed for three things only: the safety check (step 1), the installer (step 3), and creating users and SSH keys (step 5). Everything else is done in the browser.

---

## Step 1: Safety check before installing (read-only, about 2 minutes)

Nothing on the server is changed in this step.

Open **two** SSH sessions to .126 and keep the second one open until the end. If SSH ever misbehaves, you still have a way in.

```bash
lsb_release -d                                                   # A
sudo ss -ltnp | grep -E ':(22|80|443|5080) '                    # B
curl --noproxy '*' -s -o /dev/null -w '%{http_code}\n' http://172.20.1.126/v1/models   # C
ls /etc/nginx/sites-enabled/ 2>/dev/null                         # D
getent passwd git gitdash; getent group gitrepos                 # E
grep -n '^Include' /etc/ssh/sshd_config                          # F
sudo sshd -T | grep -iE '^(allowusers|allowgroups|denyusers|denygroups)'   # G
df -h /srv /opt                                                  # H
```

| Line | OK if | STOP if |
|---|---|---|
| A | Ubuntu 22.04 | anything else |
| B | port 443 is free or held by nginx; port 5080 is free | port 443 or 5080 is used by another program |
| C | `000` (nothing answers on port 80) | **200 / 401 / 404: something answers on port 80 (see the note below)** |
| D | empty, or only `default` | other sites exist (fine, but tell me the names) |
| E | prints nothing | an account named `git` or `gitdash` already exists |
| F | `Include /etc/ssh/sshd_config.d/*.conf` | the line is missing |
| G | prints nothing | an AllowUsers/AllowGroups rule exists (the `git` account would be blocked, so tell me) |
| H | a few GB free | almost full |

> **Important for .126 (line C).** Your model API is reached as `172.20.1.126/v1` with no port number, which usually means port 80. PrivateGit adds a port-80 rule for the name `172.20.1.126` that redirects to HTTPS. If line C answers, installing as-is **would capture the model API**, and all servers would lose `/v1`.
> If line C answers, do not install on .126 yet. Either I add an "HTTPS only, leave port 80 alone" option to the installer (small change), or you install on .157 or .158 instead.

Then make a safety copy of the configuration (this is your undo point):

```bash
sudo tar czf ~/before-privategit-$(date +%F).tgz /etc/nginx /etc/ssh
```

---

## Step 2: Copy the package

On Windows, run `scripts\publish.ps1` to rebuild, then copy `artifacts\privategit-linux-x64.zip` to .126 (WinSCP or `scp`). Then, on .126:

```bash
mkdir ~/pg && cd ~/pg && unzip -q ~/privategit-linux-x64.zip
bash install.sh --check             # expect: Prerequisites and executable architecture: OK.
```

---

## Step 3: Install

```bash
sudo bash install.sh
```

| Prompt | Answer on .126 |
|---|---|
| Server address | `172.20.1.126` |
| Allowed networks | `172.20.1.0/24` (add your laptop's network if it is different) |
| HTTPS | `1` (private CA) |
| First administrator | `siddhartha`, then type `INSTALL`, set a password, and add the shown secret to your authenticator app |

Paste one command at a time; never paste lines while the installer is waiting at a prompt.
The installer ends with "Installed. Open https://172.20.1.126".

**Right after installing, check that nothing else broke:**

```bash
curl --noproxy '*' -s -o /dev/null -w '%{http_code}\n' http://172.20.1.126/v1/models   # same code as line C before
ssh $USER@172.20.1.126 true && echo "my SSH login still works"
for s in ssh nginx privategit; do systemctl is-active $s; done                       # active x3
```

---

## Step 4: Browser checks (any Linux desktop: .126 itself, .157, .158 or WSL; no Windows needed)

Rules: always use the IP `172.20.1.126`, never `localhost` (PrivateGit answers only to its own address). The browser must be on a machine inside the allowed networks.

| # | Do this | Expected |
|---|---|---|
| 1 | Bypass the proxy: start the browser from a terminal with `no_proxy=172.20.1.126 firefox &`, or add `172.20.1.126` under Firefox → Settings → Network Settings → "No proxy for" | — |
| 2 | Trust the certificate: Firefox → Settings → Privacy & Security → Certificates → View Certificates → Authorities → Import → `/opt/privategit/privategit-ca.crt` → tick "trust this CA to identify websites". (Or skip this and click through the warning; it is only a test.) | — |
| 3 | Open `https://172.20.1.126` | Login page with a padlock and no warning |
| 4 | Open `http://172.20.1.126` | Switches to https automatically |
| 5 | Sign in as `siddhartha` with your password and the authenticator code | Repository list, with **Admin** shown at the top |
| 6 | Sign in with a wrong code | Refused; the account locks for 15 minutes after 5 wrong attempts |
| 7 | Create the repository `test-rtl` | It opens the **Access** tab |
| 8 | Repository page | "CLONE WITH SSH" shows `git@172.20.1.126:/srv/privategit/repositories/test-rtl.git` and the page says "Ready for the first commit" |
| 9 | From a PC **outside** the allowed networks, open the URL | Refused (403) |

---

## Step 5: Users (terminal), then check them in the browser

```bash
sudo bash /opt/privategit/admin.sh user-add alice User      # alice's password + authenticator secret
sudo bash /opt/privategit/admin.sh user-add bob User
sudo bash /opt/privategit/add-client.sh alice /path/alice.pub   # each person's SSH public key
sudo bash /opt/privategit/add-client.sh bob   /path/bob.pub
```

| # | Browser | Expected |
|---|---|---|
| 10 | As siddhartha: `test-rtl` → **Access** → alice = Read & write, bob = Read → Save | Saved; both rows are listed |
| 11 | Sign out, then sign in as **bob** | Only `test-rtl` appears, marked **Read only**, with no Access tab |
| 12 | Sign in as alice after her first push (step 6) | Code, history and diffs are visible |

---

## Step 6: Git check (VS Code or Git for Windows)

On alice's PC, her `~/.ssh/config` should contain `Host 172.20.1.126` / `User git` / `IdentityFile` pointing to her key. Then:

1. VS Code: Clone Repository → paste the URL from the "CLONE WITH SSH" bar. Expected: it clones (with an "empty repository" warning).
2. Add a file → Commit → **Publish/Push** to `main`. Expected: it succeeds.
3. Create the branch `alice-test` and push it.
4. In the browser, open `test-rtl` → **Branches & tags**. Expected: `alice-test` shows "created by alice", `main` shows **Protected**, and alice sees a **Delete** button on her own branch only.
5. As bob, push anything. Expected: "bob has read-only access to test-rtl".
6. Back up: `sudo bash /opt/privategit/admin.sh backup-list test-rtl`. Expected: one snapshot per change to main.

---

## Undo (if anything misbehaves)

This turns PrivateGit off and gives nginx and SSH back their old behaviour. Repositories and accounts stay on disk.

```bash
sudo systemctl disable --now privategit
sudo rm /etc/nginx/sites-enabled/privategit /etc/ssh/sshd_config.d/60-privategit.conf
sudo nginx -t && sudo systemctl reload nginx
sudo sshd -t && sudo systemctl reload ssh
curl --noproxy '*' -s -o /dev/null -w '%{http_code}\n' http://172.20.1.126/v1/models   # back to normal
```

The full copy of the old configuration is in `~/before-privategit-<date>.tgz`.
