#!/usr/bin/env bash
# Guided installer for the packaged Linux binary, not the source ZIP.
set -Eeuo pipefail
umask 077
PACKAGE=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)
source "$PACKAGE/deploy/install-common.sh"

usage() {
    cat <<'EOF'
PrivateGit installer — Ubuntu 22.04 or 24.04, systemd, x64 or ARM64 matching the package.
  sudo bash install.sh          guided install / resume / update
  bash install.sh --check       read-only prerequisites check
  sudo bash install.sh --offline  install using preinstalled OS packages only
  bash install.sh --help        this explanation

Missing OS packages can be installed from your configured apt repositories
during setup. --offline prevents those downloads. No external APIs, certificate
issuers, or cloud services are contacted by the app. HTTPS uses either a
certificate from IT or a private PrivateGit CA created on this server.
The server can be reached by an internal DNS name or by its fixed IP address.
Existing administrator SSH access and firewall rules are not changed. Only the
dedicated git SSH account is restricted to the network(s) you enter and to the
PrivateGit permission gate. --check does not run the application.
EOF
}
MODE=${1:-install}
[[ $# -le 1 ]] || { usage; exit 1; }
case "$MODE" in --help|-h) usage; exit 0 ;; install|--check|--offline) ;; *) usage; exit 1 ;; esac

printf '\nPrivateGit — local company-network installation\n\n'
[[ $(uname -s) == Linux ]] || { pg_die 'Run this on the Linux server, not on your Windows development PC.'; exit 1; }
[[ -f /etc/os-release ]] || { pg_die 'Cannot identify the Linux distribution.'; exit 1; }
source /etc/os-release
[[ ${ID:-} == ubuntu && ( ${VERSION_ID:-} == 22.04 || ${VERSION_ID:-} == 24.04 ) ]] || { pg_die 'This installer targets Ubuntu 22.04 or 24.04. See README.md for manual deployment on other systems.'; exit 1; }
[[ -d /run/systemd/system ]] || { pg_die 'A running systemd installation is required.'; exit 1; }
if [[ $MODE != --check && $EUID != 0 ]]; then pg_die 'Run sudo bash install.sh for installation.'; exit 1; fi
missing=()
for tool in git nginx openssl systemctl runuser groupadd useradd usermod adduser install getent sed grep cp chmod chown uname realpath od head tr curl ss ip awk tar flock cmp mktemp ssh-keygen dpkg-query stat id date dirname ln rm sleep find; do
    command -v "$tool" >/dev/null 2>&1 || missing+=("$tool")
