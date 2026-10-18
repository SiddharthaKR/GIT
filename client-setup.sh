#!/usr/bin/env bash
# PrivateGit client setup. Run on each machine where a person uses Git (e.g. .157, .158,
# or .126 itself), as that person, WITHOUT sudo. Safe to run again.
#
#   bash client-setup.sh                       server 172.20.1.126, PrivateGit name = your Linux name
#   bash client-setup.sh SERVER_IP [--name NAME]
#   bash client-setup.sh [SERVER_IP] --test    after the admin linked your key: check access
#
# It creates an SSH key only for PrivateGit (~/.ssh/id_ed25519_privategit), adds SSH settings
# that apply ONLY to connections as git@SERVER (your normal ssh logins are unchanged), and
# prints the two commands your PrivateGit admin runs on the server.
set -Eeuo pipefail
SERVER=172.20.1.126; NAME=${USER:-$(id -un)}; TEST=false
while (($#)); do
    case $1 in
        --test) TEST=true ;;
        --name) NAME=${2:-}; shift ;;
        -h|--help) sed -n '2,11p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) SERVER=$1 ;;
    esac
    shift
done
die() { printf 'ERROR: %s\n' "$*" >&2; exit 1; }
[[ $EUID != 0 ]] || die 'Run as the person who will use Git, not with sudo/root.'
[[ $SERVER =~ ^[a-z0-9]([a-z0-9.-]*[a-z0-9])?$ ]] || die "Use the server's IP address or DNS name, e.g. 172.20.1.126"
[[ $NAME =~ ^[a-z][a-z0-9_-]{2,31}$ ]] || die "PrivateGit names are 3-32 lowercase letters/digits/_/-; pass --name NAME"
command -v ssh >/dev/null && command -v ssh-keygen >/dev/null && command -v git >/dev/null \
    || die 'ssh, ssh-keygen and git are required (Ubuntu packages openssh-client and git).'
KEY=$HOME/.ssh/id_ed25519_privategit
CONFIG=$HOME/.ssh/config

if [[ $TEST == true ]]; then
    printf 'Testing git@%s with %s ...\n' "$SERVER" "$KEY"
    out=$(ssh -T -o BatchMode=yes -o ConnectTimeout=10 -o StrictHostKeyChecking=accept-new "git@$SERVER" 2>&1 || true)
    printf '%s\n' "$out" | grep -v '^Warning: Permanently added' || true
    if grep -q "Hi $NAME" <<< "$out"; then
        printf '\nOK: the server knows you as %s. Clone with the address shown on the repository page.\n' "$NAME"
    elif grep -qi 'permission denied' <<< "$out"; then
        printf '\nNot linked yet: ask the admin to run the add-client.sh command printed by this script.\n'; exit 1
    else
        printf '\nUnexpected answer (see above). Check the server address and that this machine is in the allowed networks.\n'; exit 1
    fi
    exit 0
fi

# 1. Key (kept if it already exists)
install -d -m 0700 "$HOME/.ssh"
if [[ -f $KEY ]]; then printf 'Key exists:  %s (kept)\n' "$KEY"
else ssh-keygen -q -t ed25519 -f "$KEY" -N '' -C "$NAME@$(hostname -s)"; printf 'Key created: %s\n' "$KEY"; fi

# 2. SSH settings, only for connections as git@SERVER (Match ... user git)
begin="# privategit begin $SERVER"
touch "$CONFIG"; chmod 600 "$CONFIG"
if grep -qxF "$begin" "$CONFIG"; then printf 'SSH settings for git@%s already present in %s\n' "$SERVER" "$CONFIG"
else
    [[ ! -s $CONFIG || -z $(tail -c 1 "$CONFIG") ]] || printf '\n' >> "$CONFIG"
    cat >> "$CONFIG" <<EOF
$begin
Match host $SERVER user git
    IdentityFile ~/.ssh/id_ed25519_privategit
    IdentitiesOnly yes
# privategit end $SERVER
EOF
    printf 'SSH settings added to %s (only for git@%s)\n' "$CONFIG" "$SERVER"
fi
ssh -G -l git "$SERVER" >/dev/null 2>&1 || die "$CONFIG has a syntax problem; check the lines it contains."

# 3. Git identity (only if not set yet)
[[ -n $(git config --global user.name || true) ]] || { git config --global user.name "$NAME"; printf 'Git name set to %s\n' "$NAME"; }
[[ -n $(git config --global user.email || true) ]] || printf 'Tip: set your e-mail for commits: git config --global user.email you@company\n'

# 4. What the admin runs on the server
pub=$(cat "$KEY.pub")
cat <<EOF

=== Send this to your PrivateGit admin (public key: safe to share) ===
$pub

=== The admin runs on $SERVER ===
  sudo bash /opt/privategit/admin.sh user-add $NAME User          # only if $NAME has no account yet
  echo '$pub' > /tmp/$NAME.pub
  sudo bash /opt/privategit/add-client.sh $NAME /tmp/$NAME.pub
  # then grant a repository: dashboard > repository > Access, or
  sudo bash /opt/privategit/admin.sh access-grant REPO $NAME read|write

=== Then you run here ===
  bash $0 $SERVER --test
EOF
