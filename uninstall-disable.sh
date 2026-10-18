#!/usr/bin/env bash
# Uninstall type 1 of 2: turn PrivateGit OFF but keep everything.
# Repositories, accounts, SSH keys, permissions, backups and settings stay on disk.
#
#   sudo bash uninstall-disable.sh          turn off (asks you to type DISABLE)
#   sudo bash uninstall-disable.sh --undo   turn back on exactly as before
#
# What "off" means:
#   - the dashboard service is stopped and will not start at boot
#   - nginx no longer serves the PrivateGit site (other nginx sites are untouched)
#   - the SSH rules for the git account are parked and the git account is locked,
#     so nobody can clone or push
# Your own logins, other nginx sites and system certificates are never touched.
set -Eeuo pipefail
cd /
MARKER=/etc/privategit/installer-owned
PARK=/etc/privategit/disabled
SITE=/etc/nginx/sites-enabled/privategit
SITE_SOURCE=/etc/nginx/sites-available/privategit
SSHD=/etc/ssh/sshd_config.d/60-privategit.conf
die() { printf 'ERROR: %s\n' "$*" >&2; exit 1; }
say() { printf '  %s\n' "$*"; }

MODE=disable
case "${1:-}" in
    '') ;;
    --undo) MODE=undo ;;
    -h|--help) sed -n '2,13p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) die "Unknown option: $1 (use --undo or --help)" ;;
esac
[[ $EUID == 0 ]] || die 'Run with sudo.'
[[ -f $MARKER && ! -L $MARKER ]] && grep -qx 'privategit-installer-v1' "$MARKER" \
    || die 'No PrivateGit installation (made by install.sh) was found on this server.'
getent passwd git >/dev/null || die 'The git account is missing; this installation looks incomplete.'

configs_ok() { nginx -t >/dev/null 2>&1 && /usr/sbin/sshd -t; }
reload_web_and_ssh() { systemctl reload nginx; systemctl reload ssh; }

if [[ $MODE == disable ]]; then
    [[ ! -e $PARK/60-privategit.conf ]] || { printf 'PrivateGit is already disabled. Turn it back on with: sudo bash %s --undo\n' "$0"; exit 0; }
    [[ -f $SSHD ]] || die "$SSHD is missing; nothing to disable."
    printf 'This turns PrivateGit OFF and keeps all data:\n'
    say 'stop the dashboard and keep it from starting at boot'
    say 'remove the PrivateGit site from nginx (other sites stay)'
    say 'park the SSH rules for the git account and lock that account'
    printf 'Repositories, accounts, keys and backups are NOT deleted. Undo with --undo.\n\n'
    [[ -t 0 ]] || die 'Run in an interactive terminal (a confirmation is required).'
    read -r -p 'Type DISABLE to continue: ' answer
    [[ $answer == DISABLE ]] || { printf 'Nothing changed.\n'; exit 0; }

    systemctl disable --now privategit >/dev/null 2>&1 || systemctl stop privategit || true
    install -d -o root -g root -m 0700 "$PARK"
    mv -f -- "$SSHD" "$PARK/60-privategit.conf"
    rm -f -- "$SITE"
    usermod --expiredate 1 git          # expired account: SSH refuses it even with a valid key
    if ! configs_ok; then
        # Put everything back if the remaining nginx/SSH configuration does not validate.
        mv -f -- "$PARK/60-privategit.conf" "$SSHD"; ln -sfn -- "$SITE_SOURCE" "$SITE"
        usermod --expiredate '' git; systemctl enable --now privategit >/dev/null 2>&1 || true
        die 'nginx or SSH configuration did not validate after the change; everything was restored.'
    fi
    reload_web_and_ssh
    ! systemctl is-active --quiet privategit || die 'The dashboard is still running; check: systemctl status privategit'
    printf '\nPrivateGit is OFF. Data kept in /srv/privategit and /var/lib/privategit.\n'
    printf 'Turn it back on with: sudo bash %s --undo\n' "$0"
    exit 0
fi

# --undo
[[ -f $PARK/60-privategit.conf ]] || die 'PrivateGit is not disabled by this script (nothing to undo).'
[[ -f $SITE_SOURCE ]] || die "$SITE_SOURCE is missing; rerun install.sh from the package folder instead."
mv -f -- "$PARK/60-privategit.conf" "$SSHD"
ln -sfn -- "$SITE_SOURCE" "$SITE"
usermod --expiredate '' git
if ! configs_ok; then
    mv -f -- "$SSHD" "$PARK/60-privategit.conf"; rm -f -- "$SITE"; usermod --expiredate 1 git
    die 'nginx or SSH configuration did not validate; PrivateGit stays disabled. Ask IT to check: nginx -t; sshd -t'
fi
reload_web_and_ssh
systemctl enable --now privategit >/dev/null 2>&1 || systemctl start privategit
rmdir -- "$PARK" 2>/dev/null || true
host=$(sed -n 's/^AllowedHosts=//p' /etc/privategit/environment | tail -n 1)
healthy=false
for attempt in {1..15}; do
    if curl --noproxy '*' --silent --output /dev/null --fail --max-time 3 -H "Host: ${host:-localhost}" -H 'X-Forwarded-Proto: https' http://127.0.0.1:5080/Login; then healthy=true; break; fi
    sleep 1
done
[[ $healthy == true ]] || die 'Configuration restored, but the dashboard did not answer. Check: journalctl -u privategit'
printf 'PrivateGit is ON again: https://%s (Git over SSH works again too).\n' "${host:-this-server}"
