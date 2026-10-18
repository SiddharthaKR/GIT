#!/usr/bin/env bash
# Uninstall type 2 of 2: remove PrivateGit COMPLETELY from this server.
#
#   sudo bash uninstall-remove.sh               export everything first, then remove
#   sudo bash uninstall-remove.sh --no-export   remove without an export (data is lost)
#
# Removes only what install.sh created:
#   service, nginx site, SSH rules for the git account, accounts git/gitdash, group gitrepos,
#   /opt/privategit /etc/privategit /var/lib/privategit /srv/privategit /var/backups/privategit
# Never touched: your users and SSH login rules, other nginx sites, the nginx package,
# system certificates, the firewall, and copies on a NAS.
set -Eeuo pipefail
cd /
MARKER=/etc/privategit/installer-owned
CONFIG_FILES=(/etc/systemd/system/privategit.service /etc/nginx/sites-enabled/privategit /etc/nginx/sites-available/privategit /etc/ssh/sshd_config.d/60-privategit.conf)
DATA_DIRS=(/opt/privategit /etc/privategit /var/lib/privategit /srv/privategit /var/backups/privategit)
die() { printf 'ERROR: %s\n' "$*" >&2; exit 1; }
say() { printf '  %s\n' "$*"; }

EXPORT=true
case "${1:-}" in
    '') ;;
    --no-export) EXPORT=false ;;
    -h|--help) sed -n '2,12p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) die "Unknown option: $1 (use --no-export or --help)" ;;
esac
[[ $EUID == 0 ]] || die 'Run with sudo.'
[[ -t 0 ]] || die 'Run in an interactive terminal (a confirmation is required).'
[[ -f $MARKER && ! -L $MARKER ]] && grep -qx 'privategit-installer-v1' "$MARKER" \
    || die 'No PrivateGit installation (made by install.sh) was found on this server. Nothing was removed.'
for dir in "${DATA_DIRS[@]}"; do [[ ! -L $dir ]] || die "Refusing: $dir is a symbolic link. Ask IT to inspect it."; done

# ---- What will be removed ------------------------------------------------------------
repos=0; for r in /srv/privategit/repositories/*.git; do [[ -d $r ]] && repos=$((repos + 1)); done
printf 'This REMOVES PrivateGit completely from %s:\n' "$(hostname)"
say "$repos repositories in /srv/privategit/repositories ($(du -sh /srv/privategit 2>/dev/null | cut -f1) incl. hidden backups)"
say 'dashboard accounts, permissions and SSH keys (/var/lib/privategit, /etc/privategit)'
say 'the program (/opt/privategit), its service, nginx site and SSH rules'
say 'the accounts git and gitdash and the group gitrepos'
say 'not touched: your users, your SSH login, other nginx sites, nginx itself, NAS copies'

home=$(getent passwd "${SUDO_USER:-root}" | cut -d: -f6); home=${home:-/root}
archive=$home/privategit-export-$(date -u +%Y%m%dT%H%M%SZ).tgz
if [[ $EXPORT == true ]]; then
    printf '\nAn export is made first: %s\n' "$archive"
    say 'contains repositories, hidden backups, accounts, permissions, keys, settings and the CA'
    say 'readable by root only (it holds password hashes and authenticator secrets)'
else
    printf '\nNO export will be made. Repositories and accounts will be lost permanently.\n'
fi
printf '\n'
read -r -p 'Type REMOVE to continue: ' answer
[[ $answer == REMOVE ]] || { printf 'Nothing changed.\n'; exit 0; }

# ---- 1. Export (any failure stops here, before anything is deleted) -------------------
if [[ $EXPORT == true ]]; then
    printf '\n1/5 Exporting...\n'
    existing=(); for dir in /srv/privategit /var/lib/privategit /etc/privategit; do [[ -e $dir ]] && existing+=("${dir#/}"); done
    (umask 0077; tar -C / -czf "$archive" "${existing[@]}")
    chown root:root "$archive"; chmod 0600 "$archive"
    tar -tzf "$archive" >/dev/null || die "The export $archive could not be read back. Nothing was removed."
    say "saved $(du -h "$archive" | cut -f1): $archive"
else
    printf '\n1/5 Export skipped (--no-export).\n'
fi

# ---- 2. Stop the service ------------------------------------------------------------
printf '2/5 Stopping the dashboard...\n'
systemctl disable --now privategit >/dev/null 2>&1 || systemctl stop privategit >/dev/null 2>&1 || true

# ---- 3. Remove nginx/SSH/systemd files; restore them if the rest does not validate ---
printf '3/5 Removing the nginx site, SSH rules and service file...\n'
saved=$(mktemp -d)
for file in "${CONFIG_FILES[@]}"; do
    if [[ -e $file || -L $file ]]; then mkdir -p "$saved$(dirname "$file")"; cp -a -- "$file" "$saved$file"; rm -f -- "$file"; fi
done
if ! nginx -t >/dev/null 2>&1 || ! /usr/sbin/sshd -t; then
    for file in "${CONFIG_FILES[@]}"; do [[ -e $saved$file || -L $saved$file ]] && cp -a -- "$saved$file" "$file"; done
    rm -rf -- "$saved"
    die 'nginx or SSH configuration did not validate without the PrivateGit files; they were put back. Nothing else was removed.'
fi
rm -rf -- "$saved"
systemctl daemon-reload
systemctl reload nginx
systemctl reload ssh

# ---- 4. Remove the installer's accounts ----------------------------------------------
printf '4/5 Removing accounts git, gitdash and group gitrepos...\n'
for user in git gitdash; do
    getent passwd "$user" >/dev/null || continue
    pkill -u "$user" 2>/dev/null || true; sleep 1; pkill -9 -u "$user" 2>/dev/null || true
    user_home=$(getent passwd "$user" | cut -d: -f6)
    userdel "$user"
    # Only the git account's own home made by install.sh (adduser) is deleted.
    if [[ $user == git && $user_home == /home/git && -d /home/git && ! -L /home/git ]]; then rm -rf --one-file-system -- /home/git; fi
done
if getent group gitrepos >/dev/null; then groupdel gitrepos; fi

# ---- 5. Remove the installer's folders -----------------------------------------------
printf '5/5 Removing PrivateGit folders...\n'
for dir in "${DATA_DIRS[@]}"; do [[ -e $dir ]] && rm -rf --one-file-system -- "$dir"; done
rm -f -- /run/lock/privategit-install.lock

printf '\nPrivateGit has been removed.\n'
if [[ $EXPORT == true ]]; then
    printf 'Export: %s\n' "$archive"
    printf '  The repositories inside are normal Git repositories. To get one back:\n'
    printf '    mkdir ~/restore && sudo tar -xzf %s -C ~/restore srv/privategit/repositories\n' "$archive"
    printf '    sudo chown -R "$USER": ~/restore && chmod -R u+rwX ~/restore\n'
    printf '    git clone ~/restore/srv/privategit/repositories/NAME.git\n'
fi
printf 'Optional: if nothing else uses nginx, remove it with: sudo apt-get remove nginx\n'
printf 'On clients you may delete the imported privategit-ca.crt and the "git@" SSH settings.\n'
