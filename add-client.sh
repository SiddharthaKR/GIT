#!/usr/bin/env bash
# Links one person's SSH public key to their PrivateGit user. The SSH gate uses this
# link to apply that user's per-repository permissions to clone/pull/push.
set -Eeuo pipefail
umask 077
ROOT=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)
source "$ROOT/deploy/install-common.sh"
KEY_FILE=${PRIVATEGIT_KEY_FILE:-/etc/privategit/authorized_keys}
ACCOUNTS=${PRIVATEGIT_ACCOUNTS:-/var/lib/privategit/accounts.json}
ETC=${PRIVATEGIT_ETC:-/etc/privategit}

usage() {
    cat <<'EOF'
Usage (on the Git server, with sudo):
  add-client.sh USER /absolute/path/key.pub [--from CIDR]
        Link an Ed25519 public key to dashboard user USER. One person may have
        several keys (one per machine, e.g. .126, .157, .158). --from limits this
        key to one network/machine, e.g. --from 172.20.1.157/32.
  add-client.sh --list            Show every linked key and its user
  add-client.sh --revoke USER     Remove all SSH keys of USER
Never supply a private key. Repository permissions are granted separately
(dashboard Access tab, or: admin.sh access-grant REPO USER read|write).
EOF
}
[[ ${1:-} != --help && ${1:-} != -h && $# -ge 1 ]] || { usage; exit 0; }
[[ $EUID == 0 || -n ${PRIVATEGIT_TEST:-} ]] || { pg_die 'Run with sudo on the Git server.'; exit 1; }
[[ -f $ETC/installer-owned && -f $ETC/allowed-networks ]] || { pg_die 'Complete the guided installer first.'; exit 1; }
[[ -f $KEY_FILE && ! -L $KEY_FILE ]] || { pg_die 'Authorized-key storage is missing or symbolic.'; exit 1; }
exec 9>"${PRIVATEGIT_LOCK:-/run/lock/privategit-key.lock}"
flock -x 9

write_keys() { # replace the key file atomically with the content of $1
    install -o "$(stat -c %u "$KEY_FILE")" -g "$(stat -c %g "$KEY_FILE")" -m 0640 "$1" "$KEY_FILE.new"
    mv -f -- "$KEY_FILE.new" "$KEY_FILE"
}

case "$1" in
--list)
    found=0
    while IFS= read -r line; do
        [[ $line =~ (ssh-ed25519[[:space:]]+[A-Za-z0-9+/=]+)[[:space:]]+privategit:([a-z][a-z0-9_-]{2,31}) ]] || continue
        found=1
        user=${BASH_REMATCH[2]}
        fingerprint=$(ssh-keygen -lf <(printf '%s\n' "${BASH_REMATCH[1]}") | cut -d' ' -f2)
        from=$(sed -n 's/^from="\([^"]*\)".*/\1/p' <<< "$line")
        printf '%-20s %s  from=%s\n' "$user" "$fingerprint" "${from:-any}"
    done < "$KEY_FILE"
    unlinked=$(grep -Evc '(^#|^$|privategit:[a-z])' "$KEY_FILE" || true)
    ((found)) || printf 'No linked keys yet.\n'
    ((unlinked == 0)) || printf '\n%s key line(s) from an older version are not linked to a user and are refused.\nRe-add them with: add-client.sh USER key.pub\n' "$unlinked"
    exit 0 ;;
--revoke)
    [[ $# -eq 2 ]] && pg_username "$2" || { usage; exit 1; }
    temporary=$(mktemp)
    trap 'rm -f -- "$temporary"' EXIT
    grep -Ev "(^|[[:space:]])privategit:$2([[:space:]]|$)" "$KEY_FILE" > "$temporary" || true
    removed=$(( $(wc -l < "$KEY_FILE") - $(wc -l < "$temporary") ))
    write_keys "$temporary"
    printf 'Removed %s SSH key(s) of %s. Effective for new connections immediately.\n' "$removed" "$2"
    exit 0 ;;
esac

USER_NAME=$1
[[ $# -eq 2 || ( $# -eq 4 && $3 == --from ) ]] || { usage; exit 1; }
pg_username "$USER_NAME" || { pg_die 'USER must be a dashboard username (3-32 lowercase letters, digits, _ or -).'; exit 1; }
pg_account_exists "$USER_NAME" "$ACCOUNTS" || { pg_die "No dashboard account '$USER_NAME'. Create it first: sudo bash /opt/privategit/admin.sh user-add $USER_NAME User"; exit 1; }
[[ -f $2 ]] || { pg_die 'Public-key file not found.'; exit 1; }
mapfile -t lines < <(sed 's/\r$//' -- "$2")
[[ ${#lines[@]} == 1 ]] || { pg_die 'Supply exactly one complete public key on one line.'; exit 1; }
public=${lines[0]}
pg_public_key_line "$public" || { pg_die 'Expected an Ed25519 .pub key, not a private key or an authorized_keys options line.'; exit 1; }
ssh-keygen -lf "$2" >/dev/null
if [[ $# -eq 4 ]]; then
    pg_cidr "$4" || { pg_die "Invalid --from network: $4 (example 172.20.1.157/32)."; exit 1; }
    networks=("$4")
else
    mapfile -t networks < "$ETC/allowed-networks"
fi
((${#networks[@]} > 0)) || { pg_die 'The approved network list is empty.'; exit 1; }
for network in "${networks[@]}"; do pg_cidr "$network" || { pg_die 'Invalid installed network allowlist.'; exit 1; }; done
HOST=$(sed -n 's/^AllowedHosts=//p' "$ETC/environment")
pg_host "$HOST" || { pg_die 'Invalid installed server address.'; exit 1; }
read -r kind material comment <<< "$public"
# Keep a short, harmless label of the original comment (e.g. sid@158) for --list readers.
label=$(printf '%s' "${comment:-}" | tr -cd 'A-Za-z0-9@._-' | head -c 48)
if owner=$(grep -F -- " $kind $material " "$KEY_FILE" | grep -Eo 'privategit:[a-z][a-z0-9_-]+' | head -n 1) && [[ -n $owner ]]; then
    pg_die "This key is already linked to ${owner#privategit:}. A key belongs to exactly one person; generate a separate key for $USER_NAME."
    exit 1
fi
from=$(IFS=,; printf '%s' "${networks[*]}")
temporary=$(mktemp)
trap 'rm -f -- "$temporary"' EXIT
# Drop an older, unlinked line for this same key (upgrade from the shared-key version).
awk -v key=" $kind $material" '{ line = " " $0; at = index(line, key); rest = substr(line, at + length(key), 1)
    if (at > 0 && (rest == "" || rest == " ") && line !~ /privategit:[a-z]/) next; print }' "$KEY_FILE" > "$temporary"
printf 'from="%s",restrict %s %s privategit:%s %s\n' "$from" "$kind" "$material" "$USER_NAME" "$label" >> "$temporary"
write_keys "$temporary"
printf 'Linked key to %s (allowed from %s). No SSH restart is needed.\n' "$USER_NAME" "$from"
printf '\n%s can clone/pull repositories where they have Read, and also push where they have Read & write.\n' "$USER_NAME"
printf 'Grant access in the dashboard (repository > Access) or: sudo bash /opt/privategit/admin.sh access-grant REPO %s read|write\n' "$USER_NAME"
printf '\nOn %s'"'"'s machine, add to ~/.ssh/config:\n\nHost privategit %s\n    HostName %s\n    User git\n    IdentityFile ~/.ssh/id_ed25519_privategit\n    IdentitiesOnly yes\n' "$USER_NAME" "$HOST" "$HOST"
printf '\nThen: git clone git@%s:REPO.git\n' "$HOST"
[[ ! -f /etc/ssh/ssh_host_ed25519_key.pub ]] || { printf 'Server SSH fingerprint (verify on first connect):\n'; ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub; }
