#!/usr/bin/env bash
# End-to-end test of per-user SSH permissions with a real OpenSSH server, real Git and
# the published PrivateGit binary. Linux only; needs root because it creates temporary
# system accounts and starts a private sshd on 127.0.0.1:2299. Everything it creates
# is removed afterwards. It does not touch /etc/ssh, /etc/privategit or port 22.
#   sudo bash scripts/test-ssh-gate.sh /path/to/published/PrivateGit
set -Eeuo pipefail
ROOT=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)
source "$ROOT/deploy/install-common.sh"
BINARY=$(realpath -- "${1:-$ROOT/artifacts/linux-x64/PrivateGit}")
[[ $EUID == 0 ]] || { echo 'Run with sudo (creates temporary accounts and a private sshd).'; exit 1; }
[[ -f $BINARY ]] || { echo "Published PrivateGit binary not found: $BINARY"; exit 1; }
GIT_USER=pgtestgit DASH_USER=pgtestdash GROUP=pgtestrepos PORT=2299
W=$(mktemp -d /srv/privategit-sshtest.XXXXXX)
passed=0
pass() { passed=$((passed + 1)); printf 'PASS %s\n' "$1"; }
fail() { printf 'FAIL %s\n' "$1" >&2; [[ ! -f $W/sshd.log ]] || tail -n 20 "$W/sshd.log" >&2; exit 1; }
cleanup() {
    [[ ! -f $W/sshd.pid ]] || kill "$(cat "$W/sshd.pid")" 2>/dev/null || true
    sleep 0.3
    pkill -u "$GIT_USER" 2>/dev/null || true; sleep 0.3
    userdel -r "$GIT_USER" >/dev/null 2>&1 || true
    userdel "$DASH_USER" >/dev/null 2>&1 || true
    groupdel "$GROUP" >/dev/null 2>&1 || true
    case "$W" in /srv/privategit-sshtest.*) rm -rf -- "$W" ;; esac
}
trap cleanup EXIT

