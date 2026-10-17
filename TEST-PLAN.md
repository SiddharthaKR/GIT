# PrivateGit test plan (WSL2 Ubuntu 22.04 on the laptop, or directly on .126)

Every step lists the expected result. On the real server, skip step 1's WSL parts and use the server's IP.

## 0. Build (Windows PowerShell)

```powershell
cd C:\Users\siddh\Documents\Personal\Study\GIT_LOCAL
powershell -ExecutionPolicy Bypass -File .\scripts\test.ps1      # expect: All 165 checks passed.
powershell -ExecutionPolicy Bypass -File .\scripts\publish.ps1   # expect: Transfer artifacts/privategit-linux-x64.zip ...
```

## 1. Unpack and self-test (Ubuntu / WSL)

WSL only, once: enable systemd, then restart WSL from PowerShell with `wsl --shutdown`.

```bash
printf '[boot]\nsystemd=true\n' | sudo tee /etc/wsl.conf
```

```bash
cd ~ && rm -rf ~/pg && mkdir ~/pg
unzip -q /mnt/c/Users/siddh/Documents/Personal/Study/GIT_LOCAL/artifacts/privategit-linux-x64.zip -d ~/pg   # backslash warning is harmless
cd ~/pg && ls admin.sh hooks/backup-main templates/gitattributes   # all three must exist (new package)
sudo bash scripts/test-ssh-gate.sh ./PrivateGit                    # expect: All 51 SSH gate checks passed.
```

## 2. Install

```bash
IP=$(hostname -I | awk '{print $1}'); GW=$(ip route | awk '/default/ {print $3}'); echo "IP=$IP GW=$GW"
bash install.sh --check          # expect: Prerequisites and executable architecture: OK.
sudo bash install.sh
#   Server address        -> Enter (detected IP)
#   Allowed networks      -> WSL: "$IP/32 $GW/32" typed out, e.g. 172.24.20.5/32 172.24.16.1/32   (server: 172.20.1.0/24)
#   HTTPS                 -> 1   (private CA)
#   First administrator   -> siddhartha ; type INSTALL ; set password ; add the shown secret to your authenticator app
# expect at the end: "Backing up main branches (server)" and "Installed. Open https://<IP>"
curl -s --noproxy '*' --cacert /opt/privategit/privategit-ca.crt -o /dev/null -w '%{http_code}\n' https://$IP/Login   # expect: 200
for s in ssh nginx privategit; do systemctl is-active $s; done                                                       # expect: active x3
sudo ls /srv/privategit                                                                                               # expect: access backups repositories
```

## 3. People: dashboard accounts, Linux accounts, SSH keys

```bash
sudo bash /opt/privategit/admin.sh user-add alice User      # password + secret (needed only for her web login)
sudo bash /opt/privategit/admin.sh user-add bob User
sudo bash /opt/privategit/admin.sh user-list                # expect: alice User, bob User, siddhartha Admin
for u in alice bob; do
  id $u >/dev/null 2>&1 || sudo adduser --disabled-password --gecos '' $u
  sudo -H -u $u bash -c "mkdir -p ~/.ssh && ssh-keygen -q -t ed25519 -N '' -f ~/.ssh/id_ed25519_privategit -C $u@test
    printf 'Host pg\n  HostName $IP\n  User git\n  IdentityFile ~/.ssh/id_ed25519_privategit\n  IdentitiesOnly yes\n  StrictHostKeyChecking accept-new\n' > ~/.ssh/config
    chmod 600 ~/.ssh/config; git config --global user.name $u; git config --global user.email $u@test; git config --global init.defaultBranch main"
  sudo cp /home/$u/.ssh/id_ed25519_privategit.pub /tmp/$u.pub
  sudo bash /opt/privategit/add-client.sh $u /tmp/$u.pub | head -1   # expect: Linked key to <user>
done
sudo bash /opt/privategit/add-client.sh bob /tmp/alice.pub    # expect: ERROR ... already linked to alice
sudo -iu alice ssh pg                                         # expect: Hi alice. Interactive shells are disabled ...
```

