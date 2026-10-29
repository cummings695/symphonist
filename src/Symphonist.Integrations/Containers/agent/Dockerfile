# Sandbox image for unattended Claude Code runs. Built by DockerLauncher.EnsureImageAsync as symphonist/agent-claude:1.
# Node 22 LTS comes from the official image: the SDK base (Ubuntu 24.04) only ships Node 18 via apt.
FROM node:22.23.4-bookworm-slim AS node

FROM mcr.microsoft.com/dotnet/sdk:10.0

ENV DEBIAN_FRONTEND=noninteractive \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    PLAYWRIGHT_BROWSERS_PATH=/opt/ms-playwright

RUN apt-get update \
 && apt-get install -y --no-install-recommends git curl ca-certificates iptables ipset dnsutils jq \
 && rm -rf /var/lib/apt/lists/*

COPY --from=node /usr/local/bin/ /usr/local/bin/
COPY --from=node /usr/local/lib/node_modules/ /usr/local/lib/node_modules/
COPY --from=node /usr/local/include/node/ /usr/local/include/node/
RUN node --version | grep -q '^v22\.' && npm --version

RUN npm install -g @anthropic-ai/claude-code \
 && npm cache clean --force \
 && claude --version

# Chromium: the base image is Ubuntu 24.04, whose apt "chromium-browser" is only a snap stub (unusable in a
# container) and which has no "chromium" package, so install Playwright's Chromium build plus its apt deps.
ARG PLAYWRIGHT_VERSION=1.55.0
RUN npx -y "playwright@${PLAYWRIGHT_VERSION}" install --with-deps chromium \
 && ln -s "$(find /opt/ms-playwright -type f -name chrome -path '*chrome-linux*' | head -n 1)" /usr/local/bin/chromium \
 && chmod -R a+rX /opt/ms-playwright \
 && chromium --version \
 && rm -rf /var/lib/apt/lists/* /root/.npm

# The base image ships user "ubuntu" with uid/gid 1000; replace it with "agent".
RUN userdel -r ubuntu 2>/dev/null || true \
 && groupadd -g 1000 agent \
 && useradd -m -u 1000 -g agent -s /bin/bash agent \
 && mkdir -p /workspace \
 && chown agent:agent /workspace

COPY --chmod=755 entrypoint.sh init-firewall.sh /usr/local/bin/
# Tolerate CRLF checkouts.
RUN sed -i 's/\r$//' /usr/local/bin/entrypoint.sh /usr/local/bin/init-firewall.sh

WORKDIR /workspace
ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