done
[[ -x /usr/sbin/sshd ]] || missing+=(sshd)
if ((${#missing[@]})); then
    printf 'Missing commands: %s\n' "${missing[*]}" >&2
    if [[ $MODE != install ]]; then
        pg_die 'Install the missing Ubuntu packages first or run the guided installer without --offline. --check and --offline never download packages.'
        exit 1
    fi
    printf '\nSetup can install required OS packages using this server\x27s configured apt repositories.\n'
    read -r -p 'Install missing prerequisites now? [Y/n]: ' FETCH_PACKAGES
    [[ ${FETCH_PACKAGES:-Y} == [Yy] ]] || { pg_die 'Prerequisites are required. Ask IT to install them and rerun.'; exit 1; }
    apt-get update
    apt-get install -y --no-install-recommends git openssh-server nginx openssl curl ca-certificates iproute2 util-linux coreutils passwd adduser tar grep sed gawk
    for tool in git nginx openssl systemctl runuser groupadd useradd usermod adduser install getent sed grep cp chmod chown uname realpath od head tr curl ss ip awk tar flock cmp mktemp ssh-keygen dpkg-query stat id date dirname ln rm sleep find; do
        command -v "$tool" >/dev/null 2>&1 || { pg_die "Required command still missing after package installation: $tool"; exit 1; }
    done
    [[ -x /usr/sbin/sshd ]] || { pg_die 'OpenSSH server is still missing.'; exit 1; }
fi
[[ -f $PACKAGE/PrivateGit && ! -L $PACKAGE/PrivateGit ]] || { pg_die 'Extract the Linux deployment ZIP first. The source ZIP alone cannot be installed.'; exit 1; }
magic=$(head -c 4 "$PACKAGE/PrivateGit" | od -An -tx1 | tr -d ' \n')
[[ $magic == 7f454c46 ]] || { pg_die 'The package does not contain a Linux executable.'; exit 1; }
read -r machine_low machine_high <<< "$(od -An -tu1 -j18 -N2 "$PACKAGE/PrivateGit")"
case "$(uname -m):$machine_low:$machine_high" in x86_64:62:0|aarch64:183:0) ;; *) pg_die 'The executable architecture does not match this server. Publish/download the correct x64 or ARM64 package.'; exit 1 ;; esac
for asset in deploy/nginx.conf deploy/privategit.service appsettings.json wwwroot/site.css wwwroot/history.css add-client.sh admin.sh request-certificate.sh hooks/pre-receive hooks/post-receive hooks/backup-main; do
    [[ -f $PACKAGE/$asset ]] || { pg_die "Incomplete package: missing $asset"; exit 1; }
done
printf 'Prerequisites and executable architecture: OK.\n'
[[ $MODE != --check ]] || exit 0
[[ $EUID == 0 ]] || { pg_die 'Run sudo bash install.sh for installation.'; exit 1; }
[[ -t 0 ]] || { pg_die 'Run in an interactive terminal; account passwords are entered without echo.'; exit 1; }
exec 9>/run/lock/privategit-install.lock
flock -n 9 || { pg_die 'Another installer is running.'; exit 1; }

# Refuse unrelated identities or locations. Only our own interrupted/completed
# installation may be resumed. Never reset an existing account or repository.
MARKER=/etc/privategit/installer-owned
if [[ -f $MARKER && ! -L $MARKER ]] && [[ $(stat -c %u "$MARKER") == 0 ]] && grep -qx 'privategit-installer-v1' "$MARKER"; then
    owned=true
else
    owned=false
    for path in /opt/privategit /var/lib/privategit /srv/privategit /etc/privategit /etc/nginx/sites-available/privategit /etc/nginx/sites-enabled/privategit /etc/ssh/sshd_config.d/60-privategit.conf /etc/systemd/system/privategit.service; do
        [[ ! -e $path && ! -L $path ]] || { pg_die "Existing location is not owned by this installer: $path. Ask IT to inspect it; it will not be overwritten."; exit 1; }
    done
    for user in git gitdash; do
        ! getent passwd "$user" >/dev/null || { pg_die "Account '$user' already exists. Ask IT to inspect it before installing."; exit 1; }
    done
    ! getent group gitrepos >/dev/null || { pg_die 'The gitrepos group already exists and is not owned by this installer.'; exit 1; }
fi
for path in /opt/privategit /var/lib/privategit /srv/privategit /srv/privategit/repositories /etc/privategit; do
    [[ ! -L $path ]] || { pg_die "Refusing a symbolic installation directory: $path"; exit 1; }
done
[[ $PACKAGE != /opt/privategit && $PACKAGE != /opt/privategit/* ]] || { pg_die 'Extract the ZIP in a separate staging directory, not /opt/privategit.'; exit 1; }
grep -Eq '^[[:space:]]*Include[[:space:]]+/etc/ssh/sshd_config\.d/\*\.conf' /etc/ssh/sshd_config || { pg_die 'The Ubuntu SSH configuration must include /etc/ssh/sshd_config.d/*.conf. Ask IT to review it.'; exit 1; }
nginx -t
/usr/sbin/sshd -t
if [[ $owned == false ]]; then
    [[ -z $(ss -H -ltn 'sport = :5080') ]] || { pg_die 'Port 5080 is already occupied by another application.'; exit 1; }
fi
# Port 443 must be free or already served by nginx (another HTTPS app, e.g. on the
# same lab server, would conflict).
if listeners=$(ss -H -ltnp 'sport = :443' 2>/dev/null) && [[ -n $listeners ]] && ! grep -q '"nginx"' <<< "$listeners"; then
    pg_die "TCP 443 is already used by another program: $listeners. Stop/move it or install PrivateGit on another server."; exit 1
fi

DETECTED=$(pg_primary_ipv4 || true)
PREVIOUS=$(sed -n 's/^AllowedHosts=//p' /etc/privategit/environment 2>/dev/null || true)
DEFAULT_HOST=${PREVIOUS:-${DETECTED:-git.internal}}
printf '\nServer address: the name or fixed IP people use to reach this server.\n'
printf 'Without internal DNS, use this server'"'"'s IP address (detected: %s).\n' "${DETECTED:-none}"
read -r -p "Server address [$DEFAULT_HOST]: " HOST
HOST=${HOST:-$DEFAULT_HOST}
pg_host "$HOST" || { pg_die 'Use a fixed IPv4 address such as 172.20.1.126 or a lowercase DNS name such as git.internal (no URLs or ports).'; exit 1; }
DEFAULT_NETWORKS=$(paste -sd' ' /etc/privategit/allowed-networks 2>/dev/null || true)
if [[ -z $DEFAULT_NETWORKS ]] && pg_ipv4 "$HOST"; then DEFAULT_NETWORKS="${HOST%.*}.0/24"; fi
read -r -p "Allowed client networks/machines, space separated [${DEFAULT_NETWORKS:-example 172.20.1.0/24}]: " NETWORK_TEXT
NETWORK_TEXT=${NETWORK_TEXT:-$DEFAULT_NETWORKS}
read -r -a NETWORKS <<< "$NETWORK_TEXT"
((${#NETWORKS[@]} >= 1 && ${#NETWORKS[@]} <= 16)) || { pg_die 'Enter 1-16 approved IPv4 networks.'; exit 1; }
for network in "${NETWORKS[@]}"; do pg_cidr "$network" || { pg_die "Invalid network: $network. Use a network such as 172.20.1.0/24 or a single machine as 172.20.1.157/32. /0 is not accepted."; exit 1; }; done
DEFAULT_TLS=1
[[ ! -f /etc/privategit/tls-mode ]] || DEFAULT_TLS=$(cat /etc/privategit/tls-mode)
printf '\nHTTPS certificate for the dashboard:\n  1) Create a private PrivateGit CA on this server (no IT needed; install its CA file on each client once)\n  2) Use certificate files supplied by IT\n'
read -r -p "Choose 1 or 2 [$DEFAULT_TLS]: " TLS_MODE
TLS_MODE=${TLS_MODE:-$DEFAULT_TLS}
[[ $TLS_MODE == 1 || $TLS_MODE == 2 ]] || { pg_die 'Choose 1 or 2.'; exit 1; }
if [[ $TLS_MODE == 2 ]]; then
    read -r -p 'Certificate full-chain PEM file (absolute path): ' CERT
    read -r -p 'Matching private-key PEM file (absolute path, unencrypted for Nginx): ' KEY
    read -r -p 'Trusted company CA bundle [/etc/ssl/certs/ca-certificates.crt]: ' CA
    CA=${CA:-/etc/ssl/certs/ca-certificates.crt}
    for input in "$CERT" "$KEY" "$CA"; do
        [[ $input == /* && -f $input ]] || { pg_die 'Certificate, key and CA bundle must be existing files with absolute paths.'; exit 1; }
    done
    CERT=$(realpath -- "$CERT"); KEY=$(realpath -- "$KEY"); CA=$(realpath -- "$CA")
    openssl x509 -in "$CERT" -noout -checkend 86400 >/dev/null || { pg_die 'Certificate is invalid or expires within one day.'; exit 1; }
    if pg_ipv4 "$HOST"; then
        openssl x509 -in "$CERT" -noout -checkip "$HOST" | grep -q 'does match' || { pg_die 'Certificate does not include this IP address as a subject-alternative name.'; exit 1; }
        openssl verify -purpose sslserver -verify_ip "$HOST" -CAfile "$CA" -untrusted "$CERT" "$CERT" >/dev/null || { pg_die 'Certificate chain could not be verified against the supplied CA bundle.'; exit 1; }
    else
        openssl x509 -in "$CERT" -noout -checkhost "$HOST" | grep -q 'does match' || { pg_die 'Certificate does not match the hostname.'; exit 1; }
        openssl x509 -in "$CERT" -noout -ext subjectAltName | grep -q 'DNS:' || { pg_die 'The certificate must include a DNS subject-alternative name for browser trust.'; exit 1; }
        openssl verify -purpose sslserver -verify_hostname "$HOST" -CAfile "$CA" -untrusted "$CERT" "$CERT" >/dev/null || { pg_die 'Certificate chain could not be verified against the supplied CA bundle.'; exit 1; }
    fi
    cert_public=$(openssl x509 -in "$CERT" -pubkey -noout | openssl pkey -pubin -outform DER | openssl dgst -sha256)
    key_public=$(openssl pkey -in "$KEY" -passin pass: -pubout -outform DER | openssl dgst -sha256) || { pg_die 'The private key must be a valid, unencrypted PEM key protected by file permissions.'; exit 1; }
    [[ $cert_public == "$key_public" ]] || { pg_die 'Certificate and private key do not match.'; exit 1; }
fi
FIRST_ADMIN=false
if [[ ! -s /var/lib/privategit/accounts.json ]]; then
    FIRST_ADMIN=true
    read -r -p 'First administrator username [alice]: ' ADMIN
    ADMIN=${ADMIN:-alice}
    pg_username "$ADMIN" || { pg_die 'Use 3-32 lowercase letters, numbers, _ or -, starting with a letter.'; exit 1; }
fi
printf '\nReady to install:\n  Dashboard: https://%s\n  Clone URLs: git@%s:REPO.git\n  Allowed clients: %s\n  HTTPS: %s\n  Application: /opt/privategit\n  Repositories: /srv/privategit/repositories\n' "$HOST" "$HOST" "${NETWORKS[*]}" "$([[ $TLS_MODE == 1 ]] && echo 'private PrivateGit CA' || echo 'IT certificate')"
printf '  No external API/cloud calls. Existing admin SSH and firewall policy stay under IT control.\n'
printf '  Existing installer-owned configuration will be backed up; account/repository data is retained.\n'
read -r -p 'Type INSTALL to continue: ' CONFIRM
[[ $CONFIRM == INSTALL ]] || { printf 'No installation changes made.\n'; exit 0; }

BACKUP=/var/backups/privategit/install-$(date -u +%Y%m%dT%H%M%SZ)-$$
install -d -o root -g root -m 0700 "$BACKUP"
CONFIG_PATHS=(/etc/nginx/sites-available/privategit /etc/nginx/sites-enabled/privategit /etc/ssh/sshd_config.d/60-privategit.conf /etc/systemd/system/privategit.service /etc/privategit/environment /etc/privategit/allowed-networks /etc/privategit/tls-mode /etc/privategit/tls/fullchain.pem /etc/privategit/tls/privkey.pem)
for path in "${CONFIG_PATHS[@]}"; do
    install -d -m 0700 "$BACKUP$(dirname "$path")"
    if [[ -e $path || -L $path ]]; then cp -a -- "$path" "$BACKUP$path"; fi
done
if [[ -d /opt/privategit ]]; then tar -C /opt -czf "$BACKUP/application.tar.gz" privategit; fi
restore_configuration() {
    local result=$? path
    trap - ERR
    set +e
    systemctl stop privategit >/dev/null 2>&1
    for path in "${CONFIG_PATHS[@]}"; do
        rm -f -- "$path"
        if [[ -e $BACKUP$path || -L $BACKUP$path ]]; then cp -a -- "$BACKUP$path" "$path"; fi
    done
    systemctl daemon-reload
    nginx -t && systemctl reload nginx
    /usr/sbin/sshd -t && systemctl reload ssh
    printf '\nInstallation did not finish. Previous configuration has been restored.\n' >&2
    printf 'The dashboard is stopped. Existing accounts/repositories were not deleted.\nBackup: %s\nFix the reported issue and rerun this installer from the staging directory.\n' "$BACKUP" >&2
    exit "$result"
}
trap restore_configuration ERR
install -d -o root -g root -m 0755 /etc/privategit
printf 'privategit-installer-v1\n' > "$MARKER"
chmod 0600 "$MARKER"
if ! getent group gitrepos >/dev/null; then groupadd --system gitrepos; fi
if ! getent passwd gitdash >/dev/null; then useradd --system --gid gitrepos --home-dir /var/lib/privategit --shell /usr/sbin/nologin gitdash; fi
if ! getent passwd git >/dev/null; then adduser --disabled-password --gecos '' --ingroup gitrepos git; fi
[[ $(id -gn gitdash) == gitrepos && $(id -gn git) == gitrepos ]] || { pg_die 'Installer-owned accounts have unexpected primary groups. Ask IT to inspect them.'; false; }
# sshd runs the forced PrivateGit gate through the account shell; the gate (ForceCommand)
# is the only program the git account can ever run over SSH.
usermod --shell /bin/sh git
# Reinstalling after uninstall-disable.sh: unlock the git account and drop the parked copy.
usermod --expiredate '' git
rm -rf -- /etc/privategit/disabled
install -d -o gitdash -g gitrepos -m 0700 /var/lib/privategit
install -d -o root -g gitrepos -m 0750 /srv/privategit
install -d -o gitdash -g gitrepos -m 2770 /srv/privategit/repositories
install -d -o gitdash -g gitrepos -m 2750 /srv/privategit/access
# Hidden main-branch backups: group gitrepos only (the git and dashboard accounts), no users.
install -d -o root -g gitrepos -m 2770 /srv/privategit/backups
install -d -o root -g root -m 0700 /etc/privategit/tls
install_tls_file() {
    local input=$1 target=$2
    # Reusing our existing certificate path must not truncate/copy a file onto itself.
    if [[ $input != "$target" ]]; then install -o root -g root -m 0600 "$input" "$target"; else chmod 0600 "$target"; chown root:root "$target"; fi
}
if [[ $TLS_MODE == 1 ]]; then
    pg_private_ca "$HOST" /etc/privategit/ca /etc/privategit/tls
    chown -R root:root /etc/privategit/ca /etc/privategit/tls
else
    install_tls_file "$CERT" /etc/privategit/tls/fullchain.pem
    install_tls_file "$KEY" /etc/privategit/tls/privkey.pem
fi
printf '%s\n' "$TLS_MODE" > /etc/privategit/tls-mode
if systemctl is-active --quiet privategit; then systemctl stop privategit; fi
install -d -o root -g root -m 0755 /opt/privategit
# Copy trusted release files only, not arbitrary staging content or client secrets.
install -o root -g root -m 0755 "$PACKAGE/PrivateGit" /opt/privategit/PrivateGit
install -o root -g root -m 0644 "$PACKAGE/appsettings.json" /opt/privategit/appsettings.json
install -d -o root -g root -m 0755 /opt/privategit/wwwroot /opt/privategit/deploy
cp -R -- "$PACKAGE/wwwroot/." /opt/privategit/wwwroot/
cp -R -- "$PACKAGE/deploy/." /opt/privategit/deploy/
# Push hooks: record branch creators; let creators (and admins) delete their branches; protect main and tags.
install -d -o root -g root -m 0755 /opt/privategit/hooks
for hook in pre-receive post-receive backup-main; do
    [[ -f $PACKAGE/hooks/$hook ]] || continue
    sed 's/\r$//' "$PACKAGE/hooks/$hook" > "/opt/privategit/hooks/$hook"
    chown root:root "/opt/privategit/hooks/$hook"; chmod 0755 "/opt/privategit/hooks/$hook"
done
# Project templates (.gitignore / .gitattributes) that clients can copy.
if [[ -d $PACKAGE/templates ]]; then install -d -o root -g root -m 0755 /opt/privategit/templates; cp -R -- "$PACKAGE/templates/." /opt/privategit/templates/; fi
# The installer uses umask 077 for keys/backups, but public release assets must
# remain readable by the unprivileged gitdash service and helper users.
find /opt/privategit/wwwroot /opt/privategit/deploy /opt/privategit/templates -type d -exec chmod 0755 {} + 2>/dev/null || true
find /opt/privategit/wwwroot /opt/privategit/deploy /opt/privategit/templates -type f -exec chmod 0644 {} + 2>/dev/null || true
for asset in install.sh add-client.sh admin.sh request-certificate.sh uninstall-disable.sh uninstall-remove.sh client-setup.sh README.md SECURITY.md TESTING.md EASY-SETUP.md OFFICE-SETUP.md ARCHITECTURE.md WINDOWS-CLIENT.md PrivateGit.staticwebassets.endpoints.json; do
    [[ ! -f $PACKAGE/$asset ]] || install -o root -g root -m 0644 "$PACKAGE/$asset" "/opt/privategit/$asset"
done
if [[ $TLS_MODE == 1 ]]; then install -m 0644 /etc/privategit/ca/ca.pem /opt/privategit/privategit-ca.crt; fi
chown -R root:root /opt/privategit
pg_render_environment "$HOST" > /etc/privategit/environment.new
pg_preserve_settings /etc/privategit/environment.new /etc/privategit/environment > /etc/privategit/environment.merged
mv -f /etc/privategit/environment.merged /etc/privategit/environment; rm -f /etc/privategit/environment.new
chown root:gitrepos /etc/privategit/environment; chmod 0640 /etc/privategit/environment
printf '%s\n' "${NETWORKS[@]}" > /etc/privategit/allowed-networks
chmod 0600 /etc/privategit/allowed-networks
if [[ ! -f /etc/privategit/authorized_keys ]]; then install -o root -g gitrepos -m 0640 /dev/null /etc/privategit/authorized_keys; fi
[[ ! -L /etc/privategit/authorized_keys ]] || { pg_die 'Refusing a symbolic authorized_keys file.'; false; }
chown root:gitrepos /etc/privategit/authorized_keys; chmod 0640 /etc/privategit/authorized_keys
pg_render_nginx "$PACKAGE/deploy/nginx.conf" "$HOST" "${NETWORKS[@]}" > /etc/nginx/sites-available/privategit
ln -sfn -- /etc/nginx/sites-available/privategit /etc/nginx/sites-enabled/privategit
pg_render_sshd "${NETWORKS[@]}" > /etc/ssh/sshd_config.d/60-privategit.conf
install -o root -g root -m 0644 "$PACKAGE/deploy/privategit.service" /etc/systemd/system/privategit.service
nginx -t
/usr/sbin/sshd -t
effective_ssh=$(/usr/sbin/sshd -T -C "user=git,host=$HOST,addr=${NETWORKS[0]%/*}")
for expected in 'authenticationmethods publickey' 'pubkeyauthentication yes' 'passwordauthentication no' 'kbdinteractiveauthentication no' 'disableforwarding yes' 'permittty no' 'permituserrc no' 'authorizedkeysfile /etc/privategit/authorized_keys' 'exposeauthinfo yes' 'forcecommand /opt/privategit/PrivateGit git-serve --config /etc/privategit/environment'; do
    grep -Fxq "$expected" <<< "$effective_ssh" || { pg_die "Existing SSH rules override the required Git policy: $expected. Ask IT to inspect the effective configuration."; false; }
done
if [[ $FIRST_ADMIN == true ]]; then
    printf '\nCreate the administrator password and enroll the authenticator secret shown next.\n'
    bash /opt/privategit/admin.sh user-add "$ADMIN" Admin
fi
systemctl daemon-reload
systemctl enable --now ssh nginx privategit
systemctl reload ssh
systemctl reload nginx
healthy=false
for attempt in {1..15}; do
    if curl --noproxy '*' --silent --output /dev/null --fail --max-time 3 -H "Host: $HOST" -H 'X-Forwarded-Proto: https' http://127.0.0.1:5080/Login; then healthy=true; break; fi
    sleep 1
done
[[ $healthy == true ]] || { pg_die 'The local dashboard health check failed. Check journalctl -u privategit.'; false; }
# Initial hidden backup of every repository's main branch (later: automatically on each push).
if grep -qx 'PrivateGit__MainBackup=true' /etc/privategit/environment; then
    # Server backups only: installation never waits for (or depends on) the optional NAS.
    printf '\nBacking up main branches (server):\n'; bash /opt/privategit/admin.sh backup-now --server-only || printf 'Backup step reported a problem; see above (installation continues).\n'
fi
# The SSH gate (run as the git account) must be able to read its configuration.
runuser -u git -- test -r /etc/privategit/environment -a -r /etc/privategit/authorized_keys -a -x /srv/privategit/access \
    || { pg_die 'The git account cannot read the gate configuration. Check group gitrepos permissions.'; false; }
trap - ERR
printf '\nInstalled. Open https://%s from an allowed client.\n' "$HOST"
if [[ $TLS_MODE == 1 ]]; then
    printf '\nHTTPS uses the private PrivateGit CA. Trust it once on each client:\n'
    printf '  Linux:   scp ADMIN@%s:/opt/privategit/privategit-ca.crt . && sudo cp privategit-ca.crt /usr/local/share/ca-certificates/ && sudo update-ca-certificates\n' "$HOST"
    printf '  Windows: import privategit-ca.crt into "Trusted Root Certification Authorities" (certmgr.msc)\n'
fi
printf '\nBehind the company proxy, add %s to no_proxy / the browser proxy bypass list.\n' "$HOST"
printf 'Next steps (see OFFICE-SETUP.md):\n'
printf '  1. Users:        sudo bash /opt/privategit/admin.sh user-add NAME User\n'
printf '  2. SSH keys:     sudo bash /opt/privategit/add-client.sh NAME /path/NAME.pub\n'
printf '  3. Permissions:  dashboard > repository > Access, or admin.sh access-grant REPO NAME read|write\n'
if [[ $owned == true ]] && grep -Evq '(^#|^$|privategit:[a-z])' /etc/privategit/authorized_keys; then
    printf '\nUpgrade note: existing SSH keys are not linked to users yet and are now refused.\n'
    printf 'Re-add each with add-client.sh USER key.pub (see add-client.sh --list).\n'
fi
printf 'Configuration/application backup: %s (not a repository backup).\n' "$BACKUP"
printf 'Git SSH host fingerprint for clients to verify:\n'
ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub
