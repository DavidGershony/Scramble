# The Marmot reference CLI client, for automated Dark Matter interop.
#
# Distinct from the other two peer images, and it supersedes one of them:
#
#   tests/whitenoise-docker  - whitenoise-rs, the pre-Dark-Matter daemon. That
#     repository is ARCHIVED upstream ("this repository is obsolete") and pins
#     mdk-core 0.8.0, so it can only ever test the legacy protocol. It stays
#     while Scramble.Core still ships on that protocol.
#   tests/wn-agent-docker    - the agent connector. A gateway integration, not a
#     chat client: two CLI commands and a Unix control socket. Useful for
#     KeyPackage-shape checks, but it never subscribes for inbound, so it cannot
#     complete an invite.
#
# This image builds `wn` and `wnd` from mdk's own crates/cli - a full client
# with groups, messages, invites and sync. It is the Dark Matter equivalent of
# what whitenoise-rs used to give us, which is what makes an end-to-end
# automated interop test possible again.
#
# TRACKS UPSTREAM'S MOVING `wn-agent-latest` TAG rather than a fixed release.
#
# That is a deliberate trade, made 2026-09-09. Marmot asked for interop testing
# now, so finding their regressions early is the point of this image rather
# than noise in it -- a peer frozen at a tag we chose tests a version nobody
# runs. The cost is that a red interop run may be upstream's change rather than
# ours, so read the version the readiness check prints before assuming the
# fault is local, and report what you find back to them.
#
# To reproduce a specific build, override the ref:
#   $env:MDK_REF = "wn-agent-v0.9.20"
#   docker compose -f docker-compose.test.yml build mdk-cli

FROM rust:1.91-bookworm AS builder

RUN apt-get update && apt-get install -y --no-install-recommends \
        ca-certificates \
        git \
        libssl-dev \
        pkg-config \
        cmake \
        clang \
    && rm -rf /var/lib/apt/lists/*

ARG MDK_REPO_URL=https://github.com/marmot-protocol/mdk.git
ARG MDK_REF=wn-agent-latest

# The commit MDK_REF resolved to when the build was launched.
#
# Without this the build is WRONG rather than merely stale: MDK_REF is a moving
# tag, so the clone below keys its cache on the unchanged string
# "wn-agent-latest" and Docker happily reuses a checkout from weeks ago.
# Passing the resolved commit makes the cache key move exactly when upstream
# does -- an unchanged commit reuses everything, a new one rebuilds from here.
#
# scripts/build-marmot-peers.ps1 resolves and passes it. Building by hand
# without it gets "unresolved", which caches on that literal and has the same
# staleness problem, so use the script.
ARG MDK_COMMIT=unresolved

WORKDIR /build
RUN git clone --filter=blob:none --no-checkout "${MDK_REPO_URL}" . \
    && git fetch --depth 1 origin "${MDK_REF}" \
    && git checkout FETCH_HEAD \
    && git log -1 --oneline

# Fails the build if the tag moved between resolving it and cloning it, so a
# race produces an error rather than an image mislabelled with a commit it does
# not contain.
RUN actual=$(git rev-parse HEAD); \
    echo "MDK_REF=${MDK_REF} requested=${MDK_COMMIT} actual=${actual}"; \
    if [ "${MDK_COMMIT}" != "unresolved" ] && [ "${actual}" != "${MDK_COMMIT}" ]; then \
      echo "The tag moved mid-build. Re-run."; exit 1; \
    fi

# --locked keeps the dependency graph exactly as upstream tagged it.
RUN cargo build --locked --release -p wn-cli --bin wn --bin wnd

FROM debian:bookworm-slim

RUN apt-get update && apt-get install -y --no-install-recommends \
        ca-certificates \
        libssl3 \
    && rm -rf /var/lib/apt/lists/*

COPY --from=builder /build/target/release/wn /usr/local/bin/wn
COPY --from=builder /build/target/release/wnd /usr/local/bin/wnd

ARG MDK_COMMIT
LABEL mdk.commit="${MDK_COMMIT}"

RUN mkdir -p /data /logs

ENV MARMOT_HOME=/data/wn \
    RUST_LOG=info,cgka_engine=debug,marmot_app=debug

# The container idles and the tests drive it with `docker exec`. The daemon is
# started by the harness rather than by the entrypoint so its relay flags can
# change without rebuilding this image - the whole point of a test peer is to
# be re-pointed cheaply.
#
# Two flags make this workable where the old whitenoise image needed a patched
# binary: `--secret-store file` avoids the OS keychain entirely, and the daemon's
# --discovery-relays / --default-account-relays replace patching hardcoded relay
# defaults into the source.
ENTRYPOINT ["sleep", "infinity"]