groupadd --system "$GROUP"
useradd --system --create-home --gid "$GROUP" --shell /bin/sh "$GIT_USER"
useradd --system --no-create-home --gid "$GROUP" --shell /usr/sbin/nologin "$DASH_USER"
usermod -p "*" "$GIT_USER" # not locked; key-only login (UsePAM no in this private sshd)
chmod 0755 "$W"
# Use a private copy: ZIPs made on Windows drop the execute bit, and home folders
# (0750 on Ubuntu 22.04+) are not readable by the temporary git account.
install -o root -g root -m 0755 "$BINARY" "$W/PrivateGit"
BINARY=$W/PrivateGit
install -d -o root -g "$GROUP" -m 0750 "$W/etc"
install -d -o "$DASH_USER" -g "$GROUP" -m 2770 "$W/repos"
install -d -o root -g "$GROUP" -m 2750 "$W/access"
install -d -o root -g "$GROUP" -m 2770 "$W/backups" "$W/nas"   # nas = stand-in for a mounted NAS share
install -d -o root -g root -m 0700 "$W/data" "$W/keys" "$W/clients"
install -d -o root -g root -m 0755 "$W/hooks"
install -o root -g root -m 0755 "$ROOT/hooks/pre-receive" "$W/hooks/pre-receive"
install -o root -g root -m 0755 "$ROOT/hooks/post-receive" "$W/hooks/post-receive"
install -o root -g root -m 0755 "$ROOT/hooks/backup-main" "$W/hooks/backup-main.real"
# Test-only wrapper: when $W/nas-hang exists, the NAS copy stalls like a hung SMB mount.
printf '#!/bin/sh\nif [ "$2" = "%s" ] && [ -f "%s" ]; then sleep 30; fi\nexec "%s" "$@"\n' "$W/nas" "$W/nas-hang" "$W/hooks/backup-main.real" > "$W/hooks/backup-main"
chmod 0755 "$W/hooks/backup-main"
printf 'privategit-installer-v1\n' > "$W/etc/installer-owned"
printf '127.0.0.0/8\n' > "$W/etc/allowed-networks"
cat > "$W/etc/environment" <<EOF
AllowedHosts=git.test
PrivateGit__DataDirectory=$W/data
PrivateGit__RepositoryDirectory=$W/repos
PrivateGit__AccessFile=$W/access/access.json
PrivateGit__AuthorizedKeysFile=$W/etc/authorized_keys
PrivateGit__GitExecutable=$(command -v git)
PrivateGit__HooksDirectory=$W/hooks
PrivateGit__AllowBranchDelete=true
PrivateGit__MainBackup=true
PrivateGit__BackupDirectory=$W/backups
PrivateGit__NasBackupDirectory=$W/nas
PrivateGit__NasBackupRepositories=proj
EOF
chgrp "$GROUP" "$W/etc/environment"; chmod 0640 "$W/etc/environment"
install -o root -g "$GROUP" -m 0640 /dev/null "$W/etc/authorized_keys"
# Dashboard accounts (only names/roles matter here).
printf '[%s]\n' '{"Name":"alice","Hash":"-","TotpSecret":"-","Role":"User","Stamp":"1"},{"Name":"bob","Hash":"-","TotpSecret":"-","Role":"User","Stamp":"2"},{"Name":"dave","Hash":"-","TotpSecret":"-","Role":"Reader","Stamp":"3"},{"Name":"boss","Hash":"-","TotpSecret":"-","Role":"Admin","Stamp":"4"}' > "$W/data/accounts.json"
# Like production: the dashboard account (not git) creates and owns the repositories.
for repo in proj secret; do
    runuser -u "$DASH_USER" -- git init -q --bare --shared=group --initial-branch=main "$W/repos/$repo.git"
    runuser -u "$DASH_USER" -- git --git-dir "$W/repos/$repo.git" config receive.denyNonFastForwards true
    runuser -u "$DASH_USER" -- git --git-dir "$W/repos/$repo.git" config receive.denyDeletes true
done
[[ $(stat -c %U "$W/repos/proj.git") == "$DASH_USER" ]] && pass 'Repositories owned by the dashboard account (as in production)' || fail 'repo ownership setup' 
printf '{"Admins":["boss"],"Repositories":{}}' > "$W/access/access.json"
chgrp "$GROUP" "$W/access/access.json"; chmod 0640 "$W/access/access.json"

admin() { (cd "$W" && env $(grep -v '^#' "$W/etc/environment" | xargs) "$BINARY" "$@"); }
admin access-grant proj alice write >/dev/null
admin access-grant proj bob read >/dev/null
chgrp "$GROUP" "$W/access/access.json"
[[ $(admin access-list proj) == *'alice=write'*'bob=read'* ]] && pass 'CLI grants per-user permissions' || fail 'CLI grants'
admin access-grant proj nobody read >/dev/null 2>&1 && fail 'grant to unknown user accepted' || pass 'CLI refuses unknown users'
admin access-grant nothere alice read >/dev/null 2>&1 && fail 'grant on unknown repo accepted' || pass 'CLI refuses unknown repositories'

