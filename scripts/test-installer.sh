#!/usr/bin/env bash
# Non-root tests: validation, rendered configuration and local certificate/key helpers.
set -Eeuo pipefail
# Git Bash otherwise rewrites OpenSSL's /CN= subject into a Windows path.
export MSYS2_ARG_CONV_EXCL='/CN='
ROOT=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)
source "$ROOT/deploy/install-common.sh"
passed=0
check() { "$@"; passed=$((passed + 1)); printf 'PASS %s\n' "$*"; }
reject() { if "$@"; then printf 'FAIL: accepted invalid input: %s\n' "$*" >&2; exit 1; fi; passed=$((passed + 1)); printf 'PASS rejected %s\n' "$*"; }
for script in install.sh add-client.sh admin.sh request-certificate.sh uninstall-disable.sh uninstall-remove.sh deploy/install-common.sh scripts/test-installer.sh scripts/test-ssh-gate.sh; do check bash -n "$ROOT/$script"; done
check pg_hostname git.internal
check pg_hostname code.development.company.internal
check pg_hostname git-01.company.internal
for host in localhost '*' 'https://git.internal' 'git.internal;id' '-git.internal' 'git-.internal' 'git..internal' 'git.internal/' 'git.internal:443' '192.168.1.100' 'GIT.internal'; do reject pg_hostname "$host"; done
# Offices without internal DNS use the server's fixed IP address.
for address in 172.20.1.126 172.20.1.157 10.0.0.5; do check pg_ipv4 "$address"; check pg_host "$address"; done
check pg_host git.internal
for address in 127.0.0.1 0.0.0.0 255.255.255.255 172.20.1.256 172.20.01.126 '172.20.1.126;id' '172.20.1.126:443' 'https://172.20.1.126' '172.20.1'; do reject pg_host "$address"; done
check pg_cidr 172.20.1.0/24
check pg_cidr 172.20.1.157/32
check pg_cidr 192.168.1.0/24
check pg_cidr 10.0.0.0/8
check pg_cidr 192.168.1.126/32
check pg_cidr 172.16.0.0/12
for network in 0.0.0.0/0 192.168.1.5/24 999.0.0.0/8 192.168.1.0/33 192.168.01.0/24 192.168.1.0/024 '192.168.1.0/24;id' '::/0' '192.168.1.0/24 deny all'; do reject pg_cidr "$network"; done
check pg_username alice
check pg_username git_admin-01
for user in a AB '../alice' 'alice;id' '-user'; do reject pg_username "$user"; done
mkdir -p "$ROOT/artifacts"
WORK=$(mktemp -d "$ROOT/artifacts/installer-tests.XXXXXX")
cleanup() {
    # Only remove this test's freshly-created, verified workspace directory.
    case "$WORK" in "$ROOT"/artifacts/installer-tests.*) [[ ! -L $WORK ]] && rm -rf -- "$WORK" ;; *) printf 'Unexpected test directory; refusing cleanup.\n' >&2 ;; esac
}
trap cleanup EXIT
ssh-keygen -q -t ed25519 -N '' -f "$WORK/client-key"
public=$(cat "$WORK/client-key.pub")
check pg_public_key_line "$public"
check ssh-keygen -lf "$WORK/client-key.pub"
reject pg_public_key_line '-----BEGIN OPENSSH PRIVATE KEY-----'
reject pg_public_key_line "restrict $public"
reject pg_public_key_line "ssh-rsa AAAA test"
reject pg_public_key_line "$public"$'\n''another-key'
printf 'ssh-ed25519 AAAA invalid\n' > "$WORK/invalid.pub"
reject ssh-keygen -lf "$WORK/invalid.pub"
pg_render_environment code.company.internal > "$WORK/environment"
check grep -Fxq AllowedHosts=code.company.internal "$WORK/environment"
check grep -Fxq PrivateGit__CloneBaseUrl=git@code.company.internal:/srv/privategit/repositories "$WORK/environment"
check grep -Fxq PrivateGit__AccessFile=/srv/privategit/access/access.json "$WORK/environment"
check grep -Fxq PrivateGit__AllowBranchDelete=true "$WORK/environment"
check grep -Fxq PrivateGit__HooksDirectory=/opt/privategit/hooks "$WORK/environment"
check grep -Fxq PrivateGit__MainBackup=true "$WORK/environment"
check grep -Fxq PrivateGit__NasBackupDirectory= "$WORK/environment"
check sh -n "$ROOT/hooks/backup-main"
check test -x "$ROOT/hooks/backup-main"
# Upgrade keeps administrator choices (backup off, NAS path, deletion off) but refreshes the rest.
printf 'AllowedHosts=old.example\nPrivateGit__MainBackup=false\nPrivateGit__NasBackupDirectory=/mnt/nas/git\nPrivateGit__NasBackupRepositories=fpga-rtl\nPrivateGit__AllowBranchDelete=false\n' > "$WORK/old.env"
pg_preserve_settings "$WORK/environment" "$WORK/old.env" > "$WORK/merged.env"
check grep -Fxq PrivateGit__MainBackup=false "$WORK/merged.env"
check grep -Fxq PrivateGit__NasBackupDirectory=/mnt/nas/git "$WORK/merged.env"
check grep -Fxq PrivateGit__NasBackupRepositories=fpga-rtl "$WORK/merged.env"
check grep -Fxq PrivateGit__AllowBranchDelete=false "$WORK/merged.env"
check grep -Fxq AllowedHosts=code.company.internal "$WORK/merged.env"
check test "$(pg_preserve_settings "$WORK/environment" "$WORK/missing.env")" = "$(cat "$WORK/environment")"
check sh -n "$ROOT/hooks/pre-receive"
check sh -n "$ROOT/hooks/post-receive"
check test -x "$ROOT/hooks/post-receive"
check test -x "$ROOT/hooks/pre-receive"
pg_render_environment 172.20.1.126 > "$WORK/environment-ip"
check grep -Fxq AllowedHosts=172.20.1.126 "$WORK/environment-ip"
check grep -Fxq PrivateGit__CloneBaseUrl=git@172.20.1.126:/srv/privategit/repositories "$WORK/environment-ip"
reject pg_render_environment 'git.internal;id' 
pg_render_nginx "$ROOT/deploy/nginx.conf" code.company.internal 10.20.0.0/16 192.168.1.126/32 > "$WORK/nginx.conf"
check grep -Fq 'server_name code.company.internal;' "$WORK/nginx.conf"
check grep -Fq 'allow 10.20.0.0/16;' "$WORK/nginx.conf"
check grep -Fq 'allow 192.168.1.126/32;' "$WORK/nginx.conf"
check grep -Fq 'deny all;' "$WORK/nginx.conf"
reject grep -Fq git.internal "$WORK/nginx.conf"
reject grep -Fq 'allow 192.168.1.0/24;' "$WORK/nginx.conf"
pg_render_sshd 10.20.0.0/16 192.168.1.126/32 > "$WORK/sshd.conf"
check grep -Fxq 'Match User git' "$WORK/sshd.conf"
check grep -Fq 'AllowUsers git@10.20.0.0/16 git@192.168.1.126/32' "$WORK/sshd.conf"
check grep -Fxq '    DisableForwarding yes' "$WORK/sshd.conf"
check grep -Fxq 'Match all' "$WORK/sshd.conf"
check grep -Fxq '    ExposeAuthInfo yes' "$WORK/sshd.conf"
check grep -Fxq '    ForceCommand /opt/privategit/PrivateGit git-serve --config /etc/privategit/environment' "$WORK/sshd.conf"
reject grep -Fq 'git-shell' "$WORK/sshd.conf"
# Private CA for an IP-addressed server: browsers/curl must accept it for that IP only.
pg_private_ca 172.20.1.126 "$WORK/ca" "$WORK/tls"
check openssl verify -purpose sslserver -verify_ip 172.20.1.126 -CAfile "$WORK/ca/ca.pem" "$WORK/tls/fullchain.pem"
reject openssl verify -purpose sslserver -verify_ip 172.20.1.157 -CAfile "$WORK/ca/ca.pem" "$WORK/tls/fullchain.pem"
check bash -c "openssl x509 -in '$WORK/tls/fullchain.pem' -noout -ext extendedKeyUsage | grep -q 'TLS Web Server Authentication'"
check bash -c "openssl x509 -in '$WORK/ca/ca.pem' -noout -ext basicConstraints | grep -q 'CA:TRUE'"
ca_before=$(openssl x509 -in "$WORK/ca/ca.pem" -noout -fingerprint); cert_before=$(openssl x509 -in "$WORK/tls/fullchain.pem" -noout -fingerprint)
pg_private_ca 172.20.1.126 "$WORK/ca" "$WORK/tls"
check test "$cert_before" = "$(openssl x509 -in "$WORK/tls/fullchain.pem" -noout -fingerprint)"
pg_private_ca 172.20.1.157 "$WORK/ca" "$WORK/tls"
check test "$ca_before" = "$(openssl x509 -in "$WORK/ca/ca.pem" -noout -fingerprint)"
check openssl verify -purpose sslserver -verify_ip 172.20.1.157 -CAfile "$WORK/ca/ca.pem" "$WORK/tls/fullchain.pem"
check bash -c "test \$(stat -c %a '$WORK/ca/ca.key') = 600 && test \$(stat -c %a '$WORK/tls/privkey.pem') = 600"
pg_render_nginx "$ROOT/deploy/nginx.conf" 172.20.1.157 172.20.1.0/24 > "$WORK/nginx-ip.conf"
check grep -Fq 'server_name 172.20.1.157;' "$WORK/nginx-ip.conf"
check grep -Fq 'allow 172.20.1.0/24;' "$WORK/nginx-ip.conf"
if command -v nginx >/dev/null 2>&1 && [[ $(uname -s) == Linux ]]; then
    sed -e "s#/etc/privategit/tls/#$WORK/tls/#" "$WORK/nginx-ip.conf" > "$WORK/site.conf"
    printf 'pid %s/nginx.pid;\nerror_log stderr;\nevents {}\nhttp {\n    access_log off;\n    include %s/site.conf;\n}\n' "$WORK" "$WORK" > "$WORK/nginx-main.conf"
    check nginx -q -t -c "$WORK/nginx-main.conf" -p "$WORK"
