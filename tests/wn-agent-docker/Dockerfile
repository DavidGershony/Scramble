# wn-agent — the Dark Matter reference peer, for interop testing.
#
# Distinct from tests/whitenoise-docker, which builds the pre-Dark-Matter
# whitenoise-rs daemon. That one stays for as long as we test against the old
# protocol; this one is what the Scramble.Marmot engine is verified against.
#
# Upstream moves fast (roughly 8 commits a day through mid-2026), so the ref is
# pinned rather than tracking a branch. Bump MDK_REF deliberately, together with
# the reference pin recorded in ai-tasks/scramble-marmot-phased-plan-2026-08.md
# — an unpinned build here would silently retarget every interop test.

FROM rust:1.91-bookworm AS builder

RUN apt-get update && apt-get install -y --no-install-recommends \
        ca-certificates \
        git \
        libssl-dev \
        pkg-config \
    && rm -rf /var/lib/apt/lists/*

ARG MDK_REPO_URL=https://github.com/marmot-protocol/mdk.git
ARG MDK_REF=wn-agent-v0.9.10

WORKDIR /build
RUN git clone --filter=blob:none --no-checkout "${MDK_REPO_URL}" . \
    && git fetch --depth 1 origin "${MDK_REF}" \
    && git checkout FETCH_HEAD \
    && git log -1 --oneline

# Only the agent binary is needed; --locked keeps the dependency graph exactly
# as upstream tagged it.
RUN cargo build --locked --release -p agent-connector --bin wn-agent

FROM debian:bookworm-slim

# socat is how the tests reach the agent's control socket. The control plane
# is a Unix socket inside this container and the CLI exposes only
# `bootstrap`, so without it there is no way to ask the agent anything -
# including whether it joined a group we invited it to. Test image only.
RUN apt-get update && apt-get install -y --no-install-recommends \
        ca-certificates \
        libssl3 \
        socat \
    && rm -rf /var/lib/apt/lists/*

COPY --from=builder /build/target/release/wn-agent /usr/local/bin/wn-agent

# The agent refuses to start unless the socket's parent directory is no more
# permissive than --socket-dir-mode (default 0700). A directory left at the
# usual 0755 fails with a bare PermissionDenied that names no path, so this
# chmod is load-bearing rather than tidiness.
RUN mkdir -p /data /run/marmot-agent && chmod 0700 /run/marmot-agent

ENV MARMOT_HOME=/data/marmot-agent \
    MARMOT_AGENT_SOCKET=/run/marmot-agent/wn-agent.sock \
    RUST_LOG=info,cgka_engine=debug,agent_connector=debug

# The control plane is a Unix socket inside the container, so tests drive it
# with `docker exec` rather than over the network — the same shape the existing
# whitenoise interop client already uses.
#
# Dev-only flags, and deliberately so: --debug-controls exposes local control
# requests the tests need, --dev-allow-any-invites removes the welcomer
# allowlist so a Scramble peer can invite the agent, and
# --allow-loopback-relays permits the test relay. None of these are safe
# outside a throwaway container.
# Serving is the default: `wn-agent` with no subcommand listens on the control
# socket. There is no `serve` subcommand — only `bootstrap` and
# `import-identity`.
# The relay must be a LITERAL loopback host. The agent allows plaintext ws://
# endpoints only for loopback, and rejects private ranges outright — so the
# Docker bridge address of the relay container (172.x) is refused, and a
# service-name URL like ws://nostr-relay:8080 resolves to one. That is why this
# container runs with host networking and addresses the relay's published port
# on 127.0.0.1 instead.
ENTRYPOINT ["wn-agent", \
    "--home", "/data/marmot-agent", \
    "--socket", "/run/marmot-agent/wn-agent.sock", \
    "--relay", "ws://127.0.0.1:7777", \
    "--debug-controls", \
    "--dev-allow-any-invites", \
    "--allow-loopback-relays"]
