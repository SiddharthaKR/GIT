#!/usr/bin/env bash
# Linux equivalent of scripts/publish.ps1: builds the self-contained server package.
# Needs the .NET 10 SDK on this machine (dotnet --list-sdks). Does not install anything.
#   bash scripts/publish.sh [linux-x64|linux-arm64] [--windows-guide]
set -Eeuo pipefail
ROOT=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)
RUNTIME=linux-x64; WINDOWS_GUIDE=false
for a in "$@"; do
    case $a in
        linux-x64|linux-arm64) RUNTIME=$a ;;
        --windows-guide) WINDOWS_GUIDE=true ;;
        *) printf 'Usage: bash scripts/publish.sh [linux-x64|linux-arm64] [--windows-guide]\n' >&2; exit 1 ;;
    esac
done
DOTNET=dotnet; [[ -x $ROOT/.dotnet/dotnet ]] && DOTNET=$ROOT/.dotnet/dotnet
command -v "$DOTNET" >/dev/null 2>&1 || { printf 'dotnet not found. Install the .NET 10 SDK, or build on Windows with scripts/publish.ps1.\n' >&2; exit 1; }
"$DOTNET" --list-sdks | grep -q '^10\.' || { printf 'The .NET 10 SDK is required (found: %s).\n' "$("$DOTNET" --list-sdks | tr '\n' ' ')" >&2; exit 1; }

OUT=$ROOT/artifacts/publish/$RUNTIME/package
rm -rf -- "$OUT"; mkdir -p "$OUT"
"$DOTNET" publish "$ROOT/PrivateGit/PrivateGit.csproj" -c Release -r "$RUNTIME" --self-contained true \
    -p:PublishSingleFile=true -p:DebugType=None -o "$OUT"

cp -r "$ROOT/deploy" "$OUT/deploy"
cp "$ROOT"/{README.md,SECURITY.md,TESTING.md,EASY-SETUP.md,OFFICE-SETUP.md,ARCHITECTURE.md} "$OUT/"
cp "$ROOT"/{install.sh,add-client.sh,admin.sh,request-certificate.sh} "$OUT/"
mkdir -p "$OUT/scripts"; cp "$ROOT/scripts/test-ssh-gate.sh" "$OUT/scripts/"
cp -r "$ROOT/hooks" "$OUT/hooks"
cp -r "$ROOT/templates" "$OUT/templates"
[[ $WINDOWS_GUIDE == false ]] || cp "$ROOT/WINDOWS-CLIENT.md" "$OUT/"
# Files checked out on Windows may carry CRLF; shell scripts and hooks must be LF.
find "$OUT" -name '*.sh' -type f -exec sed -i 's/\r$//' {} +
sed -i 's/\r$//' "$OUT"/hooks/*
chmod 0755 "$OUT/PrivateGit" "$OUT"/*.sh "$OUT"/scripts/*.sh "$OUT"/hooks/*

ZIP=$ROOT/artifacts/privategit-$RUNTIME.zip
if command -v zip >/dev/null 2>&1; then
    rm -f -- "$ZIP"; (cd "$OUT" && zip -qr "$ZIP" .)
    printf 'Zip for other servers: %s\n' "$ZIP"
fi
printf '\nPackage ready: %s\nOn this server: cd %s && bash install.sh --check\n' "$OUT" "$OUT"