fi
check bash "$ROOT/install.sh" --help
check bash "$ROOT/add-client.sh" --help
check bash "$ROOT/admin.sh" --help
check bash "$ROOT/request-certificate.sh" --help
check bash "$ROOT/request-certificate.sh" git.internal "$WORK/certificate"
check test -s "$WORK/certificate/privkey.pem"
check test -s "$WORK/certificate/server.csr"
openssl req -in "$WORK/certificate/server.csr" -noout -verify 2>/dev/null
passed=$((passed + 1))
openssl req -in "$WORK/certificate/server.csr" -noout -text > "$WORK/csr.txt"
check grep -Fq DNS:git.internal "$WORK/csr.txt"
check bash "$ROOT/request-certificate.sh" 172.20.1.126 "$WORK/certificate-ip"
openssl req -in "$WORK/certificate-ip/server.csr" -noout -text > "$WORK/csr-ip.txt"
check grep -Fq 'IP Address:172.20.1.126' "$WORK/csr-ip.txt"
reject bash "$ROOT/request-certificate.sh" git.internal "$WORK/certificate"
reject bash "$ROOT/request-certificate.sh" 'git.internal;id' "$WORK/invalid-certificate"
# A disposable local CA tests exactly the installer's offline verification command.
openssl req -x509 -newkey rsa:2048 -nodes -keyout "$WORK/ca.key" -out "$WORK/ca.pem" -subj '/CN=PrivateGit Test CA' -days 2 >/dev/null 2>&1
openssl x509 -req -in "$WORK/certificate/server.csr" -CA "$WORK/ca.pem" -CAkey "$WORK/ca.key" -CAcreateserial -days 2 -copy_extensions copy -out "$WORK/fullchain.pem" >/dev/null 2>&1
check openssl verify -purpose sslserver -verify_hostname git.internal -CAfile "$WORK/ca.pem" -untrusted "$WORK/fullchain.pem" "$WORK/fullchain.pem"
reject openssl verify -purpose sslserver -verify_hostname wrong.internal -CAfile "$WORK/ca.pem" -untrusted "$WORK/fullchain.pem" "$WORK/fullchain.pem"
cert_public=$(openssl x509 -in "$WORK/fullchain.pem" -pubkey -noout | openssl pkey -pubin -outform DER | openssl dgst -sha256)
key_public=$(openssl pkey -in "$WORK/certificate/privkey.pem" -passin pass: -pubout -outform DER | openssl dgst -sha256)
check test "$cert_public" = "$key_public"
printf '\nAll %s installer checks passed. No root, package install or system configuration changes performed.\n' "$passed"