# Keys, linked through the real helper (with test paths).
export PRIVATEGIT_TEST=1 PRIVATEGIT_KEY_FILE=$W/etc/authorized_keys PRIVATEGIT_ACCOUNTS=$W/data/accounts.json PRIVATEGIT_ETC=$W/etc PRIVATEGIT_LOCK=$W/key.lock
for person in alice bob dave boss carol alice2; do ssh-keygen -q -t ed25519 -N '' -C "$person@test" -f "$W/keys/$person"; done
for person in alice bob dave boss; do bash "$ROOT/add-client.sh" "$person" "$W/keys/$person.pub" >/dev/null; done
bash "$ROOT/add-client.sh" alice "$W/keys/alice2.pub" --from 10.9.9.9/32 >/dev/null
printf 'from="127.0.0.0/8",restrict %s\n' "$(cut -d' ' -f1,2 "$W/keys/carol.pub")" >> "$W/etc/authorized_keys" # pre-upgrade style line
bash "$ROOT/add-client.sh" bob "$W/keys/alice.pub" >/dev/null 2>&1 && fail 'same key linked to two users' || pass 'A key cannot be linked to two people'
bash "$ROOT/add-client.sh" ghost "$W/keys/carol.pub" >/dev/null 2>&1 && fail 'key linked to unknown account' || pass 'Keys require an existing dashboard account'
listing=$(bash "$ROOT/add-client.sh" --list)
[[ $listing == *alice*bob*dave*boss* && $listing == *'from=10.9.9.9/32'* && $listing == *'not linked'* ]] && pass 'add-client --list shows owners, limits and unlinked lines' || fail "--list: $listing"

# Private sshd using the installer's real Match block.
ssh-keygen -q -t ed25519 -N '' -f "$W/hostkey"
{
    printf 'Port %s\nListenAddress 127.0.0.1\nHostKey %s\nPidFile %s\nUsePAM no\nAuthorizedKeysFile none\n' "$PORT" "$W/hostkey" "$W/sshd.pid"
    pg_render_sshd 127.0.0.0/8 \
        | sed -e "s#Match User git#Match User $GIT_USER#" -e "s#git@127#$GIT_USER@127#" \
              -e "s#/etc/privategit/authorized_keys#$W/etc/authorized_keys#" \
              -e "s#/opt/privategit/PrivateGit git-serve --config /etc/privategit/environment#$BINARY git-serve --config $W/etc/environment#"
} > "$W/sshd_config"
mkdir -p /run/sshd
/usr/sbin/sshd -t -f "$W/sshd_config" || fail 'rendered sshd configuration is invalid'
/usr/sbin/sshd -f "$W/sshd_config" -E "$W/sshd.log"
for _ in {1..50}; do [[ -s $W/sshd.pid ]] && break; sleep 0.1; done
pass 'Installer sshd policy (ForceCommand + ExposeAuthInfo) accepted by OpenSSH'

as() { # as PERSON git ARGS...
    local person=$1; shift
    GIT_SSH_COMMAND="ssh -F /dev/null -i $W/keys/$person -p $PORT -o IdentitiesOnly=yes -o BatchMode=yes -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o LogLevel=ERROR" \
        git -c user.name="$person" -c user.email="$person@test" -c init.defaultBranch=main "$@"
}
sshas() { local person=$1; shift; ssh -F /dev/null -i "$W/keys/$person" -p "$PORT" -o IdentitiesOnly=yes -o BatchMode=yes -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o LogLevel=ERROR "$@"; }
URL=ssh://$GIT_USER@127.0.0.1:$PORT
cd "$W/clients"

