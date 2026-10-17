# Optional Windows Git client

Use this only after the Linux server and a Linux client have been tested. Windows is an additional client, not a prerequisite or a different central server. These instructions are excluded from the default Linux deployment ZIP and included only with `scripts/publish.ps1 -IncludeWindowsClientGuide`.

1. Install Git for Windows from your company's approved software source.
2. Trust the HTTPS CA on the Windows computer: import `privategit-ca.crt` (private PrivateGit CA) or the company CA into *Trusted Root Certification Authorities*. Add the server address (for example `172.20.1.126`) to the proxy bypass list.
3. Open Git Bash and generate a unique key:

```bash
mkdir -p ~/.ssh
ssh-keygen -t ed25519 -f ~/.ssh/id_ed25519_internal_git -C "windows-company-client"
cat ~/.ssh/id_ed25519_internal_git.pub
```

Keep the private key on that Windows client. Transfer only the `.pub` file to the central Linux server. Its administrator authorizes it:

```bash
sudo bash /opt/privategit/add-client.sh alice /absolute/path/windows-client.pub
```

The helper prints the SSH settings to add in Git Bash's `~/.ssh/config`, typically:

```sshconfig
Host internal-git 172.20.1.126
    HostName 172.20.1.126
    User git
    IdentityFile ~/.ssh/id_ed25519_internal_git
    IdentitiesOnly yes
```

Verify the first SSH connection's host fingerprint with the server administrator. On Windows, clone and work using the same Git commands as Linux:

```bash
git clone git@172.20.1.126:/srv/privategit/repositories/project.git
cd project
git config user.name "Your Name"
git config user.email "you@example.internal"
git pull --ff-only
# Edit project files.
git add .
git commit -m "Describe the change"
git push
```

Use a committed `.gitattributes` file to define line endings for projects shared across operating systems. Browse the same internal HTTPS dashboard with your web account and authenticator code. SSH Git authorization and web login remain separate. The key acts as that user: it can clone repositories where the user has Read and push where they have Read & write.

No server reinstallation, Windows service on the Linux server, or additional external API connection is required.
