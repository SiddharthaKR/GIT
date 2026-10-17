#!/usr/bin/env bash
# Generates a local private key + CSR. No certificate issuer is contacted.
set -Eeuo pipefail
umask 077
ROOT=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)
source "$ROOT/deploy/install-common.sh"
if [[ ${1:-} == --help ]]; then
    printf 'Usage: bash request-certificate.sh git.internal|172.20.1.126 [new-output-directory]\nGenerates a private key and a certificate request locally. Give only the .csr file to IT.\nNot needed if you choose the private PrivateGit CA in install.sh.\n'; exit 0
fi
HOST=${1:-}
if [[ -z $HOST ]]; then read -r -p 'Internal DNS hostname [git.internal]: ' HOST; HOST=${HOST:-git.internal}; fi
pg_host "$HOST" || { pg_die 'Use a lowercase internal DNS name such as git.internal or a fixed IPv4 address.'; exit 1; }
if pg_ipv4 "$HOST"; then SAN="IP:$HOST"; else SAN="DNS:$HOST"; fi
OUTPUT=${2:-privategit-certificate}
[[ ! -e $OUTPUT && ! -L $OUTPUT ]] || { pg_die 'Output directory already exists. Choose a new directory to avoid overwriting a private key.'; exit 1; }
command -v openssl >/dev/null || { pg_die 'OpenSSL is required.'; exit 1; }
mkdir -m 0700 -- "$OUTPUT"
openssl req -new -newkey rsa:3072 -nodes -keyout "$OUTPUT/privkey.pem" -out "$OUTPUT/server.csr" \
    -subj "/CN=$HOST" -addext "subjectAltName=$SAN"
chmod 0600 "$OUTPUT/privkey.pem"
printf '\nCreated locally in %s:\n  server.csr — give this request to your company IT/CA team.\n  privkey.pem — keep this private on the server. Do NOT send it.\n\nAsk IT for a server-authentication certificate with SAN %s,\na full-chain PEM file, and the company CA certificate/bundle.\nThen run sudo bash install.sh using those files.\n' "$OUTPUT" "$SAN"