as alice clone -q "$URL/proj.git" alice-proj 2>/dev/null && pass 'Write user clones' || fail 'alice clone'
printf 'hello\n' > alice-proj/README.md
as alice -C alice-proj add README.md && as alice -C alice-proj commit -qm 'first' && as alice -C alice-proj push -q origin HEAD:main 2>/dev/null && pass 'Write user pushes' || fail 'alice push'
as bob clone -q "$URL$W/repos/proj.git" bob-proj 2>/dev/null && [[ -f bob-proj/README.md ]] && pass 'Read user clones (absolute server path form)' || fail 'bob clone'
printf 'bob\n' >> bob-proj/README.md
as bob -C bob-proj commit -qam 'bob change'
out=$(as bob -C bob-proj push origin HEAD:main 2>&1) && fail 'read-only user pushed' || true
[[ $out == *'read-only access to proj'* ]] && pass 'Read user cannot push (clear message)' || fail "bob push message: $out"
out=$(as dave clone -q "$URL/proj.git" dave-proj 2>&1) && fail 'ungranted user cloned' || true
[[ $out == *"not found or dave has no access"* ]] && pass 'User without grant cannot clone' || fail "dave: $out"
out=$(as dave ls-remote "$URL/secret.git" 2>&1) && fail 'ungranted secret' || true
out2=$(as dave ls-remote "$URL/doesnotexist.git" 2>&1) || true
[[ ${out//secret/X} == "${out2//doesnotexist/X}" ]] && pass 'Forbidden and missing repositories look identical' || fail "$out | $out2"
as boss ls-remote "$URL/secret.git" >/dev/null 2>&1 && as boss ls-remote "$URL/proj.git" | grep -q refs/heads/main && pass 'Admin has access to every repository' || fail 'boss'
out=$(as carol ls-remote "$URL/proj.git" 2>&1) && fail 'unlinked key accepted' || true
[[ $out == *'not linked to a PrivateGit user'* ]] && pass 'Unlinked (old-style) key is refused' || fail "carol: $out"
as alice2 ls-remote "$URL/proj.git" >/dev/null 2>&1 && fail '--from restriction ignored' || pass 'Per-key --from network restriction enforced by sshd'
out=$(sshas alice "$GIT_USER@127.0.0.1" 2>&1 </dev/null) && fail 'shell login succeeded' || true
[[ $out == *'Interactive shells are disabled'* ]] && pass 'No interactive shell' || fail "shell: $out"
for command in 'id' 'sh -c id' 'git-upload-pack /etc' "git-upload-pack '../../etc/passwd'" "git-receive-pack 'proj.git'; touch $W/pwned" 'scp -t /tmp' 'git-upload-archive proj.git'; do
    out=$(sshas alice "$GIT_USER@127.0.0.1" "$command" 2>&1 </dev/null) && fail "command allowed: $command" || true
    [[ $out == *'PrivateGit:'* && $out != *uid=* ]] || fail "unexpected output for $command: $out"
done
[[ ! -e $W/pwned ]] && pass 'Arbitrary commands, paths and injections are refused' || fail 'injection executed'
# Try to tunnel to this sshd itself: if forwarding worked we would read its SSH banner.
sshas alice -N -L "127.0.0.1:2398:127.0.0.1:$PORT" "$GIT_USER@127.0.0.1" >/dev/null 2>&1 </dev/null & fwd=$!
sleep 1.5
banner=$(timeout 3 bash -c 'exec 3<>/dev/tcp/127.0.0.1/2398 && head -c 7 <&3' 2>/dev/null || true)
kill "$fwd" 2>/dev/null || true
[[ $banner != SSH-2.0 ]] && pass 'Port forwarding refused by the server' || fail 'port forwarding allowed'

# Branch deletion over SSH: only the branch's creator (or an admin); never main or tags.
owner_of() { git --git-dir "$W/repos/proj.git" config --get "branch.$1.privategitowner" 2>/dev/null; }
has_branch() { as boss ls-remote "$URL/proj.git" | grep -q "refs/heads/$1\$"; }
as alice -C alice-proj push -q origin HEAD:refs/heads/feature-x HEAD:refs/heads/feature-y 2>/dev/null
[[ $(owner_of feature-x) == alice && $(owner_of feature-y) == alice ]] && pass 'Creator recorded when a branch is pushed' || fail "owner not recorded: $(owner_of feature-x)"
admin access-grant proj bob write >/dev/null
as bob -C bob-proj fetch -q origin 2>/dev/null; as bob -C bob-proj push -q origin origin/main:refs/heads/bob-fix 2>/dev/null
[[ $(owner_of bob-fix) == bob ]] || fail 'bob owner not recorded'
out=$(as bob -C bob-proj push origin --delete feature-y 2>&1) && fail "bob deleted alice's branch" || true
[[ $out == *'feature-y was created by alice'* ]] && has_branch feature-y && pass "Write user cannot delete someone else's branch" || fail "bob delete feature-y: $out"
out=$(as alice -C alice-proj push origin --delete bob-fix 2>&1) && fail "alice deleted bob's branch" || true
[[ $out == *'bob-fix was created by bob'* ]] && has_branch bob-fix && pass "...and the other way round" || fail "alice delete bob-fix: $out"
out=$(as alice -C alice-proj push origin --delete feature-x 2>&1) || fail "creator could not delete own branch: $out"
[[ $out == *'branch feature-x deleted by alice'* ]] && ! has_branch feature-x && [[ -z $(owner_of feature-x) ]] && pass 'Creator deletes own branch (owner record removed)' || fail "delete feature-x: $out"
out=$(as bob -C bob-proj push origin --delete bob-fix 2>&1) && ! has_branch bob-fix && pass 'Second user deletes own branch' || fail "bob delete own: $out"
runuser -u "$DASH_USER" -- git --git-dir "$W/repos/proj.git" update-ref refs/heads/legacy "$(git --git-dir "$W/repos/proj.git" rev-parse main)"
out=$(as alice -C alice-proj push origin --delete legacy 2>&1) && fail 'unowned branch deleted by user' || true
[[ $out == *'unknown user (before ownership tracking)'* ]] && has_branch legacy && pass 'Branch without recorded creator: users cannot delete it' || fail "legacy: $out"
out=$(as boss -C alice-proj push origin --delete legacy 2>&1) && ! has_branch legacy && pass 'Admin deletes an unowned branch' || fail "boss legacy: $out"
out=$(as boss -C alice-proj push origin --delete feature-y 2>&1) && ! has_branch feature-y && pass "Admin deletes someone else's branch" || fail "boss feature-y: $out"
out=$(as alice -C alice-proj push origin --delete main 2>&1) && fail 'main branch deleted' || true
out2=$(as boss -C alice-proj push origin --delete main 2>&1) && fail 'admin deleted main' || true
[[ $out == *'main branch (main) cannot be deleted'* && $out2 == *'cannot be deleted'* ]] && has_branch main && pass 'Main branch protected, even from admins' || fail "delete main: $out / $out2"
as alice -C alice-proj tag v1.0 && as alice -C alice-proj push -q origin v1.0 2>/dev/null
out=$(as alice -C alice-proj push origin --delete v1.0 2>&1) && fail 'tag deleted' || true
[[ $out == *'version label and cannot be deleted'* ]] && pass 'Tag deletion refused' || fail "delete tag: $out"
admin access-grant proj bob read >/dev/null
as alice -C alice-proj push -q origin HEAD:refs/heads/feature-z 2>/dev/null
out=$(as bob -C bob-proj push origin --delete feature-z 2>&1) && fail 'read user deleted a branch' || true
[[ $out == *'read-only access'* ]] && has_branch feature-z && pass 'Read user cannot delete branches' || fail "bob read delete: $out"
chmod 0644 "$W/hooks/pre-receive"
out=$(as alice -C alice-proj push origin --delete feature-z 2>&1) && fail 'deleted without protection hook' || true
has_branch feature-z && pass 'Fail-safe: no executable hook means no deletion at all' || fail "fail-safe: $out"
chmod 0755 "$W/hooks/pre-receive"; sed -i 's/^PrivateGit__AllowBranchDelete=true/PrivateGit__AllowBranchDelete=false/' "$W/etc/environment"
out=$(as alice -C alice-proj push origin --delete feature-z 2>&1) && fail 'deleted with AllowBranchDelete=false' || true
has_branch feature-z && pass 'AllowBranchDelete=false turns deletion off' || fail "setting off: $out"
sed -i 's/^PrivateGit__AllowBranchDelete=false/PrivateGit__AllowBranchDelete=true/' "$W/etc/environment"
as alice -C alice-proj commit -q --allow-empty -m 'second' && as alice -C alice-proj push -q origin HEAD:main 2>/dev/null
out=$(as alice -C alice-proj push --force origin HEAD~1:main 2>&1) && fail 'force push accepted' || true
[[ $out == *'non-fast-forward'* || $out == *'rejected'* ]] && pass 'Force-push still refused' || fail "force push: $out"
# Hidden backup of main: every push to main is copied outside the served repositories.
snapshots() { git --git-dir "$1" for-each-ref refs/snapshots 2>/dev/null | wc -l; }
live_main=$(git --git-dir "$W/repos/proj.git" rev-parse main)
[[ -d $W/backups/proj.git && $(git --git-dir "$W/backups/proj.git" rev-parse main) == "$live_main" ]] && pass 'Push to main updates the hidden server backup' || fail 'backup missing or stale'
n1=$(snapshots "$W/backups/proj.git"); ((n1 >= 1)) && pass "Backup keeps timestamped snapshots of main ($n1 so far)" || fail 'no snapshots'
for _ in $(seq 1 50); do [[ $(git --git-dir "$W/nas/proj.git" rev-parse main 2>/dev/null) == "$live_main" ]] && break; sleep 0.2; done
[[ $(git --git-dir "$W/nas/proj.git" rev-parse main 2>/dev/null) == "$live_main" ]] && pass 'NAS copy of main written in the background' || fail 'NAS copy missing'
[[ ! -d $W/nas/secret.git ]] && pass 'NAS copy only for selected repositories' || fail 'unselected repo copied to NAS'
! as boss ls-remote "$URL/proj.git" | grep -q snapshots && pass 'Snapshots are not visible to clients (even admins)' || fail 'snapshots visible'
for path in "$W/backups/proj.git" "../backups/proj.git" "backups/proj"; do
    out=$(as boss ls-remote "$URL/$path" 2>&1) && fail "backup reachable over SSH via $path" || true
done
pass 'Backups cannot be reached over SSH by any path'
as alice -C alice-proj commit -q --allow-empty -m 'third' && as alice -C alice-proj push -q origin HEAD:main 2>/dev/null
n2=$(snapshots "$W/backups/proj.git"); [[ $n2 -eq $((n1 + 1)) && $(git --git-dir "$W/backups/proj.git" rev-parse main) == $(git --git-dir "$W/repos/proj.git" rev-parse main) ]] && pass 'Each new main version adds a snapshot; old ones kept' || fail "snapshots $n1 -> $n2"
as alice -C alice-proj push -q origin HEAD:refs/heads/side 2>/dev/null; [[ $(snapshots "$W/backups/proj.git") -eq $n2 ]] && pass 'Pushes to other branches do not touch the backup' || fail 'side branch made snapshot'
sed -i 's/^PrivateGit__MainBackup=true/PrivateGit__MainBackup=false/' "$W/etc/environment"
as alice -C alice-proj commit -q --allow-empty -m 'fourth' && as alice -C alice-proj push -q origin HEAD:main 2>/dev/null
[[ $(snapshots "$W/backups/proj.git") -eq $n2 ]] && pass 'MainBackup=false turns the backup off' || fail 'backup ran while off'
sed -i 's/^PrivateGit__MainBackup=false/PrivateGit__MainBackup=true/; s#^PrivateGit__NasBackupDirectory=.*#PrivateGit__NasBackupDirectory=/nonexistent/nas-offline#' "$W/etc/environment"
as alice -C alice-proj commit -q --allow-empty -m 'fifth' && t0=$(date +%s%N); out=$(as alice -C alice-proj push origin HEAD:main 2>&1); t1=$(( ($(date +%s%N) - t0) / 1000000 ))
[[ $out != *rejected* && $(git --git-dir "$W/backups/proj.git" rev-parse main) == $(git --git-dir "$W/repos/proj.git" rev-parse main) && $t1 -lt 5000 ]] && pass "NAS offline: push still succeeds quickly (${t1} ms), server backup still updated" || fail "nas offline: $out ($t1 ms)"
mv "$W/backups/proj.git" "$W/backups/proj.saved" && : > "$W/backups/proj.git"   # broken backup target
as alice -C alice-proj commit -q --allow-empty -m 'sixth'; out=$(as alice -C alice-proj push origin HEAD:main 2>&1)
rm -f "$W/backups/proj.git" && mv "$W/backups/proj.saved" "$W/backups/proj.git"
[[ $out == *'server backup of main could not be updated (your push is safe)'* && $out != *rejected* ]] && pass 'Backup failure never blocks a push (warning shown)' || fail "backup failure: $out"
# A HUNG NAS (not just offline): pushes stay fast, server backup continues, no pile-up.
sed -i "s#^PrivateGit__NasBackupDirectory=.*#PrivateGit__NasBackupDirectory=$W/nas#" "$W/etc/environment"; touch "$W/nas-hang"
slowest=0
for i in 1 2 3; do
    as alice -C alice-proj commit -q --allow-empty -m "hung-nas $i"; t0=$(date +%s%N)
    out=$(as alice -C alice-proj push origin HEAD:main 2>&1) || fail "push $i failed with hung NAS: $out"
    ms=$(( ($(date +%s%N) - t0) / 1000000 )); ((ms > slowest)) && slowest=$ms
done
stuck=$(pgrep -u "$GIT_USER" -x sleep | wc -l)
[[ $(git --git-dir "$W/backups/proj.git" rev-parse main) == $(git --git-dir "$W/repos/proj.git" rev-parse main) ]] && ((slowest < 3000)) && pass "Hung NAS: 3 pushes stayed fast (slowest ${slowest} ms), server backup current" || fail "hung NAS slow ($slowest ms) or server backup stale"
((stuck == 1)) && pass 'Hung NAS: stuck copies do not pile up (one at a time per repository)' || fail "$stuck stuck NAS copies"
nas_before=$(git --git-dir "$W/nas/proj.git" rev-parse main)
rm -f "$W/nas-hang"; pkill -u "$GIT_USER" -x sleep; sleep 1      # NAS recovers
as alice -C alice-proj commit -q --allow-empty -m 'nas back'; as alice -C alice-proj push -q origin HEAD:main 2>/dev/null
for _ in $(seq 1 50); do [[ $(git --git-dir "$W/nas/proj.git" rev-parse main) == $(git --git-dir "$W/repos/proj.git" rev-parse main) ]] && break; sleep 0.2; done
[[ $(git --git-dir "$W/nas/proj.git" rev-parse main) == $(git --git-dir "$W/repos/proj.git" rev-parse main) && $nas_before != $(git --git-dir "$W/nas/proj.git" rev-parse main) ]] && pass 'NAS catches up with the latest main after it recovers' || fail 'NAS did not catch up'
admin access-revoke proj alice >/dev/null
printf 'more\n' >> alice-proj/README.md
as alice -C alice-proj commit -qam 'after revoke'
as alice -C alice-proj push -q origin HEAD:main 2>/dev/null && fail 'push after revoke' || pass 'Revocation is effective immediately'
admin access-grant proj alice read >/dev/null
as alice -C alice-proj fetch -q origin 2>/dev/null && pass 'Downgrade to read keeps fetch working' || fail 'alice fetch'
bash "$ROOT/add-client.sh" --revoke bob >/dev/null
as bob -C bob-proj fetch -q origin 2>/dev/null && fail 'revoked key works' || pass 'add-client --revoke removes all keys of a user'
grep -q 'privategit-ssh\|PrivateGit' "$W/sshd.log" 2>/dev/null || true
printf '\nAll %s SSH gate checks passed.\n' "$passed"