## 4. Dashboard (Windows browser)

1. Add the server IP to the browser proxy bypass list, then open `https://<IP>`. The certificate warning is expected unless you import `\\wsl$\Ubuntu-22.04\opt\privategit\privategit-ca.crt` as a trusted root.
2. Sign in as `siddhartha` (password + authenticator code), create repository `fpga-rtl`, and on its **Access** tab set alice = Read & write, bob = Read.
3. Without a browser, create the repository and grant access from the terminal instead:

```bash
sudo -u gitdash bash -c 'umask 0007; git init -q --bare --shared=group --initial-branch=main /srv/privategit/repositories/fpga-rtl.git && cd /srv/privategit/repositories/fpga-rtl.git && git config receive.denyNonFastForwards true && git config receive.denyDeletes true'
sudo bash /opt/privategit/admin.sh access-grant fpga-rtl alice write
sudo bash /opt/privategit/admin.sh access-grant fpga-rtl bob read
sudo bash /opt/privategit/admin.sh access-list               # expect: fpga-rtl: alice=write, bob=read
```

## 5. Permissions over Git

```bash
sudo -iu alice bash -c 'git clone -q pg:fpga-rtl.git && cd fpga-rtl && cp /opt/privategit/templates/gitattributes .gitattributes && cp /opt/privategit/templates/gitignore-fpga .gitignore && echo "module top; endmodule" > top.v && git add -A && git commit -qm "Initial" && git push -q origin HEAD:main && echo PUSH-OK'
# expect: PUSH-OK
sudo -iu bob bash -c 'git clone -q pg:fpga-rtl.git && ls fpga-rtl'                          # expect: top.v (bob can read)
sudo -iu bob bash -c 'cd fpga-rtl && git commit -q --allow-empty -m x && git push origin HEAD:main'
# expect: PrivateGit: bob has read-only access to fpga-rtl ...
sudo -iu bob ssh pg 'cat /etc/passwd'                                                        # expect: PrivateGit: Only git clone, fetch, pull and push are allowed.
sudo bash /opt/privategit/admin.sh access-revoke fpga-rtl bob
sudo -iu bob bash -c 'cd fpga-rtl && git fetch'                                             # expect: ... not found or bob has no access.
sudo bash /opt/privategit/admin.sh access-grant fpga-rtl bob read
```

Dashboard: signed in as bob, only `fpga-rtl` appears, marked "Read only".

## 6. Branch deletion (own branches only)

```bash
sudo bash /opt/privategit/admin.sh access-grant fpga-rtl bob write     # bob needs write to own branches
sudo -iu alice bash -c 'cd fpga-rtl && git push -q origin HEAD:refs/heads/alice-test HEAD:refs/heads/alice-keep'
sudo -iu bob   bash -c 'cd fpga-rtl && git fetch -q && git push -q origin origin/main:refs/heads/bob-test'
sudo -iu bob   bash -c 'cd fpga-rtl && git push origin --delete alice-keep'   # expect: ... was created by alice. Only its creator or an administrator can delete it.
sudo -iu alice bash -c 'cd fpga-rtl && git push origin --delete alice-test'   # expect: branch alice-test deleted by alice
sudo -iu alice bash -c 'cd fpga-rtl && git push origin --delete main'         # expect: the main branch (main) cannot be deleted.
sudo -iu alice bash -c 'cd fpga-rtl && git commit -q --allow-empty -m y && git push -q origin HEAD:main && git push --force origin HEAD~1:main'
# expect: ! [remote rejected] ... (non-fast-forward)
sudo bash /opt/privategit/admin.sh access-grant fpga-rtl bob read
```

Dashboard → `fpga-rtl` → **Branches & tags**: "created by alice/bob" is shown, **Delete** appears only on your own branches, and main shows **Protected**.

