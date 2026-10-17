#!/usr/bin/env bash
# Shared validation/rendering functions. Sourcing this file makes no system changes.

pg_die() { printf '\nERROR: %s\n' "$*" >&2; return 1; }
pg_hostname() {
    local host=${1:-} label
    [[ ${#host} -le 253 && $host == *.* && $host != *..* && $host != localhost* ]] || return 1
    [[ $host =~ ^[a-z0-9.-]+$ && $host != *. && ! $host =~ ^[0-9.]+$ ]] || return 1
    local -a labels
    IFS=. read -r -a labels <<< "$host"
    for label in "${labels[@]}"; do
        [[ ${#label} -ge 1 && ${#label} -le 63 && $label =~ ^[a-z0-9]([a-z0-9-]*[a-z0-9])?$ ]] || return 1
    done
}
pg_ipv4() {
    local address=${1:-} octet
    [[ $address =~ ^([0-9]{1,3}\.){3}[0-9]{1,3}$ ]] || return 1
    local -a octets
    IFS=. read -r -a octets <<< "$address"
    for octet in "${octets[@]}"; do
        [[ $octet == 0 || $octet != 0* ]] || return 1
        ((10#$octet <= 255)) || return 1
    done
    [[ $address != 0.* && $address != 127.* && $address != 255.255.255.255 ]]
}
# The address people type in the browser/clone URL: an internal DNS name or, when the
# office has no internal DNS, the server's fixed IPv4 address (e.g. 172.20.1.126).
pg_host() { pg_hostname "${1:-}" || pg_ipv4 "${1:-}"; }
pg_cidr() {
    local network=${1:-} prefix address octet value=0 mask
    [[ $network =~ ^([0-9]{1,3}\.){3}[0-9]{1,3}/[0-9]{1,2}$ ]] || return 1
    address=${network%/*}; prefix=${network#*/}
    [[ $prefix == 0 || $prefix != 0* ]] || return 1
    prefix=$((10#$prefix))
    # An installation allowlist must not accidentally mean the entire internet.
    ((prefix >= 1 && prefix <= 32)) || return 1
    local -a octets
    IFS=. read -r -a octets <<< "$address"
    for octet in "${octets[@]}"; do
        [[ $octet == 0 || $octet != 0* ]] || return 1
        ((10#$octet <= 255)) || return 1
        value=$(((value << 8) | 10#$octet))
    done
    mask=$(((0xffffffff << (32 - prefix)) & 0xffffffff))
    (( (value & mask) == value )) # Reject addresses with host bits set.
}
pg_username() { [[ ${1:-} =~ ^[a-z][a-z0-9_-]{2,31}$ ]]; }
pg_public_key_line() {
    [[ ${1:-} =~ ^ssh-ed25519[[:space:]]+[A-Za-z0-9+/]+={0,3}([[:space:]].*)?$ && ! ${1:-} =~ [[:cntrl:]] ]]
}
pg_render_environment() {
    local host=$1
    pg_host "$host" || return 1
    cat <<EOF
AllowedHosts=$host
PrivateGit__DataDirectory=/var/lib/privategit
PrivateGit__RepositoryDirectory=/srv/privategit/repositories
PrivateGit__AccessFile=/srv/privategit/access/access.json
PrivateGit__AuthorizedKeysFile=/etc/privategit/authorized_keys
PrivateGit__CloneBaseUrl=git@$host:/srv/privategit/repositories
PrivateGit__GitExecutable=/usr/bin/git
PrivateGit__BehindProxy=true
PrivateGit__AllowBranchDelete=true
PrivateGit__HooksDirectory=/opt/privategit/hooks
PrivateGit__MainBackup=true
PrivateGit__BackupDirectory=/srv/privategit/backups
PrivateGit__NasBackupDirectory=
PrivateGit__NasBackupRepositories=*
DOTNET_EnableDiagnostics=0
EOF
}
# Keep administrator choices when the installer re-renders the environment on upgrade.
PG_PRESERVED_SETTINGS="PrivateGit__AllowBranchDelete PrivateGit__MainBackup PrivateGit__BackupDirectory PrivateGit__NasBackupDirectory PrivateGit__NasBackupRepositories"
pg_preserve_settings() {
    local new=$1 old=$2
    [[ -f $old ]] || { cat -- "$new"; return 0; }
    awk -v keys="$PG_PRESERVED_SETTINGS" '
        BEGIN { n = split(keys, k, " "); for (i = 1; i <= n; i++) keep[k[i]] = 1 }
        FNR == NR { split($0, kv, "="); if (kv[1] in keep) { old[kv[1]] = substr($0, length(kv[1]) + 2); seen[kv[1]] = 1 } next }
        { split($0, kv, "="); if (kv[1] in seen) { print kv[1] "=" old[kv[1]]; next } print }
    ' "$old" "$new"
}
pg_render_nginx() {
    local template=$1 host=$2 line network
    shift 2
    pg_host "$host" || return 1
    for network in "$@"; do pg_cidr "$network" || return 1; done
    while IFS= read -r line || [[ -n $line ]]; do
        if [[ $line == *'allow 192.168.1.0/24;'* ]]; then
            for network in "$@"; do printf '    allow %s;\n' "$network"; done
        else
            printf '%s\n' "${line//git.internal/$host}"
        fi
    done < "$template"
}
pg_render_sshd() {
    local network
    printf '# PrivateGit installer: applies to the git account only.\nMatch User git\n    AllowUsers'
    for network in "$@"; do pg_cidr "$network" || return 1; printf ' git@%s' "$network"; done
    cat <<'EOF'

    AuthenticationMethods publickey
    PubkeyAuthentication yes
    PasswordAuthentication no
    KbdInteractiveAuthentication no
    AuthorizedKeysFile /etc/privategit/authorized_keys
    DisableForwarding yes
    PermitTTY no
    PermitUserRC no
    X11Forwarding no
    # Every connection goes through the PrivateGit permission gate: it identifies the
    # user from the authenticating key and allows only permitted clone/fetch/push.
    ExposeAuthInfo yes
    ForceCommand /opt/privategit/PrivateGit git-serve --config /etc/privategit/environment
    AcceptEnv GIT_PROTOCOL
Match all
EOF
}
# Detected primary IPv4 address of this server (used only as a suggested default).
pg_primary_ipv4() {
    local address
    address=$(ip -4 route get 1.1.1.1 2>/dev/null | sed -n 's/.* src \([0-9.]*\).*/\1/p' | head -n 1)
    pg_ipv4 "$address" && printf '%s\n' "$address"
}
# Account names in the dashboard store (root-readable JSON written by the app).
pg_account_exists() {
    local user=$1 file=${2:-/var/lib/privategit/accounts.json}
    pg_username "$user" && [[ -f $file ]] && grep -Fq "\"Name\":\"$user\"" "$file"
}
# Private CA for offices without an IT-issued certificate. Idempotent: an existing CA is
# reused so clients that already trust it keep working; the server certificate is
# re-issued when the address changes or it is close to expiry.
pg_private_ca() {
    local host=$1 dir=$2 tls=$3 san wanted
    pg_host "$host" || return 1
    if pg_ipv4 "$host"; then san="IP:$host"; wanted="IP Address:$host"; else san="DNS:$host"; wanted="DNS:$host"; fi
    install -d -m 0700 "$dir"
    if [[ ! -s $dir/ca.pem || ! -s $dir/ca.key ]]; then
        openssl req -x509 -newkey rsa:3072 -nodes -sha256 -days 3650 -keyout "$dir/ca.key" -out "$dir/ca.pem" \
            -subj "/CN=PrivateGit private CA ($host)" \
            -addext 'basicConstraints=critical,CA:TRUE,pathlen:0' -addext 'keyUsage=critical,keyCertSign,cRLSign' >/dev/null 2>&1
    fi
    chmod 0600 "$dir/ca.key"; chmod 0644 "$dir/ca.pem"
    if [[ -s $tls/fullchain.pem && -s $tls/privkey.pem ]] \
        && openssl verify -CAfile "$dir/ca.pem" "$tls/fullchain.pem" >/dev/null 2>&1 \
        && openssl x509 -in "$tls/fullchain.pem" -noout -checkend 2592000 >/dev/null 2>&1 \
        && openssl x509 -in "$tls/fullchain.pem" -noout -ext subjectAltName 2>/dev/null | grep -Fq "$wanted"; then
        return 0
    fi
    install -d -m 0700 "$tls"
    openssl req -new -newkey rsa:3072 -nodes -sha256 -keyout "$tls/privkey.pem" -out "$dir/server.csr" -subj "/CN=$host" >/dev/null 2>&1
    printf 'basicConstraints=critical,CA:FALSE\nkeyUsage=critical,digitalSignature,keyEncipherment\nextendedKeyUsage=serverAuth\nsubjectAltName=%s\n' "$san" > "$dir/server.ext"
    openssl x509 -req -sha256 -days 825 -in "$dir/server.csr" -CA "$dir/ca.pem" -CAkey "$dir/ca.key" -CAcreateserial \
        -extfile "$dir/server.ext" -out "$tls/fullchain.pem" >/dev/null 2>&1
    chmod 0600 "$tls/privkey.pem" "$tls/fullchain.pem"
}
