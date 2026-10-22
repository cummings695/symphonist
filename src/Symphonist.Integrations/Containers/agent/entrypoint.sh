#!/bin/bash
# Sets up the outbound firewall as root, then runs the agent command as the unprivileged `agent` user.
# Fails closed: if the firewall cannot be set up, the agent never runs.
set -euo pipefail

if ! /usr/local/bin/init-firewall.sh >&2; then
    echo "entrypoint: firewall setup failed; refusing to run the agent" >&2
    exit 70
fi

# On Linux/macOS hosts, own workspace files as the developer. Never map to root: the agent must not keep
# root (and with it NET_ADMIN, which could undo the firewall).
if [ -n "${SYMPHONIST_UID:-}" ] && [ -n "${SYMPHONIST_GID:-}" ]; then
    if [[ ! "$SYMPHONIST_UID" =~ ^[0-9]+$ ]] || [[ ! "$SYMPHONIST_GID" =~ ^[0-9]+$ ]] \
        || [ "$SYMPHONIST_UID" = 0 ] || [ "$SYMPHONIST_GID" = 0 ]; then
        echo "entrypoint: refusing uid/gid '$SYMPHONIST_UID:$SYMPHONIST_GID'" >&2
        exit 71
    fi
    if [ "$SYMPHONIST_GID" != "$(id -g agent)" ]; then groupmod -o -g "$SYMPHONIST_GID" agent; fi
    if [ "$SYMPHONIST_UID" != "$(id -u agent)" ]; then usermod -o -u "$SYMPHONIST_UID" agent; fi
fi
unset SYMPHONIST_UID SYMPHONIST_GID

export HOME=/home/agent USER=agent
exec setpriv --reuid=agent --regid=agent --init-groups \
    --inh-caps=-all --bounding-set=-all --no-new-privs "$@"
