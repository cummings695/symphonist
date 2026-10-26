#!/bin/bash
# Default-deny outbound firewall for the agent container.
# Adapted from Claude Code's reference devcontainer firewall (iptables + ipset).
# Allows: loopback, DNS to the configured resolvers only, established/related traffic, and HTTP(S) to the A records
# of each allowed domain.
# Any failure exits non-zero; entrypoint.sh then refuses to start the agent.
set -euo pipefail
IFS=$'\n\t'

IPV4_RE='^[0-9]{1,3}(\.[0-9]{1,3}){3}$'
DOMAIN_RE='^[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?)+$'

# The agent provider's own domains (DockerLauncher.BaseDomainsVar, from AgentProviderInfo.BaseDomains): always
# allowed and must resolve. Required: without them the agent cannot work, so refuse before touching any rule.
IFS=',' read -r -a RAW_BASE_DOMAINS <<< "${SYMPHONIST_BASE_DOMAINS:-}"
IFS=$'\n\t'
BASE_DOMAINS=()
for domain in "${RAW_BASE_DOMAINS[@]}"; do
    domain="${domain//[[:space:]]/}"
    if [ -n "$domain" ]; then BASE_DOMAINS+=("$domain"); fi
done
if [ "${#BASE_DOMAINS[@]}" -eq 0 ]; then
    echo "init-firewall: no base domains (SYMPHONIST_BASE_DOMAINS is empty)" >&2
    exit 1
fi

# Resolvers this container is configured to use (plus Docker's embedded DNS); port 53 is open to these only, so
# it cannot be used as a general egress channel.
NAMESERVERS=$( (grep -E '^[[:space:]]*nameserver[[:space:]]' /etc/resolv.conf | awk '{ print $2 }'; echo 127.0.0.11)     | grep -E "$IPV4_RE" | sort -u)
if [ -z "$NAMESERVERS" ]; then
    echo "init-firewall: no IPv4 nameserver in /etc/resolv.conf" >&2
    exit 1
fi

# Keep Docker's embedded-DNS NAT rules (127.0.0.11) across the flush.
DOCKER_DNS_RULES=$(iptables-save -t nat | grep "127\.0\.0\.11" || true)

iptables -F
iptables -X
iptables -t nat -F
iptables -t nat -X
iptables -t mangle -F
iptables -t mangle -X
ipset destroy allowed 2>/dev/null || true

if [ -n "$DOCKER_DNS_RULES" ]; then
    iptables -t nat -N DOCKER_OUTPUT 2>/dev/null || true
    iptables -t nat -N DOCKER_POSTROUTING 2>/dev/null || true
    echo "$DOCKER_DNS_RULES" | xargs -L 1 iptables -t nat
fi

# Loopback (also covers Docker's embedded resolver at 127.0.0.11).
iptables -A INPUT -i lo -j ACCEPT
iptables -A OUTPUT -o lo -j ACCEPT
iptables -A OUTPUT -d 127.0.0.11 -j ACCEPT
# DNS, to the configured resolvers only.
for ns in $NAMESERVERS; do
    for proto in udp tcp; do
        iptables -A OUTPUT -p "$proto" -d "$ns" --dport 53 -j ACCEPT
        iptables -A INPUT -p "$proto" -s "$ns" --sport 53 -j ACCEPT
    done
    echo "init-firewall: DNS via $ns"
done
# Replies to connections we were allowed to open.
iptables -A INPUT -m state --state ESTABLISHED,RELATED -j ACCEPT
iptables -A OUTPUT -m state --state ESTABLISHED,RELATED -j ACCEPT

ipset create allowed hash:net

add_domain() { # domain required(0/1)
    local domain="$1" required="$2" out ips ip added=0
    if [[ ! "$domain" =~ $DOMAIN_RE ]]; then
        echo "init-firewall: invalid domain '$domain'" >&2
        [ "$required" = 1 ] && return 1
        return 0
    fi
    if ! out=$(dig +noall +comments +answer +time=3 +tries=2 A "$domain"); then
        echo "init-firewall: DNS lookup failed for '$domain'" >&2
        [ "$required" = 1 ] && return 1
        return 0
    fi
    # The answer may include CNAME records; keep only A-record IPv4 addresses.
    ips=$(echo "$out" | awk '$4 == "A" { print $5 }' | grep -E "$IPV4_RE" | sort -u || true)
    for ip in $ips; do
        ipset add allowed "$ip" -exist
        added=1
    done
    if [ "$added" = 0 ]; then
        # An authoritative "no such domain" is not a resolver failure (e.g. a retired telemetry host):
        # nothing to allow, keep going. Anything else (timeout, SERVFAIL, no A records) is fatal for base domains.
        if echo "$out" | grep -q "status: NXDOMAIN"; then
            echo "init-firewall: '$domain' does not exist (NXDOMAIN); skipping" >&2
            return 0
        fi
        echo "init-firewall: could not resolve '$domain'" >&2
        [ "$required" = 1 ] && return 1
        return 0
    fi
    echo "init-firewall: allowed $domain ($(echo "$ips" | tr '\n' ' '))"
}

is_base_domain() {
    local base
    for base in "${BASE_DOMAINS[@]}"; do [ "$base" = "$1" ] && return 0; done
    return 1
}

for domain in "${BASE_DOMAINS[@]}"; do
    add_domain "$domain" 1
done
IFS=',' read -r -a EXTRA_DOMAINS <<< "${SYMPHONIST_ALLOWED_DOMAINS:-}"
IFS=$'\n\t'
for domain in "${EXTRA_DOMAINS[@]}"; do
    domain="${domain//[[:space:]]/}"
    [ -z "$domain" ] && continue
    is_base_domain "$domain" && continue
    add_domain "$domain" 0
done

# Allowed hosts are reachable over HTTP(S) only.
iptables -A OUTPUT -p tcp -m multiport --dports 80,443 -m set --match-set allowed dst -j ACCEPT
iptables -P INPUT DROP
iptables -P FORWARD DROP
iptables -P OUTPUT DROP
# Reject (not just drop) everything else so blocked calls fail fast.
iptables -A OUTPUT -j REJECT --reject-with icmp-admin-prohibited

# No IPv6 egress at all (the allowlist is IPv4-only).
ip6tables -F
ip6tables -X
ip6tables -A INPUT -i lo -j ACCEPT
ip6tables -A OUTPUT -o lo -j ACCEPT
ip6tables -P INPUT DROP
ip6tables -P FORWARD DROP
ip6tables -P OUTPUT DROP

# Verify: an unlisted host must be unreachable.
if curl -sS --connect-timeout 5 -o /dev/null https://example.com 2>/dev/null; then
    echo "init-firewall: verification failed, example.com is reachable" >&2
    exit 1
fi
echo "init-firewall: ready"