## 7. Hidden backup of main

```bash
sudo bash /opt/privategit/admin.sh backup-list fpga-rtl        # expect: snapshots "...Z-xxxxxxxx  Initial" etc. (newest first)
sudo -iu alice ls /srv/privategit/backups                      # expect: Permission denied
sudo -iu alice git ls-remote pg:../backups/fpga-rtl.git        # expect: refused (backups are not reachable)
# Bad change, then restore the good version as a new branch (main untouched):
sudo -iu alice bash -c 'cd fpga-rtl && echo BROKEN > top.v && git commit -qam "Oops" && git push -q origin HEAD:main'
SNAP=$(sudo bash /opt/privategit/admin.sh backup-list fpga-rtl | awk '/Initial/{print $1; exit}'); echo $SNAP
sudo bash /opt/privategit/admin.sh backup-restore fpga-rtl "$SNAP"   # expect: ... as the new branch restored-main-... main was NOT changed
# Disaster: the repository disappears, then recreate it from the backup:
sudo mv /srv/privategit/repositories/fpga-rtl.git /root/fpga-rtl.lost
sudo bash /opt/privategit/admin.sh backup-restore fpga-rtl           # expect: Recreated fpga-rtl from the backup
sudo -iu bob bash -c 'rm -rf r && git clone -q pg:fpga-rtl.git r && cat r/top.v'   # expect: BROKEN (latest main is back)
```

## 8. Switches and "NAS never blocks"

```bash
E=/etc/privategit/environment
sudo sed -i 's#^PrivateGit__NasBackupDirectory=.*#PrivateGit__NasBackupDirectory=/mnt/nas-that-is-off#' $E   # NAS configured but offline
sudo -iu alice bash -c 'cd fpga-rtl && git pull -q --rebase origin main; git commit -q --allow-empty -m nas && time git push -q origin HEAD:main'
# expect: push completes in about a second; no error
sudo bash /opt/privategit/admin.sh backup-now               # expect: "server (NAS not reachable: skipped)", fast
sudo sed -i 's#^PrivateGit__NasBackupDirectory=.*#PrivateGit__NasBackupDirectory=#' $E
sudo sed -i 's/^PrivateGit__MainBackup=.*/PrivateGit__MainBackup=false/' $E
N=$(sudo bash /opt/privategit/admin.sh backup-list fpga-rtl | wc -l)
sudo -iu alice bash -c 'cd fpga-rtl && git commit -q --allow-empty -m off && git push -q origin HEAD:main'
[ "$(sudo bash /opt/privategit/admin.sh backup-list fpga-rtl | wc -l)" = "$N" ] && echo "backup OFF works"
sudo sed -i 's/^PrivateGit__MainBackup=.*/PrivateGit__MainBackup=true/' $E && sudo bash /opt/privategit/admin.sh backup-now
sudo sed -i 's/^PrivateGit__AllowBranchDelete=.*/PrivateGit__AllowBranchDelete=false/' $E
sudo -iu alice bash -c 'cd fpga-rtl && git push -q origin HEAD:refs/heads/tmp && git push origin --delete tmp'   # expect: refused
sudo sed -i 's/^PrivateGit__AllowBranchDelete=.*/PrivateGit__AllowBranchDelete=true/' $E
```

## 9. Upgrade keeps everything

```bash
cd ~/pg && sudo bash install.sh        # same answers; no admin prompt this time
sudo bash /opt/privategit/admin.sh user-list; sudo bash /opt/privategit/admin.sh access-list; sudo bash /opt/privategit/add-client.sh --list
grep -E 'MainBackup|AllowBranchDelete|NasBackup' /etc/privategit/environment       # your values kept
```

## Cleanup (WSL test only)

From PowerShell, `wsl --unregister Ubuntu-22.04` removes everything. Or keep it for the next round.
