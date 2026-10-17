#!/usr/bin/env bash
# Day-to-day administration on the Git server. Runs PrivateGit as the dashboard
# service account with the installed configuration.
set -Eeuo pipefail
ROOT=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)
usage() {
    cat <<'EOF'
Usage: sudo bash /opt/privategit/admin.sh COMMAND

People (dashboard login = password + authenticator code):
  user-add NAME Admin|User     create an account (prompts for password, shows TOTP secret)
  user-reset NAME Admin|User   new password + authenticator secret; also changes role
  user-delete NAME             delete the account and all its repository permissions
  user-list                    list accounts and roles

Repository permissions (web and SSH, effective immediately):
  access-grant REPO USER read|write
  access-revoke REPO USER
  access-list [REPO]

Hidden backups of the main branch (admins only; users cannot see them):
  backup-list REPO [--nas]                  list snapshots (newest first)
  backup-restore REPO [SNAPSHOT] [--nas]    existing repo: restore as a NEW branch
                                            (main is never changed); missing repo:
                                            recreate it from the backup
  backup-now [REPO] [--server-only]         back up main now (all repositories by default);
                                            an unreachable NAS is skipped after 10 seconds

SSH keys: sudo bash /opt/privategit/add-client.sh USER key.pub | --list | --revoke USER
Admins always have full access. Users see only repositories granted to them.
EOF
}
[[ $# -ge 1 && $1 != --help && $1 != -h ]] || { usage; exit 0; }
[[ $EUID == 0 ]] || { printf 'Run with sudo.\n' >&2; exit 1; }
[[ -f /etc/privategit/environment ]] || { printf 'PrivateGit is not installed (missing /etc/privategit/environment).\n' >&2; exit 1; }
mapfile -t settings < <(grep -E '^[A-Za-z_][A-Za-z0-9_]*=[^[:space:]]*$' /etc/privategit/environment)
run() { (cd "$ROOT" && runuser -u gitdash -- env "${settings[@]}" ASPNETCORE_ENVIRONMENT=Production "$ROOT/PrivateGit" "$@"); }
setting() { local value; value=$(sed -n "s/^$1=//p" /etc/privategit/environment | tail -n 1); printf '%s' "${value:-$2}"; }
gd() { (umask 0007; cd / && runuser -u gitdash -- git -c safe.directory='*' -c core.hooksPath=/dev/null "$@"); }
die() { printf '%s\n' "$*" >&2; exit 1; }
backup() {
    local command=$1; shift
    local repos backups nas nas_list root=backups repo="" snapshot="" args=() a server_only=false nas_ok=false
    repos=$(setting PrivateGit__RepositoryDirectory /srv/privategit/repositories)
    backups=$(setting PrivateGit__BackupDirectory /srv/privategit/backups)
    nas=$(setting PrivateGit__NasBackupDirectory "")
    nas_list=$(setting PrivateGit__NasBackupRepositories '*')
    for a in "$@"; do case $a in --nas) root=nas ;; --server-only) server_only=true ;; *) args+=("$a") ;; esac; done
    repo=${args[0]:-}; snapshot=${args[1]:-latest}
    [[ -z $repo || $repo =~ ^[a-z][a-z0-9_-]{0,62}$ ]] || die "Invalid repository name: $repo"
    [[ -z $snapshot || $snapshot == latest || $snapshot =~ ^[0-9]{8}T[0-9]{6}Z-[0-9a-f]{8}$ ]] || die "Invalid snapshot name: $snapshot"
    local source=$backups
    # The NAS is optional: probe it once with a short limit and never wait on it otherwise.
    if [[ $nas == /* && $server_only == false ]] && timeout -k 2 10 test -d "$nas" 2>/dev/null; then nas_ok=true; fi
    if [[ $root == nas ]]; then
        [[ $nas == /* ]] || die 'No NAS backup folder configured (PrivateGit__NasBackupDirectory).'
        [[ $nas_ok == true ]] || die "The NAS folder $nas is not reachable right now (checked for 10 s). Server backups are unaffected; use them without --nas."
        source=$nas
    fi
    case $command in
    backup-now)
        local list=() r branch count=0
        if [[ -n $repo ]]; then list=("$repo"); else for r in "$repos"/*.git; do [[ -d $r ]] && list+=("$(basename "$r" .git)"); done; fi
        for r in "${list[@]}"; do
            [[ -f $repos/$r.git/HEAD ]] || { printf '%-28s missing\n' "$r"; continue; }
            branch=$(gd --git-dir "$repos/$r.git" symbolic-ref -q --short HEAD || echo main)
            gd --git-dir "$repos/$r.git" rev-parse -q --verify "refs/heads/$branch" >/dev/null || { printf '%-28s empty (nothing to back up)\n' "$r"; continue; }
            if (umask 0007; cd / && runuser -u gitdash -- "$ROOT/hooks/backup-main" "$repos/$r.git" "$backups" "$r" "$branch"); then status=server; else status=FAILED; fi
            if [[ $nas == /* && $server_only == false ]] && { [[ $(echo "$nas_list" | tr -d ' ') == '*' ]] || [[ ",$(echo "$nas_list" | tr -d ' ')," == *",$r,"* ]]; }; then
                if [[ $nas_ok != true ]]; then status+=" (NAS not reachable: skipped)"
                elif (umask 0007; cd / && timeout -k 10 300 runuser -u gitdash -- flock -w 5 "$backups/.nas-$r.lock" "$ROOT/hooks/backup-main" "$repos/$r.git" "$nas" "$r" "$branch"); then status+=" + NAS"
                else status+=" + NAS FAILED (server backup is fine)"; fi
            fi
            printf '%-28s %s\n' "$r" "$status"; count=$((count + 1))
        done ;;
    backup-list)
        [[ -n $repo ]] || die 'Usage: backup-list REPO [--nas]'
        [[ -d $source/$repo.git ]] || die "No backup of $repo in $source yet."
        printf 'Snapshots of %s (newest first) in %s:\n' "$repo" "$source"
        timeout -k 5 60 runuser -u gitdash -- git -c safe.directory='*' --git-dir "$source/$repo.git" for-each-ref --sort=-refname --count="${LIMIT:-30}" --format='  %(refname:strip=2)   %(contents:subject)' refs/snapshots ;;
    backup-restore)
        [[ -n $repo ]] || die 'Usage: backup-restore REPO [SNAPSHOT|latest] [--nas]'
        local backup_repo=$source/$repo.git live=$repos/$repo.git ref commit label
        [[ -d $backup_repo ]] || die "No backup of $repo in $source."
        if [[ $snapshot == latest ]]; then ref=$(gd --git-dir "$backup_repo" for-each-ref --count=1 --format='%(refname)' refs/heads/); else ref=refs/snapshots/$snapshot; fi
        commit=$(gd --git-dir "$backup_repo" rev-parse -q --verify "$ref^{commit}") || die "Snapshot not found: $snapshot (see backup-list)."
        if [[ -f $live/HEAD ]]; then
            label=restored-main-$(date -u +%Y%m%dT%H%M%SZ)
            gd --git-dir "$live" fetch -q --no-tags --upload-pack="git -c safe.directory='*' upload-pack" "$backup_repo" "$ref:refs/heads/$label" || die 'Restore failed.'
            printf 'Restored %s (%s) into %s as the new branch %s.\nThe main branch was NOT changed. Review the branch, then merge it or ask users to continue from it.\nOnly an admin can delete this branch.\n' "${snapshot}" "${commit:0:8}" "$repo" "$label"
        else
            gd clone -q --bare --no-hardlinks --upload-pack="git -c safe.directory='*' upload-pack" "$backup_repo" "$live" || die 'Recreating the repository failed.'
            gd --git-dir "$live" remote remove origin 2>/dev/null || true
            local main_ref; main_ref=$(gd --git-dir "$backup_repo" for-each-ref --count=1 --format='%(refname)' refs/heads/)
            gd --git-dir "$live" update-ref "$main_ref" "$commit"
            gd --git-dir "$live" symbolic-ref HEAD "$main_ref"
            gd --git-dir "$live" config core.sharedRepository group
            gd --git-dir "$live" config receive.denyNonFastForwards true
            gd --git-dir "$live" config receive.denyDeletes true
            printf 'Recreated %s from the backup (main at %s). Existing dashboard permissions for %s apply again.\n' "$repo" "${commit:0:8}" "$repo"
        fi ;;
    esac
}
case "$1" in
    user-add|user-reset|user-delete)
        # The account store allows one process; pause the dashboard for the change.
        restart=false
        if systemctl is-active --quiet privategit; then systemctl stop privategit; restart=true; fi
        status=0
        run "$@" || status=$?
        if [[ $restart == true ]]; then systemctl start privategit; fi
        if [[ $1 == user-delete && $status == 0 && -x $ROOT/add-client.sh ]]; then
            bash "$ROOT/add-client.sh" --revoke "$2" || true
        fi
        exit "$status" ;;
    user-list|access-grant|access-revoke|access-list) run "$@" ;;
    backup-list|backup-restore|backup-now) backup "$@" ;;
    *) usage; exit 1 ;;
esac
